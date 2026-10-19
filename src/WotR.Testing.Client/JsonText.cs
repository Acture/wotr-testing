using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WotR.Testing.Client
{
    /// <summary>
    /// Writes report JSON from dictionaries, lists and scalars without a serializer dependency: the VSTest adapter runs
    /// in a .NET Framework test host. <see cref="Raw"/> embeds JSON that a host or worker already produced.
    /// </summary>
    public static class JsonText
    {
        public sealed class Raw
        {
            public Raw(string json) => Json = json;
            public string Json { get; }
        }

        public static string Write(object value)
        {
            var text = new StringBuilder();
            Write(text, value, 0);
            return text.Append('\n').ToString();
        }

        private static void Write(StringBuilder text, object value, int depth)
        {
            switch (value) {
                case null: text.Append("null"); break;
                case Raw raw: text.Append(string.IsNullOrWhiteSpace(raw.Json) ? "null" : raw.Json.Trim()); break;
                case string s: String(text, s); break;
                case bool b: text.Append(b ? "true" : "false"); break;
                case DateTime time: String(text, time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> map:
                    text.Append('{');
                    var first = true;
                    foreach (var pair in map) {
                        text.Append(first ? "\n" : ",\n").Append(' ', (depth + 1) * 2);
                        String(text, pair.Key);
                        text.Append(": ");
                        Write(text, pair.Value, depth + 1);
                        first = false;
                    }
                    if (!first) text.Append('\n').Append(' ', depth * 2);
                    text.Append('}');
                    break;
                case IEnumerable list:
                    text.Append('[');
                    var any = false;
                    foreach (var item in list) {
                        text.Append(any ? ",\n" : "\n").Append(' ', (depth + 1) * 2);
                        Write(text, item, depth + 1);
                        any = true;
                    }
                    if (any) text.Append('\n').Append(' ', depth * 2);
                    text.Append(']');
                    break;
                case IFormattable number: text.Append(number.ToString(null, CultureInfo.InvariantCulture)); break;
                default: String(text, value.ToString()); break;
            }
        }

        private static void String(StringBuilder text, string value)
        {
            text.Append('"');
            foreach (var c in value) {
                switch (c) {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }
    }
}
