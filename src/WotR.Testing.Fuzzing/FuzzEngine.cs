using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace WotR.Testing.Fuzzing
{
    internal sealed class FuzzEngine
    {
        private readonly Type api;
        private readonly FuzzOptions options;
        private readonly MethodSpec[] methods;
        private readonly FuzzReport report;
        private readonly List<string> skipped = new List<string>();

        internal FuzzEngine(Type api, FuzzOptions options)
        {
            this.api = api;
            this.options = (options ?? new FuzzOptions()).Clone();
            if (!api.IsInterface || api.ContainsGenericParameters) throw new ArgumentException("Register a closed interface type.", nameof(api));
            if (this.options.Cases <= 0) throw new ArgumentOutOfRangeException(nameof(FuzzOptions.Cases));
            if (this.options.MaxCalls <= 0) throw new ArgumentOutOfRangeException(nameof(FuzzOptions.MaxCalls));
            if (this.options.MaxShrinks < 0) throw new ArgumentOutOfRangeException(nameof(FuzzOptions.MaxShrinks));
            methods = new[] { api }.Concat(api.GetInterfaces())
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(method => !method.IsSpecialName &&
                    !(typeof(IDisposable).IsAssignableFrom(api) && method.Name == nameof(IDisposable.Dispose) &&
                        method.ReturnType == typeof(void) && method.GetParameters().Length == 0) &&
                    (this.options.IncludeMethod == null || this.options.IncludeMethod(method)))
                .Select(method => new MethodSpec(method, this.options.Generators))
                .OrderBy(method => method.Key, StringComparer.Ordinal).ToArray();
            if (methods.Length == 0) throw new ArgumentException("The registered interface has no selected ordinary public methods.", nameof(api));
            report = new FuzzReport { Seed = this.options.Seed, SelectedMethods = methods.Select(method => method.Key).ToArray(), SkippedReasons = skipped.AsReadOnly() };
        }

        internal FuzzReport Run(Func<object> factory)
        {
            var random = new FuzzRandom(options.Seed);
            for (int iteration = 0; iteration < options.Cases; iteration++)
            {
                List<SavedCall> calls;
                try
                {
                    int count = 1 + random.Next(options.MaxCalls);
                    calls = new List<SavedCall>(count);
                    for (int index = 0; index < count; index++)
                    {
                        var method = methods[random.Next(methods.Length)];
                        calls.Add(new SavedCall { Method = method.Key, Arguments = method.Arguments.Select(generator =>
                            generator.Serialize(generator.Generate(random))).ToArray() });
                    }
                }
                catch (Exception exception)
                {
                    FailInfrastructure("generation", null, exception);
                    return report;
                }
                var result = Execute(factory, calls);
                Count(result);
                if (result.Failure != null)
                {
                    var failing = calls.Take(result.FailingIndex + 1).ToList();
                    if (result.Failure.Kind == "invocation") failing = Minimize(factory, failing, result.Failure);
                    report.ReplayCase = Save(failing);
                    return report;
                }
                if (result.Unavailable != null && report.ReplayCase == null) report.ReplayCase = Save(calls.Take(result.FailingIndex + 1).ToList());
            }
            report.Status = report.CasesPassed == 0 ? FuzzStatus.AllSkipped : FuzzStatus.Passed;
            return report;
        }

        internal FuzzReport Replay(Func<object> factory, string serializedCase)
        {
            if (serializedCase == null) throw new ArgumentNullException(nameof(serializedCase));
            SavedCase saved;
            try
            {
                saved = JsonSerializer.Deserialize<SavedCase>(serializedCase);
                if (saved == null || saved.FormatVersion != 1 || saved.Interface != api.AssemblyQualifiedName ||
                    saved.Calls == null || saved.Calls.Count == 0)
                    throw new ArgumentException("The replay format, interface identity/version, or call list is incompatible.");
                foreach (var call in saved.Calls)
                {
                    var method = Find(call);
                    // Validate and decode everything before constructing a target.
                    method.Decode(call.Arguments);
                }
            }
            catch (Exception exception)
            {
                throw new ArgumentException("Invalid or incompatible replay case. Use the same interface version, method selection and argument codecs.", nameof(serializedCase), exception);
            }
            report.Seed = saved.Seed;
            var result = Execute(factory, saved.Calls);
            Count(result);
            if (result.Unavailable != null) report.Status = FuzzStatus.AllSkipped;
            report.ReplayCase = serializedCase;
            return report;
        }

        private string Save(List<SavedCall> calls) => JsonSerializer.Serialize(new SavedCase {
            FormatVersion = 1, Interface = api.AssemblyQualifiedName, Seed = report.Seed,
            EngineVersion = typeof(Fuzz).Assembly.GetName().Version.ToString(), Calls = calls
        }, JsonFormat.Options);

        private MethodSpec Find(SavedCall call)
        {
            if (call == null) throw new ArgumentException("Replay contains a null call.");
            return methods.FirstOrDefault(method => method.Key == call.Method) ?? throw new ArgumentException("Unknown or excluded replay method: " + call.Method);
        }

        private Execution Execute(Func<object> factory, List<SavedCall> calls)
        {
            var result = new Execution();
            object target;
            try { target = factory() ?? throw new InvalidOperationException("The interface factory returned null."); }
            catch (Exception exception) { result.Failure = Failure("factory", null, exception); return result; }
            bool cleanupSupported = true;
            try
            {
                try
                {
                    if (target is IDisposable)
                    {
                        cleanupSupported = false;
                        var cleanup = target.GetType().GetInterfaceMap(typeof(IDisposable));
                        if (cleanup.TargetMethods.Any(method => method.IsDefined(typeof(AsyncStateMachineAttribute), false)))
                            throw new NotSupportedException("async void IDisposable.Dispose cannot be awaited; provide synchronous cleanup instead.");
                        cleanupSupported = true;
                    }
                    foreach (var method in methods.Where(item => item.Method.ReturnType == typeof(void)))
                    {
                        var mapping = target.GetType().GetInterfaceMap(method.Method.DeclaringType);
                        int slot = Array.IndexOf(mapping.InterfaceMethods, method.Method);
                        if (slot >= 0 && mapping.TargetMethods[slot].IsDefined(typeof(AsyncStateMachineAttribute), false))
                            throw new NotSupportedException("async void cannot be awaited; expose Task instead: " + method.Key);
                    }
                }
                catch (Exception exception) { result.Failure = Failure("signature", null, exception); return result; }
                for (int index = 0; index < calls.Count; index++)
                {
                    result.FailingIndex = index;
                    var method = Find(calls[index]);
                    object[] arguments;
                    try { arguments = method.Decode(calls[index].Arguments); }
                    catch (Exception exception) { result.Failure = Failure("serialization", method.Key, exception); break; }
                    result.CallsAttempted++;
                    try
                    {
                        var returned = Invoke(method.Method, target, arguments);
                        if (returned is Task task) task.GetAwaiter().GetResult();
                        else if (typeof(Task).IsAssignableFrom(method.Method.ReturnType)) throw new InvalidOperationException("A Task-returning method returned null.");
                        result.CallsCompleted++;
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            if (options.IsUnavailable != null && ExceptionTree(exception).Any(options.IsUnavailable))
                            {
                                result.Unavailable = method.Key + ": " + exception;
                                break;
                            }
                            if (options.IsExpectedException != null && options.IsExpectedException(exception))
                            { result.ExpectedExceptions++; continue; }
                            result.Failure = Failure("invocation", method.Key, exception);
                        }
                        catch (Exception classifierFailure) { result.Failure = Failure("classifier", method.Key, classifierFailure); }
                        break;
                    }
                }
            }
            finally
            {
                if (cleanupSupported && target is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception exception)
                    {
                        var prior = result.Failure;
                        result.Failure = Failure("dispose", null, exception);
                        if (prior != null) result.Failure.ExceptionDetails += "\nEarlier failure: " + prior.ExceptionDetails;
                        result.Unavailable = null;
                    }
                }
            }
            return result;
        }

        private static object Invoke(MethodInfo method, object target, object[] arguments)
        {
            try { return method.Invoke(target, arguments); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        }

        private static IEnumerable<Exception> ExceptionTree(Exception exception)
        {
            var pending = new Stack<Exception>();
            var seen = new HashSet<Exception>();
            pending.Push(exception);
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                if (!seen.Add(item)) continue;
                yield return item;
                if (item is AggregateException aggregate)
                    foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
                else if (item.InnerException != null) pending.Push(item.InnerException);
            }
        }

        private void Count(Execution result)
        {
            report.CasesAttempted++;
            report.CallsAttempted += result.CallsAttempted;
            report.CallsCompleted += result.CallsCompleted;
            report.ExpectedExceptions += result.ExpectedExceptions;
            if (result.Failure != null)
            {
                report.Failure = result.Failure;
                report.Status = result.Failure.Kind == "invocation" ? FuzzStatus.Finding : FuzzStatus.InfrastructureFailure;
            }
            else if (result.Unavailable != null) { report.CasesSkipped++; skipped.Add(result.Unavailable); }
            else { report.CasesPassed++; report.Status = FuzzStatus.Passed; }
        }

        private List<SavedCall> Minimize(Func<object> factory, List<SavedCall> current, FuzzFinding finding)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { Save(current) };
            // Remove contiguous chunks, then individual calls. Every attempt uses a new target.
            for (int chunk = Math.Max(1, current.Count / 2); chunk > 0 && report.ShrinkAttempts < options.MaxShrinks; chunk /= 2)
            {
                bool progress;
                do
                {
                    progress = false;
                    for (int start = 0; start + chunk <= current.Count && report.ShrinkAttempts < options.MaxShrinks; start++)
                    {
                        if (current.Count <= chunk) break;
                        var candidate = current.Take(start).Concat(current.Skip(start + chunk)).ToList();
                        int accepted = TryCandidate(factory, candidate, finding, seen, out var trimmed);
                        if (accepted < 0) return current;
                        if (accepted > 0) { current = trimmed; progress = true; break; }
                    }
                } while (progress && report.ShrinkAttempts < options.MaxShrinks);
            }
            try
            {
                bool progress;
                do
                {
                    progress = false;
                    for (int index = 0; index < current.Count && !progress && report.ShrinkAttempts < options.MaxShrinks; index++)
                    {
                        var method = Find(current[index]);
                        for (int argument = 0; argument < method.Arguments.Length && !progress && report.ShrinkAttempts < options.MaxShrinks; argument++)
                        {
                            var generator = method.Arguments[argument];
                            var value = generator.Deserialize(current[index].Arguments[argument]);
                            foreach (var smaller in generator.Shrink(value))
                            {
                                if (report.ShrinkAttempts >= options.MaxShrinks) break;
                                var candidate = current.Select(call => new SavedCall { Method = call.Method, Arguments = (string[])call.Arguments.Clone() }).ToList();
                                candidate[index].Arguments[argument] = generator.Serialize(smaller);
                                int accepted = TryCandidate(factory, candidate, finding, seen, out var trimmed);
                                if (accepted < 0) return current;
                                if (accepted > 0) { current = trimmed; progress = true; break; }
                            }
                        }
                    }
                } while (progress && report.ShrinkAttempts < options.MaxShrinks);
            }
            catch (Exception exception) { FailInfrastructure("generation", finding.Method, exception); }
            return current;
        }

        private int TryCandidate(Func<object> factory, List<SavedCall> candidate, FuzzFinding original,
            HashSet<string> seen, out List<SavedCall> trimmed)
        {
            trimmed = candidate;
            report.ShrinkAttempts++;
            if (!seen.Add(Save(candidate))) return 0;
            var result = Execute(factory, candidate);
            if (result.Failure != null && result.Failure.Kind != "invocation")
            {
                report.Failure = result.Failure;
                report.Status = FuzzStatus.InfrastructureFailure;
                return -1;
            }
            if (result.Failure == null || result.Failure.Method != original.Method || result.Failure.ExceptionType != original.ExceptionType) return 0;
            report.Failure = result.Failure;
            trimmed = candidate.Take(result.FailingIndex + 1).ToList();
            return 1;
        }

        private void FailInfrastructure(string kind, string method, Exception exception)
        { report.Status = FuzzStatus.InfrastructureFailure; report.Failure = Failure(kind, method, exception); }

        private static FuzzFinding Failure(string kind, string method, Exception exception) => new FuzzFinding {
            Kind = kind, Method = method, ExceptionType = exception.GetType().FullName,
            Message = exception.Message, ExceptionDetails = exception.ToString()
        };

        private sealed class Execution
        {
            internal FuzzFinding Failure;
            internal string Unavailable;
            internal int FailingIndex;
            internal int CallsAttempted;
            internal int CallsCompleted;
            internal int ExpectedExceptions;
        }

        private sealed class MethodSpec
        {
            internal readonly MethodInfo Method;
            internal readonly string Key;
            internal readonly IValueGenerator[] Arguments;
            internal MethodSpec(MethodInfo method, FuzzGenerators generators)
            {
                if (method.IsStatic || method.ContainsGenericParameters || method.ReturnType.IsByRef || IsByRefLike(method.ReturnType) ||
                    method.ReturnType.IsPointer || method.ReturnType.FullName?.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal) == true ||
                    method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef || IsByRefLike(parameter.ParameterType) || parameter.ParameterType.IsPointer || parameter.IsOut))
                    throw new NotSupportedException("Unsupported fuzz method (static, generic, ref/out/pointer, byref-like, or ValueTask): " + method);
                Method = method;
                Key = method.DeclaringType.FullName + "." + method.Name + "(" +
                    string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.AssemblyQualifiedName)) + "):" + method.ReturnType.AssemblyQualifiedName;
                Arguments = method.GetParameters().Select(parameter => generators.Resolve(parameter.ParameterType)).ToArray();
            }

            // Type.IsByRefLike is unavailable on .NET Standard 2.0 and the game's Mono.
            private static bool IsByRefLike(Type type) => type.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");

            internal object[] Decode(string[] arguments)
            {
                if (arguments == null || arguments.Length != Arguments.Length) throw new ArgumentException("Replay argument count does not match " + Key);
                return Arguments.Select((generator, index) => generator.Deserialize(arguments[index])).ToArray();
            }
        }
    }

    internal sealed class SavedCase
    {
        public int FormatVersion { get; set; }
        public string Interface { get; set; }
        public string EngineVersion { get; set; }
        public int Seed { get; set; }
        public List<SavedCall> Calls { get; set; }
    }

    internal sealed class SavedCall
    {
        public string Method { get; set; }
        public string[] Arguments { get; set; }
    }
}
