using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace DeferredReality.API
{
    /// <summary>Public mapping facade separating active Map references from durable region identities.</summary>
    public static class RealityRegionMappingService
    {
        /// <summary>Maps an active Map to a stable region, creating only its descriptor and alias.</summary>
        public static RealityRegionId ForMap(Map map)
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            return world?.RegisterMap(map) ?? default(RealityRegionId);
        }

        /// <summary>Finds the currently materialized Map for a region without claiming one exists.</summary>
        public static bool TryGetActiveMap(RealityRegionId regionId, out Map map)
        {
            map = null;
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null || !world.TryGetRegion(regionId, out RealityRegionSnapshot region) || region.activeMapUniqueId < 0) return false;
            map = Find.Maps?.FirstOrDefault(item => item != null && item.uniqueID == region.activeMapUniqueId);
            return map != null;
        }

        /// <summary>Returns adjacent regions represented by persisted topology links.</summary>
        public static IReadOnlyList<RealityRegionId> Adjacent(RealityRegionId regionId, string kind = null)
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null) return Array.Empty<RealityRegionId>();
            var result = new List<RealityRegionId>();
            foreach (RealityTopologyLink link in world.TopologySnapshots(regionId.ToString()))
            {
                if (!string.IsNullOrEmpty(kind) && link.kind != kind) continue;
                bool outgoing = link.fromRegionId == regionId.ToString();
                if (!outgoing && link.oneWay) continue;
                string target = outgoing ? link.toRegionId : link.fromRegionId;
                if (RealityRegionId.TryParse(target, out RealityRegionId parsed) && !result.Contains(parsed)) result.Add(parsed);
            }
            return result.OrderBy(item => item.ToString(), StringComparer.Ordinal).ToList();
        }

        /// <summary>Resolves a legacy Map.uniqueID alias after migration.</summary>
        public static bool TryFromLegacyMapId(int mapId, out RealityRegionId regionId)
        {
            regionId = default(RealityRegionId);
            return DeferredRealityWorldComponent.Current?.TryRegionForLegacyMap(mapId, out regionId) == true;
        }
    }
}
