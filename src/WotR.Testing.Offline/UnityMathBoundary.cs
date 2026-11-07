using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Managed versions of two native Unity math functions the game's unit code calls, for example
    /// <c>UnitEntityData.GetLookAtAngle</c> when a unit turns to its target. They follow Unity's conventions: a left-handed
    /// basis built from forward and up, and Euler angles in Unity's order (Z, then X, then Y).
    /// </summary>
    internal static class UnityMath
    {
        private const float Epsilon = 0.00001f;

        internal static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            var length = forward.magnitude;
            if (length < Epsilon) return Quaternion.identity;
            var z = forward / length;
            var x = Vector3.Cross(up, z);
            var xLength = x.magnitude;
            if (xLength < Epsilon) return FromForwardAxis(z);
            x /= xLength;
            var y = Vector3.Cross(z, x);
            return FromBasis(x, y, z);
        }

        /// <summary>Rotation that turns +Z onto <paramref name="to"/> (a unit vector), as Unity uses when forward is parallel to up.</summary>
        private static Quaternion FromForwardAxis(Vector3 to)
        {
            var from = Vector3.forward;
            var dot = Vector3.Dot(from, to);
            if (dot > 1f - Epsilon) return Quaternion.identity;
            if (dot < -1f + Epsilon) return new Quaternion(0f, 1f, 0f, 0f);
            var axis = Vector3.Cross(from, to);
            var s = (float)Math.Sqrt((1f + dot) * 2f);
            return Normalize(new Quaternion(axis.x / s, axis.y / s, axis.z / s, s * 0.5f));
        }

        /// <summary>Quaternion of the rotation matrix whose columns are x, y and z.</summary>
        private static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z)
        {
            // m[row, column]
            var m = new float[3, 3]
            {
                { x.x, y.x, z.x },
                { x.y, y.y, z.y },
                { x.z, y.z, z.z },
            };
            var trace = m[0, 0] + m[1, 1] + m[2, 2];
            var q = new float[4]; // x, y, z, w
            if (trace > 0f)
            {
                var root = (float)Math.Sqrt(trace + 1f);
                q[3] = 0.5f * root;
                root = 0.5f / root;
                q[0] = (m[2, 1] - m[1, 2]) * root;
                q[1] = (m[0, 2] - m[2, 0]) * root;
                q[2] = (m[1, 0] - m[0, 1]) * root;
            }
            else
            {
                var next = new[] { 1, 2, 0 };
                var i = 0;
                if (m[1, 1] > m[0, 0]) i = 1;
                if (m[2, 2] > m[i, i]) i = 2;
                var j = next[i];
                var k = next[j];
                var root = (float)Math.Sqrt(m[i, i] - m[j, j] - m[k, k] + 1f);
                q[i] = 0.5f * root;
                root = 0.5f / root;
                q[3] = (m[k, j] - m[j, k]) * root;
                q[j] = (m[j, i] + m[i, j]) * root;
                q[k] = (m[k, i] + m[i, k]) * root;
            }
            return Normalize(new Quaternion(q[0], q[1], q[2], q[3]));
        }

        /// <summary>Euler angles in radians for Unity's rotation order (Z, X, Y), before Unity wraps them to 0..360 degrees.</summary>
        internal static Vector3 ToEulerRad(Quaternion q)
        {
            float x2 = q.x * 2f, y2 = q.y * 2f, z2 = q.z * 2f;
            float xx = q.x * x2, yy = q.y * y2, zz = q.z * z2;
            float xy = q.x * y2, xz = q.x * z2, yz = q.y * z2;
            float wx = q.w * x2, wy = q.w * y2, wz = q.w * z2;
            float m00 = 1f - (yy + zz), m01 = xy - wz, m02 = xz + wy;
            float m10 = xy + wz, m11 = 1f - (xx + zz), m12 = yz - wx;
            float m22 = 1f - (xx + yy);
            if (m12 < 0.999f)
            {
                if (m12 > -0.999f)
                    return new Vector3((float)Math.Asin(-m12), (float)Math.Atan2(m02, m22), (float)Math.Atan2(m10, m11));
                return new Vector3((float)(Math.PI * 0.5), (float)Math.Atan2(m01, m00), 0f);
            }
            return new Vector3((float)(-Math.PI * 0.5), (float)Math.Atan2(-m01, m00), 0f);
        }

        /// <summary>Quaternion of Euler angles in radians, applied in Unity's order: Z, then X, then Y.</summary>
        internal static Quaternion FromEulerRad(Vector3 euler)
        {
            float cx = (float)Math.Cos(euler.x * 0.5f), sx = (float)Math.Sin(euler.x * 0.5f);
            float cy = (float)Math.Cos(euler.y * 0.5f), sy = (float)Math.Sin(euler.y * 0.5f);
            float cz = (float)Math.Cos(euler.z * 0.5f), sz = (float)Math.Sin(euler.z * 0.5f);
            // q = qy * qx * qz
            return new Quaternion(
                cy * sx * cz + sy * cx * sz,
                sy * cx * cz - cy * sx * sz,
                cy * cx * sz - sy * sx * cz,
                cy * cx * cz + sy * sx * sz);
        }

        private static Quaternion Normalize(Quaternion q)
        {
            var length = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return length < Epsilon ? Quaternion.identity : new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
        }

        /// <summary>Translation, then rotation, then scale, as Unity's Matrix4x4.TRS.</summary>
        internal static Matrix4x4 TRS(Vector3 position, Quaternion q, Vector3 scale)
        {
            float x2 = q.x * 2f, y2 = q.y * 2f, z2 = q.z * 2f;
            float xx = q.x * x2, yy = q.y * y2, zz = q.z * z2;
            float xy = q.x * y2, xz = q.x * z2, yz = q.y * z2;
            float wx = q.w * x2, wy = q.w * y2, wz = q.w * z2;
            var m = new Matrix4x4();
            m.m00 = (1f - (yy + zz)) * scale.x; m.m01 = (xy - wz) * scale.y; m.m02 = (xz + wy) * scale.z; m.m03 = position.x;
            m.m10 = (xy + wz) * scale.x; m.m11 = (1f - (xx + zz)) * scale.y; m.m12 = (yz - wx) * scale.z; m.m13 = position.y;
            m.m20 = (xz - wy) * scale.x; m.m21 = (yz + wx) * scale.y; m.m22 = (1f - (xx + yy)) * scale.z; m.m23 = position.z;
            m.m33 = 1f;
            return m;
        }

        internal static Quaternion FromToRotation(Vector3 from, Vector3 to)
        {
            var a = from.normalized;
            var b = to.normalized;
            if (a == Vector3.zero || b == Vector3.zero) return Quaternion.identity;
            var dot = Vector3.Dot(a, b);
            if (dot > 1f - Epsilon) return Quaternion.identity;
            if (dot < -1f + Epsilon)
            {
                var axis = Vector3.Cross(Vector3.right, a);
                if (axis.sqrMagnitude < Epsilon) axis = Vector3.Cross(Vector3.up, a);
                axis.Normalize();
                return new Quaternion(axis.x, axis.y, axis.z, 0f);
            }
            var cross = Vector3.Cross(a, b);
            var s = (float)Math.Sqrt((1f + dot) * 2f);
            return Normalize(new Quaternion(cross.x / s, cross.y / s, cross.z / s, s * 0.5f));
        }

        internal static Quaternion Lerp(Quaternion a, Quaternion b, float t)
        {
            var sign = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w < 0f ? -1f : 1f;
            return Normalize(new Quaternion(
                a.x + (sign * b.x - a.x) * t, a.y + (sign * b.y - a.y) * t, a.z + (sign * b.z - a.z) * t, a.w + (sign * b.w - a.w) * t));
        }

        internal static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            double dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            var sign = dot < 0 ? -1.0 : 1.0;
            dot *= sign;
            if (dot > 0.9995) return Lerp(a, b, t);
            var theta = Math.Acos(dot);
            var sin = Math.Sin(theta);
            var wa = Math.Sin((1 - t) * theta) / sin;
            var wb = sign * Math.Sin(t * theta) / sin;
            return Normalize(new Quaternion(
                (float)(a.x * wa + b.x * wb), (float)(a.y * wa + b.y * wb), (float)(a.z * wa + b.z * wb), (float)(a.w * wa + b.w * wb)));
        }

        /// <summary>Unity's Vector3.RotateTowards: turn by at most maxRadians, change length by at most maxMagnitude.</summary>
        internal static Vector3 RotateTowards(Vector3 current, Vector3 target, float maxRadians, float maxMagnitude)
        {
            var currentLength = current.magnitude;
            var targetLength = target.magnitude;
            if (currentLength <= Epsilon || targetLength <= Epsilon) return Vector3.MoveTowards(current, target, maxMagnitude);
            var from = current / currentLength;
            var to = target / targetLength;
            var dot = Vector3.Dot(from, to);
            if (dot > 1f - Epsilon) return Vector3.MoveTowards(current, target, maxMagnitude);
            Vector3 axis;
            float angle;
            if (dot < -1f + Epsilon)
            {
                // Opposite directions: Unity's native code turns by the whole maxRadians about an axis orthogonal to
                // from, without stopping at the target, and so does this.
                axis = Math.Abs(from.z) > 0.7071f ? new Vector3(0f, -from.z, from.y).normalized : new Vector3(-from.y, from.x, 0f).normalized;
                angle = maxRadians;
            }
            else
            {
                axis = Vector3.Cross(from, to).normalized;
                angle = Math.Min(maxRadians, (float)Math.Acos(dot));
            }
            return Rotate(from, axis, angle) * ClampedMove(currentLength, targetLength, maxMagnitude);
        }

        /// <summary>Spherical interpolation of direction with linear interpolation of length, as Unity's Vector3.Slerp.</summary>
        internal static Vector3 Slerp(Vector3 a, Vector3 b, float t)
        {
            var aLength = a.magnitude;
            var bLength = b.magnitude;
            if (aLength <= Epsilon || bLength <= Epsilon) return Vector3.LerpUnclamped(a, b, t);
            var from = a / aLength;
            var to = b / bLength;
            var dot = Math.Max(-1f, Math.Min(1f, Vector3.Dot(from, to)));
            var length = aLength + (bLength - aLength) * t;
            if (dot > 1f - Epsilon) return Vector3.LerpUnclamped(from, to, t).normalized * length;
            var axis = dot < -1f + Epsilon
                ? (Math.Abs(from.z) > 0.7071f ? new Vector3(0f, -from.z, from.y).normalized : new Vector3(-from.y, from.x, 0f).normalized)
                : Vector3.Cross(from, to).normalized;
            return Rotate(from, axis, (float)Math.Acos(dot) * t) * length;
        }

        private static float ClampedMove(float from, float to, float maxDelta)
        {
            var delta = to - from;
            return delta > 0f ? from + Math.Min(delta, maxDelta) : from - Math.Min(-delta, maxDelta);
        }

        /// <summary>Rodrigues' rotation of v about a unit axis.</summary>
        private static Vector3 Rotate(Vector3 v, Vector3 axis, float angle)
        {
            var cos = (float)Math.Cos(angle);
            var sin = (float)Math.Sin(angle);
            return v * cos + Vector3.Cross(axis, v) * sin + axis * (Vector3.Dot(axis, v) * (1f - cos));
        }

        internal static float Determinant(Matrix4x4 m)
        {
            double Minor(int r0, int r1, int r2, int c0, int c1, int c2)
                => m[r0, c0] * ((double)m[r1, c1] * m[r2, c2] - (double)m[r1, c2] * m[r2, c1])
                   - m[r0, c1] * ((double)m[r1, c0] * m[r2, c2] - (double)m[r1, c2] * m[r2, c0])
                   + m[r0, c2] * ((double)m[r1, c0] * m[r2, c1] - (double)m[r1, c1] * m[r2, c0]);
            return (float)(m[0, 0] * Minor(1, 2, 3, 1, 2, 3) - m[0, 1] * Minor(1, 2, 3, 0, 2, 3)
                           + m[0, 2] * Minor(1, 2, 3, 0, 1, 3) - m[0, 3] * Minor(1, 2, 3, 0, 1, 2));
        }

        /// <summary>General 4x4 inverse in double precision; a singular matrix gives the zero matrix.</summary>
        internal static Matrix4x4 Inverse(Matrix4x4 source)
        {
            var a = new double[4, 8];
            for (var r = 0; r < 4; r++)
            {
                for (var c = 0; c < 4; c++) a[r, c] = source[r, c];
                a[r, 4 + r] = 1.0;
            }
            for (var column = 0; column < 4; column++)
            {
                var pivot = column;
                for (var r = column + 1; r < 4; r++)
                    if (Math.Abs(a[r, column]) > Math.Abs(a[pivot, column])) pivot = r;
                if (Math.Abs(a[pivot, column]) < 1e-12) return new Matrix4x4();
                if (pivot != column)
                    for (var c = 0; c < 8; c++) (a[pivot, c], a[column, c]) = (a[column, c], a[pivot, c]);
                var divisor = a[column, column];
                for (var c = 0; c < 8; c++) a[column, c] /= divisor;
                for (var r = 0; r < 4; r++)
                {
                    if (r == column) continue;
                    var factor = a[r, column];
                    if (factor == 0.0) continue;
                    for (var c = 0; c < 8; c++) a[r, c] -= factor * a[column, c];
                }
            }
            var result = new Matrix4x4();
            for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++) result[r, c] = (float)a[r, 4 + c];
            return result;
        }
    }

    [HarmonyPatch(typeof(Matrix4x4), "Inverse_Injected")]
    internal static class MatrixInversePatch
    {
        private static bool Prefix(ref Matrix4x4 m, ref Matrix4x4 ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.Inverse(m);
            return false;
        }
    }

    [HarmonyPatch(typeof(Quaternion))]
    internal static class QuaternionPatches
    {
        // Unity's Inverse is the conjugate; rotations are unit quaternions.
        [HarmonyPatch("Inverse_Injected"), HarmonyPrefix]
        private static bool Inverse(ref Quaternion rotation, ref Quaternion ret)
        {
            Boundaries.Hit("unity-math");
            ret = new Quaternion(-rotation.x, -rotation.y, -rotation.z, rotation.w);
            return false;
        }

        [HarmonyPatch("AngleAxis_Injected"), HarmonyPrefix]
        private static bool AngleAxis(float angle, ref Vector3 axis, ref Quaternion ret)
        {
            Boundaries.Hit("unity-math");
            var length = axis.magnitude;
            if (length < 0.000001f)
            {
                ret = Quaternion.identity;
                return false;
            }
            var half = angle * (float)(Math.PI / 360.0);
            var s = (float)Math.Sin(half) / length;
            ret = new Quaternion(axis.x * s, axis.y * s, axis.z * s, (float)Math.Cos(half));
            return false;
        }

        [HarmonyPatch("FromToRotation_Injected"), HarmonyPrefix]
        private static bool FromTo(ref Vector3 fromDirection, ref Vector3 toDirection, ref Quaternion ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.FromToRotation(fromDirection, toDirection);
            return false;
        }

        [HarmonyPatch("Lerp_Injected"), HarmonyPrefix]
        private static bool Lerp(ref Quaternion a, ref Quaternion b, float t, ref Quaternion ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.Lerp(a, b, Mathf.Clamp01(t));
            return false;
        }

        [HarmonyPatch("LerpUnclamped_Injected"), HarmonyPrefix]
        private static bool LerpUnclamped(ref Quaternion a, ref Quaternion b, float t, ref Quaternion ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.Lerp(a, b, t);
            return false;
        }

        [HarmonyPatch("Slerp_Injected"), HarmonyPrefix]
        private static bool Slerp(ref Quaternion a, ref Quaternion b, float t, ref Quaternion ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.Slerp(a, b, Mathf.Clamp01(t));
            return false;
        }

        [HarmonyPatch("SlerpUnclamped_Injected"), HarmonyPrefix]
        private static bool SlerpUnclamped(ref Quaternion a, ref Quaternion b, float t, ref Quaternion ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.Slerp(a, b, t);
            return false;
        }

        [HarmonyPatch("Internal_ToAxisAngleRad_Injected"), HarmonyPrefix]
        private static bool ToAxisAngle(ref Quaternion q, ref Vector3 axis, ref float angle)
        {
            Boundaries.Hit("unity-math");
            var w = Math.Max(-1.0, Math.Min(1.0, q.w));
            angle = (float)(2.0 * Math.Acos(w));
            var s = Math.Sqrt(1.0 - w * w);
            axis = s < 0.000001 ? Vector3.right : new Vector3((float)(q.x / s), (float)(q.y / s), (float)(q.z / s));
            return false;
        }
    }

    [HarmonyPatch(typeof(Vector3))]
    internal static class VectorPatches
    {
        [HarmonyPatch("RotateTowards_Injected"), HarmonyPrefix]
        private static bool RotateTowards(ref Vector3 current, ref Vector3 target, float maxRadiansDelta, float maxMagnitudeDelta, ref Vector3 ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.RotateTowards(current, target, maxRadiansDelta, maxMagnitudeDelta);
            return false;
        }

        [HarmonyPatch("Slerp_Injected"), HarmonyPrefix]
        private static bool Slerp(ref Vector3 a, ref Vector3 b, float t, ref Vector3 ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.Slerp(a, b, Mathf.Clamp01(t));
            return false;
        }

        [HarmonyPatch("SlerpUnclamped_Injected"), HarmonyPrefix]
        private static bool SlerpUnclamped(ref Vector3 a, ref Vector3 b, float t, ref Vector3 ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.Slerp(a, b, t);
            return false;
        }
    }

    /// <summary>
    /// While a battle runs its game loop, its physics scene is empty: no level is loaded, so no colliders exist. Casts
    /// report no hit and overlaps find nothing, as Unity answers for an empty scene; the movement agent, for example,
    /// then keeps the height the navigation mesh gives. Outside battles the native call runs and fails as before, so a
    /// test that needs real physics is reported as unity-runtime-required, never answered with an invented scene.
    /// </summary>
    [HarmonyPatch]
    internal static class PhysicsCastPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
            => typeof(PhysicsScene).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(m => m.ReturnType == typeof(bool) && m.Name.EndsWith("_Injected", StringComparison.Ordinal)
                            && (m.Name.StartsWith("Internal_Raycast", StringComparison.Ordinal) || m.Name.StartsWith("Query_", StringComparison.Ordinal)));

        private static bool Prefix(ref bool __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("physics-scene");
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class PhysicsQueryPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
            => typeof(PhysicsScene).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(m => m.ReturnType == typeof(int) && m.Name.Contains("NonAlloc") && m.Name.EndsWith("_Injected", StringComparison.Ordinal)
                            && m.Name != "OverlapCapsuleNonAlloc_Internal_Injected" && m.Name != "OverlapSphereNonAlloc_Internal_Injected");

        private static bool Prefix(ref int __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("physics-scene");
            __result = 0;
            return false;
        }
    }

    /// <summary>
    /// Overlaps on the unit bounds layer are answered from the battle units' bound spheres, the only colliders the
    /// game's entity-bounds physics scene holds for units. Other layers find nothing: no scene is loaded.
    /// </summary>
    [HarmonyPatch(typeof(PhysicsScene))]
    internal static class PhysicsOverlapPatch
    {
        private const int UnitBounds = 1 << (int)Owlcat.Runtime.Core.Utils.Layers.SphereBoundsUnit;

        [HarmonyPatch("OverlapCapsuleNonAlloc_Internal_Injected"), HarmonyPrefix]
        private static bool Capsule(ref Vector3 point0, ref Vector3 point1, float radius, Collider[] results, int layerMask, ref int __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("physics-scene");
            __result = (layerMask & UnitBounds) != 0 ? OfflineUnitView.OverlapUnitBounds(point0, point1, radius, results) : 0;
            return false;
        }

        [HarmonyPatch("OverlapSphereNonAlloc_Internal_Injected"), HarmonyPrefix]
        private static bool Sphere(ref Vector3 position, float radius, Collider[] results, int layerMask, ref int __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("physics-scene");
            __result = (layerMask & UnitBounds) != 0 ? OfflineUnitView.OverlapUnitBounds(position, position, radius, results) : 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(SphereCollider))]
    internal static class SphereColliderPatches
    {
        [HarmonyPatch("get_radius"), HarmonyPrefix]
        private static bool GetRadius(SphereCollider __instance, ref float __result) => !OfflineUnitView.TryGetRadius(__instance, out __result);

        [HarmonyPatch("set_radius"), HarmonyPrefix]
        private static bool SetRadius(SphereCollider __instance, float value) => !OfflineUnitView.TrySetRadius(__instance, value);
    }

    [HarmonyPatch(typeof(Physics), "get_defaultPhysicsScene_Injected")]
    internal static class DefaultPhysicsScenePatch
    {
        private static bool Prefix(ref PhysicsScene ret)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("physics-scene");
            ret = default;
            return false;
        }
    }

    /// <summary>
    /// The entity-bounds scene the game's EntityBoundsController creates on its first tick, while a battle runs: a scene
    /// with no level whose physics scene is the battle's (above). Only that scene is valid; other scene calls run the
    /// native code and fail as before.
    /// </summary>
    [HarmonyPatch]
    internal static class EntityBoundsScenePatches
    {
        internal const int Handle = 0x7EB00001;
        private static readonly FieldInfo SceneHandle = AccessTools.Field(typeof(UnityEngine.SceneManagement.Scene), "m_Handle");

        [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager), "CreateScene_Injected"), HarmonyPrefix]
        private static bool CreateScene(string sceneName, ref UnityEngine.SceneManagement.Scene ret)
        {
            if (!BattleLoop.Running || sceneName != "EntityBounds") return true;
            Boundaries.Hit("physics-scene");
            object scene = default(UnityEngine.SceneManagement.Scene);
            SceneHandle.SetValue(scene, Handle);
            ret = (UnityEngine.SceneManagement.Scene)scene;
            return false;
        }

        // A default Scene (handle 0) is never valid in Unity; it is what EntityBoundsController holds before its first tick.
        [HarmonyPatch(typeof(UnityEngine.SceneManagement.Scene), "IsValidInternal"), HarmonyPrefix]
        private static bool SceneIsValid(int sceneHandle, ref bool __result)
        {
            if (!BattleLoop.Running || (sceneHandle != Handle && sceneHandle != 0)) return true;
            __result = sceneHandle == Handle;
            return false;
        }

        [HarmonyPatch(typeof(PhysicsSceneExtensions), "GetPhysicsScene_Internal_Injected"), HarmonyPrefix]
        private static bool GetPhysicsScene(ref UnityEngine.SceneManagement.Scene scene, ref PhysicsScene ret)
        {
            if (!BattleLoop.Running || scene.handle != Handle) return true;
            ret = default;
            return false;
        }

        [HarmonyPatch(typeof(PhysicsScene), "IsValid_Internal_Injected"), HarmonyPrefix]
        private static bool PhysicsSceneIsValid(ref PhysicsScene physicsScene, ref bool __result)
        {
            if (!BattleLoop.Running || !physicsScene.Equals(default(PhysicsScene))) return true;
            __result = true;
            return false;
        }
    }

    /// <summary>Lines Unity draws in the editor's scene view; a player build draws nothing either.</summary>
    [HarmonyPatch(typeof(Debug), "DrawLine_Injected")]
    internal static class DebugDrawPatch
    {
        private static bool Prefix()
        {
            Boundaries.Hit("debug-draw");
            return false;
        }
    }

    [HarmonyPatch(typeof(Matrix4x4))]
    internal static class MatrixPatches
    {
        // Unity compares with a tolerance of 1e-5 per element.
        [HarmonyPatch("IsIdentity_Injected"), HarmonyPrefix]
        private static bool IsIdentity(ref Matrix4x4 _unity_self, ref bool __result)
        {
            Boundaries.Hit("unity-math");
            var identity = true;
            for (var r = 0; r < 4 && identity; r++)
                for (var c = 0; c < 4 && identity; c++)
                    identity = Math.Abs(_unity_self[r, c] - (r == c ? 1f : 0f)) <= 0.00001f;
            __result = identity;
            return false;
        }

        [HarmonyPatch("GetDeterminant_Injected"), HarmonyPrefix]
        private static bool Determinant(ref Matrix4x4 _unity_self, ref float __result)
        {
            Boundaries.Hit("unity-math");
            __result = UnityMath.Determinant(_unity_self);
            return false;
        }

        [HarmonyPatch("Transpose_Injected"), HarmonyPrefix]
        private static bool Transpose(ref Matrix4x4 m, ref Matrix4x4 ret)
        {
            Boundaries.Hit("unity-math");
            var result = new Matrix4x4();
            for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++) result[r, c] = m[c, r];
            ret = result;
            return false;
        }

        // Unity: true and the inverse, or false and a copy of the input when the matrix cannot be inverted.
        [HarmonyPatch("Inverse3DAffine_Injected"), HarmonyPrefix]
        private static bool InverseAffine(ref Matrix4x4 input, ref Matrix4x4 result, ref bool __result)
        {
            Boundaries.Hit("unity-math");
            __result = Math.Abs(UnityMath.Determinant(input)) > 1e-12f;
            result = __result ? UnityMath.Inverse(input) : input;
            return false;
        }
    }

    [HarmonyPatch(typeof(Matrix4x4), "TRS_Injected")]
    internal static class MatrixTrsPatch
    {
        private static bool Prefix(ref Vector3 pos, ref Quaternion q, ref Vector3 s, ref Matrix4x4 ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.TRS(pos, q, s);
            return false;
        }
    }

    [HarmonyPatch(typeof(Quaternion), "LookRotation_Injected")]
    internal static class QuaternionLookRotationPatch
    {
        private static bool Prefix(ref Vector3 forward, ref Vector3 upwards, ref Quaternion ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.LookRotation(forward, upwards);
            return false;
        }
    }

    [HarmonyPatch(typeof(Quaternion), "Internal_FromEulerRad_Injected")]
    internal static class QuaternionFromEulerPatch
    {
        private static bool Prefix(ref Vector3 euler, ref Quaternion ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.FromEulerRad(euler);
            return false;
        }
    }

    [HarmonyPatch(typeof(Quaternion), "Internal_ToEulerRad_Injected")]
    internal static class QuaternionToEulerPatch
    {
        private static bool Prefix(ref Quaternion rotation, ref Vector3 ret)
        {
            Boundaries.Hit("unity-math");
            ret = UnityMath.ToEulerRad(rotation);
            return false;
        }
    }
}
