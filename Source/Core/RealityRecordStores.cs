using System.Collections.Generic;
using Verse;

namespace DeferredReality.API
{
    /// <summary>
    /// Owns the durable records that describe latent reality. The world component remains the
    /// RimWorld WorldComponent and calls this store from its ExposeData entry point.
    /// </summary>
    internal sealed class RealityLatentRecordStore
    {
        internal List<RealityRegionDescriptor> regions = new List<RealityRegionDescriptor>();
        internal readonly RealityRegionGraphStore graph = new RealityRegionGraphStore();
        internal List<RealityPopulationRecord> populations = new List<RealityPopulationRecord>();
        internal List<RealityAnchorRecord> anchors = new List<RealityAnchorRecord>();
        internal List<RealityConstraint> constraints = new List<RealityConstraint>();
        internal List<RealityProcessRecord> processes = new List<RealityProcessRecord>();
        internal List<RealityObservationRecord> observations = new List<RealityObservationRecord>();
        internal List<RealityProviderPayload> providerPayloads = new List<RealityProviderPayload>();
        internal List<RealityAppliedOperation> appliedOperations = new List<RealityAppliedOperation>();
        internal List<RealityConflictReport> conflicts = new List<RealityConflictReport>();
        internal List<RealityQuarantineRecord> quarantine = new List<RealityQuarantineRecord>();
        internal List<RealityOperationRetentionWatermark> operationWatermarks =
            new List<RealityOperationRetentionWatermark>();
        internal List<RealityFidelityEscalationRecord> fidelityEscalations =
            new List<RealityFidelityEscalationRecord>();

        internal void ExposeData()
        {
            Scribe_Collections.Look(ref regions, "deferredRealityRegions", LookMode.Deep);
            graph.ExposeData();
            Scribe_Collections.Look(ref populations, "deferredRealityPopulations", LookMode.Deep);
            Scribe_Collections.Look(ref anchors, "deferredRealityAnchors", LookMode.Deep);
            Scribe_Collections.Look(ref constraints, "deferredRealityConstraints", LookMode.Deep);
            Scribe_Collections.Look(ref processes, "deferredRealityProcesses", LookMode.Deep);
            Scribe_Collections.Look(ref observations, "deferredRealityObservations", LookMode.Deep);
            Scribe_Collections.Look(ref providerPayloads, "deferredRealityProviderPayloads", LookMode.Deep);
            Scribe_Collections.Look(ref appliedOperations, "deferredRealityAppliedOperations", LookMode.Deep);
            Scribe_Collections.Look(ref conflicts, "deferredRealityConflicts", LookMode.Deep);
            Scribe_Collections.Look(ref quarantine, "deferredRealityQuarantine", LookMode.Deep);
            Scribe_Collections.Look(ref operationWatermarks, "deferredRealityOperationWatermarks", LookMode.Deep);
            Scribe_Collections.Look(ref fidelityEscalations, "deferredRealityFidelityEscalations", LookMode.Deep);
        }

        internal void EnsureLists()
        {
            regions = regions ?? new List<RealityRegionDescriptor>();
            populations = populations ?? new List<RealityPopulationRecord>();
            anchors = anchors ?? new List<RealityAnchorRecord>();
            constraints = constraints ?? new List<RealityConstraint>();
            processes = processes ?? new List<RealityProcessRecord>();
            observations = observations ?? new List<RealityObservationRecord>();
            providerPayloads = providerPayloads ?? new List<RealityProviderPayload>();
            appliedOperations = appliedOperations ?? new List<RealityAppliedOperation>();
            conflicts = conflicts ?? new List<RealityConflictReport>();
            quarantine = quarantine ?? new List<RealityQuarantineRecord>();
            operationWatermarks = operationWatermarks ?? new List<RealityOperationRetentionWatermark>();
            fidelityEscalations = fidelityEscalations ?? new List<RealityFidelityEscalationRecord>();
        }
    }

    /// <summary>
    /// Owns durable state for live projections and cross-region transitions. These records are
    /// recovery state, not latent population or identity state.
    /// </summary>
    internal sealed class RealityProjectionRecordStore
    {
        internal List<RealityTransferJournalRecord> transferJournals = new List<RealityTransferJournalRecord>();
        internal List<RealityAdjacentMapRecord> adjacentMaps = new List<RealityAdjacentMapRecord>();
        internal List<RealityExcursionTicket> excursions = new List<RealityExcursionTicket>();
        internal List<RealityMapCreationIntentRecord> mapCreationIntents =
            new List<RealityMapCreationIntentRecord>();
        internal List<RealityAdjacentDiagnosticRecord> adjacentDiagnostics =
            new List<RealityAdjacentDiagnosticRecord>();

        internal void ExposeData()
        {
            Scribe_Collections.Look(ref transferJournals, "deferredRealityTransferJournals", LookMode.Deep);
            Scribe_Collections.Look(ref adjacentMaps, "deferredRealityAdjacentMaps", LookMode.Deep);
            Scribe_Collections.Look(ref excursions, "deferredRealityExcursions", LookMode.Deep);
            Scribe_Collections.Look(ref mapCreationIntents, "deferredRealityMapCreationIntents", LookMode.Deep);
            Scribe_Collections.Look(ref adjacentDiagnostics, "deferredRealityAdjacentDiagnostics", LookMode.Deep);
        }

        internal void EnsureLists()
        {
            transferJournals = transferJournals ?? new List<RealityTransferJournalRecord>();
            adjacentMaps = adjacentMaps ?? new List<RealityAdjacentMapRecord>();
            excursions = excursions ?? new List<RealityExcursionTicket>();
            mapCreationIntents = mapCreationIntents ?? new List<RealityMapCreationIntentRecord>();
            adjacentDiagnostics = adjacentDiagnostics ?? new List<RealityAdjacentDiagnosticRecord>();
        }
    }
}
