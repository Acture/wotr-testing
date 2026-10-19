using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    /// <summary>
    /// Which environments and workloads a test assembly declares, read from metadata only: no test code, fixture,
    /// factory or Theory data runs, and no game type loads. Decides which partitions a run needs before any starts.
    /// </summary>
    public sealed class TestScope
    {
        private static readonly string[] TestAttributes = { "Xunit.FactAttribute", "Xunit.TheoryAttribute" };
        private const string TraitAttribute = "Xunit.TraitAttribute";
        private const string FuzzAttribute = "WotR.Testing.Fuzzing.WotrFuzzAttribute";
        private readonly Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public List<string> Errors { get; } = new List<string>();

        /// <summary>Declared cases or targets for a mode (offgame, ingame) and kind (tests, fuzz).</summary>
        public int Count(string mode, string kind) => counts.TryGetValue(mode + "/" + kind, out var count) ? count : 0;

        private void Add(string mode, string kind) => counts[mode + "/" + kind] = Count(mode, kind) + 1;

        public static TestScope Scan(string assemblyPath)
        {
            var scope = new TestScope();
            using (var types = new TypeResolver(Path.GetDirectoryName(Path.GetFullPath(assemblyPath)))) {
                var module = types.Open(assemblyPath);
                var reader = module.Reader;
                foreach (var typeHandle in reader.TypeDefinitions) {
                    var type = reader.GetTypeDefinition(typeHandle);
                    var typeName = TypeResolver.FullName(reader, typeHandle);
                    var classModes = Modes(reader, type.GetCustomAttributes(), typeName, scope);
                    foreach (var methodHandle in type.GetMethods()) {
                        var method = reader.GetMethodDefinition(methodHandle);
                        var name = typeName + "." + reader.GetString(method.Name);
                        var isTest = false;
                        int? fuzzModes = null;
                        foreach (var attributeHandle in method.GetCustomAttributes()) {
                            var attribute = reader.GetCustomAttribute(attributeHandle);
                            var attributeType = types.AttributeType(module, attribute);
                            if (attributeType == FuzzAttribute) fuzzModes = FuzzModes(reader, attribute, name, scope);
                            else if (types.DerivesFrom(module, attribute, TestAttributes)) isTest = true;
                        }
                        if (isTest) {
                            var modes = classModes.Concat(Modes(reader, method.GetCustomAttributes(), name, scope)).ToList();
                            if (modes.Count == 0) modes.Add(Welcome.OffGame);
                            foreach (var mode in new[] { Welcome.OffGame, Welcome.InGame })
                                if (modes.Contains(mode) || modes.Contains("both")) scope.Add(mode, TestDescriptor.TestKind);
                        }
                        if (fuzzModes is int flags) {
                            if ((flags & 1) != 0) scope.Add(Welcome.OffGame, TestDescriptor.FuzzKind);
                            if ((flags & 2) != 0) scope.Add(Welcome.InGame, TestDescriptor.FuzzKind);
                        }
                    }
                }
            }
            return scope;
        }

        private static List<string> Modes(MetadataReader reader, CustomAttributeHandleCollection attributes, string owner, TestScope scope)
        {
            var modes = new List<string>();
            foreach (var handle in attributes) {
                var attribute = reader.GetCustomAttribute(handle);
                if (TypeResolver.ConstructorType(reader, attribute) != TraitAttribute) continue;
                var value = attribute.DecodeValue(new NameProvider());
                if (value.FixedArguments.Length != 2 || (value.FixedArguments[0].Value as string) != TestDescriptor.ModeTrait) continue;
                var mode = value.FixedArguments[1].Value as string;
                // A misspelled mode would match no partition and the case would silently never run.
                if (mode != Welcome.OffGame && mode != Welcome.InGame && mode != "both")
                    scope.Errors.Add(owner + ": WotrMode must be offgame, ingame or both, not \"" + mode + "\".");
                else modes.Add(mode);
            }
            return modes;
        }

        private static int FuzzModes(MetadataReader reader, CustomAttribute attribute, string owner, TestScope scope)
        {
            var value = attribute.DecodeValue(new NameProvider());
            foreach (var named in value.NamedArguments)
                if (named.Name == "Modes") {
                    var modes = Convert.ToInt32(named.Value);
                    if (modes < 1 || modes > 3) scope.Errors.Add(owner + ": WotrFuzz Modes must be OffGame, InGame or Both.");
                    return modes;
                }
            return 1;
        }

        /// <summary>Type names for attribute decoding; enums are assumed to be int-based, as WotrTestModes is.</summary>
        private sealed class NameProvider : ICustomAttributeTypeProvider<string>
        {
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetSystemType() => "System.Type";
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeResolver.FullName(reader, handle);
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeResolver.FullName(reader, handle);
            public string GetTypeFromSerializedName(string name) => name;
            public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
            public bool IsSystemType(string type) => type == "System.Type";
        }

        /// <summary>Follows attribute base types into referenced assemblies beside the test assembly.</summary>
        private sealed class TypeResolver : IDisposable
        {
            private readonly string directory;
            private readonly Dictionary<string, Module> modules = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);

            internal TypeResolver(string directory) => this.directory = directory;

            internal sealed class Module
            {
                internal PEReader Pe;
                internal MetadataReader Reader;
            }

            internal Module Open(string path)
            {
                path = Path.GetFullPath(path);
                if (modules.TryGetValue(path, out var module)) return module;
                var pe = new PEReader(File.OpenRead(path));
                module = new Module { Pe = pe, Reader = pe.GetMetadataReader() };
                modules[path] = module;
                return module;
            }

            internal string AttributeType(Module module, CustomAttribute attribute) => ConstructorType(module.Reader, attribute);

            internal bool DerivesFrom(Module module, CustomAttribute attribute, string[] bases)
            {
                var reader = module.Reader;
                EntityHandle type = attribute.Constructor.Kind == HandleKind.MethodDefinition
                    ? (EntityHandle)reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()
                    : reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                return DerivesFrom(module, type, bases, 0);
            }

            private bool DerivesFrom(Module module, EntityHandle type, string[] bases, int depth)
            {
                if (depth > 10 || type.IsNil) return false;
                var reader = module.Reader;
                if (type.Kind == HandleKind.TypeDefinition) {
                    var handle = (TypeDefinitionHandle)type;
                    if (bases.Contains(FullName(reader, handle))) return true;
                    return DerivesFrom(module, reader.GetTypeDefinition(handle).BaseType, bases, depth + 1);
                }
                if (type.Kind != HandleKind.TypeReference) return false;
                var reference = reader.GetTypeReference((TypeReferenceHandle)type);
                var name = FullName(reader, (TypeReferenceHandle)type);
                if (bases.Contains(name)) return true;
                if (name.StartsWith("System.", StringComparison.Ordinal) || reference.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
                var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                var path = Path.Combine(directory, assembly + ".dll");
                if (!File.Exists(path)) return false;
                var other = Open(path);
                foreach (var handle in other.Reader.TypeDefinitions)
                    if (FullName(other.Reader, handle) == name) return DerivesFrom(other, other.Reader.GetTypeDefinition(handle).BaseType, bases, depth + 1);
                return false;
            }

            internal static string ConstructorType(MetadataReader reader, CustomAttribute attribute)
            {
                switch (attribute.Constructor.Kind) {
                    case HandleKind.MethodDefinition:
                        return FullName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
                    case HandleKind.MemberReference:
                        var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                        if (parent.Kind == HandleKind.TypeReference) return FullName(reader, (TypeReferenceHandle)parent);
                        if (parent.Kind == HandleKind.TypeDefinition) return FullName(reader, (TypeDefinitionHandle)parent);
                        return null;
                    default: return null;
                }
            }

            internal static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
            {
                var type = reader.GetTypeDefinition(handle);
                var name = reader.GetString(type.Name);
                var declaring = type.GetDeclaringType();
                if (!declaring.IsNil) return FullName(reader, declaring) + "+" + name;
                var ns = reader.GetString(type.Namespace);
                return ns.Length == 0 ? name : ns + "." + name;
            }

            internal static string FullName(MetadataReader reader, TypeReferenceHandle handle)
            {
                var type = reader.GetTypeReference(handle);
                var name = reader.GetString(type.Name);
                if (type.ResolutionScope.Kind == HandleKind.TypeReference) return FullName(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
                var ns = reader.GetString(type.Namespace);
                return ns.Length == 0 ? name : ns + "." + name;
            }

            public void Dispose()
            {
                foreach (var module in modules.Values) module.Pe.Dispose();
            }
        }
    }
}
