using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;
using LudeonTK;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeferredReality.Materialization
{
    /// <summary>Opt-in experimental cardinal surface topology and safe transfer facade.</summary>
    public static class RealityAdjacentSurfaceService
    {
        private static readonly Dictionary<string, Map> WarmMaps = new Dictionary<string, Map>(StringComparer.Ordinal);
        private static readonly Dictionary<string, IAdjacentRegionTransferHost> ProviderHosts =
            new Dictionary<string, IAdjacentRegionTransferHost>(StringComparer.Ordinal);
        private static IAdjacentRegionTransferHost transferHost;

        /// <summary>Registers the host that owns group transfer implementation.</summary>
        public static void RegisterTransferHost(IAdjacentRegionTransferHost host) => transferHost = host;

        /// <summary>Registers a transfer host under one provider namespace.</summary>
        public static bool RegisterTransferHost(string providerId, IAdjacentRegionTransferHost host)
        {
            if (string.IsNullOrWhiteSpace(providerId) || host == null) return false;
            ProviderHosts[providerId.Trim()] = host;
            return true;
        }

        /// <summary>Creates stable cardinal neighbor descriptors and bidirectional links.</summary>
        public static IReadOnlyList<RealityRegionSnapshot> EnsureCardinalNeighbors(Map sourceMap)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new List<RealityRegionSnapshot>();
            if (!DeferredRealityModSettings.Current.enableAdjacentRegions || sourceMap == null || !sourceMap.Tile.Valid || Find.WorldGrid == null) return result;
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
                string linkId = "surface:" + source + ":" + destination;
                int continuitySeed = RealityDeterminism.Seed(world.WorldSeed, source.ToString(), "core", linkId, 0);
                world.UpsertTopology(new RealityTopologyLink
                {
                    linkId = linkId,
                    fromRegionId = source.ToString(),
                    toRegionId = destination.ToString(),
                    kind = "cardinal-surface",
                    travelCost = 1f,
                    metadata = new List<RealityPayloadField>
                    {
                        new RealityPayloadField { key = "edgeSlot", value = i.ToString() },
                        new RealityPayloadField { key = "continuitySeed", value = continuitySeed.ToString() },
                        new RealityPayloadField { key = "sourceTile", value = source.WorldTile.ToString() },
                        new RealityPayloadField { key = "destinationTile", value = ((int)tile).ToString() }
                    }
                });
                world.UpsertTopology(new RealityTopologyLink
                {
                    linkId = "surface:" + destination + ":" + source,
                    fromRegionId = destination.ToString(),
                    toRegionId = source.ToString(),
                    kind = "cardinal-surface",
                    travelCost = 1f,
                    metadata = new List<RealityPayloadField>
                    {
                        new RealityPayloadField { key = "edgeSlot", value = i.ToString() },
                        new RealityPayloadField { key = "continuitySeed", value = continuitySeed.ToString() }
                    }
                });
            }
            return result;
        }

        /// <summary>Attempts an explicitly prepared transfer; otherwise returns safe world-travel fallback.</summary>
        public static RealityAdjacentTransferResult TryTransfer(RealityAdjacentTransferRequest request)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new RealityAdjacentTransferResult();
            if (!DeferredRealityModSettings.Current.enableAdjacentRegions)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "Adjacent regions are disabled by default.";
                return result;
            }
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null || request == null || request.sourceMap == null || request.destinationMap == null)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "No safe adjacent transfer host or materialized destination is available.";
                return result;
            }
            request.sourceRegionId = world.RegisterMap(request.sourceMap);
            request.destinationRegionId = world.RegisterMap(request.destinationMap);
            if (string.IsNullOrEmpty(request.providerId)) request.providerId = request.sourceRegionId.ProviderNamespace;
            IAdjacentRegionTransferHost host = ProviderHosts.TryGetValue(request.providerId ?? string.Empty, out IAdjacentRegionTransferHost scopedHost)
                ? scopedHost : transferHost;
            if (host == null)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "No safe adjacent transfer host or materialized destination is available.";
                return result;
            }
            string pawnKey = string.Join("|", (request.pawns ?? Array.Empty<Pawn>()).Where(pawn => pawn != null)
                .Select(pawn => pawn.GetUniqueLoadID()).OrderBy(value => value, StringComparer.Ordinal).ToArray());
            request.transferId = request.transferId ?? "transfer:" + RealityDeterminism.Combine(request.sourceRegionId.ToString(),
                request.destinationRegionId.ToString(), request.entryEdge, request.sourceCell.x + ":" + request.sourceCell.z, pawnKey);
            if (world.TryGetTransferJournal(request.transferId, out RealityTransferJournalRecord previous))
            {
                if (previous.status == RealityTransferStatus.Completed)
                {
                    result.succeeded = true;
                    result.diagnostic = "Transfer was already committed.";
                    return result;
                }
                if (previous.status != RealityTransferStatus.RolledBack && previous.status != RealityTransferStatus.Fallback)
                {
                    result.fallbackToWorldTravel = true;
                    result.diagnostic = "An interrupted transfer journal requires explicit recovery before retry.";
                    return result;
                }
            }
            var journal = new RealityTransferJournalRecord
            {
                transferId = request.transferId,
                sourceRegionId = request.sourceRegionId.ToString(),
                destinationRegionId = request.destinationRegionId.ToString(),
                edge = request.entryEdge,
                pawnLoadIds = string.Join(",", (request.pawns ?? Array.Empty<Pawn>()).Where(pawn => pawn != null).Select(pawn => pawn.GetUniqueLoadID()).ToArray()),
                sourceCellX = request.sourceCell.x,
                sourceCellZ = request.sourceCell.z,
                createdTick = world.Now,
                updatedTick = world.Now,
                status = RealityTransferStatus.Prepared
            };
            world.UpsertTransferJournal(journal);
            var vetoes = new List<RealityVeto>();
            try
            {
                if (!host.CanTransfer(request, vetoes) || vetoes.Count > 0)
                    throw new RealityTransitionHostException(vetoes.Count > 0 ? string.Join("; ", vetoes.Select(item => item.ToString()).ToArray()) : "Host vetoed transfer.");
                journal.status = RealityTransferStatus.Acquiring;
                journal.updatedTick = world.Now;
                world.UpsertTransferJournal(journal);
                if (!host.Prepare(request, journal, out string prepareDiagnostic))
                    throw new RealityTransitionHostException(prepareDiagnostic ?? "Transfer preparation failed.");
                journal.status = RealityTransferStatus.Committing;
                journal.updatedTick = world.Now;
                world.UpsertTransferJournal(journal);
                if (!host.Commit(request, journal, out string commitDiagnostic))
                    throw new RealityTransitionHostException(commitDiagnostic ?? "Transfer commit failed.");
                journal.status = RealityTransferStatus.Completed;
                journal.updatedTick = world.Now;
                world.UpsertTransferJournal(journal);
                Warm(request.destinationRegionId, request.destinationMap);
                result.succeeded = true;
                return result;
            }
            catch (Exception exception)
            {
                try { host.Rollback(request, journal); } catch (Exception rollbackException) { Log.Error("Deferred Reality transfer rollback failed: " + rollbackException); }
                journal.status = RealityTransferStatus.Fallback;
                journal.diagnostic = exception.Message;
                journal.updatedTick = world.Now;
                world.UpsertTransferJournal(journal);
                result.rolledBack = true;
                result.fallbackToWorldTravel = true;
                result.diagnostic = exception.Message;
                result.vetoes = vetoes;
                return result;
            }
        }

        /// <summary>Rolls back an interrupted transfer journal when its host can still locate the party.</summary>
        public static RealityAdjacentTransferResult RecoverTransfer(RealityAdjacentTransferRequest request)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new RealityAdjacentTransferResult();
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null || request?.sourceMap == null || request.destinationMap == null)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "Recovery requires both source and destination maps.";
                return result;
            }
            request.sourceRegionId = world.RegisterMap(request.sourceMap);
            request.destinationRegionId = world.RegisterMap(request.destinationMap);
            if (string.IsNullOrEmpty(request.providerId)) request.providerId = request.sourceRegionId.ProviderNamespace;
            if (!world.TryGetTransferJournal(request.transferId, out RealityTransferJournalRecord journal))
            {
                result.diagnostic = "No transfer journal exists for the requested recovery.";
                return result;
            }
            if (journal.status == RealityTransferStatus.Completed)
            {
                result.succeeded = true;
                result.diagnostic = "Transfer was already committed.";
                return result;
            }
            if (journal.status == RealityTransferStatus.RolledBack || journal.status == RealityTransferStatus.Fallback)
            {
                result.rolledBack = true;
                result.diagnostic = "Transfer was already rolled back or sent to fallback travel.";
                return result;
            }
            IAdjacentRegionTransferHost host = ProviderHosts.TryGetValue(request.providerId ?? string.Empty, out IAdjacentRegionTransferHost scopedHost)
                ? scopedHost : transferHost;
            if (host == null)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "No provider transfer host is registered for this journal.";
                return result;
            }
            try
            {
                host.Rollback(request, journal);
                journal.status = RealityTransferStatus.RolledBack;
                journal.updatedTick = world.Now;
                journal.diagnostic = "Interrupted transfer was explicitly rolled back.";
                world.UpsertTransferJournal(journal);
                result.rolledBack = true;
                result.diagnostic = journal.diagnostic;
                return result;
            }
            catch (Exception exception)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = exception.Message;
                return result;
            }
        }

        /// <summary>Stores a bounded warm-map reference without removing a Map.</summary>
        public static void Warm(RealityRegionId regionId, Map map)
        {
            if (map == null || !DeferredRealityModSettings.Current.enableAdjacentRegions) return;
            WarmMaps[regionId.ToString()] = map;
            int limit = Math.Max(0, DeferredRealityModSettings.Current.warmMapCacheLimit);
            while (WarmMaps.Count > limit)
            {
                string key = WarmMaps.Keys.OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault();
                if (key == null) break;
                WarmMaps.Remove(key);
            }
        }

        /// <summary>Gets a warm map reference if it is still materialized.</summary>
        public static bool TryGetWarm(RealityRegionId regionId, out Map map) => WarmMaps.TryGetValue(regionId.ToString(), out map) && map != null;

        /// <summary>Removes a deinitialized map from the runtime warm cache.</summary>
        public static void Forget(Map map)
        {
            if (map == null) return;
            foreach (string key in WarmMaps.Where(item => item.Value == map).Select(item => item.Key).ToList()) WarmMaps.Remove(key);
        }
    }

    internal sealed class RealityTransitionHostException : Exception
    {
        internal RealityTransitionHostException(string message) : base(message) { }
    }

    /// <summary>Persisted opt-in setting for the experimental surface MVP.</summary>
    public sealed class DeferredRealityModSettings : ModSettings
    {
        private static DeferredRealityModSettings current;
        public bool enableAdjacentRegions;
        public int warmMapCacheLimit = 1;

        /// <summary>Current settings instance.</summary>
        public static DeferredRealityModSettings Current => current ?? (current = new DeferredRealityModSettings());

        /// <inheritdoc />
        public override void ExposeData()
        {
            Scribe_Values.Look(ref enableAdjacentRegions, "enableAdjacentRegions", false);
            Scribe_Values.Look(ref warmMapCacheLimit, "warmMapCacheLimit", 1);
            warmMapCacheLimit = Mathf.Clamp(warmMapCacheLimit, 0, 8);
        }

        internal static void SetCurrent(DeferredRealityModSettings value) => current = value;
    }

    /// <summary>Mod entry point for the framework setting only.</summary>
    public sealed class DeferredRealityMod : Mod
    {
        public DeferredRealityMod(ModContentPack content) : base(content)
        {
            DeferredRealityModSettings.SetCurrent(GetSettings<DeferredRealityModSettings>());
        }

        public override string SettingsCategory() => "Deferred Reality Framework";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            DeferredRealityModSettings settings = DeferredRealityModSettings.Current;
            listing.CheckboxLabeled("Enable experimental adjacent surface regions", ref settings.enableAdjacentRegions,
                "Disabled by default; unsafe transfers fall back to ordinary world travel.");
            settings.warmMapCacheLimit = Mathf.RoundToInt(listing.SliderLabeled("Warm map cache limit", settings.warmMapCacheLimit, 0, 8, 1f));
            listing.Label("Only successfully materialized maps are cached; the framework never removes a map from this setting.");
            listing.End();
        }
    }

    /// <summary>Developer action for creating topology descriptors without changing travel behavior.</summary>
    public static class RealityAdjacentDebugActions
    {
        [DebugAction("Deferred Reality", "Create cardinal adjacent region descriptors", actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void CreateCardinalRegions()
        {
            IReadOnlyList<RealityRegionSnapshot> regions = RealityAdjacentSurfaceService.EnsureCardinalNeighbors(Find.CurrentMap);
            Log.Message("[DeferredReality] cardinal descriptors created/verified: " + regions.Count);
        }
    }
}
