using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;

namespace WotR.Testing.Fuzzing
{
    /// <summary>Per-run typed FsCheck generators and optional exact argument codecs.</summary>
    public sealed class FuzzGenerators
    {
        private readonly Dictionary<Type, IValueGenerator> registered = new Dictionary<Type, IValueGenerator>();

        /// <summary>Registers a generator for a built-in argument type; custom types also require a codec.</summary>
        public void Register<T>(Arbitrary<T> arbitrary)
        {
            if (arbitrary == null) throw new ArgumentNullException(nameof(arbitrary));
            registered[typeof(T)] = new TypedGenerator<T>(arbitrary, null, null);
        }

        /// <summary>Registers a custom generator and lossless serializer/deserializer. Codec text is stored inside JSON.</summary>
        public void Register<T>(Arbitrary<T> arbitrary, Func<T, string> serialize, Func<string, T> deserialize)
        {
            if (arbitrary == null) throw new ArgumentNullException(nameof(arbitrary));
            if (serialize == null) throw new ArgumentNullException(nameof(serialize));
            if (deserialize == null) throw new ArgumentNullException(nameof(deserialize));
            registered[typeof(T)] = new TypedGenerator<T>(arbitrary, serialize, deserialize);
        }

        internal FuzzGenerators Clone()
        {
            var copy = new FuzzGenerators();
            foreach (var item in registered) copy.registered.Add(item.Key, item.Value);
            return copy;
        }

        internal IValueGenerator Resolve(Type type)
        {
            if (registered.TryGetValue(type, out var custom))
            {
                if (custom.HasCodec) return custom;
                var codec = BuiltIn(type);
                if (codec == null) throw Unsupported(type);
                return new GeneratorWithCodec(custom, codec);
            }
            return BuiltIn(type) ?? throw Unsupported(type);
        }

        private static NotSupportedException Unsupported(Type type) => new NotSupportedException(
            "No replayable generator for " + type + ". Register Arbitrary<T> with an explicit serialize/deserialize codec, or exclude the method.");

        private IValueGenerator BuiltIn(Type type)
        {
            if (type == typeof(bool)) return Primitive(false, true);
            if (type == typeof(byte)) return Primitive<byte>(0, 1, byte.MaxValue);
            if (type == typeof(sbyte)) return Primitive<sbyte>(0, -1, 1, sbyte.MinValue, sbyte.MaxValue);
            if (type == typeof(short)) return Primitive<short>(0, -1, 1, short.MinValue, short.MaxValue);
            if (type == typeof(ushort)) return Primitive<ushort>(0, 1, ushort.MaxValue);
            if (type == typeof(int)) return Primitive(0, -1, 1, int.MinValue, int.MaxValue);
            if (type == typeof(uint)) return Primitive<uint>(0, 1, uint.MaxValue);
            if (type == typeof(long)) return Primitive(0L, -1L, 1L, long.MinValue, long.MaxValue);
            if (type == typeof(ulong)) return Primitive<ulong>(0, 1, ulong.MaxValue);
            if (type == typeof(float))
            {
                var source = Primitive(0f, -0f, 1f, -1f, float.Epsilon, float.MinValue, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity);
                return new DelegateGenerator(source.Generate, source.Shrink,
                    value => unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes((float)value), 0)).ToString("X8", CultureInfo.InvariantCulture),
                    text => BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)uint.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))), 0));
            }
            if (type == typeof(double))
            {
                var source = Primitive(0d, -0d, 1d, -1d, double.Epsilon, double.MinValue, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity);
                return new DelegateGenerator(source.Generate, source.Shrink,
                    value => unchecked((ulong)BitConverter.DoubleToInt64Bits((double)value)).ToString("X16", CultureInfo.InvariantCulture),
                    text => BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))));
            }
            if (type == typeof(decimal)) return Primitive(0m, 1m, -1m, decimal.MinValue, decimal.MaxValue);
            if (type == typeof(char))
            {
                var source = Primitive('\0', 'a', '\n', char.MinValue, char.MaxValue, '\ud800');
                return new DelegateGenerator(source.Generate, source.Shrink,
                    value => ((int)(char)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    text => (char)int.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
            }
            if (type == typeof(string))
            {
                var source = Primitive<string>(null, "", " ", "\0", "abc", "\ud800", "汉字", new string('a', 256));
                return new DelegateGenerator(source.Generate, source.Shrink, EncodeString, DecodeString);
            }
            if (type.IsEnum)
            {
                var underlying = Resolve(Enum.GetUnderlyingType(type));
                var values = Enum.GetValues(type).Cast<object>().ToArray();
                return new DelegateGenerator(random => values.Length > 0 && random.Next(2) == 0 ? values[random.Next(values.Length)] :
                    Enum.ToObject(type, underlying.Generate(random)), value => underlying.Shrink(Convert.ChangeType(value, Enum.GetUnderlyingType(type)))
                        .Select(item => Enum.ToObject(type, item)),
                    value => underlying.Serialize(Convert.ChangeType(value, Enum.GetUnderlyingType(type))),
                    text => Enum.ToObject(type, underlying.Deserialize(text)));
            }
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
            {
                var inner = Resolve(nullable);
                return new DelegateGenerator(random => random.Next(4) == 0 ? null : inner.Generate(random),
                    value => value == null ? Enumerable.Empty<object>() : new[] { (object)null }.Concat(inner.Shrink(value)),
                    value => value == null ? null : inner.Serialize(value), text => text == null ? null : inner.Deserialize(text));
            }
            if (type.IsArray && type.GetArrayRank() == 1)
            {
                var elementType = type.GetElementType();
                var element = Resolve(elementType);
                return new DelegateGenerator(random => {
                    if (random.Next(8) == 0) return null;
                    int length = random.Next(17);
                    var array = Array.CreateInstance(elementType, length);
                    for (int i = 0; i < length; i++) array.SetValue(element.Generate(random), i);
                    return array;
                }, value => ShrinkArray((Array)value, element, elementType), value => value == null ? null :
                    JsonSerializer.Serialize(((Array)value).Cast<object>().Select(element.Serialize).ToArray()), text => {
                    if (text == null) return null;
                    var parts = JsonSerializer.Deserialize<string[]>(text);
                    var array = Array.CreateInstance(elementType, parts.Length);
                    for (int i = 0; i < parts.Length; i++) array.SetValue(element.Deserialize(parts[i]), i);
                    return array;
                });
            }
            return null;
        }

        private static IEnumerable<object> ShrinkArray(Array array, IValueGenerator element, Type elementType)
        {
            if (array == null) yield break;
            yield return null;
            if (array.Length == 0) yield break;
            yield return Array.CreateInstance(elementType, 0);
            for (int index = 0; index < array.Length; index++)
            {
                var shorter = Array.CreateInstance(elementType, array.Length - 1);
                for (int source = 0, target = 0; source < array.Length; source++)
                    if (source != index) shorter.SetValue(array.GetValue(source), target++);
                yield return shorter;
                foreach (var smaller in element.Shrink(array.GetValue(index)))
                {
                    var copy = (Array)array.Clone();
                    copy.SetValue(smaller, index);
                    yield return copy;
                }
            }
        }

        private static IValueGenerator Primitive<T>(params T[] boundaries)
        {
            var original = ArbMap.Default.ArbFor<T>();
            return new TypedGenerator<T>(Arb.From(Gen.OneOf(Gen.Elements(boundaries), original.Generator), value => original.Shrinker(value)),
                value => JsonSerializer.Serialize(value, JsonFormat.Options),
                text => JsonSerializer.Deserialize<T>(text, JsonFormat.Options));
        }

        private static string EncodeString(object value)
        {
            if (value == null) return null;
            var chars = ((string)value).ToCharArray();
            var bytes = new byte[chars.Length * 2];
            for (int i = 0; i < chars.Length; i++) { bytes[i * 2] = (byte)chars[i]; bytes[i * 2 + 1] = (byte)(chars[i] >> 8); }
            return Convert.ToBase64String(bytes);
        }

        private static object DecodeString(string text)
        {
            if (text == null) return null;
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length % 2 != 0) throw new ArgumentException("Invalid UTF-16 replay value.");
            var chars = new char[bytes.Length / 2];
            for (int i = 0; i < chars.Length; i++) chars[i] = (char)(bytes[i * 2] | bytes[i * 2 + 1] << 8);
            return new string(chars);
        }
    }

    internal interface IValueGenerator
    {
        bool HasCodec { get; }
        object Generate(FuzzRandom random);
        IEnumerable<object> Shrink(object value);
        string Serialize(object value);
        object Deserialize(string text);
    }

    internal sealed class TypedGenerator<T> : IValueGenerator
    {
        private readonly Arbitrary<T> arbitrary;
        private readonly Func<T, string> serialize;
        private readonly Func<string, T> deserialize;
        public bool HasCodec => serialize != null;
        internal TypedGenerator(Arbitrary<T> arbitrary, Func<T, string> serialize, Func<string, T> deserialize)
        { this.arbitrary = arbitrary; this.serialize = serialize; this.deserialize = deserialize; }
        public object Generate(FuzzRandom random) => random.Sample(arbitrary.Generator);
        public IEnumerable<object> Shrink(object value) => arbitrary.Shrinker((T)value).Select(item => (object)item);
        public string Serialize(object value) => serialize((T)value);
        public object Deserialize(string text) => deserialize(text);
    }

    internal sealed class DelegateGenerator : IValueGenerator
    {
        private readonly Func<FuzzRandom, object> generate;
        private readonly Func<object, IEnumerable<object>> shrink;
        private readonly Func<object, string> serialize;
        private readonly Func<string, object> deserialize;
        public bool HasCodec => true;
        internal DelegateGenerator(Func<FuzzRandom, object> generate, Func<object, IEnumerable<object>> shrink,
            Func<object, string> serialize, Func<string, object> deserialize)
        { this.generate = generate; this.shrink = shrink; this.serialize = serialize; this.deserialize = deserialize; }
        public object Generate(FuzzRandom random) => generate(random);
        public IEnumerable<object> Shrink(object value) => shrink(value);
        public string Serialize(object value) => serialize(value);
        public object Deserialize(string text) => deserialize(text);
    }

    internal sealed class GeneratorWithCodec : IValueGenerator
    {
        private readonly IValueGenerator generator, codec;
        internal GeneratorWithCodec(IValueGenerator generator, IValueGenerator codec) { this.generator = generator; this.codec = codec; }
        public bool HasCodec => true;
        public object Generate(FuzzRandom random) => generator.Generate(random);
        public IEnumerable<object> Shrink(object value) => generator.Shrink(value);
        public string Serialize(object value) => codec.Serialize(value);
        public object Deserialize(string text) => codec.Deserialize(text);
    }

    internal sealed class FuzzRandom
    {
        private ulong seed;
        internal FuzzRandom(int seed) { this.seed = unchecked((ulong)(long)seed); }
        internal T Sample<T>(Gen<T> generator)
        {
            // Each sample gets a deterministic independent FsCheck random stream.
            seed = unchecked(seed + 0x9e3779b97f4a7c15UL);
            return generator.Sample(1, new Rnd(seed), 50)[0];
        }
        internal int Next(int exclusiveMaximum) => Sample(Gen.Choose(0, exclusiveMaximum - 1));
    }
}
