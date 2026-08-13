using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DeferredReality.API;
using DeferredReality.Materialization;
using DeferredReality.Simulation;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeferredReality.Diagnostics
{
    /// <summary>Immutable, cached summary used by UI and developer commands.</summary>
    public sealed class RealityDiagnosticsSnapshot
    {
        public readonly int revision;
        public readonly long tick;
        public readonly int regions;
        public readonly int connections;
        public readonly int enabledConnections;
        public readonly int disabledConnections;
        public readonly int quarantinedConnections;
        public readonly int orphanConnections;
        public readonly int populations;
        public readonly int anchors;
        public readonly int constraints;
        public readonly int processes;
        public readonly int observations;
        public readonly int conflicts;
        public readonly int quarantine;
        public readonly int providers;
        public readonly int adjacentMaps;
        public readonly int excursions;
        public readonly int activeExcursions;
        public readonly int historicalExcursions;
        public readonly int mapCreationIntents;
        public readonly int operationWatermarks;
        public readonly int pendingFidelityEscalations;
        public readonly int approvedFidelityEscalations;
        public readonly int failedFidelityEscalations;
        public readonly bool storageMaintenanceDirty;
        public readonly long nextStorageMaintenanceTick;
        public readonly int compactedExcursions;
        public readonly int compactedRetiredAdjacentMaps;
        public readonly int compactedAdjacentDiagnostics;
        public readonly IReadOnlyList<string> rollbackFailures;
        public readonly IReadOnlyList<string> adjacentLines;
        public readonly long totalProcessExecutions;
        public readonly long totalProcessFailures;
        public readonly long boundedCatchups;
        public readonly IReadOnlyList<RealityRegionSnapshot> regionRows;
        public readonly IReadOnlyList<RealityProcessSnapshot> processRows;
        public readonly IReadOnlyList<string> providerLines;

        internal RealityDiagnosticsSnapshot(DeferredRealityWorldComponent world)
        {
            revision = world?.Revision ?? 0;
            tick = world?.Now ?? 0;
            regionRows = world?.RegionSnapshots() ?? Array.Empty<RealityRegionSnapshot>();
            regions = regionRows.Count;
            IReadOnlyList<RealityRegionConnection> connectionRows = world?.ConnectionSnapshots() ?? Array.Empty<RealityRegionConnection>();
            connections = connectionRows.Count;
            enabledConnections = connectionRows.Count(item => item != null && item.IsEnabled);
            disabledConnections = connectionRows.Count(item => item != null &&
                item.lifecycle == RealityRegionConnectionLifecycle.Disabled);
            quarantinedConnections = connectionRows.Count(item => item != null &&
                item.lifecycle == RealityRegionConnectionLifecycle.Quarantined);
            HashSet<string> regionIds = new HashSet<string>(regionRows.Select(item => item.id.ToString()), StringComparer.Ordinal);
            orphanConnections = connectionRows.Count(item => item != null &&
                (!regionIds.Contains(item.sourceRegionId) || !regionIds.Contains(item.destinationRegionId)));
            populations = world?.PopulationSnapshots().Count ?? 0;
            anchors = world?.AnchorSnapshots().Count ?? 0;
            constraints = world?.ConstraintSnapshots().Count ?? 0;
            processRows = world?.ProcessSnapshots() ?? Array.Empty<RealityProcessSnapshot>();
            processes = processRows.Count;
            observations = world?.ObservationSnapshots().Count ?? 0;
            conflicts = world?.ConflictSnapshots().Count ?? 0;
            quarantine = world?.QuarantineSnapshots().Count ?? 0;
            providers = RealityProviderRegistry.Registrations().Count;
            adjacentLines = world == null ? Array.Empty<string>() : RealityProjectionDiagnostics.Lines(world);
            adjacentMaps = world?.AdjacentMapSnapshots().Count ?? 0;
            IReadOnlyList<RealityExcursionTicket> excursionRows = world?.ExcursionSnapshots() ?? Array.Empty<RealityExcursionTicket>();
            excursions = excursionRows.Count;
            activeExcursions = excursionRows.Count(ticket => ticket != null &&
                !RealityRetentionPolicy.IsTerminalExcursion(ticket));
            historicalExcursions = excursions - activeExcursions;
            mapCreationIntents = world?.MapCreationIntentSnapshots().Count ?? 0;
            operationWatermarks = world?.OperationWatermarkSnapshots().Count ?? 0;
            IReadOnlyList<RealityFidelityEscalationRecord> escalationRows =
                world?.FidelityEscalationSnapshots() ?? Array.Empty<RealityFidelityEscalationRecord>();
            pendingFidelityEscalations = escalationRows.Count(item => item != null &&
                item.status == RealityFidelityEscalationStatus.Pending);
            approvedFidelityEscalations = escalationRows.Count(item => item != null &&
                item.status == RealityFidelityEscalationStatus.Approved);
            failedFidelityEscalations = escalationRows.Count(item => item != null &&
                item.status == RealityFidelityEscalationStatus.Failed);
            storageMaintenanceDirty = world?.StorageMaintenanceDirty == true;
            nextStorageMaintenanceTick = world?.NextStorageMaintenanceTick ?? 0;
            RealityCompactionReport compaction = world?.LastCompactionReport ?? new RealityCompactionReport();
            compactedExcursions = compaction.excursionsRemoved;
            compactedRetiredAdjacentMaps = compaction.retiredAdjacentMapsRemoved;
            compactedAdjacentDiagnostics = compaction.adjacentDiagnosticsRemoved;
            rollbackFailures = RealityMaterializationService.LastRollbackFailures;
            totalProcessExecutions = RealityProcessScheduler.TotalExecutions;
            totalProcessFailures = RealityProcessScheduler.TotalFailures;
            boundedCatchups = RealityProcessScheduler.TotalBoundedCatchups;
            providerLines = RealityProviderRegistry.OfType<IRealityDiagnosticsProvider>()
                .SelectMany(provider => SafeLines(provider, world)).Where(line => !string.IsNullOrEmpty(line)).ToList();
        }

        private static IEnumerable<string> SafeLines(IRealityDiagnosticsProvider provider, DeferredRealityWorldComponent world)
        {
            try { return provider.DiagnosticLines(new RealityDiagnosticsContext(world, world?.Now ?? 0)) ?? Enumerable.Empty<string>(); }
            catch (Exception exception) { return new[] { "provider-error=" + exception.Message }; }
        }
    }

    /// <summary>Cached diagnostics facade. UI does not scan Maps or Things.</summary>
    public static class RealityDiagnostics
    {
        private static DeferredRealityWorldComponent cachedWorld;
        private static int cachedRevision = -1;
        private static int cachedProviderRevision = -1;
        private static RealityDiagnosticsSnapshot cached;

        /// <summary>Returns a snapshot invalidated by store or provider revisions.</summary>
        public static RealityDiagnosticsSnapshot Snapshot(DeferredRealityWorldComponent world = null)
        {
            world = world ?? DeferredRealityWorldComponent.Current;
            if (world == null) return null;
            if (cached == null || cachedWorld != world || cachedRevision != world.Revision ||
                cachedProviderRevision != RealityProviderRegistry.Revision)
            {
                cachedWorld = world;
                cachedRevision = world.Revision;
                cachedProviderRevision = RealityProviderRegistry.Revision;
                cached = new RealityDiagnosticsSnapshot(world);
            }
            return cached;
        }

        /// <summary>Returns a concise deterministic text dump for save/load comparisons.</summary>
        public static string Dump(DeferredRealityWorldComponent world = null)
        {
            world = world ?? DeferredRealityWorldComponent.Current;
            if (world == null) return "Deferred Reality: no world";
            var builder = new StringBuilder();
            RealityDiagnosticsSnapshot summary = Snapshot(world);
            builder.Append("revision=").Append(summary.revision).Append(" tick=").Append(summary.tick)
                .Append(" regions=").Append(summary.regions).Append(" connections=").Append(summary.connections)
                .Append(" enabledConnections=").Append(summary.enabledConnections)
                .Append(" disabledConnections=").Append(summary.disabledConnections)
                .Append(" quarantinedConnections=").Append(summary.quarantinedConnections)
                .Append(" orphanConnections=").Append(summary.orphanConnections)
                .Append(" populations=").Append(summary.populations)
                .Append(" anchors=").Append(summary.anchors).Append(" constraints=").Append(summary.constraints)
                .Append(" processes=").Append(summary.processes).Append(" observations=").Append(summary.observations)
                .Append(" conflicts=").Append(summary.conflicts).Append(" quarantine=").Append(summary.quarantine)
                .Append(" adjacentMaps=").Append(summary.adjacentMaps).Append(" excursions=").Append(summary.excursions)
                .Append(" activeExcursions=").Append(summary.activeExcursions)
                .Append(" historicalExcursions=").Append(summary.historicalExcursions)
                .Append(" mapCreationIntents=").Append(summary.mapCreationIntents)
                .Append(" operationWatermarks=").Append(summary.operationWatermarks)
                .Append(" fidelityEscalations=pending:").Append(summary.pendingFidelityEscalations)
                .Append(",approved:").Append(summary.approvedFidelityEscalations)
                .Append(",failed:").Append(summary.failedFidelityEscalations)
                .Append(" maintenanceDirty=").Append(summary.storageMaintenanceDirty)
                .Append(" nextMaintenance=").Append(summary.nextStorageMaintenanceTick).AppendLine();
            builder.Append("compaction|excursions=").Append(summary.compactedExcursions)
                .Append("|retiredAdjacentMaps=").Append(summary.compactedRetiredAdjacentMaps)
                .Append("|adjacentDiagnostics=").Append(summary.compactedAdjacentDiagnostics).AppendLine();
            foreach (string rollbackFailure in summary.rollbackFailures)
                builder.Append("rollback-failure|").Append(rollbackFailure).AppendLine();
            foreach (RealityRegionSnapshot region in summary.regionRows)
                builder.Append(region.id).Append('|').Append(region.fidelity).Append('|').Append(region.observationLevel).AppendLine();
            foreach (RealityRegionConnection connection in world.ConnectionSnapshots())
                builder.Append("connection|").Append(connection.connectionId).Append('|')
                    .Append(connection.sourceRegionId).Append("->").Append(connection.destinationRegionId).Append('|')
                    .Append(connection.direction).Append('|').Append(connection.kind).Append('|')
                    .Append(connection.lifecycle).Append('|').Append(connection.traversalCost.ToString("R", CultureInfo.InvariantCulture))
                    .AppendLine();
            foreach (RealityPopulationSnapshot population in world.PopulationSnapshots())
                builder.Append("population|").Append(population.record.populationId).Append('|')
                    .Append(population.record.amount.ToString("R", CultureInfo.InvariantCulture))
                    .Append('|').Append(population.record.uncertainty.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
            foreach (RealityProcessSnapshot process in summary.processRows)
            {
                builder.Append("process|").Append(process.record.processId).Append('|').Append(process.record.providerId)
                    .Append('|').Append(process.record.paused).Append('|').Append(process.record.pauseReason)
                    .Append('|').Append(process.record.nextDueTick).Append('|').Append(process.record.executionCount)
                    .Append('|').Append(process.record.lastError ?? string.Empty).AppendLine();
            }
            foreach (RealityFidelityEscalationRecord escalation in world.FidelityEscalationSnapshots())
                builder.Append("fidelity-escalation|").Append(escalation.requestId).Append('|')
                    .Append(escalation.regionId).Append('|').Append(escalation.currentFidelity)
                    .Append("->").Append(escalation.requestedFidelity).Append('|').Append(escalation.reason)
                    .Append('|').Append(escalation.status).Append('|').Append(escalation.diagnostic ?? string.Empty)
                    .AppendLine();
            foreach (string line in summary.adjacentLines) builder.Append(line).AppendLine();
            foreach (RealityOperationRetentionWatermark watermark in world.OperationWatermarkSnapshots())
                builder.Append("operation-watermark|").Append(watermark.providerId).Append('|').Append(watermark.kind)
                    .Append('|').Append(watermark.domainId).Append('|').Append(watermark.safeThroughTick)
                    .Append('|').Append(watermark.proof ?? string.Empty).AppendLine();
            return builder.ToString();
        }
    }

    /// <summary>Non-destructive save audit report.</summary>
    public sealed class RealityAuditReport
    {
        public readonly List<string> warnings = new List<string>();
        public readonly List<string> errors = new List<string>();
        public bool Passed => errors.Count == 0;
        public string Text => string.Join("\n", errors.Select(item => "ERROR " + item).Concat(warnings.Select(item => "WARN " + item)).ToArray());
    }

    /// <summary>Validates references, provider availability, and stable IDs.</summary>
    public static class RealityAuditService
    {
        /// <summary>Runs an explicit audit; normal ticks do not call this method.</summary>
        public static RealityAuditReport Audit(DeferredRealityWorldComponent world)
        {
            var report = new RealityAuditReport();
            if (world == null) { report.errors.Add("No world component."); return report; }
            HashSet<string> regionIds = new HashSet<string>(world.RegionSnapshots().Select(item => item.id.ToString()), StringComparer.Ordinal);
            foreach (RealityRegionConnection connection in world.ConnectionSnapshots())
            {
                if (!regionIds.Contains(connection.sourceRegionId) || !regionIds.Contains(connection.destinationRegionId))
                    report.errors.Add("orphan-connection=" + connection.connectionId);
                if (!string.IsNullOrEmpty(connection.ownerNamespace) && connection.ownerNamespace != "core" &&
                    !RealityProviderRegistry.TryGet(connection.ownerNamespace, out _))
                    report.warnings.Add("missing-connection-provider=" + connection.ownerNamespace + "/" + connection.connectionId);
                if (connection.lifecycle == RealityRegionConnectionLifecycle.Disabled)
                    report.warnings.Add("disabled-connection=" + connection.connectionId);
                else if (connection.lifecycle == RealityRegionConnectionLifecycle.Quarantined)
                    report.warnings.Add("quarantined-connection=" + connection.connectionId);
            }
            foreach (RealityPopulationSnapshot population in world.PopulationSnapshots())
            {
                if (!regionIds.Contains(population.record.regionId)) report.errors.Add("orphan-population=" + population.record.populationId);
                if (population.record.amount < 0f || float.IsNaN(population.record.amount)) report.errors.Add("invalid-population=" + population.record.populationId);
                if (!RealityProviderRegistry.TryGet(population.record.providerId, out _)) report.warnings.Add("missing-population-provider=" + population.record.providerId);
            }
            foreach (RealityAnchorSnapshot anchor in world.AnchorSnapshots())
                if (!regionIds.Contains(anchor.record.regionId)) report.errors.Add("orphan-anchor=" + anchor.record.anchorId);
            foreach (RealityProcessSnapshot process in world.ProcessSnapshots())
            {
                if (!RealityProviderRegistry.TryGet(process.record.providerId, out _)) report.warnings.Add("missing-process-provider=" + process.record.providerId);
                if (process.record.paused) report.warnings.Add("paused-process=" + process.record.processId + ":" + process.record.pauseReason);
            }
            foreach (RealityConflictReport conflict in world.ConflictSnapshots()) report.warnings.Add("unresolved-conflict=" + conflict.conflictId);
            foreach (RealityQuarantineRecord record in world.QuarantineSnapshots()) report.warnings.Add("quarantine=" + record.recordType + "/" + record.recordId);
            foreach (RealityAdjacentMapRecord marker in world.AdjacentMapSnapshots())
            {
                if (!RealityProviderRegistry.TryGet(marker.providerId, out _))
                    report.warnings.Add("missing-adjacent-provider=" + marker.providerId + "/" + marker.mapUniqueId);
                if (!RealityRegionId.TryParse(marker.regionId, out _))
                    report.errors.Add("invalid-adjacent-region=" + marker.mapUniqueId);
            }
            foreach (RealityExcursionTicket ticket in world.ExcursionSnapshots())
            {
                if (!RealityProviderRegistry.TryGet(ticket.providerId, out _))
                    report.warnings.Add("missing-excursion-provider=" + ticket.excursionId);
                if (!RealityRetentionPolicy.IsTerminalExcursion(ticket) &&
                    ticket.status != RealityExcursionStatus.Quarantined && ticket.originMapUniqueId < 0)
                    report.errors.Add("invalid-excursion-origin=" + ticket.excursionId);
            }
            foreach (RealityMapCreationIntentRecord intent in world.MapCreationIntentSnapshots())
            {
                if (string.IsNullOrEmpty(intent.transactionId) || string.IsNullOrEmpty(intent.providerId))
                    report.errors.Add("invalid-map-creation-intent=" + (intent.transactionId ?? "null"));
                else if (!RealityProviderRegistry.TryGet(intent.providerId, out _))
                    report.warnings.Add("missing-map-creation-provider=" + intent.providerId + "/" + intent.transactionId);
            }
            return report;
        }
    }

    /// <summary>Compact inspector for region, fidelity, graph, records, and audit state.</summary>
    public sealed class Window_DeferredRealityInspector : Window
    {
        public override Vector2 InitialSize => new Vector2(Mathf.Min(1100f, UI.screenWidth * 0.92f), Mathf.Min(720f, UI.screenHeight * 0.88f));
        private Vector2 scrollPosition;

        /// <inheritdoc />
        public override void DoWindowContents(Rect rect)
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            RealityDiagnosticsSnapshot summary = RealityDiagnostics.Snapshot(world);
            if (world == null || summary == null)
            {
                Widgets.Label(rect, "Deferred Reality is not attached to a world.");
                return;
            }
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f), "Deferred Reality Framework");
            Rect view = new Rect(0f, 0f, rect.width - 24f, 2400f);
            Widgets.BeginScrollView(new Rect(rect.x, rect.y + 30f, rect.width, rect.height - 30f), ref scrollPosition, view);
            float y = 0f;
            Widgets.Label(new Rect(0f, y, view.width, 22f), "Revision " + summary.revision + " | Tick " + summary.tick +
                " | Regions " + summary.regions + " | Connections " + summary.connections + " (enabled " +
                summary.enabledConnections + ", disabled " + summary.disabledConnections + ", quarantined " +
                summary.quarantinedConnections + ") | Populations " + summary.populations +
                " | Anchors " + summary.anchors); y += 24f;
            Widgets.Label(new Rect(0f, y, view.width, 22f), "Constraints " + summary.constraints + " | Processes " + summary.processes +
                " | Observations " + summary.observations + " | Conflicts " + summary.conflicts + " | Quarantine " + summary.quarantine); y += 30f;
            Widgets.Label(new Rect(0f, y, view.width, 22f), "Providers " + summary.providers + " | Process runs " + summary.totalProcessExecutions +
                " | Failures " + summary.totalProcessFailures + " | Bounded catch-ups " + summary.boundedCatchups); y += 30f;
            Widgets.Label(new Rect(0f, y, view.width, 22f), "Adjacent maps " + summary.adjacentMaps + " | Excursions " + summary.excursions +
                " | Creation intents " + summary.mapCreationIntents + " | Construction: " + RealityAdjacentConstructionGuards.RejectionMessage); y += 24f;
            Widgets.Label(new Rect(0f, y, view.width, 22f), "Fidelity escalations pending " + summary.pendingFidelityEscalations +
                " | approved " + summary.approvedFidelityEscalations + " | failed " + summary.failedFidelityEscalations); y += 24f;
            foreach (RealityRegionSnapshot region in world.RegionSnapshots())
            {
                Widgets.Label(new Rect(0f, y, view.width, 22f), region.id + " | " + region.fidelity + " | authority " + region.authority +
                    " | observed " + region.observationLevel + " | projectionMap " + region.projectionMapUniqueId); y += 22f;
            }
            foreach (RealityProcessSnapshot process in summary.processRows)
            {
                Widgets.Label(new Rect(0f, y, view.width, 22f), "process " + process.record.processId + " | " +
                    process.record.providerId + " | " + (process.record.paused ? process.record.pauseReason.ToString() : "Runnable") +
                    " | due " + process.record.nextDueTick + " | runs " + process.record.executionCount); y += 22f;
            }
            foreach (string line in summary.adjacentLines)
            {
                Widgets.Label(new Rect(0f, y, view.width, 22f), line); y += 22f;
            }
            foreach (string line in summary.providerLines)
            {
                Widgets.Label(new Rect(0f, y, view.width, 22f), line); y += 22f;
            }
            Widgets.EndScrollView();
        }
    }

    /// <summary>Developer actions that exercise only explicit framework operations.</summary>
    public static class RealityDebugActions
    {
        private const string Category = "Deferred Reality";

        [DebugAction(Category, "Open Deferred Reality inspector", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void OpenInspector()
        {
            if (DeferredRealityWorldComponent.Current != null) Find.WindowStack.Add(new Window_DeferredRealityInspector());
        }

        [DebugAction(Category, "Audit all stable IDs and references", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void Audit()
        {
            RealityAuditReport report = RealityAuditService.Audit(DeferredRealityWorldComponent.Current);
            Log.Message("[DeferredReality]\n" + (report.Passed ? "PASS" : "FAIL") + "\n" + report.Text);
            Messages.Message(report.Passed ? "Deferred Reality audit passed." : "Deferred Reality audit found issues; see log.",
                report.Passed ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.CautionInput, false);
        }

        [DebugAction(Category, "Dump concise latent snapshot", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpSnapshot() => Log.Message(RealityDiagnostics.Dump());

        [DebugAction(Category, "Simulate one day of scheduled processes", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void SimulateDay() => Simulate(60000);

        [DebugAction(Category, "Simulate one quadrum of scheduled processes", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void SimulateQuadrum() => Simulate(900000);

        [DebugAction(Category, "Simulate one year of scheduled processes", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void SimulateYear() => Simulate(3600000);

        [DebugAction(Category, "Attempt compression dry-run", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void CompressionDryRun()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            if (!RealityRegionMappingService.TryFromProjectionMapId(map.uniqueID, out RealityRegionId regionId))
            {
                Log.Message("[DeferredReality][Compression] compression.no-region: The current map has no registered live projection binding.");
                return;
            }
            var request = new RealityCompressionRequest { Map = map, regionId = regionId, now = Find.TickManager.TicksGame, dryRun = true };
            IReadOnlyList<RealityVeto> vetoes = RealityCompressionService.CanCompress(DeferredRealityWorldComponent.Current, request);
            Log.Message("[DeferredReality][Compression] " + string.Join("; ", vetoes.Select(item => item.ToString()).ToArray()));
        }

        [DebugAction(Category, "Dump adjacent excursion diagnostics", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpAdjacentDiagnostics()
        {
            Log.Message("[DeferredReality][Adjacent]\n" + string.Join("\n",
                RealityProjectionDiagnostics.Lines(DeferredRealityWorldComponent.Current).ToArray()));
        }

        [DebugAction(Category, "Monitor adjacent excursion safety now", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void MonitorAdjacent()
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null) return;
            RealityAdjacentSurfaceService.Monitor(world, world.Now);
            Messages.Message("Adjacent excursion safety monitor ran; inspect the diagnostics dump for blockers.",
                MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction(Category, "Attempt adjacent warm-map eviction", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void EvictAdjacent()
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null) return;
            Log.Message("[DeferredReality][Adjacent eviction]\n" + string.Join("\n",
                RealityProjectionCacheService.TryEvictWarmMaps(world, world.Now).ToArray()));
        }

        [DebugAction(Category, "Heartbeat selected adjacent excursion", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void HeartbeatSelectedAdjacentExcursion()
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            RealityExcursionTicket ticket = world?.ExcursionSnapshots()
                .Where(item => !RealityRetentionPolicy.IsTerminalExcursion(item))
                .OrderBy(item => item.excursionId, StringComparer.Ordinal).FirstOrDefault();
            bool passed = ticket != null && world.HeartbeatExcursion(ticket.excursionId, world.Now,
                RealityAdjacentPolicy.DefaultLeaseTicks, "Provider integration heartbeat.");
            Log.Message("[DeferredReality][Adjacent heartbeat] " + (passed ? "PASS" : "FAIL") +
                "; provider=" + (ticket?.providerId ?? "none") + "; task=" + (ticket?.taskId ?? "none"));
        }

        [DebugAction(Category, "Request return for selected adjacent excursion", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RequestReturnForSelectedAdjacentExcursion()
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            RealityExcursionTicket ticket = world?.ExcursionSnapshots()
                .Where(item => !RealityRetentionPolicy.IsTerminalExcursion(item))
                .OrderBy(item => item.excursionId, StringComparer.Ordinal).FirstOrDefault();
            bool passed = ticket != null && world.RequestImmediateReturn(ticket.excursionId,
                "Provider integration requested return.");
            Log.Message("[DeferredReality][Adjacent return] " + (passed ? "PASS" : "DEFERRED") +
                "; provider=" + (ticket?.providerId ?? "none") + "; task=" + (ticket?.taskId ?? "none"));
        }

        [DebugAction(Category, "Run provider-neutral adjacent release checks", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAdjacentReleaseChecks()
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            Map ordinaryMap = world == null ? null : Find.Maps?.FirstOrDefault(map => map != null && !world.IsAdjacentMap(map));
            var checks = new List<string>();
            Action<bool, string> check = (passed, name) => checks.Add((passed ? "PASS: " : "FAIL: ") + name);
            check(world != null, "world component exists");
            if (world != null)
            {
                IReadOnlyList<RealityAdjacentMapRecord> markers = world.AdjacentMapSnapshots();
                check(markers.All(marker => marker != null && !string.IsNullOrEmpty(marker.providerId)),
                    "adjacent markers have explicit provider owners");
                check(markers.Where(marker => marker.lifecycle == RealityAdjacentMapLifecycle.Active).All(marker =>
                    Find.Maps?.Any(map => map != null && map.uniqueID == marker.mapUniqueId && RealityAdjacentConstructionGuards.IsBlocked(map)) == true),
                    "active adjacent maps are non-buildable");
                check(ordinaryMap == null || !RealityAdjacentConstructionGuards.IsBlocked(ordinaryMap),
                    "ordinary maps remain unaffected");
                check(world.ExcursionSnapshots().Where(ticket => !RealityRetentionPolicy.IsTerminalExcursion(ticket))
                    .GroupBy(ticket => ticket.pawnLoadId, StringComparer.Ordinal).All(group => group.Count() == 1),
                    "nonterminal Pawn ownership is unique");
                check(!world.HasAdjacentSafetyWork || world.ExcursionSnapshots().Any(ticket => !RealityRetentionPolicy.IsTerminalExcursion(ticket)) ||
                    world.MapCreationIntentSnapshots().Count > 0 || world.AdjacentMapSnapshots().Any(marker =>
                        marker.lifecycle == RealityAdjacentMapLifecycle.Active || marker.lifecycle == RealityAdjacentMapLifecycle.Materializing ||
                        marker.lifecycle == RealityAdjacentMapLifecycle.Evicting || marker.lifecycle == RealityAdjacentMapLifecycle.Quarantined),
                    "historical terminal records do not alone keep monitoring active");
            }
            Log.Message("[DeferredReality][Adjacent release checks]\n" + string.Join("\n", checks.ToArray()) +
                "\nMANUAL: use the consuming provider mod to create a marked site, attempt build/wall/furniture/floor/install/blueprint/frame, transfer an exact Pawn, save/reload resident and returning states, complete/expire its provider task, monitor twice, then evict only after the provider factory removes the real map." +
                "\nPURE: provider-neutral policy, same-tile claims, partial Prepare rollback, watermark retention, terminal transitions, and scoped compression are covered by DeferredReality.PureTests.");
        }

        private static void Simulate(int ticks)
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null) return;
            RealityProcessRunReport report = RealityProcessScheduler.RunDue(world, world.Now + ticks,
                new RealityProcessRunOptions { maximumProcesses = 128, maximumAnalyticalSteps = 32, maximumMilliseconds = 500 });
            Log.Message("[DeferredReality] simulated " + ticks + " ticks; attempted=" + report.attempted + " executed=" + report.executed +
                " bounded=" + report.bounded + " failures=" + report.failed);
        }
    }
}
