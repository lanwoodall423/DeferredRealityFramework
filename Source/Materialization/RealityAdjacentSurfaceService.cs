using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DeferredReality.Materialization
{
    /// <summary>Opt-in experimental cardinal surface topology and safe transfer facade.</summary>
    public static class RealityAdjacentSurfaceService
    {
        private static readonly Dictionary<string, Map> WarmMaps = new Dictionary<string, Map>(StringComparer.Ordinal);
        private static readonly Dictionary<string, IAdjacentRegionTransferHost> ProviderHosts =
            new Dictionary<string, IAdjacentRegionTransferHost>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> WarmMapAccessTicks = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> EvictionDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
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
            if (!request.sourceRegionId.IsValid || !request.destinationRegionId.IsValid || request.sourceMap == request.destinationMap)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "The transfer maps do not have two distinct stable region identities.";
                return result;
            }
            bool providerWasSpecified = !string.IsNullOrEmpty(request.providerId);
            bool excursionTransfer = !string.IsNullOrEmpty(request.excursionId) || request.isOutboundExcursion;
            if (excursionTransfer && (request.isOutboundExcursion
                    ? !world.IsAdjacentMap(request.destinationMap)
                    : !world.IsAdjacentMap(request.sourceMap)))
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "An excursion transfer must cross a marked temporary adjacent map.";
                return result;
            }
            if (string.IsNullOrEmpty(request.providerId)) request.providerId = request.sourceRegionId.ProviderNamespace;
            if (excursionTransfer && !string.IsNullOrEmpty(request.excursionId) && !request.isOutboundExcursion)
            {
                if (!world.TryGetExcursion(request.excursionId, out RealityExcursionTicket ticket))
                {
                    result.fallbackToWorldTravel = true;
                    result.diagnostic = "The return transfer does not match a live excursion ticket.";
                    return result;
                }
                request.transferId = string.IsNullOrEmpty(request.transferId) ? ticket.returnTransferId : request.transferId;
                if (ticket.status == RealityExcursionStatus.Completed ||
                    ticket.providerId != request.providerId ||
                    ticket.returnTransferId != request.transferId ||
                    request.pawns == null || request.pawns.Count != 1 ||
                    request.pawns[0] == null || request.pawns[0].GetUniqueLoadID() != ticket.pawnLoadId ||
                    request.sourceMap.uniqueID != ticket.destinationMapUniqueId ||
                    request.destinationMap.uniqueID != ticket.originMapUniqueId ||
                    request.sourceRegionId.ToString() != ticket.destinationRegionId ||
                    request.destinationRegionId.ToString() != ticket.originRegionId ||
                    !string.Equals(request.entryEdge, ticket.inverseReturnEdge, StringComparison.OrdinalIgnoreCase))
                {
                    result.fallbackToWorldTravel = true;
                    result.diagnostic = "The return transfer does not match a live excursion ticket.";
                    return result;
                }
            }
            bool requireScopedHost = providerWasSpecified || excursionTransfer;
            IAdjacentRegionTransferHost host = ProviderHosts.TryGetValue(request.providerId ?? string.Empty, out IAdjacentRegionTransferHost scopedHost)
                ? scopedHost : requireScopedHost ? null : transferHost;
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
                    if (!JournalMatchesRequest(previous, request))
                    {
                        result.fallbackToWorldTravel = true;
                        result.diagnostic = "The completed transfer journal does not match the requested ownership identity.";
                        return result;
                    }
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
                providerId = request.providerId,
                excursionId = request.excursionId,
                sourceRegionId = request.sourceRegionId.ToString(),
                destinationRegionId = request.destinationRegionId.ToString(),
                sourceMapUniqueId = request.sourceMap.uniqueID,
                destinationMapUniqueId = request.destinationMap.uniqueID,
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
                if (request.isOutboundExcursion && !world.CommitOutboundExcursion(request.excursionId, request, journal,
                    out string excursionDiagnostic))
                    throw new RealityTransitionHostException(excursionDiagnostic ?? "The committed Pawn could not be attached to its excursion ticket.");
                journal.status = RealityTransferStatus.Completed;
                journal.updatedTick = world.Now;
                world.UpsertTransferJournal(journal);
                Warm(request.destinationRegionId, request.destinationMap);
                result.succeeded = true;
                return result;
            }
            catch (Exception exception)
            {
                var rollbackErrors = new List<string>();
                try { host.Rollback(request, journal); }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add(rollbackException.Message);
                    Log.Error("Deferred Reality transfer rollback failed: " + rollbackException);
                }
                bool pawnRestored = !request.isOutboundExcursion || PawnsAreOnMap(request.pawns, request.sourceMap);
                if (request.isOutboundExcursion && pawnRestored && rollbackErrors.Count == 0)
                    world.CancelPendingExcursion(request.excursionId);
                journal.status = request.isOutboundExcursion && (!pawnRestored || rollbackErrors.Count > 0)
                    ? RealityTransferStatus.Committing : RealityTransferStatus.Fallback;
                journal.diagnostic = rollbackErrors.Count == 0 ? exception.Message :
                    exception.Message + " | rollback: " + string.Join("; ", rollbackErrors.ToArray());
                if (!pawnRestored) journal.diagnostic += " | exact Pawn location was not restored; recovery remains pending.";
                journal.updatedTick = world.Now;
                world.UpsertTransferJournal(journal);
                result.rolledBack = true;
                result.fallbackToWorldTravel = true;
                result.diagnostic = journal.diagnostic;
                result.vetoes = vetoes;
                result.rollbackErrors = rollbackErrors;
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
            if (!world.TryGetTransferJournal(request.transferId, out RealityTransferJournalRecord journal))
            {
                result.diagnostic = "No transfer journal exists for the requested recovery.";
                return result;
            }
            if (journal.status == RealityTransferStatus.RolledBack || journal.status == RealityTransferStatus.Fallback)
            {
                result.rolledBack = true;
                result.diagnostic = "Transfer was already rolled back or sent to fallback travel.";
                return result;
            }
            bool excursionRecovery = !string.IsNullOrEmpty(request.excursionId) || !string.IsNullOrEmpty(journal.excursionId);
            if (!string.IsNullOrEmpty(request.excursionId) && request.excursionId != journal.excursionId)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "The recovery request does not match the excursion journal.";
                return result;
            }
            if (!string.IsNullOrEmpty(request.providerId) && !string.IsNullOrEmpty(journal.providerId) &&
                request.providerId != journal.providerId)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "The recovery request does not match the journal provider.";
                return result;
            }
            request.providerId = journal.providerId ?? request.providerId ?? request.sourceRegionId.ProviderNamespace;
            request.excursionId = request.excursionId ?? journal.excursionId;
            if (journal.sourceMapUniqueId != request.sourceMap.uniqueID || journal.destinationMapUniqueId != request.destinationMap.uniqueID ||
                journal.sourceRegionId != request.sourceRegionId.ToString() || journal.destinationRegionId != request.destinationRegionId.ToString() ||
                !string.Equals(journal.providerId ?? string.Empty, request.providerId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(journal.excursionId ?? string.Empty, request.excursionId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(journal.edge ?? string.Empty, request.entryEdge ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                (request.pawns != null && request.pawns.Count > 0 && !JournalMatchesRequest(journal, request)))
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "The recovery maps do not match the durable transfer journal.";
                return result;
            }
            if (journal.status == RealityTransferStatus.Completed)
            {
                if (!JournalMatchesRequest(journal, request))
                {
                    result.fallbackToWorldTravel = true;
                    result.diagnostic = "The completed recovery journal does not match the requested ownership identity.";
                    return result;
                }
                result.succeeded = true;
                result.diagnostic = "Transfer was already committed.";
                return result;
            }
            IAdjacentRegionTransferHost host = ProviderHosts.TryGetValue(request.providerId ?? string.Empty, out IAdjacentRegionTransferHost scopedHost)
                ? scopedHost : excursionRecovery ? null : transferHost;
            if (host == null)
            {
                result.fallbackToWorldTravel = true;
                result.diagnostic = "No provider transfer host is registered for this journal.";
                return result;
            }
            try
            {
                host.Rollback(request, journal);
                if (!PawnsAreOnMap(request.pawns, request.sourceMap))
                {
                    journal.status = RealityTransferStatus.Committing;
                    journal.updatedTick = world.Now;
                    journal.diagnostic = "Recovery did not prove that every transferred Pawn returned to the source map.";
                    world.UpsertTransferJournal(journal);
                    result.fallbackToWorldTravel = true;
                    result.diagnostic = journal.diagnostic;
                    result.rollbackErrors = new[] { journal.diagnostic };
                    return result;
                }
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
                result.rollbackErrors = new[] { exception.Message };
                return result;
            }
        }

        /// <summary>Stores a bounded warm-map reference without removing a Map.</summary>
        public static void Warm(RealityRegionId regionId, Map map)
        {
            if (map == null || !DeferredRealityModSettings.Current.enableAdjacentRegions) return;
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null || !world.IsAdjacentMap(map)) return;
            string key = regionId.IsValid ? regionId.ToString() : GetAdjacentKey(world, map);
            if (string.IsNullOrEmpty(key)) return;
            WarmMaps[key] = map;
            WarmMapAccessTicks[key] = Math.Max(WarmMapAccessTicks.TryGetValue(key, out long previous) ? previous : long.MinValue,
                world.Now);
            world.TouchAdjacentMap(map.uniqueID, world.Now);
            int limit = Math.Max(0, DeferredRealityModSettings.Current.warmMapCacheLimit);
            while (WarmMaps.Count > limit)
            {
                string candidate = RealityAdjacentPolicy.SelectWarmEvictions(WarmMapAccessTicks, limit).FirstOrDefault();
                if (candidate == null || !TryEvictWarmMap(candidate, world, world.Now)) break;
            }
        }

        /// <summary>Tracks a loaded marked map without triggering eviction during map initialization.</summary>
        public static void TrackWarm(Map map, long tick = -1)
        {
            if (map == null || !DeferredRealityModSettings.Current.enableAdjacentRegions) return;
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null || !world.TryGetAdjacentMapRecord(map.uniqueID, out RealityAdjacentMapRecord marker)) return;
            string key = marker.regionId;
            WarmMaps[key] = map;
            long value = tick >= 0 ? tick : world.Now;
            WarmMapAccessTicks[key] = Math.Max(WarmMapAccessTicks.TryGetValue(key, out long previous) ? previous : long.MinValue, value);
        }

        private static string GetAdjacentKey(DeferredRealityWorldComponent world, Map map)
        {
            return world.TryGetAdjacentMapRecord(map.uniqueID, out RealityAdjacentMapRecord marker) ? marker.regionId : null;
        }

        /// <summary>Gets a warm map reference if it is still materialized.</summary>
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

        /// <summary>Runs the bounded adjacent-map and excursion monitor. It is called at a coarse world interval.</summary>
        public static void Monitor(DeferredRealityWorldComponent world, long now)
        {
            RealityThreadGuard.RequireMainThread();
            if (world == null || (!DeferredRealityModSettings.Current.enableAdjacentRegions && !world.HasAdjacentSafetyWork)) return;
            try { ReconcileExcursionJournals(world, now); }
            catch (Exception exception) { RecordMonitorFailure(world, "journal-reconciliation", exception); }
            foreach (RealityAdjacentMapRecord marker in world.AdjacentMapSnapshots()
                .Where(item => item != null && item.lifecycle == RealityAdjacentMapLifecycle.Active))
            {
                try
                {
                    Map map = Find.Maps?.FirstOrDefault(candidate => candidate != null && candidate.uniqueID == marker.mapUniqueId);
                    if (map == null)
                    {
                        EvictionDiagnostics[marker.regionId] = "Marked adjacent map is not currently materialized; role retained for recovery.";
                        continue;
                    }
                    world.TouchAdjacentMap(map.uniqueID, now);
                    TrackWarm(map, now);
                    WarmMapAccessTicks[marker.regionId] = Math.Max(
                        WarmMapAccessTicks.TryGetValue(marker.regionId, out long previous) ? previous : long.MinValue, now);
                    RealityAdjacentConstructionGuards.RemovePlayerConstructionArtifacts(map, world);
                }
                catch (Exception exception) { RecordMonitorFailure(world, marker.regionId, exception); }
            }
            foreach (RealityExcursionTicket ticket in world.ExcursionSnapshots())
            {
                try { MonitorExcursion(world, ticket, now); }
                catch (Exception exception) { RecordMonitorFailure(world, ticket?.excursionId, exception); }
            }
            try { TryEvictWarmMaps(world, now); }
            catch (Exception exception) { RecordMonitorFailure(world, "eviction", exception); }
        }

        private static void RecordMonitorFailure(DeferredRealityWorldComponent world, string key, Exception exception)
        {
            string diagnostic = "Adjacent safety monitoring failed: " + exception.Message;
            EvictionDiagnostics[key ?? "monitor"] = diagnostic;
            try { world?.Quarantine("adjacent-monitor", key, "core", diagnostic, exception.GetType().FullName); }
            catch { Log.Error("[DeferredReality] " + diagnostic); }
        }

        /// <summary>Attempts all overdue warm-cache evictions and retains a diagnostic for each veto.</summary>
        public static IReadOnlyList<string> TryEvictWarmMaps(DeferredRealityWorldComponent world, long now = -1)
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

        /// <summary>Returns stable diagnostics for marked maps and the last eviction vetoes.</summary>
        public static IReadOnlyList<string> AdjacentDiagnostics(DeferredRealityWorldComponent world = null)
        {
            world = world ?? DeferredRealityWorldComponent.Current;
            if (world == null) return Array.Empty<string>();
            var lines = new List<string>();
            foreach (RealityAdjacentMapRecord marker in world.AdjacentMapSnapshots())
                lines.Add("map|" + marker.mapUniqueId + "|" + marker.regionId + "|" + marker.providerId + "|" + marker.lifecycle +
                    "|origin=" + marker.originRegionId + "@" + marker.originMapUniqueId + "|last=" + marker.lastAccessTick);
            foreach (RealityExcursionTicket ticket in world.ExcursionSnapshots())
                lines.Add("excursion|" + ticket.excursionId + "|" + ticket.providerId + "|" + ticket.pawnLoadId +
                    "|" + ticket.status + "|" + ticket.originMapUniqueId + "->" + ticket.destinationMapUniqueId +
                    "|retry=" + ticket.retryTick + "|" + (ticket.diagnostic ?? string.Empty));
            lines.Add("construction|marked-maps=" + world.AdjacentMapSnapshots().Count + "|rejection=" +
                RealityAdjacentConstructionGuards.RejectionMessage);
            lines.AddRange(EvictionDiagnostics.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => "eviction|" + item.Key + "|" + item.Value));
            return lines;
        }

        private static void MonitorExcursion(DeferredRealityWorldComponent world, RealityExcursionTicket ticket, long now)
        {
            if (ticket == null || ticket.status == RealityExcursionStatus.Completed ||
                ticket.status == RealityExcursionStatus.Quarantined || ticket.retryTick > now) return;
            Pawn pawn = FindPawnAnywhere(ticket.pawnLoadId, out Map pawnMap, out bool inCaravanOrWorldPawns,
                out bool ambiguousOwnership);
            if (ambiguousOwnership)
            {
                world.Quarantine("excursion", ticket.excursionId, ticket.providerId,
                    "The tracked Pawn load ID exists in multiple ownership locations; no return was attempted.",
                    ticket.pawnLoadId);
                world.SetExcursionDiagnostic(ticket.excursionId, "Pawn ownership is ambiguous; quarantined without teleporting.",
                    now + RealityAdjacentPolicy.RetryBackoffTicks);
                return;
            }
            if (pawn == null)
            {
                world.SetExcursionDiagnostic(ticket.excursionId, "Tracked Pawn is not currently loaded; ticket retained for save/load recovery.",
                    now + RealityAdjacentPolicy.RetryBackoffTicks);
                return;
            }
            if (pawnMap != null && pawnMap.uniqueID == ticket.originMapUniqueId)
            {
                world.MarkExcursionReturned(ticket.excursionId, pawn, "Pawn was already safely on its exact origin map.");
                return;
            }
            if (inCaravanOrWorldPawns)
            {
                world.SetExcursionDiagnostic(ticket.excursionId,
                    "Tracked Pawn is in world-pawn or caravan storage; no map substitution is permitted.",
                    now + RealityAdjacentPolicy.RetryBackoffTicks);
                return;
            }
            if (pawnMap == null || pawnMap.uniqueID != ticket.destinationMapUniqueId)
            {
                world.Quarantine("excursion", ticket.excursionId, ticket.providerId,
                    "Tracked Pawn is on an unexpected map; ownership is ambiguous and no return was attempted.",
                    pawn.GetUniqueLoadID());
                world.SetExcursionDiagnostic(ticket.excursionId, "Pawn location is ambiguous; quarantined without teleporting.",
                    now + RealityAdjacentPolicy.RetryBackoffTicks);
                return;
            }
            bool unsafeState = IsUnsafeOrMeaningful(pawn);
            bool safelyIdle = !unsafeState && IsSafeIdle(pawn);
            bool taskCompleted = ticket.status == RealityExcursionStatus.ReturnRequested ||
                ticket.status == RealityExcursionStatus.Cancelled;
            if (!RealityAdjacentPolicy.IsReturnDue(ticket, now, taskCompleted, safelyIdle, unsafeState, false))
            {
                if (unsafeState) world.SetExcursionDiagnostic(ticket.excursionId,
                    "Return is waiting for the Pawn to leave combat, medical, drafted, carried, or provider work state.",
                    now + RealityAdjacentPolicy.RetryBackoffTicks);
                return;
            }
            if (!RealityProviderRegistry.TryGet(ticket.providerId, out _) ||
                !ProviderHosts.ContainsKey(ticket.providerId))
            {
                world.SetExcursionDiagnostic(ticket.excursionId,
                    "The excursion provider is unavailable; the adjacent map and Pawn remain alive for reactivation.",
                    now + RealityAdjacentPolicy.RetryBackoffTicks);
                return;
            }
            Map originMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID == ticket.originMapUniqueId);
            if (originMap == null)
            {
                world.SetExcursionDiagnostic(ticket.excursionId,
                    "The exact origin map is missing or unloading; no alternate colony map will be selected.",
                    now + RealityAdjacentPolicy.RetryBackoffTicks);
                return;
            }
            if (!RealityRegionId.TryParse(ticket.originRegionId, out RealityRegionId originRegion) ||
                !RealityRegionId.TryParse(ticket.destinationRegionId, out RealityRegionId destinationRegion))
            {
                world.Quarantine("excursion", ticket.excursionId, ticket.providerId,
                    "Excursion region identities could not be parsed during return monitoring.", null);
                return;
            }
            if (pawn.CurJob != null && IsSafeIdle(pawn)) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            world.MarkExcursionReturning(ticket.excursionId, now + RealityAdjacentPolicy.RetryBackoffTicks, "Return transfer in progress.");
            RealityAdjacentTransferResult result = TryTransfer(new RealityAdjacentTransferRequest
            {
                providerId = ticket.providerId,
                excursionId = ticket.excursionId,
                sourceMap = pawnMap,
                destinationMap = originMap,
                sourceRegionId = destinationRegion,
                destinationRegionId = originRegion,
                sourceCell = pawn.Position,
                entryEdge = ticket.inverseReturnEdge,
                pawns = new[] { pawn },
                transferId = string.IsNullOrEmpty(ticket.returnTransferId)
                    ? "return:" + ticket.excursionId : ticket.returnTransferId
            });
            if (!result.succeeded || pawn.Spawned != true || pawn.Map != originMap)
            {
                world.MarkExcursionRetry(ticket.excursionId, now + RealityAdjacentPolicy.RetryBackoffTicks,
                    result.diagnostic ?? "The return transfer did not verify the original Pawn instance on its origin map.");
                return;
            }
            world.MarkExcursionReturned(ticket.excursionId, pawn, "Pawn returned through the exact inverse adjacent transfer.");
            Messages.Message(pawn.LabelShortCap + " returned from the temporary adjacent excursion site.",
                pawn, MessageTypeDefOf.PositiveEvent, false);
        }

        private static void ReconcileExcursionJournals(DeferredRealityWorldComponent world, long now)
        {
            foreach (RealityTransferJournalRecord journal in world.TransferJournalSnapshots()
                .Where(item => item != null && item.status != RealityTransferStatus.Completed &&
                    item.status != RealityTransferStatus.RolledBack && item.status != RealityTransferStatus.Fallback))
            {
                if (journal.sourceMapUniqueId < 0 || journal.destinationMapUniqueId < 0) continue;
                Map sourceMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID == journal.sourceMapUniqueId);
                Map destinationMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID == journal.destinationMapUniqueId);
                if (sourceMap == null || destinationMap == null) continue;
                if (!RealityRegionId.TryParse(journal.sourceRegionId, out RealityRegionId sourceRegion) ||
                    !RealityRegionId.TryParse(journal.destinationRegionId, out RealityRegionId destinationRegion))
                {
                    world.Quarantine("transfer", journal.transferId, journal.providerId,
                        "An interrupted transfer journal has invalid region identities.", journal.excursionId);
                    continue;
                }
                string pawnId = (journal.pawnLoadIds ?? string.Empty).Split(',')
                    .FirstOrDefault(value => !string.IsNullOrEmpty(value));
                Pawn pawn = FindPawnAnywhere(pawnId, out _, out _, out bool ambiguousOwnership);
                if (ambiguousOwnership)
                {
                    world.Quarantine("transfer", journal.transferId, journal.providerId,
                        "An interrupted transfer has ambiguous Pawn ownership and was not guessed.", pawnId);
                    continue;
                }
                RealityAdjacentTransferResult recovery = RecoverTransfer(new RealityAdjacentTransferRequest
                {
                    providerId = journal.providerId ?? destinationRegion.ProviderNamespace,
                    excursionId = journal.excursionId,
                    sourceMap = sourceMap,
                    destinationMap = destinationMap,
                    sourceRegionId = sourceRegion,
                    destinationRegionId = destinationRegion,
                    sourceCell = new IntVec3(journal.sourceCellX, 0, journal.sourceCellZ),
                    entryEdge = journal.edge,
                    pawns = pawn == null ? Array.Empty<Pawn>() : new[] { pawn },
                    transferId = journal.transferId
                });
                if (!recovery.rolledBack)
                    world.SetExcursionDiagnostic(journal.excursionId,
                        recovery.diagnostic ?? "Interrupted transfer remains pending explicit recovery.",
                        now + RealityAdjacentPolicy.RetryBackoffTicks);
            }

            foreach (RealityTransferJournalRecord journal in world.TransferJournalSnapshots()
                .Where(item => item != null && !string.IsNullOrEmpty(item.excursionId) &&
                    item.status == RealityTransferStatus.Completed))
            {
                if (world.TryGetExcursion(journal.excursionId, out RealityExcursionTicket existingTicket))
                {
                    if (journal.transferId == existingTicket.returnTransferId)
                    {
                        Pawn returnedPawn = FindPawnAnywhere(existingTicket.pawnLoadId, out Map returnedMap,
                            out _, out bool returnedAmbiguous);
                        if (returnedAmbiguous)
                        {
                            world.Quarantine("excursion", existingTicket.excursionId, existingTicket.providerId,
                                "A completed return journal has ambiguous Pawn ownership.", existingTicket.pawnLoadId);
                        }
                        else if (returnedPawn != null && returnedMap?.uniqueID == existingTicket.originMapUniqueId)
                        {
                            world.MarkExcursionReturned(existingTicket.excursionId, returnedPawn,
                                "Completed return journal reconciled after load.");
                        }
                    }
                    continue;
                }
                if (journal.destinationMapUniqueId < 0 || journal.sourceMapUniqueId < 0 ||
                    !RealityRegionId.TryParse(journal.sourceRegionId, out RealityRegionId sourceRegion) ||
                    !RealityRegionId.TryParse(journal.destinationRegionId, out RealityRegionId destinationRegion)) continue;
                Map sourceMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID == journal.sourceMapUniqueId);
                Map destinationMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID == journal.destinationMapUniqueId);
                string pawnId = (journal.pawnLoadIds ?? string.Empty).Split(',')
                    .FirstOrDefault(value => !string.IsNullOrEmpty(value));
                Pawn pawn = FindPawnAnywhere(pawnId, out Map pawnMap, out bool inStorage, out bool ambiguousOwnership);
                if (ambiguousOwnership)
                {
                    world.Quarantine("excursion", journal.excursionId, journal.providerId,
                        "A completed excursion journal has ambiguous Pawn ownership.", pawnId);
                    continue;
                }
                var request = new RealityExcursionRequest
                {
                    excursionId = journal.excursionId,
                    providerId = journal.providerId ?? destinationRegion.ProviderNamespace,
                    pawnLoadId = pawnId,
                    originRegionId = sourceRegion,
                    originMapUniqueId = journal.sourceMapUniqueId,
                    destinationRegionId = destinationRegion,
                    destinationMapUniqueId = journal.destinationMapUniqueId,
                    originCell = new IntVec3(journal.sourceCellX, 0, journal.sourceCellZ),
                    inverseReturnEdge = RealityAdjacentPolicy.InverseEdge(journal.edge),
                    outboundTransferId = journal.transferId,
                    returnTransferId = "return:" + journal.excursionId,
                    startTick = journal.createdTick,
                    graceDeadline = now + RealityAdjacentPolicy.DefaultGraceTicks
                };
                if (pawn != null && pawnMap == destinationMap)
                {
                    if (!world.BeginExcursion(request, out _, out string beginDiagnostic))
                    {
                        world.Quarantine("excursion", journal.excursionId, journal.providerId,
                            beginDiagnostic ?? "A completed outbound journal could not acquire unique Pawn ownership.", pawnId);
                        continue;
                    }
                    if (!world.CommitOutboundExcursion(journal.excursionId, new RealityAdjacentTransferRequest
                    {
                        providerId = request.providerId,
                        excursionId = request.excursionId,
                        isOutboundExcursion = true,
                        sourceMap = sourceMap,
                        destinationMap = destinationMap,
                        sourceRegionId = sourceRegion,
                        destinationRegionId = destinationRegion,
                        sourceCell = request.originCell,
                        entryEdge = journal.edge,
                        pawns = new[] { pawn },
                        transferId = journal.transferId
                    }, journal, out string commitDiagnostic))
                    {
                        world.Quarantine("excursion", journal.excursionId, journal.providerId,
                            commitDiagnostic ?? "A completed outbound journal could not be promoted to a durable ticket.", pawnId);
                    }
                }
                else if (pawn != null && pawnMap == sourceMap)
                {
                    world.RecoverCompletedExcursion(request, pawn,
                        "Completed outbound journal reconciled after the Pawn was already on the origin map.", out _);
                }
                else if (inStorage)
                {
                    world.Quarantine("excursion", journal.excursionId, journal.providerId,
                        "A completed excursion journal points to world-pawn or caravan storage; ownership was not guessed.", pawnId);
                }
            }
        }

        private static Pawn FindPawnAnywhere(string loadId, out Map map, out bool inCaravanOrWorldPawns,
            out bool ambiguousOwnership)
        {
            map = null;
            inCaravanOrWorldPawns = false;
            ambiguousOwnership = false;
            if (string.IsNullOrEmpty(loadId)) return null;
            Pawn found = null;
            Map foundMap = null;
            bool foundInWorldStorage = false;
            bool ambiguous = false;
            Action<Pawn, Map, bool> consider = (pawn, candidateMap, worldStorage) =>
            {
                if (pawn == null || pawn.GetUniqueLoadID() != loadId) return;
                if (found == null)
                {
                    found = pawn;
                    foundMap = candidateMap;
                    foundInWorldStorage = worldStorage;
                    return;
                }
                if (!ReferenceEquals(found, pawn))
                {
                    ambiguous = true;
                    return;
                }
                // A spawned map location is authoritative over a duplicate storage enumeration
                // of the same Pawn instance during load reconciliation.
                if (candidateMap != null && foundMap == null)
                {
                    foundMap = candidateMap;
                    foundInWorldStorage = false;
                }
            };
            foreach (Map candidate in Find.Maps?.OrderBy(value => value.uniqueID) ?? Enumerable.Empty<Map>())
            {
                foreach (Pawn pawn in candidate?.mapPawns?.AllPawnsSpawned ?? Enumerable.Empty<Pawn>())
                    consider(pawn, candidate, false);
                foreach (Pawn pawn in candidate?.listerThings?.AllThings?.OfType<Pawn>() ?? Enumerable.Empty<Pawn>())
                    consider(pawn, candidate, false);
            }
            foreach (Pawn worldPawn in Find.WorldPawns?.AllPawnsAlive ?? Enumerable.Empty<Pawn>())
                consider(worldPawn, null, true);
            foreach (Pawn caravanPawn in Find.WorldObjects?.AllWorldObjects?.OfType<Caravan>()
                .SelectMany(caravan => caravan.PawnsListForReading ?? new List<Pawn>()) ?? Enumerable.Empty<Pawn>())
                consider(caravanPawn, null, true);
            map = foundMap;
            inCaravanOrWorldPawns = foundInWorldStorage;
            ambiguousOwnership = ambiguous;
            return ambiguous ? null : found;
        }

        private static Pawn FindPawnOnMap(Map map, string loadId)
        {
            return map?.mapPawns?.AllPawnsSpawned?.FirstOrDefault(pawn => pawn?.GetUniqueLoadID() == loadId) ??
                map?.listerThings?.AllThings?.OfType<Pawn>().FirstOrDefault(pawn => pawn?.GetUniqueLoadID() == loadId);
        }

        private static bool JournalMatchesRequest(RealityTransferJournalRecord journal, RealityAdjacentTransferRequest request)
        {
            if (journal == null || request == null || request.sourceMap == null || request.destinationMap == null) return false;
            string pawnIds = string.Join(",", (request.pawns ?? Array.Empty<Pawn>()).Where(pawn => pawn != null)
                .Select(pawn => pawn.GetUniqueLoadID()).ToArray());
            return string.Equals(journal.transferId, request.transferId, StringComparison.Ordinal) &&
                string.Equals(journal.providerId, request.providerId, StringComparison.Ordinal) &&
                string.Equals(journal.excursionId ?? string.Empty, request.excursionId ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(journal.sourceRegionId, request.sourceRegionId.ToString(), StringComparison.Ordinal) &&
                string.Equals(journal.destinationRegionId, request.destinationRegionId.ToString(), StringComparison.Ordinal) &&
                journal.sourceMapUniqueId == request.sourceMap.uniqueID &&
                journal.destinationMapUniqueId == request.destinationMap.uniqueID &&
                string.Equals(journal.edge ?? string.Empty, request.entryEdge ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journal.pawnLoadIds ?? string.Empty, pawnIds, StringComparison.Ordinal);
        }

        private static bool PawnsAreOnMap(IReadOnlyList<Pawn> pawns, Map map)
        {
            return map != null && pawns != null && pawns.Count > 0 && pawns.All(pawn => pawn != null && pawn.Spawned &&
                pawn.Map != null && (ReferenceEquals(pawn.Map, map) || pawn.Map.uniqueID == map.uniqueID));
        }

        private static bool IsUnsafeOrMeaningful(Pawn pawn)
        {
            if (pawn == null || pawn.CarriedBy != null || pawn.Downed || pawn.InMentalState || pawn.Drafted || pawn.GetLord() != null ||
                pawn.CurJob?.playerForced == true) return true;
            return pawn.CurJobDef != null && !IsSafeIdle(pawn);
        }

        private static bool IsSafeIdle(Pawn pawn)
        {
            return pawn != null && RealityAdjacentPolicy.IsSafeIdleJob(pawn.CurJobDef?.defName,
                pawn.CurJobDef != null, pawn.Downed, pawn.InMentalState, pawn.Drafted,
                pawn.GetLord() != null, pawn.CarriedBy != null, pawn.CurJob?.playerForced == true);
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
            if (Find.CurrentMap == map)
                return RecordEvictionVeto(key, "The adjacent map is currently viewed.");
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
                reason = "adjacent-warm-cache-eviction",
                dryRun = false
            };
            IReadOnlyList<RealityVeto> vetoes = RealityCompressionService.CanCompress(world, compressionRequest);
            if (vetoes.Count > 0) return RecordEvictionVeto(key, string.Join("; ", vetoes.Select(veto => veto.ToString()).ToArray()));
            RealityWorldState compressionState = world.CaptureState();
            RealityTransitionResult compression = RealityCompressionService.TryCompress(world, compressionRequest);
            if (!compression.succeeded) return RecordEvictionVeto(key, compression.error ?? "Provider compression failed.");
            try { factory.RemoveMap(map); }
            catch (Exception exception)
            {
                IReadOnlyList<string> compensationErrors = RealityCompressionService.RollbackCommitted(world,
                    compressionRequest, compressionState);
                string diagnostic = "Map factory removal failed: " + exception.Message;
                if (compensationErrors.Count > 0) diagnostic += " | compression compensation: " + string.Join("; ", compensationErrors.ToArray());
                return RecordEvictionVeto(key, diagnostic);
            }
            if (Find.Maps?.Any(candidate => candidate != null &&
                (ReferenceEquals(candidate, map) || candidate.uniqueID == map.uniqueID)) == true)
            {
                IReadOnlyList<string> compensationErrors = RealityCompressionService.RollbackCommitted(world,
                    compressionRequest, compressionState);
                string diagnostic = "The provider factory did not remove the live map.";
                if (compensationErrors.Count > 0) diagnostic += " | compression compensation: " + string.Join("; ", compensationErrors.ToArray());
                return RecordEvictionVeto(key, diagnostic);
            }
            world.UnregisterMap(map);
            world.RetireAdjacentMap(map.uniqueID, "Warm-cache eviction completed through the provider factory.");
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
            EvictionDiagnostics[key] = diagnostic ?? "Eviction was vetoed.";
            return false;
        }

        /// <summary>Removes a deinitialized map from the runtime warm cache.</summary>
        public static void Forget(Map map)
        {
            if (map == null) return;
            foreach (string key in WarmMaps.Where(item => item.Value == map).Select(item => item.Key).ToList())
            {
                WarmMaps.Remove(key);
                WarmMapAccessTicks.Remove(key);
            }
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
            listing.CheckboxLabeled("Enable experimental temporary excursion sites", ref settings.enableAdjacentRegions,
                "Disabled by default; enabled maps are non-buildable work sites. Transfers use ordinary world travel only after exact rollback; unresolved failures remain recoverable.");
            settings.warmMapCacheLimit = Mathf.RoundToInt(listing.SliderLabeled("Warm map cache limit", settings.warmMapCacheLimit, 0, 8, 1f));
            listing.Label("Only marked maps are cached; over-limit entries are removed only after provider compression and real factory eviction succeed.");
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
