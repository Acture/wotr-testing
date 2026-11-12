using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The serialized settings of the AstarPath component in a mechanics scene, without its graph data, which battles read
    /// from the area's .nav bundle. Read from the scene's bundle, or from the navigation settings file a snapshot holds in
    /// place of the scene bundles. Shared by the offline library and the snapshot tool (src/WotR.Testing.Snapshot), so it
    /// uses no game or Unity types.
    /// </summary>
    internal static class SceneNavigationSettings
    {
        /// <summary>The file at the snapshot root.</summary>
        internal const string FileName = "navigation-settings.json";

        /// <summary>
        /// The scene bundles the snapshot tool reads. In 2.7.0 every area part with a navigation graph names a mechanics
        /// scene whose name contains "mechanic" (_mechanics, _mechanic, _basemechanics, _mechanics_main).
        /// </summary>
        internal const string ScenePattern = "*mechanic*.scenes";

        private const int FormatVersion = 1;

        /// <summary>Fields only the game's AstarPath serializes together.</summary>
        private static readonly string[] AstarPathFields = { "data", "maxNearestNodeDistance", "fullGetNearestSearch", "prioritizeGraphs", "heuristic", "threadCount" };

        /// <summary>The AstarPath components a scene bundle holds, each without its graph data (the field <c>data</c>).</summary>
        internal static List<AssetTypeValueField> Read(string bundlePath)
        {
            var found = new List<AssetTypeValueField>();
            var manager = new AssetsManager { UseMonoTemplateFieldCache = true };
            try
            {
                var bundle = manager.LoadBundleFile(bundlePath, true);
                for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                {
                    AssetsFileInstance assets;
                    // A scene bundle also holds resource files, which are not serialized files.
                    try { assets = manager.LoadAssetsFileFromBundle(bundle, i, false); }
                    catch (Exception) { continue; }
                    if (assets == null) continue;
                    foreach (var info in assets.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                    {
                        // The component's type tree tells an AstarPath apart before its values are read.
                        var template = manager.GetTemplateBaseField(assets, info);
                        if (template == null || !AstarPathFields.All(name => template.Children.Any(child => child.Name == name))) continue;
                        var field = manager.GetBaseField(assets, info);
                        field.Children.RemoveAll(child => child.FieldName == "data");
                        found.Add(field);
                    }
                }
            }
            finally
            {
                manager.UnloadAll(true);
            }
            return found;
        }

        /// <summary>One scene bundle's entry: its SHA-256 and its AstarPath components.</summary>
        internal sealed class Scene
        {
            internal Scene(string bundle, string sha256, IReadOnlyList<AssetTypeValueField> astarPaths)
            {
                Bundle = bundle;
                Sha256 = sha256;
                AstarPaths = astarPaths;
            }

            /// <summary>The bundle's file name in Bundles, in lower case.</summary>
            internal string Bundle { get; }
            internal string Sha256 { get; }
            internal IReadOnlyList<AssetTypeValueField> AstarPaths { get; }
        }

        /// <summary>A navigation settings file: the game version it was read from and its scenes by bundle name.</summary>
        internal sealed class Snapshot
        {
            internal Snapshot(string gameVersion, IReadOnlyDictionary<string, Scene> scenes)
            {
                GameVersion = gameVersion;
                Scenes = scenes;
            }

            internal string GameVersion { get; }
            internal IReadOnlyDictionary<string, Scene> Scenes { get; }
        }

        /// <summary>
        /// Writes every scene read, sorted by bundle name, also those without an AstarPath: a battle in their area fails as
        /// it does with the scene bundle.
        /// </summary>
        internal static void Write(string path, string gameVersion, IEnumerable<Scene> scenes)
        {
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteString("description", "The serialized settings of the AstarPath component of each mechanics scene, without its graph data, "
                + "for WotR.Testing.Offline. Read from licensed game files: do not commit or redistribute.");
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("gameVersion", gameVersion);
            writer.WriteString("scenePattern", ScenePattern);
            writer.WriteStartArray("scenes");
            foreach (var scene in scenes.OrderBy(s => s.Bundle, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("bundle", scene.Bundle);
                writer.WriteString("sha256", scene.Sha256);
                writer.WriteStartArray("astarPaths");
                foreach (var field in scene.AstarPaths) WriteField(writer, field);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        internal static Snapshot Load(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var format = root.GetProperty("formatVersion").GetInt32();
            if (format != FormatVersion)
                throw new InvalidDataException($"{path} has format version {format}; this library reads version {FormatVersion}. Make the snapshot again.");
            var scenes = new Dictionary<string, Scene>(StringComparer.OrdinalIgnoreCase);
            foreach (var scene in root.GetProperty("scenes").EnumerateArray())
            {
                var bundle = scene.GetProperty("bundle").GetString();
                scenes.Add(bundle, new Scene(bundle, scene.GetProperty("sha256").GetString(),
                    scene.GetProperty("astarPaths").EnumerateArray().Select(ReadField).ToList()));
            }
            return new Snapshot(root.GetProperty("gameVersion").GetString(), scenes);
        }

        /// <summary>
        /// A field as its name, its Unity type name and its value, its fields or its items. The value type follows from
        /// the type name as AssetsTools derives it, so <see cref="ReadField"/> builds the same field.
        /// </summary>
        internal static void WriteField(Utf8JsonWriter writer, AssetTypeValueField field)
        {
            var template = field.TemplateField;
            writer.WriteStartObject();
            writer.WriteString("name", field.FieldName);
            writer.WriteString("type", field.TypeName);
            if (template.IsArray && template.ValueType == AssetValueType.ByteArray)
            {
                writer.WriteString("bytes", Convert.ToBase64String(field.AsByteArray));
            }
            else if (template.IsArray)
            {
                writer.WriteStartArray("items");
                foreach (var item in field.Children) WriteField(writer, item);
                writer.WriteEndArray();
            }
            else if (template.ValueType == AssetValueType.None)
            {
                writer.WriteStartArray("fields");
                foreach (var child in field.Children) WriteField(writer, child);
                writer.WriteEndArray();
            }
            else
            {
                if (AssetTypeValueField.GetValueTypeByTypeName(field.TypeName) != template.ValueType)
                    throw new NotSupportedException($"{field.FieldName}: a {field.TypeName} read as {template.ValueType} cannot be written.");
                switch (template.ValueType)
                {
                    case AssetValueType.Bool: writer.WriteBoolean("value", field.AsBool); break;
                    case AssetValueType.String: writer.WriteString("value", field.AsString); break;
                    case AssetValueType.Float: writer.WriteNumber("value", field.AsFloat); break;
                    case AssetValueType.Double: writer.WriteNumber("value", field.AsDouble); break;
                    case AssetValueType.Int8:
                    case AssetValueType.Int16:
                    case AssetValueType.Int32:
                    case AssetValueType.Int64: writer.WriteNumber("value", field.AsLong); break;
                    case AssetValueType.UInt8:
                    case AssetValueType.UInt16:
                    case AssetValueType.UInt32:
                    case AssetValueType.UInt64: writer.WriteNumber("value", field.AsULong); break;
                    default: throw new NotSupportedException($"{field.FieldName}: {template.ValueType} values cannot be written.");
                }
            }
            writer.WriteEndObject();
        }

        /// <summary>
        /// The first difference between two fields in names, types, values and the fields and items they hold, or null.
        /// Floating-point values compare exactly.
        /// </summary>
        internal static string Difference(AssetTypeValueField expected, AssetTypeValueField actual, string path = null)
        {
            path = path == null ? expected.FieldName : path + "." + expected.FieldName;
            var template = expected.TemplateField;
            if (actual.FieldName != expected.FieldName || actual.TypeName != expected.TypeName
                || actual.TemplateField.IsArray != template.IsArray || actual.TemplateField.ValueType != template.ValueType)
                return $"{path}: {actual.FieldName} {actual.TypeName} ({actual.TemplateField.ValueType}) instead of {expected.TypeName} ({template.ValueType})";
            switch (template.ValueType)
            {
                case AssetValueType.ByteArray:
                    return expected.AsByteArray.SequenceEqual(actual.AsByteArray) ? null : $"{path}: different bytes";
                case AssetValueType.String:
                    return actual.AsString == expected.AsString ? null : $"{path}: '{actual.AsString}' instead of '{expected.AsString}'";
                case AssetValueType.None:
                case AssetValueType.Array:
                    if (actual.Children.Count != expected.Children.Count)
                        return $"{path}: {actual.Children.Count} fields or items instead of {expected.Children.Count}";
                    for (var i = 0; i < expected.Children.Count; i++)
                    {
                        var difference = Difference(expected.Children[i], actual.Children[i], template.IsArray ? $"{path}[{i}]" : path);
                        if (difference != null) return difference;
                    }
                    return null;
                default:
                    return Equals(actual.Value.AsObject, expected.Value.AsObject) ? null : $"{path}: {actual.Value.AsObject} instead of {expected.Value.AsObject}";
            }
        }

        /// <summary>
        /// The field <see cref="WriteField"/> wrote, with a template of the fields it holds. An array's template does not
        /// describe its items; the items carry their own.
        /// </summary>
        internal static AssetTypeValueField ReadField(JsonElement node)
        {
            var type = node.GetProperty("type").GetString();
            var template = new AssetTypeTemplateField
            {
                Name = node.GetProperty("name").GetString(),
                Type = type,
                Children = new List<AssetTypeTemplateField>(),
            };
            var children = new List<AssetTypeValueField>();
            AssetTypeValue value;
            if (node.TryGetProperty("bytes", out var bytes))
            {
                template.IsArray = true;
                template.ValueType = AssetValueType.ByteArray;
                value = new AssetTypeValue(AssetValueType.ByteArray, Convert.FromBase64String(bytes.GetString()));
            }
            else if (node.TryGetProperty("items", out var items))
            {
                template.IsArray = true;
                template.ValueType = AssetValueType.Array;
                children.AddRange(items.EnumerateArray().Select(ReadField));
                value = new AssetTypeValue(AssetValueType.Array, new AssetTypeArrayInfo { size = children.Count });
            }
            else if (node.TryGetProperty("fields", out var fields))
            {
                template.ValueType = AssetValueType.None;
                children.AddRange(fields.EnumerateArray().Select(ReadField));
                template.Children.AddRange(children.Select(child => child.TemplateField));
                value = null;
            }
            else
            {
                var raw = node.GetProperty("value");
                template.ValueType = AssetTypeValueField.GetValueTypeByTypeName(type);
                template.HasValue = true;
                value = template.ValueType switch
                {
                    AssetValueType.Bool => new AssetTypeValue(raw.GetBoolean()),
                    AssetValueType.String => new AssetTypeValue(raw.GetString()),
                    AssetValueType.Float => new AssetTypeValue(raw.GetSingle()),
                    AssetValueType.Double => new AssetTypeValue(raw.GetDouble()),
                    AssetValueType.Int8 => new AssetTypeValue(raw.GetSByte()),
                    AssetValueType.Int16 => new AssetTypeValue(raw.GetInt16()),
                    AssetValueType.Int32 => new AssetTypeValue(raw.GetInt32()),
                    AssetValueType.Int64 => new AssetTypeValue(raw.GetInt64()),
                    AssetValueType.UInt8 => new AssetTypeValue(raw.GetByte()),
                    AssetValueType.UInt16 => new AssetTypeValue(raw.GetUInt16()),
                    AssetValueType.UInt32 => new AssetTypeValue(raw.GetUInt32()),
                    AssetValueType.UInt64 => new AssetTypeValue(raw.GetUInt64()),
                    _ => throw new InvalidDataException($"{template.Name}: no value of type {type} is read."),
                };
            }
            var field = new AssetTypeValueField();
            field.Read(value, template, children);
            return field;
        }
    }
}
