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
            RealityThreadGuard.InitializeMainThread();
            new Harmony("lan.deferredreality.framework").PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    /// <summary>Tracks active maps as projections of stable regions.</summary>
    public static class RealityMapLifecycle
    {
        public static void OnMapReady(Map map)
        {
            if (map == null) return;
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null) return;
            world.RegisterMap(map);
            map.GetComponent<DeferredRealityMapProjectionComponent>();
        }
    }

    /// <summary>Minimal active-map projection. It owns no regional truth.</summary>
    public sealed class DeferredRealityMapProjectionComponent : MapComponent
    {
        public DeferredRealityMapProjectionComponent(Map map) : base(map) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RealityMapLifecycle.OnMapReady(map);
        }

        public override void MapComponentTick() { }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    internal static class MapFinalizeInitPatch
    {
        private static void Postfix(Map __instance) => RealityMapLifecycle.OnMapReady(__instance);
    }

    [HarmonyPatch(typeof(MapDeiniter), nameof(MapDeiniter.Deinit))]
    internal static class MapDeinitPatch
    {
        private static void Postfix(Map map)
        {
            Materialization.RealityAdjacentSurfaceService.Forget(map);
            DeferredRealityWorldComponent.Current?.UnregisterMap(map);
        }
    }
}
