using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Reads ScriptableObject data from the game's own <c>Bundles/blueprint.assets</c> through its Unity type tree
    /// and copies it field by field onto the game's managed types. Native Unity assets (sprites, prefabs, textures)
    /// cannot exist without the engine and stay null; every such gap is recorded.
    /// </summary>
    public sealed class UnityAssetMapper
    {
        private readonly object gate = new();
        private readonly AssetsManager manager;
        private readonly AssetsFileInstance file;
        private readonly Dictionary<long, object> byPathId = new();
        private List<AssetTypeValueField> referencedEntries;

        public SortedDictionary<string, int> Gaps { get; } = new(StringComparer.Ordinal);
        public int MappedObjects => byPathId.Count;

        private UnityAssetMapper(AssetsManager manager, AssetsFileInstance file)
        {
            this.manager = manager;
            this.file = file;
        }

        public static UnityAssetMapper Open(string bundlePath)
        {
            var manager = new AssetsManager();
            var bundle = manager.LoadBundleFile(bundlePath, true);
            return new UnityAssetMapper(manager, manager.LoadAssetsFileFromBundle(bundle, 0, false));
        }

        /// <summary>Maps the single MonoBehaviour whose script class is <paramref name="className"/>.</summary>
        public object MapByScriptClass(string className, Type type)
        {
            lock (gate)
            {
                var (info, field) = FindByScriptClass(className);
                return MapObject(field, type, info.PathId);
            }
        }

        /// <summary>Resolves entry <paramref name="index"/> of the game's BlueprintReferencedAssets list as <paramref name="expected"/>.</summary>
        public UnityEngine.Object MapReferencedAsset(int index, Type expected)
        {
            lock (gate)
            {
                referencedEntries ??= FindByScriptClass("BlueprintReferencedAssets").field["m_Entries"]["Array"].Children;
                if (index < 0 || index >= referencedEntries.Count) return null;
                var asset = referencedEntries[index]["Asset"];
                if (expected == null || !typeof(UnityEngine.ScriptableObject).IsAssignableFrom(expected))
                {
                    Gap($"referenced {expected?.Name ?? "unknown"}: native Unity asset left null");
                    return null;
                }
                var external = manager.GetExtAsset(file, asset);
                if (external.baseField == null || external.info.TypeId != (int)AssetClassID.MonoBehaviour)
                {
                    Gap($"referenced {expected.Name}: not a ScriptableObject in blueprint.assets, left null");
                    return null;
                }
                var concrete = ConcreteType(external, expected);
                return concrete == null ? null : (UnityEngine.Object)MapObject(external.baseField, concrete, asset["m_PathID"].AsLong);
            }
        }

        /// <summary>
        /// The declared field type can be abstract. Use the MonoScript class when this bundle contains it; otherwise the
        /// single concrete subclass whose fields cover the type tree. Ambiguity leaves the reference null and is recorded.
        /// </summary>
        private Type ConcreteType(AssetExternal external, Type expected)
        {
            var className = manager.GetExtAsset(file, external.baseField["m_Script"]).baseField?["m_ClassName"].AsString;
            var candidates = Subclasses(expected);
            if (!string.IsNullOrEmpty(className))
            {
                var named = candidates.FirstOrDefault(t => t.Name == className);
                if (named != null) return named;
            }
            if (!expected.IsAbstract) return expected;
            var fields = external.baseField.Children.Select(c => c.FieldName)
                .Where(n => n is not ("m_GameObject" or "m_Enabled" or "m_Script" or "m_Name" or "m_EditorHideFlags" or "m_EditorClassIdentifier"))
                .ToArray();
            var matches = candidates.Where(t => fields.All(f => FindField(t, f) != null)).ToArray();
            var exact = matches.Where(t => SerializedFieldCount(t) == fields.Length).ToArray();
            var chosen = exact.Length == 1 ? exact[0] : matches.Length == 1 ? matches[0] : null;
            Gap(chosen == null
                ? $"referenced {expected.Name}: script outside this bundle and {matches.Length} candidate types, left null"
                : $"referenced {expected.Name}: script outside this bundle, matched {chosen.Name} by fields");
            return chosen;
        }

        private static readonly Dictionary<Type, Type[]> SubclassCache = new();

        private static Type[] Subclasses(Type baseType)
        {
            lock (SubclassCache)
            {
                if (SubclassCache.TryGetValue(baseType, out var cached)) return cached;
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); } })
                    .Where(t => !t.IsAbstract && baseType.IsAssignableFrom(t))
                    .ToArray();
                return SubclassCache[baseType] = types;
            }
        }

        private static int SerializedFieldCount(Type type)
        {
            var count = 0;
            for (var t = type; t != null && t != typeof(UnityEngine.ScriptableObject) && t != typeof(UnityEngine.Object); t = t.BaseType)
            {
                count += t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Count(f => !f.IsNotSerialized && (f.IsPublic || f.IsDefined(typeof(UnityEngine.SerializeField), false)));
            }
            return count;
        }

        private (AssetFileInfo info, AssetTypeValueField field) FindByScriptClass(string className)
        {
            foreach (var info in file.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var field = manager.GetBaseField(file, info);
                if (manager.GetExtAsset(file, field["m_Script"]).baseField?["m_ClassName"].AsString == className) return (info, field);
            }
            throw new OfflineInitializationException("asset-mapping", $"{className} was not found in blueprint.assets.");
        }

        private void Gap(string description) => Gaps[description] = Gaps.TryGetValue(description, out var count) ? count + 1 : 1;

        private object MapObject(AssetTypeValueField field, Type type, long pathId)
        {
            if (pathId != 0 && byPathId.TryGetValue(pathId, out var cached)) return cached;
            var instance = Create(type);
            if (pathId != 0) byPathId[pathId] = instance;
            foreach (var child in field.Children)
            {
                if (child.FieldName is "m_GameObject" or "m_Enabled" or "m_Script" or "m_Name" or "m_EditorHideFlags" or "m_EditorClassIdentifier") continue;
                var target = FindField(type, child.FieldName);
                if (target == null)
                {
                    Gap($"{type.Name}.{child.FieldName}: no managed field");
                    continue;
                }
                try
                {
                    target.SetValue(instance, MapValue(child, target.FieldType, $"{type.Name}.{target.Name}"));
                }
                catch (Exception error) when (error is InvalidCastException or FormatException or ArgumentException or OverflowException)
                {
                    Gap($"{type.Name}.{target.Name}: {error.GetType().Name}");
                }
            }
            return instance;
        }

        private object MapValue(AssetTypeValueField field, Type type, string path)
        {
            if (type.IsEnum) return Enum.ToObject(type, Convert.ToInt64(field.Value.AsObject));
            if (type == typeof(string)) return field.AsString;
            if (type == typeof(bool)) return field.AsBool;
            if (type.IsPrimitive) return Convert.ChangeType(field.Value.AsObject, type);
            if (field.TypeName.StartsWith("PPtr<"))
            {
                if (field["m_PathID"].AsLong == 0) return null;
                var external = manager.GetExtAsset(file, field);
                if (external.baseField == null || external.info.TypeId != (int)AssetClassID.MonoBehaviour || !typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type))
                {
                    Gap($"{path}: {field.TypeName} native Unity asset left null");
                    return null;
                }
                var concrete = ConcreteType(external, type);
                return concrete == null ? null : MapObject(external.baseField, concrete, field["m_PathID"].AsLong);
            }
            var array = field.Children.FirstOrDefault(c => c.FieldName == "Array");
            if (array != null && (type.IsArray || typeof(IList).IsAssignableFrom(type)))
            {
                var elementType = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
                var items = array.Children.Select((c, i) => MapValue(c, elementType, $"{path}[]")).ToList();
                if (type.IsArray)
                {
                    var result = Array.CreateInstance(elementType, items.Count);
                    for (var i = 0; i < items.Count; i++) result.SetValue(items[i], i);
                    return result;
                }
                var list = (IList)Activator.CreateInstance(type);
                foreach (var item in items) list.Add(item);
                return list;
            }
            if (type == typeof(UnityEngine.Color32) && !field["rgba"].IsDummy)
            {
                // Unity stores Color32 packed as one 32-bit value, little-endian r, g, b, a.
                var rgba = field["rgba"].AsUInt;
                return new UnityEngine.Color32((byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16), (byte)(rgba >> 24));
            }
            if (type.IsAbstract)
            {
                Gap($"{path}: abstract {type.Name} without a type reference, left null");
                return null;
            }
            if (type == typeof(UnityEngine.AnimationCurve) || type == typeof(UnityEngine.Gradient))
            {
                Gap($"{path}: {type.Name} is native-backed, contents dropped");
                return Activator.CreateInstance(type);
            }
            return MapObject(field, type, 0);
        }

        private static object Create(Type type)
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)
                || type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null)
            {
                return FormatterServices.GetUninitializedObject(type);
            }
            return Activator.CreateInstance(type, nonPublic: true);
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }
    }
}
