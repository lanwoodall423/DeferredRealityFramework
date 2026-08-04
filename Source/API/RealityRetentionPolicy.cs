using System;
using System.Collections.Generic;
using System.Linq;

namespace DeferredReality.API
{
    /// <summary>Pure retention decisions. Unknown or unlisted domains are retained for safety.</summary>
    public static class RealityRetentionPolicy
    {
        public const long DefaultTerminalTransferRetentionTicks = 600000L;
        public const int MaximumTerminalTransferJournals = 256;

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

        public static bool CanExpireOperation(RealityProviderRegistration registration, string kind, long appliedTick, long now)
        {
            if (registration == null || registration.operationRetentionTicks <= 0 ||
                string.IsNullOrEmpty(kind) || appliedTick >= now) return false;
            if (!(registration.compactableOperationKinds ?? new List<string>()).Contains(kind, StringComparer.Ordinal)) return false;
            return now - appliedTick >= registration.operationRetentionTicks;
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
        public int mapAliasesRemoved;

        public int TotalRemoved => quarantineRemoved + conflictsRemoved + observationsRemoved + transferJournalsRemoved +
            appliedOperationsRemoved + cancelledProcessesRemoved + mapAliasesRemoved;
    }
}
