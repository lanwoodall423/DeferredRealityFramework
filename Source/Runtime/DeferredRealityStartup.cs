using System;
using System.Reflection;
using DeferredReality.API;
using HarmonyLib;
using Verse;

namespace DeferredReality.Runtime
{
    /// <summary>Small lifecycle bridge. It does not replace RimWorld lifecycle methods.</summary>
    [StaticConstructorOnStartup]
    public static class DeferredRealityStartup
    {
        static DeferredRealityStartup()
        {
            // StaticConstructorOnStartup is RimWorld's explicit main-thread lifecycle point.
            RealityThreadGuard.EstablishMainThread();
            new Harmony("lan.deferredreality.framework").PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    /// <summary>Tracks active maps as projections of stable regions.</summary>
    public static class RealityMapLifecycle
    {
        public static void OnMapReady(Map map)
        {
            if (map == null) return;
            if (!RealityThreadGuard.IsMainThread)
            {
                RunOnMainThread(() => OnMapReady(map));
                return;
            }
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null) return;
            world.RegisterMap(map);
            Materialization.RealityProjectionCacheService.TrackWarm(map);
        }

        /// <summary>Runs map-related mutations after RimWorld's worker-thread long event completes.</summary>
        public static void RunOnMainThread(Action action)
        {
            if (action == null) return;
            if (RealityThreadGuard.IsMainThread) action();
            else LongEventHandler.ExecuteWhenFinished(action);
        }

        public static void OnMapDeinit(Map map)
        {
            if (map == null) return;
            if (!RealityThreadGuard.IsMainThread)
            {
                RunOnMainThread(() => OnMapDeinit(map));
                return;
            }
            Materialization.RealityProjectionCacheService.Forget(map);
            DeferredRealityWorldComponent.Current?.UnregisterMap(map);
        }
    }

    /// <summary>Minimal active-map projection. It owns no regional truth.</summary>
    public sealed class DeferredRealityMapProjectionComponent : MapComponent
    {
        public DeferredRealityMapProjectionComponent(Map map) : base(map) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // Map.FinalizeInit Harmony postfix is the single readiness lifecycle path.
        }

        public override void MapComponentTick() { }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    internal static class MapFinalizeInitPatch
    {
        private static void Postfix(Map __instance)
        {
            RealityMapLifecycle.OnMapReady(__instance);
            Materialization.RealityAdjacentConstructionGuards.RemovePlayerConstructionArtifacts(__instance,
                DeferredRealityWorldComponent.Current);
        }
    }

    [HarmonyPatch(typeof(MapDeiniter), nameof(MapDeiniter.Deinit))]
    internal static class MapDeinitPatch
    {
        private static void Postfix(Map map)
        {
            RealityMapLifecycle.OnMapDeinit(map);
        }
    }
}
