using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;
using DeferredReality.Diagnostics;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DeferredReality.Materialization
{
    /// <summary>Opt-in experimental cardinal surface connections and safe transfer facade.</summary>
    public static class RealityAdjacentSurfaceService
    {
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
                        string diagnostic = "Marked adjacent map is not currently materialized; role retained for recovery.";
                        RealityProjectionCacheService.RememberEvictionDiagnostic(marker.regionId, diagnostic);
                        world.RecordAdjacentDiagnostic("map-unloaded", marker.providerId, marker.regionId, diagnostic);
                        continue;
                    }
                    RealityProjectionCacheService.TrackWarm(map, marker.lastAccessTick);
                    if (RealityProjectionCacheService.TryGetEvictionDiagnostic(marker.regionId, out string unloadedDiagnostic) &&
                        unloadedDiagnostic.StartsWith("Marked adjacent map is not currently materialized", StringComparison.Ordinal))
                        world.RecordAdjacentDiagnostic("map-unloaded", marker.providerId, marker.regionId,
                            unloadedDiagnostic, now, true);
                    RealityAdjacentConstructionGuards.RemovePlayerConstructionArtifacts(map, world);
                }
                catch (Exception exception) { RecordMonitorFailure(world, marker.regionId, exception); }
            }
            foreach (RealityExcursionTicket ticket in world.ExcursionSnapshots())
            {
                if (RealityRetentionPolicy.IsTerminalExcursion(ticket))
                {
                    foreach (IRealityExcursionTaskCleanupProvider cleanup in RealityProviderRegistry.OfType<IRealityExcursionTaskCleanupProvider>())
                    {
                        IRealityProvider provider = cleanup as IRealityProvider;
                        if (provider?.Registration?.providerId != ticket.providerId) continue;
                        try { cleanup.ForgetExcursionTask(ticket); }
                        catch (Exception exception) { RecordMonitorFailure(world, "task-cleanup", exception); }
                    }
                    continue;
                }
                try { MonitorExcursion(world, ticket, now); }
                catch (Exception exception) { RecordMonitorFailure(world, ticket?.excursionId, exception); }
            }
            try { RealityProjectionCacheService.TryEvictWarmMaps(world, now); }
            catch (Exception exception) { RecordMonitorFailure(world, "eviction", exception); }
        }

        private static void RecordMonitorFailure(DeferredRealityWorldComponent world, string key, Exception exception)
        {
            string diagnostic = "Adjacent safety monitoring failed: " + exception.Message;
            RealityProjectionCacheService.RememberEvictionDiagnostic(key ?? "monitor", diagnostic);
            world?.RecordAdjacentDiagnostic("monitor-failure", "core", key, diagnostic);
            try { world?.Quarantine("adjacent-monitor", key, "core", diagnostic, exception.GetType().FullName); }
            catch { Log.Error("[DeferredReality] " + diagnostic); }
        }

        private static void MonitorExcursion(DeferredRealityWorldComponent world, RealityExcursionTicket ticket, long now)
        {
            if (ticket == null || RealityRetentionPolicy.IsTerminalExcursion(ticket) ||
                ticket.status == RealityExcursionStatus.Quarantined || ticket.retryTick > now) return;
            bool returning = ticket.status == RealityExcursionStatus.Returning;
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
            bool providerTaskActive = false;
            bool taskCompleted = ticket.status == RealityExcursionStatus.ReturnRequested ||
                ticket.status == RealityExcursionStatus.Cancelled;
            if (RealityProviderRegistry.TryGetCapability(ticket.providerId, out IRealityExcursionTaskProvider taskProvider) &&
                !string.IsNullOrEmpty(ticket.taskId))
            {
                if (taskProvider.TryObserveExcursionTask(ticket, now, out RealityExcursionTaskObservation observation) && observation != null)
                {
                    if (!string.IsNullOrEmpty(observation.taskId) && observation.taskId != ticket.taskId)
                    {
                        world.SetExcursionDiagnostic(ticket.excursionId,
                            "The provider returned task evidence for a different task; evidence was ignored.",
                            now + RealityAdjacentPolicy.RetryBackoffTicks);
                    }
                    else if (observation.completed)
                    {
                        if (!returning)
                            world.CompleteExcursion(ticket.excursionId, observation.diagnostic ?? "The provider task completed.");
                        taskCompleted = true;
                    }
                    else if (observation.abandoned)
                    {
                        if (!returning)
                            world.CancelExcursion(ticket.excursionId, observation.diagnostic ?? "The provider task was abandoned.");
                        taskCompleted = true;
                    }
                    else if (observation.active && RealityAdjacentPolicy.IsFreshTaskEvidence(observation.evidenceTick, now))
                    {
                        providerTaskActive = true;
                        if (observation.evidenceTick > ticket.lastTaskHeartbeat)
                            world.HeartbeatExcursion(ticket.excursionId, now, RealityAdjacentPolicy.DefaultLeaseTicks,
                                observation.diagnostic ?? "Provider task activity observed.");
                    }
                }
            }
            if (!RealityAdjacentPolicy.IsReturnDue(ticket, now, taskCompleted, safelyIdle, unsafeState, providerTaskActive))
            {
                if (unsafeState) world.SetExcursionDiagnostic(ticket.excursionId,
                    "Return is waiting for the Pawn to leave combat, medical, drafted, carried, or provider work state.",
                    now + RealityAdjacentPolicy.RetryBackoffTicks);
                return;
            }
            if (!RealityProviderRegistry.TryGet(ticket.providerId, out _) ||
                !RealityRegionTransferService.HasHost(ticket.providerId))
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
            if (returning)
            {
                RealityExcursionReturnDisposition disposition = RealityAdjacentPolicy.EvaluateReturn(
                    RealityProviderRegistry.TryGetCapability(ticket.providerId, out IRealityExcursionReturnGate gate)
                        ? gate : null, ticket, now, out string readinessDiagnostic);
                if (disposition != RealityExcursionReturnDisposition.Ready)
                {
                    world.SetExcursionDiagnostic(ticket.excursionId,
                        string.IsNullOrEmpty(readinessDiagnostic)
                            ? "The provider return gate is pending; inverse transfer remains deferred."
                            : "The provider return gate is pending: " + readinessDiagnostic,
                        now + RealityAdjacentPolicy.RetryBackoffTicks);
                    return;
                }
            }
            if (pawn.CurJob != null && IsSafeIdle(pawn)) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (!returning)
            {
                if (!world.MarkExcursionReturning(ticket.excursionId,
                    now + RealityAdjacentPolicy.RetryBackoffTicks, "Return transfer boundary entered; awaiting the next monitor pass.")) return;
                return;
            }
            RealityAdjacentTransferResult result = RealityRegionTransferService.TryTransfer(new RealityAdjacentTransferRequest
            {
                    providerId = ticket.providerId,
                    excursionId = ticket.excursionId,
                    providerTaskId = ticket.taskId,
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
                RealityAdjacentTransferResult recovery = RealityRegionTransferService.RecoverTransfer(new RealityAdjacentTransferRequest
                {
                    providerId = journal.providerId ?? destinationRegion.ProviderNamespace,
                    excursionId = journal.excursionId,
                    providerTaskId = journal.providerTaskId,
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
                        providerTaskId = request.taskId,
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

    }

}
