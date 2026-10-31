using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace WotR.Testing.Offline
{
    /// <summary>The offline game RNG: one <see cref="System.Random"/> behind <c>UnityEngine.Random</c>.</summary>
    public static class GameRandom
    {
        public const int DefaultSeed = 1;

        private static readonly FieldInfo SeedArrayField = typeof(Random).GetField("SeedArray", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InextField = typeof(Random).GetField("inext", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InextpField = typeof(Random).GetField("inextp", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo State0 = typeof(UnityEngine.Random.State).GetField("s0", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Dictionary<string, long> Methods = new Dictionary<string, long>();
        private static readonly Dictionary<int, GameRandomState> Handles = new Dictionary<int, GameRandomState>();
        private static readonly List<string> UncontrolledSources = new List<string>();
        private static Random current;
        private static List<GameRandomCall> recording;
        private static int nextHandle = 1;
        private static bool fieldsChecked;

        public static int Seed { get; private set; } = DefaultSeed;
        public static long CallsSinceInit { get; private set; }
        internal static long UnseededConstructions { get; private set; }

        public static void Init(int seed)
        {
            current = new Random(seed);
            Seed = seed;
            CallsSinceInit = 0;
        }

        public static GameRandomState Save()
        {
            EnsureReady();
            EnsureFields();
            return new GameRandomState((int[])((int[])SeedArrayField.GetValue(current)).Clone(), (int)InextField.GetValue(current), (int)InextpField.GetValue(current), Seed, CallsSinceInit);
        }

        public static void Restore(GameRandomState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            EnsureReady();
            EnsureFields();
            SeedArrayField.SetValue(current, (int[])state.SeedArray.Clone());
            InextField.SetValue(current, state.Inext);
            InextpField.SetValue(current, state.Inextp);
            Seed = state.Seed;
            CallsSinceInit = state.CallsSinceInit;
        }

        /// <summary>Records Unity random calls made by <paramref name="action"/>. Cannot be nested.</summary>
        public static IReadOnlyList<GameRandomCall> Record(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (recording != null) throw new InvalidOperationException("GameRandom.Record cannot be nested.");
            var calls = new List<GameRandomCall>();
            recording = calls;
            try { action(); }
            finally { recording = null; }
            return calls;
        }

        internal static void CountUnseeded() => UnseededConstructions++;

        internal static void NoteUncontrolled(string typeName)
        {
            if (!string.IsNullOrEmpty(typeName) && !UncontrolledSources.Contains(typeName)) UncontrolledSources.Add(typeName);
        }

        internal static int RangeInt(int minInclusive, int maxExclusive)
        {
            EnsureReady();
            var value = minInclusive >= maxExclusive ? minInclusive : current.Next(minInclusive, maxExclusive);
            Note("RandomRangeInt", Pair(minInclusive, maxExclusive), value.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        internal static float RangeFloat(float minInclusive, float maxInclusive)
        {
            EnsureReady();
            float value;
            if (minInclusive >= maxInclusive) value = minInclusive;
            else value = (float)(minInclusive + (maxInclusive - minInclusive) * current.NextDouble());
            Note("Range", Pair(minInclusive, maxInclusive), value.ToString("R", CultureInfo.InvariantCulture));
            return value;
        }

        internal static float Value()
        {
            EnsureReady();
            var value = (float)current.NextDouble();
            Note("value", "", value.ToString("R", CultureInfo.InvariantCulture));
            return value;
        }

        internal static void NoteInitState(int seed)
        {
            Init(seed);
            Note("InitState", seed.ToString(CultureInfo.InvariantCulture), "");
        }

        internal static void NoteSeedSet(int seed)
        {
            Init(seed);
            Note("set_seed", seed.ToString(CultureInfo.InvariantCulture), "");
        }

        internal static int ReadSeed()
        {
            Note("get_seed", "", Seed.ToString(CultureInfo.InvariantCulture));
            return Seed;
        }

        internal static UnityEngine.Random.State CaptureState()
        {
            var snapshot = Save();
            var id = nextHandle++;
            Handles[id] = snapshot;
            Note("get_state", "", id.ToString(CultureInfo.InvariantCulture));
            return StateFor(id);
        }

        internal static void ApplyState(UnityEngine.Random.State state)
        {
            if (State0 == null) throw new InvalidOperationException("UnityEngine.Random.State has no s0 field.");
            var id = (int)State0.GetValue(state);
            if (!Handles.TryGetValue(id, out var snapshot))
                throw new InvalidOperationException("UnityEngine.Random.state does not match a snapshot from this process.");
            Restore(snapshot);
            Note("set_state", id.ToString(CultureInfo.InvariantCulture), "");
        }

        internal static object Report() => new
        {
            algorithm = "System.Random",
            runtimeKind = OfflineRuntime.RuntimeKind,
            seed = current == null ? (int?)null : Seed,
            callsSinceInit = CallsSinceInit,
            intRange = "[min, max)",
            floatRange = "[min, max)",
            methods = Methods.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value),
            uncontrolled = new
            {
                unseededSystemRandom = UnseededConstructions,
                sources = UncontrolledSources.ToArray(),
            },
        };

        private static void Note(string method, string arguments, string result)
        {
            CallsSinceInit++;
            Methods.TryGetValue(method, out var count);
            Methods[method] = count + 1;
            recording?.Add(new GameRandomCall(method, arguments, result));
        }

        private static void EnsureReady()
        {
            if (current == null) throw new InvalidOperationException("GameRandom.Init has not run.");
        }

        private static void EnsureFields()
        {
            if (fieldsChecked) return;
            if (SeedArrayField == null || InextField == null || InextpField == null)
                throw new InvalidOperationException("System.Random state fields SeedArray, inext and inextp are not available on " + OfflineRuntime.RuntimeKind + ".");
            fieldsChecked = true;
        }

        private static UnityEngine.Random.State StateFor(int id)
        {
            object boxed = new UnityEngine.Random.State();
            State0.SetValue(boxed, id);
            return (UnityEngine.Random.State)boxed;
        }

        private static string Pair(object left, object right) =>
            Convert.ToString(left, CultureInfo.InvariantCulture) + "," + Convert.ToString(right, CultureInfo.InvariantCulture);
    }

    /// <summary>An in-process copy of the game <see cref="System.Random"/> fields.</summary>
    public sealed class GameRandomState
    {
        internal int[] SeedArray { get; }
        internal int Inext { get; }
        internal int Inextp { get; }
        internal int Seed { get; }
        internal long CallsSinceInit { get; }

        internal GameRandomState(int[] seedArray, int inext, int inextp, int seed, long callsSinceInit)
        {
            SeedArray = seedArray;
            Inext = inext;
            Inextp = inextp;
            Seed = seed;
            CallsSinceInit = callsSinceInit;
        }
    }

    /// <summary>One Unity random entry observed by <see cref="GameRandom.Record"/>.</summary>
    public sealed class GameRandomCall
    {
        public string Method { get; }
        public string Arguments { get; }
        public string Result { get; }

        internal GameRandomCall(string method, string arguments, string result)
        {
            Method = method;
            Arguments = arguments;
            Result = result;
        }
    }
}
