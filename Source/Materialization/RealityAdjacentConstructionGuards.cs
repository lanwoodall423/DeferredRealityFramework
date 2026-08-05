using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeferredReality.API;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeferredReality.Materialization
{
    /// <summary>Central construction predicate and defense-in-depth cleanup for marked adjacent maps.</summary>
    public static class RealityAdjacentConstructionGuards
    {
        internal const string RejectionMessage = "Adjacent regions are temporary work sites and cannot be built on.";

        /// <summary>Returns whether the framework construction policy blocks player construction on this map.</summary>
        public static bool IsBlocked(Map map)
        {
            return map != null && DeferredRealityWorldComponent.Current?.IsAdjacentMap(map) == true;
        }

        internal static void Reject()
        {
            Messages.Message(RejectionMessage, MessageTypeDefOf.RejectInput, false);
        }

        internal static void RemovePlayerConstructionArtifacts(Map map, DeferredRealityWorldComponent world)
        {
            if (!IsBlocked(map) || map.listerThings == null) return;
            List<Thing> artifacts = map.listerThings.AllThings
                .Where(thing => thing != null && thing.Map == map && (thing is Blueprint || thing is Frame)).ToList();
            foreach (Thing artifact in artifacts)
            {
                try { artifact.Destroy(DestroyMode.Cancel); }
                catch (Exception exception)
                {
                    world?.Quarantine("adjacent-construction", artifact.thingIDNumber.ToString(), "core",
                        "A construction artifact could not be removed from a temporary adjacent map.", exception.Message);
                }
            }
        }

        private static Map DesignatorMap(object instance)
        {
            if (instance == null) return null;
            try
            {
                MethodInfo getter = AccessTools.PropertyGetter(instance.GetType(), "Map");
                return getter?.Invoke(instance, null) as Map;
            }
            catch { return null; }
        }

        private static bool IsAdjacentDesignator(object instance)
        {
            return IsBlocked(DesignatorMap(instance));
        }

        internal static void RejectIfNeeded(object instance, ref AcceptanceReport report)
        {
            if (!IsAdjacentDesignator(instance)) return;
            report = new AcceptanceReport(RejectionMessage);
        }

        internal static bool AllowDesignation(object instance)
        {
            if (!IsAdjacentDesignator(instance)) return true;
            Reject();
            return false;
        }

        internal static bool AllowSpawn(Thing thing, Map map)
        {
            if (!IsBlocked(map) || !(thing is Blueprint) && !(thing is Frame)) return true;
            Reject();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class AdjacentBuildCellDesignatorPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase build = AccessTools.Method(typeof(Designator_Build), "CanDesignateCell", new[] { typeof(IntVec3) });
            if (build != null) yield return build;
        }

        private static void Postfix(object __instance, ref AcceptanceReport __result)
        {
            RealityAdjacentConstructionGuards.RejectIfNeeded(__instance, ref __result);
        }
    }

    [HarmonyPatch]
    internal static class AdjacentInstallCellDesignatorPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase install = AccessTools.Method(typeof(Designator_Install), "CanDesignateCell", new[] { typeof(IntVec3) });
            if (install != null) yield return install;
        }

        private static void Postfix(object __instance, ref AcceptanceReport __result)
        {
            RealityAdjacentConstructionGuards.RejectIfNeeded(__instance, ref __result);
        }
    }

    [HarmonyPatch]
    internal static class AdjacentInstallThingDesignatorPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(Designator_Install))
                .Where(item => item.Name == "CanDesignateThing" && item.GetParameters().Any(parameter => parameter.ParameterType == typeof(Thing))))
                yield return method;
        }

        private static void Postfix(object __instance, ref AcceptanceReport __result)
        {
            RealityAdjacentConstructionGuards.RejectIfNeeded(__instance, ref __result);
        }
    }

    [HarmonyPatch]
    internal static class AdjacentBuildDesignationExecutionPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase build = AccessTools.Method(typeof(Designator_Build), "DesignateSingleCell", new[] { typeof(IntVec3) });
            if (build != null) yield return build;
            MethodBase install = AccessTools.Method(typeof(Designator_Install), "DesignateSingleCell", new[] { typeof(IntVec3) });
            if (install != null) yield return install;
        }

        private static bool Prefix(object __instance)
        {
            return RealityAdjacentConstructionGuards.AllowDesignation(__instance);
        }
    }

    [HarmonyPatch]
    internal static class AdjacentBlueprintValidationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(GenConstruct))
                .Where(item => item.Name == "CanPlaceBlueprintAt" && item.ReturnType == typeof(AcceptanceReport)))
                yield return method;
        }

        private static bool Prefix(object[] __args, ref AcceptanceReport __result)
        {
            Map map = __args?.OfType<Map>().FirstOrDefault();
            if (!RealityAdjacentConstructionGuards.IsBlocked(map)) return true;
            __result = new AcceptanceReport(RealityAdjacentConstructionGuards.RejectionMessage);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class AdjacentBlueprintSpawnPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(GenSpawn))
                .Where(item => item.Name == "Spawn" && item.GetParameters().Any(parameter => parameter.ParameterType == typeof(Thing)) &&
                    item.GetParameters().Any(parameter => parameter.ParameterType == typeof(IntVec3)) &&
                    item.GetParameters().Any(parameter => parameter.ParameterType == typeof(Map))))
                yield return method;
        }

        private static bool Prefix(object[] __args)
        {
            Thing thing = __args?.OfType<Thing>().FirstOrDefault();
            Map map = __args?.OfType<Map>().FirstOrDefault();
            return RealityAdjacentConstructionGuards.AllowSpawn(thing, map);
        }
    }

    [HarmonyPatch]
    internal static class AdjacentBlueprintPlacementPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            string[] names = { "PlaceBlueprintForBuild", "PlaceBlueprintForInstall", "PlaceBlueprintForReinstall" };
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(GenConstruct))
                .Where(item => names.Contains(item.Name, StringComparer.Ordinal)))
                yield return method;
        }

        private static bool Prefix(object[] __args)
        {
            Map map = __args?.OfType<Map>().FirstOrDefault() ?? __args?.OfType<Thing>()
                .Select(item => item?.Map).FirstOrDefault(item => item != null);
            if (!RealityAdjacentConstructionGuards.IsBlocked(map)) return true;
            RealityAdjacentConstructionGuards.Reject();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class AdjacentBlueprintReplacementPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(Blueprint))
                .Where(item => item.Name == "TryReplaceWithSolidThing"))
                yield return method;
        }

        private static bool Prefix(Blueprint __instance)
        {
            if (!RealityAdjacentConstructionGuards.IsBlocked(__instance?.Map)) return true;
            RealityAdjacentConstructionGuards.Reject();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class AdjacentFrameCompletionPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(Frame))
                .Where(item => item.Name == "CompleteConstruction"))
                yield return method;
        }

        private static bool Prefix(Frame __instance)
        {
            if (!RealityAdjacentConstructionGuards.IsBlocked(__instance?.Map)) return true;
            RealityAdjacentConstructionGuards.Reject();
            return false;
        }
    }
}
