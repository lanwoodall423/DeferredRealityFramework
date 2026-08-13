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
            List<RealityProcessRecord> due = world.DueProcessRecords(now);
            for (int i = 0; i < due.Count && report.attempted < Math.Max(1, options.maximumProcesses); i++)
            {
                if (timer.ElapsedMilliseconds >= Math.Max(1, options.maximumMilliseconds)) break;
                RealityProcessRecord process = world.ProcessRecord(due[i].processId);
                if (process == null || process.cancelled || process.paused || process.nextDueTick > now) continue;
                report.attempted++;
                if (RealityRegionId.TryParse(process.regionId, out RealityRegionId processRegion) &&
                    world.TryGetRegion(processRegion, out RealityRegionSnapshot regionSnapshot))
                {
                    // Provider resolution happens before projection gating so a provider can declare
                    // whether a narrowly scoped process is legal while a live Map is authoritative.
                }
                if (!RealityProviderRegistry.TryGetCapability(process.providerId, out IRealityProcessProvider provider))
                {
                    RealityProcessPausePolicy.SetProviderUnavailable(process,
                        "Provider is unavailable; process retained and suspended.");
                    world.RefreshProcessScheduleCache();
                    world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                    report.paused++;
                    continue;
                }
                RealityRegionSnapshot currentRegion = null;
                if (RealityRegionId.TryParse(process.regionId, out RealityRegionId parsedRegion) &&
                    world.TryGetRegion(parsedRegion, out RealityRegionSnapshot resolvedRegion)) currentRegion = resolvedRegion;
                RealityProcessFidelityPolicy fidelityPolicy = null;
                if (currentRegion != null)
                {
                    try { fidelityPolicy = provider.DescribeProcessFidelity(process.Clone(), currentRegion); }
                    catch (Exception exception)
                    {
                        RealityProcessPausePolicy.SetProviderFailure(process,
                            "Provider fidelity policy failed: " + exception.Message,
                            now + Math.Max(1, options.retryDelayTicks));
                        world.RefreshProcessScheduleCache();
                        world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                        world.Quarantine("process.fidelity-policy", process.processId, process.providerId,
                            exception.Message, process.payload);
                        report.paused++;
                        continue;
                    }
                    fidelityPolicy = fidelityPolicy ?? new RealityProcessFidelityPolicy();
                    bool legalAtCurrentFidelity = fidelityPolicy.IsLegalAt(currentRegion.fidelity);
                    if (currentRegion.authority == RealityRegionAuthority.LiveProjection &&
                        (!fidelityPolicy.runsWhileLiveProjection || !legalAtCurrentFidelity))
                    {
                        RealityProcessPausePolicy.SetProjectionAuthoritative(process,
                            "Aggregate simulation is suspended while the live Map projection is authoritative.");
                        world.RefreshProcessScheduleCache();
                        world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                        report.paused++;
                        continue;
                    }
                    if (currentRegion.authority != RealityRegionAuthority.Latent &&
                        currentRegion.authority != RealityRegionAuthority.LiveProjection)
                    {
                        RealityProcessPausePolicy.SetProjectionTransition(process,
                            "Aggregate simulation is suspended while the region projection is transitioning.");
                        world.RefreshProcessScheduleCache();
                        world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                        report.paused++;
                        continue;
                    }
                    if (!legalAtCurrentFidelity)
                    {
                        if (fidelityPolicy.mayRequestEscalation &&
                             RealityFidelityRules.IsHigher(fidelityPolicy.escalationTarget, currentRegion.fidelity))
                        {
                            var escalation = new RealityProcessEscalationRequest
                            {
                                requestedFidelity = fidelityPolicy.escalationTarget,
                                reason = RealityFidelityEscalationReason.InsufficientResolution,
                                disposition = RealityFidelityEscalationDisposition.Request,
                                policy = fidelityPolicy.escalationPolicy
                            };
                            RealityFidelityEscalationRecord request = world.RequestFidelityEscalation(process, escalation,
                                currentRegion, now);
                            if (request == null ||
                                (request.status != RealityFidelityEscalationStatus.Pending &&
                                 request.status != RealityFidelityEscalationStatus.Approved))
                            {
                                RealityProcessPausePolicy.SetProviderFailure(process,
                                    "The provider's fidelity escalation could not be persisted as pending.",
                                    now + Math.Max(1, options.retryDelayTicks));
                                world.RefreshProcessScheduleCache();
                                world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                                world.Quarantine("process.fidelity-escalation", process.processId, process.providerId,
                                    "Policy escalation request was already resolved or could not be persisted.", process.payload);
                                report.failed++;
                                totalFailures++;
                                continue;
                            }
                            RealityProcessPausePolicy.SetFidelityEscalation(process, request?.requestId,
                                "Process requires higher fidelity before it can continue.");
                            world.RefreshProcessScheduleCache();
                            world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                            report.paused++;
                            continue;
                        }
                        RealityProcessPausePolicy.SetProviderRequested(process,
                            "Process is not legal at the region's current fidelity and declared no escalation path.", now);
                        world.RefreshProcessScheduleCache();
                        world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                        report.paused++;
                        continue;
                    }
                }
                long fromTick = process.executionCount > 0 || process.nextDueTick > process.lastExecutionTick
                    ? process.lastExecutionTick : process.nextDueTick;
                long elapsed = Math.Max(0, now - fromTick);
                int interval = Math.Max(1, process.intervalTicks);
                int requestedSteps = RealityProcessScheduling.RequestedAnalyticalSteps(elapsed, interval);
                int boundedSteps = RealityProcessScheduling.BoundedAnalyticalSteps(elapsed, interval, options.maximumAnalyticalSteps);
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
                    RealityProcessPausePolicy.SetProviderFailure(process, error,
                        now + Math.Max(1, options.retryDelayTicks));
                    world.RefreshProcessScheduleCache();
                    world.Touch("process.paused", process.providerId, process.regionId, process.processId);
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
                    string error = result.error ?? "Provider execution failed.";
                    RealityProcessPausePolicy.SetProviderFailure(process, error,
                        now + Math.Max(1, options.retryDelayTicks));
                    world.RefreshProcessScheduleCache();
                    world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                    world.Quarantine("process", process.processId, process.providerId, error, process.payload);
                    report.failed++;
                    totalFailures++;
                    continue;
                }
                if (result.escalation != null)
                {
                    if (!result.escalation.IsValid)
                    {
                        RealityProcessPausePolicy.SetProviderFailure(process,
                            "Provider returned an invalid typed fidelity escalation request.",
                            now + Math.Max(1, options.retryDelayTicks));
                        world.RefreshProcessScheduleCache();
                        world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                        world.Quarantine("process.fidelity-escalation", process.processId, process.providerId,
                            "Invalid typed escalation request.", process.payload);
                        report.failed++;
                        totalFailures++;
                        continue;
                    }
                    if (currentRegion == null)
                    {
                        RealityProcessPausePolicy.SetProviderFailure(process,
                            "Provider requested fidelity escalation for an unregistered region.",
                            now + Math.Max(1, options.retryDelayTicks));
                        world.RefreshProcessScheduleCache();
                        world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                        world.Quarantine("process.fidelity-escalation", process.processId, process.providerId,
                            "Typed escalation could not be linked to a registered region.", process.payload);
                        report.failed++;
                        totalFailures++;
                        continue;
                    }
                    RealityFidelityEscalationRecord escalation = world.RequestFidelityEscalation(process,
                        result.escalation, currentRegion, now);
                    if (result.escalation.disposition == RealityFidelityEscalationDisposition.Request)
                    {
                        if (escalation == null ||
                            (escalation.status != RealityFidelityEscalationStatus.Pending &&
                             escalation.status != RealityFidelityEscalationStatus.Approved))
                        {
                            RealityProcessPausePolicy.SetProviderFailure(process,
                                "Provider escalation request could not be persisted as pending.",
                                now + Math.Max(1, options.retryDelayTicks));
                            world.RefreshProcessScheduleCache();
                            world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                            world.Quarantine("process.fidelity-escalation", process.processId, process.providerId,
                                "Typed escalation request was already resolved or could not be persisted.", process.payload);
                            report.failed++;
                            totalFailures++;
                            continue;
                        }
                        RealityProcessPausePolicy.SetFidelityEscalation(process, escalation?.requestId,
                            "Process requested higher fidelity before resolving this event.");
                        world.RefreshProcessScheduleCache();
                        world.Touch("process.paused", process.providerId, process.regionId, process.processId);
                        report.paused++;
                        continue;
                    }
                }
                process.lastExecutionTick = now;
                process.executionCount++;
                long nextDueTick = now + Math.Max(1, result.nextDelayTicks >= 0 ? result.nextDelayTicks : interval);
                if (result.cancel)
                    RealityProcessPausePolicy.Cancel(process, now);
                else if (result.pause)
                    RealityProcessPausePolicy.SetProviderRequested(process, result.error, nextDueTick);
                else
                {
                    process.paused = false;
                    process.pauseReason = RealityProcessPauseReason.None;
                    process.lastError = null;
                    process.cancelled = false;
                    process.nextDueTick = nextDueTick;
                }
                world.RefreshProcessScheduleCache();
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

    }
}
