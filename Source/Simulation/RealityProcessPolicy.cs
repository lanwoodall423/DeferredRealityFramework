using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;

namespace DeferredReality.Simulation
{
    /// <summary>Pure pause-state transitions shared by the scheduler, controls, and provider lifecycle.</summary>
    public static class RealityProcessPausePolicy
    {
        public static void Normalize(RealityProcessRecord process, bool providerAvailable)
        {
            if (process == null) return;
            if (process.cancelled)
            {
                process.paused = false;
                process.pauseReason = RealityProcessPauseReason.None;
                return;
            }
            if (!process.paused)
            {
                process.pauseReason = RealityProcessPauseReason.None;
                return;
            }
            if (process.pauseReason == RealityProcessPauseReason.None)
                process.pauseReason = providerAvailable
                    ? RealityProcessPauseReason.Manual
                    : RealityProcessPauseReason.ProviderUnavailable;
            if (!Enum.IsDefined(typeof(RealityProcessPauseReason), process.pauseReason))
                process.pauseReason = providerAvailable
                    ? RealityProcessPauseReason.Manual
                    : RealityProcessPauseReason.ProviderUnavailable;
        }

        public static void SetManual(RealityProcessRecord process)
        {
            if (process == null || process.cancelled) return;
            process.paused = true;
            process.pauseReason = RealityProcessPauseReason.Manual;
            process.lastError = null;
        }

        public static void SetProviderUnavailable(RealityProcessRecord process, string error)
        {
            if (process == null || process.cancelled) return;
            process.paused = true;
            process.pauseReason = RealityProcessPauseReason.ProviderUnavailable;
            process.lastError = error;
        }

        public static void SetProviderFailure(RealityProcessRecord process, string error, long retryTick)
        {
            if (process == null || process.cancelled) return;
            process.paused = true;
            process.pauseReason = RealityProcessPauseReason.ProviderFailure;
            process.lastError = error;
            process.nextDueTick = retryTick;
        }

        public static void SetProviderRequested(RealityProcessRecord process, string error, long nextDueTick)
        {
            if (process == null || process.cancelled) return;
            process.paused = true;
            process.pauseReason = RealityProcessPauseReason.ProviderRequested;
            process.lastError = error;
            process.nextDueTick = nextDueTick;
        }

        public static void SetProjectionAuthoritative(RealityProcessRecord process, string error)
        {
            if (process == null || process.cancelled) return;
            process.paused = true;
            process.pauseReason = RealityProcessPauseReason.ProjectionAuthoritative;
            process.lastError = error;
        }

        public static void SetProjectionTransition(RealityProcessRecord process, string error)
        {
            if (process == null || process.cancelled) return;
            process.paused = true;
            process.pauseReason = RealityProcessPauseReason.ProjectionTransition;
            process.lastError = error;
        }

        public static void SetFidelityEscalation(RealityProcessRecord process, string requestId, string error)
        {
            if (process == null || process.cancelled) return;
            process.paused = true;
            process.pauseReason = RealityProcessPauseReason.FidelityEscalation;
            process.pendingEscalationRequestId = requestId;
            process.lastError = error;
        }

        public static bool Resume(RealityProcessRecord process, long nextDueTick)
        {
            if (process == null || process.cancelled) return false;
            process.paused = false;
            process.pauseReason = RealityProcessPauseReason.None;
            process.pendingEscalationRequestId = null;
            process.lastError = null;
            process.nextDueTick = nextDueTick;
            return true;
        }

        public static bool Cancel(RealityProcessRecord process, long cancelledAt)
        {
            if (process == null || process.cancelled) return false;
            process.cancelled = true;
            process.paused = false;
            process.pauseReason = RealityProcessPauseReason.None;
            process.pendingEscalationRequestId = null;
            process.cancelledTick = cancelledAt;
            process.nextDueTick = long.MaxValue;
            return true;
        }

        /// <summary>Reactivates only provider-absence suspensions and preserves all process timing/payload state.</summary>
        public static bool ReactivateUnavailable(RealityProcessRecord process, string providerId)
        {
            if (process == null || process.cancelled || !process.paused ||
                process.pauseReason != RealityProcessPauseReason.ProviderUnavailable ||
                !string.Equals(process.providerId, providerId, StringComparison.Ordinal)) return false;
            process.paused = false;
            process.pauseReason = RealityProcessPauseReason.None;
            process.lastError = null;
            return true;
        }
    }

    /// <summary>Pure scheduler math and ordering seams used by the runtime and tests.</summary>
    public static class RealityProcessScheduling
    {
        public static bool IsRunnable(RealityProcessRecord process, long now)
        {
            return process != null && !process.cancelled && !process.paused && process.nextDueTick <= now;
        }

        public static long EarliestRunnableDue(IEnumerable<RealityProcessRecord> processes)
        {
            long earliest = long.MaxValue;
            if (processes == null) return earliest;
            foreach (RealityProcessRecord process in processes)
                if (process != null && !process.cancelled && !process.paused && process.nextDueTick < earliest)
                    earliest = process.nextDueTick;
            return earliest;
        }

        public static IReadOnlyList<RealityProcessRecord> OrderDue(IEnumerable<RealityProcessRecord> processes, long now)
        {
            return (processes ?? new List<RealityProcessRecord>()).Where(process => IsRunnable(process, now))
                .OrderBy(process => process.nextDueTick)
                .ThenByDescending(process => process.priority)
                .ThenBy(process => process.providerId, StringComparer.Ordinal)
                .ThenBy(process => process.processId, StringComparer.Ordinal).ToList();
        }

        /// <summary>Returns one step for the first due window and one step per fully elapsed interval thereafter.</summary>
        public static int RequestedAnalyticalSteps(long elapsedTicks, int intervalTicks)
        {
            int interval = Math.Max(1, intervalTicks);
            if (elapsedTicks <= 0) return 1;
            long steps = elapsedTicks / interval;
            return (int)Math.Min(int.MaxValue, Math.Max(1L, steps));
        }

        public static int BoundedAnalyticalSteps(long elapsedTicks, int intervalTicks, int maximumAnalyticalSteps)
        {
            return Math.Min(Math.Max(1, maximumAnalyticalSteps), RequestedAnalyticalSteps(elapsedTicks, intervalTicks));
        }
    }
}
