using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace DeferredReality.API
{
    /// <summary>Public mapping facade separating live Map projections from durable region identities.</summary>
    public static class RealityRegionMappingService
    {
        /// <summary>Maps a live Map to a stable region and records only its projection binding.</summary>
        public static RealityRegionId ForMap(Map map)
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            return world?.RegisterMap(map) ?? default(RealityRegionId);
        }

        /// <summary>Finds the current live projection for a region without claiming one exists.</summary>
        public static bool TryGetActiveMap(RealityRegionId regionId, out Map map)
        {
            map = null;
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null || !world.TryGetRegion(regionId, out RealityRegionSnapshot region) ||
                region.authority != RealityRegionAuthority.LiveProjection || region.projectionMapUniqueId < 0) return false;
            map = Find.Maps?.FirstOrDefault(item => item != null && item.uniqueID == region.projectionMapUniqueId);
            return map != null;
        }

        /// <summary>Returns adjacent regions represented by enabled persisted connections.</summary>
        public static IReadOnlyList<RealityRegionId> Adjacent(RealityRegionId regionId, string kind = null)
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            return world?.Neighbors(regionId, kind) ?? Array.Empty<RealityRegionId>();
        }

        /// <summary>Resolves a live projection binding by Map.uniqueID.</summary>
        public static bool TryFromProjectionMapId(int mapId, out RealityRegionId regionId)
        {
            regionId = default(RealityRegionId);
            return DeferredRealityWorldComponent.Current?.TryRegionForProjectionMapId(mapId, out regionId) == true;
        }
    }
}
