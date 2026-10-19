using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace WotR.Testing.Fuzzing
{
    public static class FuzzTargets
    {
        /// <summary>
        /// Finds methods marked with <see cref="WotrFuzzAttribute"/> by reflection only; no factory or options provider
        /// is called. Invalid registrations are returned with <see cref="FuzzTarget.Error"/> set. Ordered by Id (ordinal).
        /// </summary>
        public static IReadOnlyList<FuzzTarget> Discover(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception)
            {
                // Skipping unloadable types could silently drop registrations.
                throw new InvalidOperationException("Cannot discover fuzz targets: types in " + assembly.GetName().Name + " failed to load: " +
                    string.Join("; ", exception.LoaderExceptions.Where(error => error != null).Select(error => error.Message).Distinct()), exception);
            }
            return types
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Select(method => new { method, registration = method.GetCustomAttribute<WotrFuzzAttribute>(false) })
                .Where(item => item.registration != null)
                .Select(item => new FuzzTarget(item.method, item.registration))
                .OrderBy(target => target.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>A factory registered with <see cref="WotrFuzzAttribute"/>.</summary>
    public sealed class FuzzTarget
    {
        private readonly MethodInfo untyped;

        internal FuzzTarget(MethodInfo factory, WotrFuzzAttribute registration)
        {
            Factory = factory;
            Modes = registration.Modes;
            var returned = factory.ReturnType;
            bool returnsTask = returned.IsGenericType && returned.GetGenericTypeDefinition() == typeof(Task<>);
            var produced = returnsTask ? returned.GetGenericArguments()[0] : returned;
            var problems = new List<string>();
            if (!factory.IsPublic || !factory.IsStatic) problems.Add("the method must be public static");
            if (factory.IsAbstract) problems.Add("the method must not be abstract");
            if (factory.GetParameters().Length != 0) problems.Add("the method must have no parameters");
            if (factory.IsGenericMethodDefinition) problems.Add("the method must not be generic");
            if (factory.DeclaringType.ContainsGenericParameters) problems.Add("the declaring type must not be an open generic type");
            if (!produced.IsInterface || produced.ContainsGenericParameters)
                problems.Add("the method must return an interface or Task<interface>, not " + TypeName(returned));
            if (Modes == 0 || (Modes & ~WotrTestModes.Both) != 0)
                problems.Add("Modes must be OffGame, InGame or Both, not " + (int)Modes);
            var provider = FindOptionsProvider(factory.DeclaringType, registration.OptionsProvider, problems);

            Id = factory.DeclaringType.Assembly.GetName().Name + ":" + TypeName(factory.DeclaringType) + "." + MethodName(factory) + ":" + TypeName(produced);
            DisplayName = factory.DeclaringType.Name + "." + factory.Name + " (" + produced.Name + ")";
            if (problems.Count > 0)
            {
                Error = "Invalid [WotrFuzz] registration " + TypeName(factory.DeclaringType) + "." + MethodName(factory) + ": " + string.Join("; ", problems) + ".";
                return;
            }
            Interface = produced;
            OptionsProvider = provider;
            if (returnsTask) untyped = typeof(FuzzEngine).GetMethod(nameof(FuzzEngine.Untyped), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(produced);
        }

        /// <summary>Stable: assembly simple name, declaring type, method and interface, such as <c>MyTests:MyNs.Registrations.CreateApi:MyNs.IMyModApi</c>.</summary>
        public string Id { get; }
        public string DisplayName { get; }
        /// <summary>The fuzzed interface; null for an invalid registration.</summary>
        public Type Interface { get; }
        public MethodInfo Factory { get; }
        /// <summary>The validated options provider; null when none is named or the registration is invalid.</summary>
        public MethodInfo OptionsProvider { get; }
        public WotrTestModes Modes { get; }
        /// <summary>Why the registration cannot run, or null when it is valid. Invalid registrations are reported, never dropped.</summary>
        public string Error { get; }

        /// <summary>
        /// Calls the options provider afresh (or uses default options), applies <paramref name="seed"/> to a copy, then runs
        /// <see cref="Fuzz.RunAsync{TApi}"/> semantics with the registered factory on the caller's synchronization context.
        /// Throws <see cref="InvalidOperationException"/> before constructing any target for an invalid registration or when
        /// the options provider throws (the original is the inner exception) or returns null; engine configuration errors
        /// are also thrown. Findings and factory, call or cleanup failures are reported in the returned report.
        /// </summary>
        public Task<FuzzReport> RunAsync(int? seed = null)
        {
            var options = CreateOptions();
            if (seed.HasValue) options.Seed = seed.Value;
            return new FuzzEngine(Interface, options).RunAsync(CreateTarget);
        }

        /// <summary>Replays a saved <see cref="FuzzReport.ReplayCase"/> exactly once, without generation or shrinking; errors as <see cref="RunAsync"/>.</summary>
        public Task<FuzzReport> ReplayAsync(string serializedCase)
        {
            if (serializedCase == null) throw new ArgumentNullException(nameof(serializedCase));
            return new FuzzEngine(Interface, CreateOptions()).ReplayAsync(CreateTarget, serializedCase);
        }

        public override string ToString() => Id;

        private FuzzOptions CreateOptions()
        {
            if (Error != null) throw new InvalidOperationException(Error);
            if (OptionsProvider == null) return new FuzzOptions();
            FuzzOptions provided;
            try { provided = (FuzzOptions)OptionsProvider.Invoke(null, null); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw new InvalidOperationException("The options provider " + OptionsProvider.Name + " of fuzz target " + Id + " threw " +
                    exception.InnerException.GetType().FullName + ": " + exception.InnerException.Message, exception.InnerException);
            }
            if (provided == null) throw new InvalidOperationException("The options provider " + OptionsProvider.Name + " of fuzz target " + Id + " returned null.");
            // The provider may return a shared instance; a seed override must not change it.
            return provided.Clone();
        }

        private Task<object> CreateTarget()
        {
            object created;
            try { created = Factory.Invoke(null, null); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
            if (untyped == null) return Task.FromResult(created);
            return (Task<object>)untyped.Invoke(null, new[] { created });
        }

        private static MethodInfo FindOptionsProvider(Type type, string name, List<string> problems)
        {
            if (name == null) return null;
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == name).ToArray();
            if (candidates.Length == 0) { problems.Add("options provider '" + name + "' was not found in " + TypeName(type)); return null; }
            if (candidates.Length > 1) { problems.Add("options provider '" + name + "' is ambiguous: " + candidates.Length + " methods have that name"); return null; }
            var provider = candidates[0];
            if (!provider.IsPublic || !provider.IsStatic || provider.GetParameters().Length != 0 || provider.IsGenericMethodDefinition ||
                provider.ReturnType != typeof(FuzzOptions))
            {
                problems.Add("options provider '" + name + "' must be a public static parameterless non-generic method returning FuzzOptions");
                return null;
            }
            return provider;
        }

        // Valid factories are parameterless and non-generic; the rest keep invalid overloads apart.
        private static string MethodName(MethodInfo method) => method.Name +
            (method.IsGenericMethodDefinition ? "``" + method.GetGenericArguments().Length : "") +
            (method.GetParameters().Length == 0 ? "" : "(" + string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) + ")");

        // Without assembly versions, so Ids match across runtimes.
        private static string TypeName(Type type)
        {
            if (type.IsArray) return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsByRef) return TypeName(type.GetElementType()) + "&";
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
                return type.GetGenericTypeDefinition().FullName + "[" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + "]";
            return type.FullName ?? type.Name;
        }
    }
}
