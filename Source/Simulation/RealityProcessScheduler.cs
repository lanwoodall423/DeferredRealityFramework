using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeferredReality.API;

namespace DeferredReality.Simulation
{
    /// <summary>Bounded scheduler settings. Missed time is passed analytically, never replayed tick by tick.</summary>
    public sealed class RealityProcessRunOptions
    {
        /// <summary>Default bounded world-tick budget.</summary>
        public static readonly RealityProcessRunOptions Default = new RealityProcessRunOptions();

        public int maximumProcesses = 8;
        public int maximumAnalyticalSteps = 8;
        public int maximumMilliseconds = 2;
        public int retryDelayTicks = 60000;
    }

    /// <summary>Process execution counters useful to diagnostics and benchmarks.</summary>
    public sealed class RealityProcessRunReport
    {
        public int attempted;
        public int executed;
        public int failed;
        public int paused;
        public int bounded;
        public long elapsedTicks;
        public long wallMilliseconds;
    }

    /// <summary>Deterministic priority-queue style process runner.</summary>
    public static class RealityProcessScheduler
    {
        private static long totalExecutions;
        private static long totalFailures;
        private static long totalBoundedCatchups;

        /// <summary>Total provider execution count since process start.</summary>
        public static long TotalExecutions => totalExecutions;

        /// <summary>Total isolated provider failures.</summary>
        public static long TotalFailures => totalFailures;

        /// <summary>Total times analytical catch-up was bounded.</summary>
        public static long TotalBoundedCatchups => totalBoundedCatchups;

        /// <summary>Runs due processes in deterministic order under a bounded budget.</summary>
        public static RealityProcessRunReport RunDue(DeferredRealityWorldComponent world, long now, RealityProcessRunOptions options)
        {
            if (world == null) return new RealityProcessRunReport();
            RealityThreadGuard.RequireMainThread();
            options = options ?? RealityProcessRunOptions.Default;
            Stopwatch timer = Stopwatch.StartNew();
            var report = new RealityProcessRunReport();
            List<RealityProcessRecord> due = world.ProcessSnapshots().Select(item => item.record)
                .Where(item => item != null && !item.cancelled && !item.paused && item.nextDueTick <= now)
                .OrderBy(item => item.nextDueTick)
                .ThenByDescending(item => item.priority)
                .ThenBy(item => item.providerId, StringComparer.Ordinal)
                .ThenBy(item => item.processId, StringComparer.Ordinal).ToList();
            for (int i = 0; i < due.Count && report.attempted < Math.Max(1, options.maximumProcesses); i++)
            {
                if (timer.ElapsedMilliseconds >= Math.Max(1, options.maximumMilliseconds)) break;
                RealityProcessRecord process = world.ProcessRecord(due[i].processId);
                if (process == null || process.cancelled || process.paused || process.nextDueTick > now) continue;
                report.attempted++;
                if (!RealityProviderRegistry.TryGet(process.providerId, out IRealityProvider registered) || !(registered is IRealityProcessProvider provider))
                {
                    Pause(process, world, "Provider is unavailable; process retained and paused.", options.retryDelayTicks, true);
                    report.paused++;
                    continue;
                }
                long fromTick = process.executionCount > 0 || process.nextDueTick > process.lastExecutionTick
                    ? process.lastExecutionTick : process.nextDueTick;
                long elapsed = Math.Max(0, now - fromTick);
                int interval = Math.Max(1, process.intervalTicks);
                int requestedSteps = elapsed <= 0 ? 1 : 1 + (int)Math.Min(int.MaxValue - 1L, elapsed / interval);
                int boundedSteps = Math.Min(Math.Max(1, options.maximumAnalyticalSteps), Math.Max(1, requestedSteps));
                bool wasBounded = requestedSteps > boundedSteps;
                var execution = new RealityProcessExecution
                {
                    providerId = process.providerId,
                    processId = process.processId,
                    regionId = process.regionId,
                    fromTick = fromTick,
                    toTick = now,
                    elapsedTicks = elapsed,
                    executionCount = process.executionCount,
                    boundedStepCount = boundedSteps,
                    maximumStepCount = Math.Max(1, options.maximumAnalyticalSteps),
                    random = new RealityRandomStream(RealityDeterminism.Seed(world.WorldSeed, process.regionId,
                        process.providerId, process.processId, process.executionCount))
                };
                var vetoes = new List<RealityVeto>();
                bool canExecute;
                try { canExecute = provider.CanExecute(process.Clone(), execution, vetoes); }
                catch (Exception exception)
                {
                    canExecute = false;
                    vetoes.Add(new RealityVeto("provider.exception", exception.Message, process.providerId, 3));
                }
                if (!canExecute || vetoes.Count > 0)
                {
                    string error = vetoes.Count > 0 ? string.Join("; ", vetoes.Select(item => item.ToString()).ToArray()) : "Provider vetoed execution.";
                    Pause(process, world, error, options.retryDelayTicks, false);
                    world.Quarantine("process", process.processId, process.providerId, error, process.payload);
                    report.paused++;
                    continue;
                }
                RealityProcessResult result;
                try { result = provider.Execute(process.Clone(), execution) ?? new RealityProcessResult { succeeded = false, error = "Provider returned no result." }; }
                catch (Exception exception)
                {
                    result = new RealityProcessResult { succeeded = false, error = exception.ToString() };
                }
                if (!result.succeeded)
                {
                    Pause(process, world, result.error ?? "Provider execution failed.", options.retryDelayTicks, false);
                    world.Quarantine("process", process.processId, process.providerId, result.error ?? "Provider execution failed.", process.payload);
                    report.failed++;
                    totalFailures++;
                    continue;
                }
                process.lastExecutionTick = now;
                process.executionCount++;
                process.lastError = null;
                process.paused = result.pause;
                process.cancelled = result.cancel;
                process.nextDueTick = result.cancel ? long.MaxValue : now + Math.Max(1, result.nextDelayTicks >= 0 ? result.nextDelayTicks : interval);
                world.Touch("process.executed", process.providerId, process.regionId, process.processId);
                report.executed++;
                report.elapsedTicks += elapsed;
                report.bounded += wasBounded ? 1 : 0;
                totalExecutions++;
                if (wasBounded) totalBoundedCatchups++;
            }
            report.wallMilliseconds = timer.ElapsedMilliseconds;
            return report;
        }

        private static void Pause(RealityProcessRecord process, DeferredRealityWorldComponent world, string error, int retryDelay, bool pause)
        {
            process.lastError = error;
            process.paused = pause;
            process.nextDueTick = world.Now + Math.Max(1, retryDelay);
            world.Touch("process.paused", process.providerId, process.regionId, process.processId);
        }
    }
}
