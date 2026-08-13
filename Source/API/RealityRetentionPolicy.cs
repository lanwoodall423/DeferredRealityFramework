using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.Simulation;

namespace DeferredReality.API
{
    /// <summary>Pure retention decisions. Unknown or unlisted domains are retained for safety.</summary>
    public static class RealityRetentionPolicy
    {
        public const long DefaultTerminalTransferRetentionTicks = 600000L;
        public const int MaximumTerminalTransferJournals = 256;
        public const long DefaultTerminalExcursionRetentionTicks = 600000L;
        public const int MaximumTerminalExcursions = 1024;
        public const long DefaultRetiredAdjacentMapRetentionTicks = 600000L;
        public const int MaximumRetiredAdjacentMaps = 256;
        public const long DefaultResolvedAdjacentDiagnosticRetentionTicks = 600000L;
        public const int MaximumResolvedAdjacentDiagnostics = 2048;

        public static bool IsInterruptedTransfer(RealityTransferJournalRecord journal)
        {
            return journal != null && !IsTerminalTransfer(journal);
        }

        public static bool IsTerminalTransfer(RealityTransferJournalRecord journal)
        {
            if (journal == null) return false;
            return journal.status == RealityTransferStatus.Completed ||
                journal.status == RealityTransferStatus.RolledBack ||
                journal.status == RealityTransferStatus.Fallback;
        }

        /// <summary>Retains every interrupted journal, recent terminal journals, and deterministic newest terminal overflow.</summary>
        public static IReadOnlyList<RealityTransferJournalRecord> SelectTransferJournals(
            IEnumerable<RealityTransferJournalRecord> journals, long now,
            long retentionTicks = DefaultTerminalTransferRetentionTicks,
            int terminalCap = MaximumTerminalTransferJournals)
        {
            List<RealityTransferJournalRecord> unique = (journals ?? Enumerable.Empty<RealityTransferJournalRecord>())
                .Where(journal => journal != null && !string.IsNullOrEmpty(journal.transferId))
                .GroupBy(journal => journal.transferId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(journal => IsTerminalTransfer(journal) ? 1 : 0)
                    // Recovery-safe input wins over a terminal duplicate, then newest data wins within that state.
                    .ThenByDescending(journal => journal.updatedTick)
                    .ThenBy(journal => journal.sourceRegionId, StringComparer.Ordinal)
                    .ThenBy(journal => journal.destinationRegionId, StringComparer.Ordinal)
                    .ThenBy(journal => journal.edge, StringComparer.Ordinal)
                    .ThenBy(journal => journal.pawnLoadIds, StringComparer.Ordinal)
                    .ThenBy(journal => journal.diagnostic, StringComparer.Ordinal).First())
                .ToList();
            List<RealityTransferJournalRecord> interrupted = unique.Where(journal => IsInterruptedTransfer(journal))
                .OrderByDescending(journal => journal.updatedTick)
                .ThenBy(journal => journal.transferId, StringComparer.Ordinal).ToList();
            long age = Math.Max(0L, retentionTicks);
            List<RealityTransferJournalRecord> terminal = unique.Where(IsTerminalTransfer)
                .Where(journal => journal.updatedTick > now || now - journal.updatedTick < age)
                .OrderByDescending(journal => journal.updatedTick)
                .ThenBy(journal => journal.transferId, StringComparer.Ordinal)
                .Take(Math.Max(0, terminalCap)).ToList();
            return interrupted.Concat(terminal).OrderByDescending(journal => journal.updatedTick)
                .ThenBy(journal => journal.transferId, StringComparer.Ordinal).ToList();
        }

        /// <summary>Operation age is insufficient by itself; a matching persisted watermark must prove replay is impossible.</summary>
        public static bool CanExpireOperation(RealityProviderRegistration registration, RealityAppliedOperation operation,
            IEnumerable<RealityOperationRetentionWatermark> watermarks, long now)
        {
            if (operation == null || registration == null || registration.operationRetentionTicks <= 0 ||
                string.IsNullOrEmpty(operation.providerId) || string.IsNullOrEmpty(operation.kind) ||
                string.IsNullOrEmpty(operation.domainId) || operation.sequence < 0 ||
                !(registration.compactableOperationKinds ?? new List<string>()).Contains(operation.kind, StringComparer.Ordinal))
                return false;
            RealityOperationRetentionWatermark watermark = (watermarks ?? Enumerable.Empty<RealityOperationRetentionWatermark>())
                .FirstOrDefault(item => item != null && item.providerId == operation.providerId &&
                    item.kind == operation.kind && item.domainId == operation.domainId);
            return watermark != null && RealityExactlyOncePolicy.IsProvenExpired(operation.sequence,
                watermark.sequenceMode, watermark.sequenceCursor, watermark.proof, operation.tick, now,
                registration.operationRetentionTicks);
        }

        public static bool IsTerminalExcursion(RealityExcursionTicket ticket)
        {
            return ticket != null && (ticket.status == RealityExcursionStatus.Completed ||
                (ticket.status == RealityExcursionStatus.Cancelled && ticket.terminalTick >= 0));
        }

        /// <summary>Retains all recoverable tickets and deterministic recent terminal history.</summary>
        public static IReadOnlyList<RealityExcursionTicket> SelectExcursions(IEnumerable<RealityExcursionTicket> tickets,
            long now, long retentionTicks = DefaultTerminalExcursionRetentionTicks,
            int terminalCap = MaximumTerminalExcursions)
        {
            List<RealityExcursionTicket> unique = (tickets ?? Enumerable.Empty<RealityExcursionTicket>())
                .Where(ticket => ticket != null && !string.IsNullOrEmpty(ticket.excursionId))
                .GroupBy(ticket => ticket.excursionId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(ticket => IsTerminalExcursion(ticket) ? 1 : 0)
                    .ThenByDescending(TerminalTick).ThenBy(ticket => ticket.providerId, StringComparer.Ordinal).First())
                .ToList();
            List<RealityExcursionTicket> recoverable = unique.Where(ticket => !IsTerminalExcursion(ticket)).ToList();
            long age = Math.Max(0L, retentionTicks);
            List<RealityExcursionTicket> terminal = unique.Where(IsTerminalExcursion)
                .Where(ticket => TerminalTick(ticket) > now || now - TerminalTick(ticket) < age)
                .OrderByDescending(TerminalTick).ThenBy(ticket => ticket.excursionId, StringComparer.Ordinal)
                .Take(Math.Max(0, terminalCap)).ToList();
            return recoverable.Concat(terminal).OrderBy(ticket => ticket.excursionId, StringComparer.Ordinal).ToList();
        }

        public static IReadOnlyList<RealityAdjacentMapRecord> SelectRetiredAdjacentMaps(
            IEnumerable<RealityAdjacentMapRecord> records, long now,
            long retentionTicks = DefaultRetiredAdjacentMapRetentionTicks,
            int terminalCap = MaximumRetiredAdjacentMaps)
        {
            long age = Math.Max(0L, retentionTicks);
            IEnumerable<RealityAdjacentMapRecord> source = records ?? Enumerable.Empty<RealityAdjacentMapRecord>();
            List<RealityAdjacentMapRecord> live = source.Where(record => record != null &&
                record.lifecycle != RealityAdjacentMapLifecycle.Retired).ToList();
            List<RealityAdjacentMapRecord> retired = source.Where(record => record != null &&
                    record.lifecycle == RealityAdjacentMapLifecycle.Retired &&
                    (record.retiredTick > now || now - record.retiredTick < age))
                .OrderByDescending(record => record.retiredTick).ThenBy(record => record.mapUniqueId)
                .Take(Math.Max(0, terminalCap)).ToList();
            return live.Concat(retired).OrderBy(record => record.mapUniqueId).ToList();
        }

        public static IReadOnlyList<RealityAdjacentDiagnosticRecord> SelectResolvedAdjacentDiagnostics(
            IEnumerable<RealityAdjacentDiagnosticRecord> records, long now,
            long retentionTicks = DefaultResolvedAdjacentDiagnosticRetentionTicks,
            int terminalCap = MaximumResolvedAdjacentDiagnostics)
        {
            long age = Math.Max(0L, retentionTicks);
            List<RealityAdjacentDiagnosticRecord> unresolved = (records ?? Enumerable.Empty<RealityAdjacentDiagnosticRecord>())
                .Where(record => record != null && record.resolvedTick < 0).ToList();
            List<RealityAdjacentDiagnosticRecord> resolved = (records ?? Enumerable.Empty<RealityAdjacentDiagnosticRecord>())
                .Where(record => record != null && record.resolvedTick >= 0 &&
                    (record.resolvedTick > now || now - record.resolvedTick < age))
                .OrderByDescending(record => record.resolvedTick).ThenBy(record => record.diagnosticId, StringComparer.Ordinal)
                .Take(Math.Max(0, terminalCap)).ToList();
            return unresolved.Concat(resolved).OrderBy(record => record.diagnosticId, StringComparer.Ordinal).ToList();
        }

        private static long TerminalTick(RealityExcursionTicket ticket)
        {
            return ticket == null ? long.MinValue : ticket.terminalTick >= 0 ? ticket.terminalTick : ticket.lastTaskHeartbeat;
        }

        /// <summary>Finds the deterministic oldest observation without sorting the full history.</summary>
        public static RealityObservationRecord FindOldestObservation(IEnumerable<RealityObservationRecord> observations)
        {
            RealityObservationRecord oldest = null;
            foreach (RealityObservationRecord item in observations ?? Enumerable.Empty<RealityObservationRecord>())
            {
                if (item == null) continue;
                if (oldest == null || item.tick < oldest.tick ||
                    (item.tick == oldest.tick && string.CompareOrdinal(item.observationId, oldest.observationId) < 0))
                    oldest = item;
            }
            return oldest;
        }

        /// <summary>Finds the oldest observation that explicitly permits disappearance.</summary>
        public static RealityObservationRecord FindOldestDiscardableObservation(
            IEnumerable<RealityObservationRecord> observations)
        {
            return FindOldestObservation((observations ?? Enumerable.Empty<RealityObservationRecord>())
                .Where(item => item != null && item.compressionSignificance == RealityCompressionSignificance.Disposable));
        }
    }

    /// <summary>Counts the records removed by one conservative storage compaction pass.</summary>
    public sealed class RealityCompactionReport
    {
        public int quarantineRemoved;
        public int conflictsRemoved;
        public int observationsRemoved;
        public int transferJournalsRemoved;
        public int appliedOperationsRemoved;
        public int cancelledProcessesRemoved;
        public int excursionsRemoved;
        public int retiredAdjacentMapsRemoved;
        public int adjacentDiagnosticsRemoved;

        public int TotalRemoved => quarantineRemoved + conflictsRemoved + observationsRemoved + transferJournalsRemoved +
            appliedOperationsRemoved + cancelledProcessesRemoved + excursionsRemoved +
            retiredAdjacentMapsRemoved + adjacentDiagnosticsRemoved;
    }
}
