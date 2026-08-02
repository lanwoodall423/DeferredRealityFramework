using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;
using DeferredReality.Diagnostics;
using DeferredReality.Materialization;
using DeferredReality.Simulation;
using RimWorldDevBridge;
using Verse;

namespace DeferredReality.BridgeAdapter
{
    /// <summary>Optional typed DevBridge adapter. The gameplay framework does not require DevBridge.</summary>
    public sealed class DeferredRealityBridgeAdapterProvider : IBridgeAdapterProvider
    {
        public BridgeAdapterMetadata Metadata { get; } = new BridgeAdapterMetadata
        {
            Id = "lan.deferredreality.framework",
            DisplayName = "Deferred Reality Framework",
            Version = "1",
            Generation = "typed-v3",
            ChangeSummary = "Region, process, audit, deterministic snapshot, and conservative transition diagnostics."
        };

        public IEnumerable<BridgeCommandDescriptor> Commands => new[]
        {
            Descriptor("DEFERRED_REALITY", "Compact framework status and cached snapshot.", false, BridgeCommandMode.PureRead, BridgeCostClass.Normal),
            Descriptor("DR_REGIONS", "List stable regions, fidelity, active-map links, and observation levels.", false, BridgeCommandMode.PureRead, BridgeCostClass.Normal),
            Descriptor("DR_PROCESSES", "List scheduled processes and next due ticks.", false, BridgeCommandMode.PureRead, BridgeCostClass.Normal),
            Descriptor("DR_DUMP", "Dump a concise deterministic latent snapshot.", false, BridgeCommandMode.PureRead, BridgeCostClass.Normal),
            Descriptor("DR_AUDIT", "Run a read-only stable-ID and reference audit.", false, BridgeCommandMode.PureRead, BridgeCostClass.Normal),
            Descriptor("DR_COMPRESSION_DRY_RUN", "Collect conservative compression vetoes for the current map.", true, BridgeCommandMode.PureRead, BridgeCostClass.Normal),
            Descriptor("DR_SIMULATE_DAY", "Run one bounded day of analytical processes.", true, BridgeCommandMode.TemporaryTestMutation, BridgeCostClass.Simulation)
        };

        public BridgeResult Execute(BridgeExecutionContext context)
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null) return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "world_missing");
            string command = (context?.Request?.Command ?? string.Empty).ToUpperInvariant();
            var result = BridgeResult.Ok("deferred-reality.result");
            result.Provider = Metadata.Id;
            result.ProviderVersion = Metadata.Version;
            switch (command)
            {
                case "DEFERRED_REALITY":
                    RealityDiagnosticsSnapshot summary = RealityDiagnostics.Snapshot(world);
                    result.Add("revision", summary.revision).Add("tick", summary.tick).Add("regions", summary.regions)
                        .Add("populations", summary.populations).Add("anchors", summary.anchors).Add("constraints", summary.constraints)
                        .Add("processes", summary.processes).Add("conflicts", summary.conflicts).Add("quarantine", summary.quarantine);
                    break;
                case "DR_REGIONS":
                    foreach (RealityRegionSnapshot region in world.RegionSnapshots())
                        result.AddLine(region.id + " | " + region.fidelity + " | observed=" + region.observationLevel + " | map=" + region.activeMapUniqueId);
                    break;
                case "DR_PROCESSES":
                    foreach (RealityProcessSnapshot process in world.ProcessSnapshots())
                        result.AddLine(process.record.processId + " | " + process.record.providerId + " | due=" + process.record.nextDueTick +
                            " | paused=" + process.record.paused + " | executions=" + process.record.executionCount);
                    break;
                case "DR_DUMP":
                    foreach (string line in RealityDiagnostics.Dump(world).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)) result.AddLine(line);
                    break;
                case "DR_AUDIT":
                    RealityAuditReport audit = RealityAuditService.Audit(world);
                    result.Add("passed", audit.Passed).Add("errors", audit.errors.Count).Add("warnings", audit.warnings.Count);
                    foreach (string line in audit.errors.Concat(audit.warnings)) result.AddLine(line);
                    break;
                case "DR_COMPRESSION_DRY_RUN":
                    IReadOnlyList<RealityVeto> vetoes = RealityCompressionService.CanCompress(world,
                        new RealityCompressionRequest { Map = context.Map, regionId = RealityRegionMappingService.ForMap(context.Map), now = world.Now, dryRun = true });
                    foreach (RealityVeto veto in vetoes) result.AddLine(veto.ToString());
                    result.Add("vetoes", vetoes.Count);
                    break;
                case "DR_SIMULATE_DAY":
                    RealityProcessRunReport report = RealityProcessScheduler.RunDue(world, world.Now + 60000,
                        new RealityProcessRunOptions { maximumProcesses = 128, maximumAnalyticalSteps = 32, maximumMilliseconds = 500 });
                    result.Add("attempted", report.attempted).Add("executed", report.executed).Add("failed", report.failed).Add("bounded", report.bounded);
                    break;
                default:
                    return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "command_not_found");
            }
            return result;
        }

        private static BridgeCommandDescriptor Descriptor(string name, string description, bool requiresMap,
            BridgeCommandMode mode, BridgeCostClass cost)
        {
            return new BridgeCommandDescriptor
            {
                Name = name,
                Description = description,
                Provider = "lan.deferredreality.framework",
                ProviderVersion = "1",
                Mode = mode,
                Cost = cost,
                RequiresMap = requiresMap,
                ArgumentSchema = "none",
                ResultSchema = "deferred-reality.result",
                SchemaVersion = 1
            };
        }
    }
}
