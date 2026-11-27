using HarmonyLib;
using Kingmaker.Controllers.Projectiles;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Visual.Particles;
using UnityEngine;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// A projectile's view while a battle runs (boundary projectile-view). The game's ProjectileController.CreateView takes
    /// the launch position from the launcher's model and claims the projectile's prefab from the effects pool; a battle has
    /// no models or prefabs, so the projectile gets a stand-in view object that keeps the position and rotation its flight
    /// gives it. The launch position follows CreateView: a custom launch position as given; without a source bone (or for
    /// a trap) the unit's view, which stands where the unit does; a source bone is a point of the unit's model, so the
    /// game's own fallback for a missing bone applies, the launcher's point one meter up; then the source bone's
    /// corpulence offset. The target offset, the launch and the flight (Projectile.Tick) are the game's.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileController), "CreateView")]
    internal static class ProjectileViewPatch
    {
        private static bool Prefix(Projectile projectile, Vector3? customLaunchPosition)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("projectile-view");
            var launcher = projectile.Launcher;
            var unit = launcher.Unit;
            Vector3 point;
            if (customLaunchPosition.HasValue)
            {
                point = customLaunchPosition.Value;
            }
            else
            {
                var changes = unit?.Get<UnitPartVisualChanges>();
                var bone = changes != null && changes.BoneReplaced ? changes.GetReplacementBone() : projectile.Blueprint.SourceBone;
                point = unit?.View != null && (string.IsNullOrEmpty(bone) || unit.Get<UnitPartTrapActor>() != null) ? unit.Position : launcher.Point + Vector3.up;
                if (unit?.View != null && projectile.Blueprint.SourceBoneCorpulenceOffset != 0f)
                {
                    // (point - launcher.Point).To2D().To3D(): the horizontal direction.
                    var horizontal = point - launcher.Point;
                    horizontal.y = 0f;
                    var normalized = horizontal.normalized;
                    point -= unit.View.Corpulence * projectile.Blueprint.SourceBoneCorpulenceOffset * normalized;
                }
            }
            if (projectile.Blueprint.SourceBoneOffsetAtTarget) projectile.TargetOffset = point - launcher.Point;
            projectile.LaunchPosition = point;
            projectile.BeforeLaunch();
            var direction = projectile.GetTargetPoint() - point;
            projectile.View = OfflineUnitView.CreateDetachedObject(point, direction != Vector3.zero ? Quaternion.LookRotation(direction) : Quaternion.identity);
            return false;
        }
    }

    /// <summary>
    /// ProjectileController.Clear, which the game calls when its mode stops (IControllerStop), destroys the parent object of
    /// all projectile views and with it the views; offline there is no parent, so the stand-in views of the projectiles it
    /// clears are released here. It leaves projectiles launched during the controller's last tick in its new list; the
    /// controller is the game's single one, so a battle drops those with their views rather than pass them to the next.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileController), nameof(ProjectileController.Clear))]
    internal static class ProjectileClearPatch
    {
        private static readonly System.Reflection.FieldInfo Projectiles = AccessTools.Field(typeof(ProjectileController), "m_Projectiles");
        private static readonly System.Reflection.FieldInfo NewProjectiles = AccessTools.Field(typeof(ProjectileController), "m_NewProjectiles");

        private static void Prefix(ProjectileController __instance)
        {
            foreach (Projectile projectile in (System.Collections.IEnumerable)Projectiles.GetValue(__instance))
                if (OfflineUnitView.ReleaseDetachedObject(projectile.View)) Boundaries.Hit("projectile-view");
            if (!BattleLoop.Running) return;
            var fresh = (System.Collections.Generic.List<Projectile>)NewProjectiles.GetValue(__instance);
            foreach (var projectile in fresh) OfflineUnitView.ReleaseDetachedObject(projectile.View);
            fresh.Clear();
        }
    }

    /// <summary>
    /// A projectile's trajectory shift while a battle runs: no shift. Projectile.Tick adds it only to the position of the
    /// view, not to the flight that decides the hit (CorePosition); the trajectory is an AnimationCurve, which only Unity's
    /// native code evaluates.
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Utility.TrajectoryCalculator), nameof(Kingmaker.Utility.TrajectoryCalculator.CalculateShift),
        new[] { typeof(Kingmaker.Blueprints.BlueprintProjectileTrajectory), typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(float), typeof(bool) })]
    internal static class ProjectileTrajectoryPatch
    {
        private static bool Prefix(ref Vector3 __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("projectile-view");
            __result = Vector3.zero;
            return false;
        }
    }

    /// <summary>
    /// The effects pool takes back a projectile's stand-in view by releasing it; other objects go to the pool as shipped.
    /// Applied after the attribute patches: compiling the replacement initializes GameObjectsPool, whose static fields ask
    /// Application.isPlaying, so the unity-platform boundary must be in place first.
    /// </summary>
    internal static class ProjectileViewReleasePatch
    {
        internal static void Apply(Harmony harmony)
            => harmony.Patch(AccessTools.Method(typeof(GameObjectsPool), nameof(GameObjectsPool.Release)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProjectileViewReleasePatch), nameof(Prefix))));

        private static bool Prefix(GameObject instance) => !OfflineUnitView.ReleaseDetachedObject(instance);
    }
}
