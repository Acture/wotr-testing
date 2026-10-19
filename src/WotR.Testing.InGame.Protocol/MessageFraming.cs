using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace WotR.Testing.InGame.Protocol
{
    /// <summary>Frames: a four-byte little-endian payload length, then the message as UTF-8 JSON.</summary>
    public static class MessageFraming
    {
        public const int MaxMessageBytes = 1024 * 1024;
        private static readonly ConcurrentDictionary<Type, DataContractJsonSerializer> Serializers = new ConcurrentDictionary<Type, DataContractJsonSerializer>();

        public static byte[] Encode(Message message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (!Message.Types.TryGetValue(message.GetType().Name, out var type) || type != message.GetType())
                throw new ArgumentException("Not a protocol message type: " + message.GetType());
            message.Type = type.Name;
            using (var payload = new MemoryStream()) {
                payload.Position = 4;
                Serializer(type).WriteObject(payload, message);
                var length = payload.Length - 4;
                // Senders batch discovery results and limit diagnostic text; anything still too large is a sender bug.
                if (length > MaxMessageBytes)
                    throw new ProtocolException(ProtocolException.InvalidLength, type.Name + " is " + length + " bytes; the limit is " + MaxMessageBytes + ".");
                var frame = payload.ToArray();
                for (int index = 0; index < 4; index++) frame[index] = (byte)(length >> (8 * index));
                return frame;
            }
        }

        public static Message Decode(byte[] payload)
        {
            Message envelope;
            try {
                RequireOneObject(payload);
                envelope = (Message)Read(typeof(Envelope), payload);
            }
            catch (Exception error) when (error is SerializationException || error is XmlException || error is DecoderFallbackException)
            { throw new ProtocolException(ProtocolException.Malformed, "The message is not valid protocol JSON.", error); }
            if (envelope == null) throw new ProtocolException(ProtocolException.Malformed, "The message is empty.");
            if (envelope.ProtocolVersion != Message.CurrentVersion)
                throw new ProtocolException(ProtocolException.Mismatch, "The peer speaks protocol " + envelope.ProtocolVersion + "; this side speaks " + Message.CurrentVersion + ".");
            if (envelope.Type == null || !Message.Types.TryGetValue(envelope.Type, out var type))
                throw new ProtocolException(ProtocolException.Malformed, "Unknown message type: " + (envelope.Type ?? "none"));
            try { return (Message)Read(type, payload); }
            catch (Exception error) when (error is SerializationException || error is XmlException || error is DecoderFallbackException)
            { throw new ProtocolException(ProtocolException.Malformed, "Invalid " + type.Name + " message.", error); }
        }

        public static async Task WriteAsync(Stream stream, Message message, CancellationToken cancellationToken = default)
        {
            var frame = Encode(message);
            await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Reads one message, or null when the peer closed the stream between messages.</summary>
        public static async Task<Message> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var header = new byte[4];
            if (!await Fill(stream, header, true, cancellationToken).ConfigureAwait(false)) return null;
            var length = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
            // Checked before allocating, so a corrupt header cannot request a huge buffer.
            if (length <= 0 || length > MaxMessageBytes)
                throw new ProtocolException(ProtocolException.InvalidLength, "Invalid message length " + length + ".");
            var payload = new byte[length];
            await Fill(stream, payload, false, cancellationToken).ConfigureAwait(false);
            return Decode(payload);
        }

        // Pipes may return fewer bytes than requested; keep reading until the buffer is full.
        private static async Task<bool> Fill(Stream stream, byte[] buffer, bool endAllowed, CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < buffer.Length;) {
                var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0) {
                    if (endAllowed && offset == 0) return false;
                    throw new ProtocolException(ProtocolException.Disconnected, "The connection closed inside a message.");
                }
                offset += read;
            }
            return true;
        }

        private static object Read(Type type, byte[] payload)
        {
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(payload, 0, payload.Length, new UTF8Encoding(false, true), XmlDictionaryReaderQuotas.Max, null))
                return Serializer(type).ReadObject(reader);
        }

        // The in-box JSON reader closes unterminated objects at the end of input and ignores trailing data.
        private static void RequireOneObject(byte[] payload)
        {
            int depth = 0, end = -1;
            bool text = false, escaped = false;
            for (int index = 0; index < payload.Length; index++) {
                var value = payload[index];
                if (text) {
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == '"') text = false;
                    continue;
                }
                if (value == ' ' || value == '\t' || value == '\r' || value == '\n') continue;
                if (end >= 0) throw new SerializationException("Data follows the message object.");
                if (depth == 0 && value != '{') throw new SerializationException("A message must be one JSON object.");
                if (value == '"') text = true;
                else if (value == '{' || value == '[') depth++;
                else if ((value == '}' || value == ']') && --depth == 0) end = index;
            }
            if (end < 0) throw new SerializationException("The message object is incomplete.");
        }

        private static DataContractJsonSerializer Serializer(Type type) => Serializers.GetOrAdd(type, t => new DataContractJsonSerializer(t));
    }

    /// <summary>The host's only event writer: stamps the session and the next sequence number in send order.</summary>
    public sealed class EventWriter
    {
        private readonly Stream stream;
        private readonly string sessionId;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private long sequence;
        private bool broken;

        public EventWriter(Stream stream, string sessionId)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        }

        public async Task WriteAsync(Event message, CancellationToken cancellationToken = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                // A failed or cancelled write may have sent part of a frame; nothing after it can be trusted.
                if (broken) throw new ProtocolException(ProtocolException.Disconnected, "An earlier write failed; the session cannot continue.");
                message.SessionId = sessionId;
                message.Sequence = sequence + 1;
                var frame = MessageFraming.Encode(message);
                broken = true;
                await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                broken = false;
                sequence++;
            }
            finally { gate.Release(); }
        }
    }
}
