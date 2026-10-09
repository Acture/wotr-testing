using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WotR.Testing.Offline
{
    /// <summary>Evidence that a rewritten runtime copy keeps the game's method bodies.</summary>
    public static class IlComparison
    {
        /// <summary>
        /// Compares every method body by opcode and resolved operand. Metadata tokens are renumbered when an assembly is
        /// written, so raw IL bytes are not comparable; names and values are. Returns the number of bodies compared and
        /// throws <see cref="InvalidOperationException"/> at the first difference.
        /// </summary>
        public static int CompareMethodBodies(string originalPath, string copyPath)
        {
            using var original = ModuleDefinition.ReadModule(originalPath);
            using var copy = ModuleDefinition.ReadModule(copyPath);
            var copies = copy.GetTypes().ToDictionary(t => t.FullName);
            var compared = 0;
            foreach (var type in original.GetTypes())
            {
                if (!copies.TryGetValue(type.FullName, out var otherType)) Fail($"Missing type {type.FullName}");
                if (type.Methods.Count != otherType.Methods.Count) Fail($"Method count changed: {type.FullName}");
                for (var m = 0; m < type.Methods.Count; m++)
                {
                    var method = type.Methods[m];
                    var other = otherType.Methods[m];
                    if (method.FullName != other.FullName) Fail($"Method order changed: {method.FullName}");
                    if (method.HasBody != other.HasBody) Fail($"Body presence changed: {method.FullName}");
                    if (!method.HasBody) continue;
                    var before = method.Body.Instructions;
                    var after = other.Body.Instructions;
                    if (before.Count != after.Count) Fail($"Instruction count changed: {method.FullName}");
                    for (var i = 0; i < before.Count; i++)
                    {
                        if (before[i].OpCode != after[i].OpCode || Operand(before[i].Operand) != Operand(after[i].Operand))
                            Fail($"IL changed in {method.FullName} at {before[i].Offset}: {before[i]} -> {after[i]}");
                    }
                    compared++;
                }
            }
            return compared;
        }

        private static void Fail(string message) => throw new InvalidOperationException(message);

        private static string Operand(object operand) => operand switch
        {
            null => "",
            Instruction target => "IL_" + target.Offset,
            Instruction[] targets => string.Join(",", targets.Select(t => "IL_" + t.Offset)),
            MemberReference member => member.FullName,
            VariableDefinition variable => "V_" + variable.Index,
            ParameterDefinition parameter => "A_" + parameter.Index,
            _ => Convert.ToString(operand, System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
