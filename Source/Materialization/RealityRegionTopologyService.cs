using System.Collections.Generic;
using DeferredReality.API;
using LudeonTK;
using RimWorld.Planet;
using Verse;

namespace DeferredReality.Materialization
{
    /// <summary>
    /// Projects engine topology into durable region connections. It does not create maps or move entities.
    /// </summary>
    public static class RealityRegionTopologyService
    {
        /// <summary>Creates stable cardinal neighbor descriptors and graph edges for a loaded surface map.</summary>
        public static IReadOnlyList<RealityRegionSnapshot> EnsureCardinalNeighbors(Map sourceMap)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new List<RealityRegionSnapshot>();
            if (!DeferredRealityModSettings.Current.enableAdjacentRegions || sourceMap == null ||
                !sourceMap.Tile.Valid || Find.WorldGrid == null) return result;
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null) return result;
            RealityRegionId source = world.RegisterMap(sourceMap);
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(sourceMap.Tile, neighbors);
            for (int i = 0; i < neighbors.Count; i++)
            {
                PlanetTile tile = neighbors[i];
                RealityRegionId destination = RealityRegionId.Surface((int)tile);
                result.Add(world.EnsureRegion(destination, "Adjacent world tile " + (int)tile, world.Now));
                const string kind = "cardinal-surface";
                const string owner = "core";
                const string identityKey = "cardinal";
                string connectionId = RealityRegionConnection.StableId(source, destination,
                    RealityRegionConnectionDirection.Bidirectional, kind, owner, identityKey);
                int continuitySeed = RealityDeterminism.Seed(world.WorldSeed, source.ToString(), owner, connectionId, 0);
                world.UpsertConnection(new RealityRegionConnection
                {
                    connectionId = connectionId,
                    sourceRegionId = source.ToString(),
                    destinationRegionId = destination.ToString(),
                    direction = RealityRegionConnectionDirection.Bidirectional,
                    kind = kind,
                    identityKey = identityKey,
                    traversalCost = 1f,
                    ownerNamespace = owner,
                    metadata = new List<RealityPayloadField>
                    {
                        new RealityPayloadField { key = "edgeSlot", value = i.ToString() },
                        new RealityPayloadField { key = "continuitySeed", value = continuitySeed.ToString() },
                        new RealityPayloadField { key = "sourceTile", value = source.WorldTile.ToString() },
                        new RealityPayloadField { key = "destinationTile", value = ((int)tile).ToString() }
                    }
                });
            }
            return result;
        }
    }

    /// <summary>Developer action for creating topology descriptors without changing travel behavior.</summary>
    public static class RealityRegionTopologyDebugActions
    {
        [DebugAction("Deferred Reality", "Create cardinal region connections", actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void CreateCardinalRegions()
        {
            IReadOnlyList<RealityRegionSnapshot> regions =
                RealityRegionTopologyService.EnsureCardinalNeighbors(Find.CurrentMap);
            Log.Message("[DeferredReality] cardinal descriptors created/verified: " + regions.Count);
        }
    }
}
