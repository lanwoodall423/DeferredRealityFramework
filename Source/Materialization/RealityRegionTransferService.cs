using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeferredReality.Materialization
{
    /// <summary>Transactional cross-region transfer service. Topology is queried elsewhere; this service changes ownership.</summary>
    public static class RealityRegionTransferService
    {
        private static readonly Dictionary<string, IAdjacentRegionTransferHost> ProviderHosts =
            new Dictionary<string, IAdjacentRegionTransferHost>(StringComparer.Ordinal);

        /// <summary>Registers a transfer host under one provider namespace.</summary>
        public static bool RegisterTransferHost(string providerId, IAdjacentRegionTransferHost host)
        {
            if (string.IsNullOrWhiteSpace(providerId) || host == null) return false;
            ProviderHosts[providerId.Trim()] = host;
            return true;
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
                if (RealityRetentionPolicy.IsTerminalExcursion(ticket) ||
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
            IAdjacentRegionTransferHost host = ProviderHosts.TryGetValue(request.providerId ?? string.Empty, out IAdjacentRegionTransferHost scopedHost)
                ? scopedHost : null;
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
                providerTaskId = request.providerTaskId,
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
                RealityProjectionCacheService.Warm(request.destinationRegionId, request.destinationMap);
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
                ? scopedHost : null;
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

        /// <summary>Returns whether a provider-scoped transfer host is available for recovery or excursion work.</summary>
        internal static bool HasHost(string providerId)
        {
            return !string.IsNullOrWhiteSpace(providerId) && ProviderHosts.ContainsKey(providerId.Trim());
        }

        private static bool JournalMatchesRequest(RealityTransferJournalRecord journal, RealityAdjacentTransferRequest request)
        {
            if (journal == null || request == null || request.sourceMap == null || request.destinationMap == null) return false;
            string pawnIds = string.Join(",", (request.pawns ?? Array.Empty<Pawn>()).Where(pawn => pawn != null)
                .Select(pawn => pawn.GetUniqueLoadID()).ToArray());
            return string.Equals(journal.transferId, request.transferId, StringComparison.Ordinal) &&
                string.Equals(journal.providerId, request.providerId, StringComparison.Ordinal) &&
                string.Equals(journal.excursionId ?? string.Empty, request.excursionId ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(journal.providerTaskId ?? string.Empty, request.providerTaskId ?? string.Empty, StringComparison.Ordinal) &&
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

    }

    internal sealed class RealityTransitionHostException : Exception
    {
        internal RealityTransitionHostException(string message) : base(message) { }
    }
}
