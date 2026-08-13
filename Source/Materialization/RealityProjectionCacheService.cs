using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;
using RimWorld;
using Verse;

namespace DeferredReality.Materialization
{
    /// <summary>
    /// Owns runtime references to temporary map projections and their bounded eviction diagnostics.
    /// Persisted adjacent-map records remain owned by DeferredRealityWorldComponent.
    /// </summary>
    public static class RealityProjectionCacheService
    {
        private static readonly Dictionary<string, Map> WarmMaps =
            new Dictionary<string, Map>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> WarmMapAccessTicks =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> EvictionDiagnostics =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void Warm(RealityRegionId regionId, Map map)
        {
            if (map == null || !DeferredRealityModSettings.Current.enableAdjacentRegions) return;
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null || !world.IsAdjacentMap(map)) return;
            string key = regionId.IsValid ? regionId.ToString() : GetProjectionKey(world, map);
            if (string.IsNullOrEmpty(key)) return;
            WarmMaps[key] = map;
            WarmMapAccessTicks[key] = Math.Max(WarmMapAccessTicks.TryGetValue(key, out long previous)
                ? previous : long.MinValue, world.Now);
            world.TouchAdjacentMap(map.uniqueID, world.Now);
            int limit = Math.Max(0, DeferredRealityModSettings.Current.warmMapCacheLimit);
            while (WarmMaps.Count > limit)
            {
                string candidate = RealityAdjacentPolicy.SelectWarmEvictions(WarmMapAccessTicks, limit).FirstOrDefault();
                if (candidate == null || !TryEvictWarmMap(candidate, world, world.Now)) break;
            }
        }

        /// <summary>Tracks a loaded projection without triggering eviction during map initialization.</summary>
        public static void TrackWarm(Map map, long tick = -1)
        {
            if (map == null || !DeferredRealityModSettings.Current.enableAdjacentRegions) return;
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null || !world.TryGetAdjacentMapRecord(map.uniqueID, out RealityAdjacentMapRecord marker)) return;
            string key = marker.regionId;
            WarmMaps[key] = map;
            long value = tick >= 0 ? tick : marker.lastAccessTick;
            WarmMapAccessTicks[key] = Math.Max(WarmMapAccessTicks.TryGetValue(key, out long previous)
                ? previous : long.MinValue, value);
        }

        public static bool TryGetWarm(RealityRegionId regionId, out Map map)
        {
            if (WarmMaps.TryGetValue(regionId.ToString(), out map) && map != null)
            {
                DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
                long now = world?.Now ?? 0;
                WarmMapAccessTicks[regionId.ToString()] = Math.Max(
                    WarmMapAccessTicks.TryGetValue(regionId.ToString(), out long previous) ? previous : long.MinValue, now);
                world?.TouchAdjacentMap(map.uniqueID, now);
                return true;
            }
            return false;
        }

        internal static IReadOnlyList<string> TryEvictWarmMaps(DeferredRealityWorldComponent world, long now = -1)
        {
            RealityThreadGuard.RequireMainThread();
            if (world == null) return Array.Empty<string>();
            long tick = now >= 0 ? now : world.Now;
            int limit = Math.Max(0, DeferredRealityModSettings.Current.warmMapCacheLimit);
            foreach (string key in RealityAdjacentPolicy.SelectWarmEvictions(WarmMapAccessTicks, limit))
                TryEvictWarmMap(key, world, tick);
            return EvictionDiagnostics.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Key + "=" + item.Value).ToList();
        }

        internal static bool TryGetEvictionDiagnostic(string key, out string diagnostic)
        {
            return EvictionDiagnostics.TryGetValue(key ?? string.Empty, out diagnostic);
        }

        internal static IReadOnlyList<string> EvictionDiagnosticLines()
        {
            return EvictionDiagnostics.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => "eviction|" + item.Key + "|" + item.Value).ToList();
        }

        internal static void RememberEvictionDiagnostic(string key, string diagnostic)
        {
            if (string.IsNullOrEmpty(key)) key = "projection";
            EvictionDiagnostics[key] = diagnostic ?? string.Empty;
            foreach (string stale in EvictionDiagnostics.Keys.OrderBy(value => value, StringComparer.Ordinal)
                .Skip(RealityRetentionPolicy.MaximumResolvedAdjacentDiagnostics).ToList())
                EvictionDiagnostics.Remove(stale);
        }

        public static void Forget(Map map)
        {
            if (map == null) return;
            foreach (string key in WarmMaps.Where(item => item.Value == map).Select(item => item.Key).ToList())
            {
                WarmMaps.Remove(key);
                WarmMapAccessTicks.Remove(key);
            }
        }

        private static string GetProjectionKey(DeferredRealityWorldComponent world, Map map)
        {
            return world.TryGetAdjacentMapRecord(map.uniqueID, out RealityAdjacentMapRecord marker)
                ? marker.regionId : null;
        }

        private static bool TryEvictWarmMap(string key, DeferredRealityWorldComponent world, long now)
        {
            if (!WarmMaps.TryGetValue(key, out Map map) || map == null)
            {
                WarmMaps.Remove(key);
                WarmMapAccessTicks.Remove(key);
                return true;
            }
            if (!world.TryGetAdjacentMapRecord(map.uniqueID, out RealityAdjacentMapRecord marker)) return false;
            if (Find.CurrentMap == map) return RecordEvictionVeto(key, "The adjacent map is currently viewed.");
            if (RealityAdjacentPolicy.HasActiveLease(world.ExcursionSnapshots(), map.uniqueID))
                return RecordEvictionVeto(key, "An excursion ticket still references the map.");
            RealityAdjacentConstructionGuards.RemovePlayerConstructionArtifacts(map, world);
            if (map.listerThings?.AllThings?.Any(thing => thing is Blueprint || thing is Frame) == true)
                return RecordEvictionVeto(key, "A construction blueprint or frame remains on the adjacent map.");
            if (map.listerThings?.AllThings?.Any(thing => IsEvictionBlocker(thing, map)) == true)
                return RecordEvictionVeto(key, "A player pawn, prisoner, corpse, carried pawn, or player-owned item remains.");
            if (world.TransferJournalSnapshots().Any(journal => journal != null && RealityRetentionPolicy.IsInterruptedTransfer(journal) &&
                (journal.sourceMapUniqueId == map.uniqueID || journal.destinationMapUniqueId == map.uniqueID ||
                 journal.sourceRegionId == marker.regionId || journal.destinationRegionId == marker.regionId)))
                return RecordEvictionVeto(key, "An interrupted or unresolved transfer journal references the map.");
            if (!RealityMapFactoryRegistry.TryGet(marker.providerId, out IRealityMapFactory factory))
                return RecordEvictionVeto(key, "The registered provider map factory is unavailable.");
            if (!RealityRegionId.TryParse(marker.regionId, out RealityRegionId adjacentRegion))
            {
                world.Quarantine("adjacent-map", marker.mapUniqueId.ToString(), marker.providerId,
                    "The adjacent map region identity could not be parsed for eviction.", marker.regionId);
                return RecordEvictionVeto(key, "The adjacent map region identity is invalid.");
            }
            var compressionRequest = new RealityCompressionRequest
            {
                Map = map,
                regionId = adjacentRegion,
                providerId = marker.providerId,
                now = now,
                reason = "projection-cache-eviction",
                dryRun = false
            };
            IReadOnlyList<RealityVeto> vetoes = RealityCompressionService.CanCompress(world, compressionRequest);
            if (vetoes.Count > 0)
                return RecordEvictionVeto(key, string.Join("; ", vetoes.Select(veto => veto.ToString()).ToArray()));
            RealityWorldState compressionState = world.CaptureState();
            RealityTransitionResult compression = RealityCompressionService.TryCompress(world, compressionRequest);
            if (!compression.succeeded) return RecordEvictionVeto(key, compression.error ?? "Provider compression failed.");
            try { factory.RemoveMap(map); }
            catch (Exception exception)
            {
                IReadOnlyList<string> compensationErrors = RealityCompressionService.RollbackCommitted(
                    world, compressionRequest, compressionState);
                string diagnostic = "Map factory removal failed: " + exception.Message;
                if (compensationErrors.Count > 0) diagnostic += " | compression compensation: " +
                    string.Join("; ", compensationErrors.ToArray());
                return RecordEvictionVeto(key, diagnostic);
            }
            if (Find.Maps?.Any(candidate => candidate != null &&
                (ReferenceEquals(candidate, map) || candidate.uniqueID == map.uniqueID)) == true)
            {
                IReadOnlyList<string> compensationErrors = RealityCompressionService.RollbackCommitted(
                    world, compressionRequest, compressionState);
                string diagnostic = "The provider factory did not remove the live map.";
                if (compensationErrors.Count > 0) diagnostic += " | compression compensation: " +
                    string.Join("; ", compensationErrors.ToArray());
                return RecordEvictionVeto(key, diagnostic);
            }
            world.UnregisterMap(map);
            world.RetireAdjacentMap(map.uniqueID, "Projection-cache eviction completed through the provider factory.");
            if (EvictionDiagnostics.TryGetValue(key, out string priorDiagnostic))
                world.RecordAdjacentDiagnostic("eviction-veto", "core", key, priorDiagnostic, now, true);
            WarmMaps.Remove(key);
            WarmMapAccessTicks.Remove(key);
            EvictionDiagnostics.Remove(key);
            return true;
        }

        private static bool IsEvictionBlocker(Thing thing, Map map)
        {
            if (thing == null || thing.Map != map) return false;
            if (thing is Corpse) return true;
            if (thing is Pawn pawn)
                return pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony || pawn.CarriedBy != null;
            return thing.Faction == Faction.OfPlayer;
        }

        private static bool RecordEvictionVeto(string key, string diagnostic)
        {
            string value = diagnostic ?? "Eviction was vetoed.";
            RememberEvictionDiagnostic(key, value);
            DeferredRealityWorldComponent.Current?.RecordAdjacentDiagnostic(
                "eviction-veto", "core", key, value);
            return false;
        }
    }
}
