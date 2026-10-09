using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WotR.OfflineTesting
{
    /// <summary>
    /// Writes copies of the WotR managed assemblies that can run on .NET Framework without the Unity player.
    /// The originals are never modified. Only two kinds of change are made:
    /// Unity internal calls (engine C++ code) get managed bodies, and game members the mod side references become public.
    /// Game method bodies (Assembly-CSharp, Owlcat) are not changed.
    /// </summary>
    public static class RuntimeAssemblyRewriter
    {
        public const string NativeMarker = "UNITY_NATIVE_UNAVAILABLE";

        private static bool IsFramework(string name)
            => name is "mscorlib.dll" or "netstandard.dll" || name.StartsWith("System") || name.StartsWith("Mono.") || name.StartsWith("I18N") || name.StartsWith("Microsoft.");

        /// <summary>True when the assembly references Assembly-CSharp, so it may rely on publicized game members.</summary>
        public static bool ReferencesGame(string path)
        {
            try
            {
                using var module = ModuleDefinition.ReadModule(path);
                return module.AssemblyReferences.Any(r => r.Name == "Assembly-CSharp");
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public static RewriteSummary Generate(string managed, string outputDirectory, IReadOnlyCollection<string> consumers)
        {
            Directory.CreateDirectory(outputDirectory);
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managed);
            var widen = new HashSet<string>();
            foreach (var consumer in consumers) widen.UnionWith(CollectInaccessible(resolver, consumer));
            var summary = new RewriteSummary { WidenedMemberCandidates = widen.Count, WideningConsumers = consumers.Select(Path.GetFileName).ToList() };

            foreach (var path in Directory.GetFiles(managed, "*.dll"))
            {
                var name = Path.GetFileName(path);
                if (IsFramework(name)) continue;
                ModuleDefinition module;
                try
                {
                    module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
                }
                catch (BadImageFormatException)
                {
                    // Native libraries in Managed are copied as-is.
                    File.Copy(path, Path.Combine(outputDirectory, name), true);
                    continue;
                }

                var changed = 0;
                var notSupported = module.ImportReference(typeof(NotSupportedException).GetConstructor(new[] { typeof(string) }));
                var finalizerCalls = new HashSet<MethodDefinition>(module.GetTypes()
                    .SelectMany(t => t.Methods.Where(m => m.Name == "Finalize" && m.HasBody))
                    .SelectMany(m => m.Body.Instructions)
                    .Select(i => (i.Operand as MethodReference)?.Resolve())
                    .Where(m => m != null && m.IsInternalCall && m.Module == module));

                foreach (var type in module.GetTypes())
                {
                    if (widen.Contains(Key(type)) && !(type.IsPublic || type.IsNestedPublic))
                    {
                        if (type.IsNested) type.IsNestedPublic = true; else type.IsPublic = true;
                        summary.WidenedTypes++;
                        changed++;
                    }
                    foreach (var field in type.Fields)
                    {
                        if (field.IsPublic || !widen.Contains(Key(field))) continue;
                        // Owlcat's FieldsContractResolver serializes public fields and [SerializeField]/[SerializeReference] fields.
                        // A field that was not serialized while private is marked NotSerialized so the blueprint field set stays the same.
                        if (!field.CustomAttributes.Any(a => a.AttributeType.Name is "SerializeField" or "SerializeReference"))
                        {
                            field.IsNotSerialized = true;
                            summary.FieldsKeptOutOfSerialization++;
                        }
                        field.IsPublic = true;
                        summary.WidenedFields++;
                        changed++;
                    }
                    foreach (var method in type.Methods)
                    {
                        // Virtual methods keep their access so overrides in the mod do not become narrower than the base.
                        if (!method.IsPublic && !method.IsVirtual && widen.Contains(Key(method)))
                        {
                            method.IsPublic = true;
                            summary.WidenedMethods++;
                            changed++;
                        }
                        if (!method.IsInternalCall) continue;
                        ReplaceInternalCall(module, type, method, finalizerCalls, notSupported, summary);
                        changed++;
                    }
                }

                if (changed == 0)
                {
                    module.Dispose();
                    File.Copy(path, Path.Combine(outputDirectory, name), true);
                    continue;
                }
                module.Write(Path.Combine(outputDirectory, name));
                module.Dispose();
                summary.RewrittenAssemblies.Add(name);
            }
            return summary;
        }

        private static void ReplaceInternalCall(ModuleDefinition module, TypeDefinition type, MethodDefinition method,
            HashSet<MethodDefinition> finalizerCalls, MethodReference notSupported, RewriteSummary summary)
        {
            method.IsInternalCall = false;
            method.ImplAttributes &= ~MethodImplAttributes.InternalCall;
            method.Body = new MethodBody(method);
            var il = method.Body.GetILProcessor();
            var returnsVoid = method.ReturnType.MetadataType == MetadataType.Void;

            if (type.Namespace.StartsWith("UnityEngine.Analytics") || type.Namespace.StartsWith("UnityEngine.CrashReportHandler") || type.FullName == "UnityEngine.CrashReport")
            {
                // Rule: telemetry. No mechanical effect; answer with defaults (empty arrays) instead of failing.
                if (method.ReturnType.IsArray)
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Newarr, ((ArrayType)method.ReturnType).ElementType);
                }
                else if (!returnsVoid)
                {
                    var local = new VariableDefinition(method.ReturnType);
                    method.Body.Variables.Add(local);
                    method.Body.InitLocals = true;
                    il.Emit(OpCodes.Ldloc, local);
                }
                il.Emit(OpCodes.Ret);
                summary.TelemetryDefaults++;
                return;
            }
            if (returnsVoid && finalizerCalls.Contains(method))
            {
                // Rule: native release called from a finalizer. Must not throw on the finalizer thread.
                il.Emit(OpCodes.Ret);
                summary.FinalizerReleaseNoOps++;
                return;
            }
            if (returnsVoid && !method.IsStatic && (method.Name.StartsWith("set_") || method.Name.StartsWith("Set")))
            {
                // Rule: writes into an instance without native backing (no native object can exist offline) are dropped.
                // Reads still throw, so a mechanic that depends on the value fails visibly.
                il.Emit(OpCodes.Ret);
                summary.DetachedInstanceWriteNoOps++;
                return;
            }
            if (method.IsStatic && (method.Name.StartsWith("Internal_Create") || method.Name == "Init") && method.ReturnType.FullName == "System.IntPtr")
            {
                // Rule: native allocation returns a null handle, so value holders such as AnimationCurve can be constructed.
                il.Emit(OpCodes.Ldsfld, module.ImportReference(typeof(IntPtr).GetField(nameof(IntPtr.Zero))));
                il.Emit(OpCodes.Ret);
                summary.NullHandleAllocations++;
                return;
            }
            // Default rule: any other engine call fails with a recognizable marker.
            il.Emit(OpCodes.Ldstr, $"{NativeMarker}: {type.FullName}::{method.Name}");
            il.Emit(OpCodes.Newobj, notSupported);
            il.Emit(OpCodes.Throw);
            summary.ThrowingInternalCalls++;
        }

        private static string Key(TypeDefinition t) => "T:" + t.FullName;
        private static string Key(FieldDefinition f) => "F:" + f.DeclaringType.FullName + "::" + f.Name;
        private static string Key(MethodDefinition m) => "M:" + m.FullName;

        /// <summary>
        /// Mods and libraries such as BlueprintCore are often compiled against a publicized Assembly-CSharp. Mono does not
        /// check member access, .NET Framework does, so collect exactly the non-public game members they reference.
        /// </summary>
        private static HashSet<string> CollectInaccessible(IAssemblyResolver resolver, string consumer)
        {
            var result = new HashSet<string>();
            using var module = ModuleDefinition.ReadModule(consumer, new ReaderParameters { AssemblyResolver = resolver });
            void AddDeclaringTypes(TypeDefinition type)
            {
                for (var t = type; t != null; t = t.DeclaringType)
                    if (!(t.IsPublic || t.IsNestedPublic)) result.Add(Key(t));
            }
            foreach (var reference in module.GetTypeReferences())
            {
                TypeDefinition definition;
                try { definition = reference.Resolve(); } catch (AssemblyResolutionException) { continue; }
                AddDeclaringTypes(definition);
            }
            foreach (var reference in module.GetMemberReferences())
            {
                IMemberDefinition definition;
                try { definition = reference.Resolve(); } catch (AssemblyResolutionException) { continue; }
                switch (definition)
                {
                    case FieldDefinition field when !field.IsPublic: result.Add(Key(field)); break;
                    case MethodDefinition method when !method.IsPublic && !method.IsVirtual: result.Add(Key(method)); break;
                }
                AddDeclaringTypes(definition?.DeclaringType);
            }
            return result;
        }
    }

    public sealed class RewriteSummary
    {
        public List<string> WideningConsumers { get; set; } = new();
        public List<string> RewrittenAssemblies { get; set; } = new();
        public int ThrowingInternalCalls { get; set; }
        public int TelemetryDefaults { get; set; }
        public int FinalizerReleaseNoOps { get; set; }
        public int DetachedInstanceWriteNoOps { get; set; }
        public int NullHandleAllocations { get; set; }
        public int WidenedMemberCandidates { get; set; }
        public int WidenedTypes { get; set; }
        public int WidenedFields { get; set; }
        public int FieldsKeptOutOfSerialization { get; set; }
        public int WidenedMethods { get; set; }
    }
}
