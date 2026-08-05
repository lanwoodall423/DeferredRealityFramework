using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;

namespace DeferredReality.Materialization
{
    /// <summary>Pure bounded-excursion and warm-cache decisions used by the live RimWorld bridge.</summary>
    public static class RealityAdjacentPolicy
    {
        public const int MonitorIntervalTicks = 120;
        public const int DefaultGraceTicks = 600;
        public const int DefaultLeaseTicks = 6000;
        public const int RetryBackoffTicks = 300;

        /// <summary>Pure fallback classification: only no-job and explicit idle jobs are safe to return.</summary>
        public static bool IsSafeIdleJob(string jobDefName, bool hasJob, bool downed, bool mental,
            bool drafted, bool lordOwned, bool carried, bool playerForced)
        {
            if (downed || mental || drafted || lordOwned || carried || playerForced) return false;
            if (!hasJob) return true;
            return string.Equals(jobDefName, "Wait", StringComparison.Ordinal) ||
                string.Equals(jobDefName, "Wait_Wander", StringComparison.Ordinal) ||
                string.Equals(jobDefName, "GotoWander", StringComparison.Ordinal);
        }

        public static bool IsReturnDue(RealityExcursionTicket ticket, long now, bool taskCompleted,
            bool safelyIdle, bool unsafeState, bool providerTaskActive)
        {
            if (ticket == null || ticket.status == RealityExcursionStatus.Completed ||
                ticket.status == RealityExcursionStatus.Quarantined || ticket.retryTick > now) return false;
            if (taskCompleted) return !unsafeState;
            if (ticket.status == RealityExcursionStatus.ReturnRequested || ticket.status == RealityExcursionStatus.Cancelled)
                return !unsafeState;
            if (ticket.status == RealityExcursionStatus.Returning) return !unsafeState && !providerTaskActive;
            bool leaseExpired = now >= ticket.graceDeadline ||
                ticket.lastTaskHeartbeat > 0 && now - ticket.lastTaskHeartbeat >= DefaultLeaseTicks;
            return leaseExpired && safelyIdle && !unsafeState && !providerTaskActive;
        }

        public static string InverseEdge(string edge)
        {
            if (string.IsNullOrEmpty(edge)) return null;
            switch (edge.Trim().ToLowerInvariant())
            {
                case "north": return "south";
                case "south": return "north";
                case "east": return "west";
                case "west": return "east";
                case "northeast": return "southwest";
                case "northwest": return "southeast";
                case "southeast": return "northwest";
                case "southwest": return "northeast";
                default: return edge;
            }
        }

        public static IReadOnlyList<string> SelectWarmEvictions(IEnumerable<KeyValuePair<string, long>> entries, int limit)
        {
            if (entries == null || limit < 0) return Array.Empty<string>();
            List<KeyValuePair<string, long>> ordered = entries.OrderBy(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.Ordinal).ToList();
            return ordered.Take(Math.Max(0, ordered.Count - limit)).Select(item => item.Key).ToList();
        }

        public static bool HasActiveLease(IEnumerable<RealityExcursionTicket> tickets, int mapUniqueId)
        {
            return (tickets ?? Enumerable.Empty<RealityExcursionTicket>()).Any(ticket => ticket != null &&
                ticket.status != RealityExcursionStatus.Completed &&
                (ticket.originMapUniqueId == mapUniqueId || ticket.destinationMapUniqueId == mapUniqueId));
        }

        public static bool IsFreshTaskEvidence(long evidenceTick, long now)
        {
            return evidenceTick >= 0 && evidenceTick <= now && now - evidenceTick <= DefaultLeaseTicks;
        }
    }
}
