using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.Materialization;
using RimWorld;
using RimWorld.Planet;
using DeferredReality.Simulation;
using Verse;

namespace DeferredReality.API
{
    /// <summary>Authoritative world-level store for latent reality.</summary>
    public sealed class DeferredRealityWorldComponent : WorldComponent
    {
        private const int MaximumObservationHistory = 8192;
        private const int MaximumQuarantineRecords = 4096;
        private const int MaximumConflictRecords = 2048;
        private const int MaximumCancelledProcesses = 1024;
        private const int MaximumAdjacentDiagnostics = 2048;
        private const int MaximumTerminalExcursions = 1024;
        private const int MaximumRetiredAdjacentMaps = 256;
        private const long AdjacentTerminalRetentionTicks = 600000L;
        private const long StorageMaintenanceIntervalTicks = 6000L;
        private const int StorageMaintenanceMutationThreshold = 64;

        private readonly RealityLatentRecordStore latentRecords = new RealityLatentRecordStore();
        private readonly RealityProjectionRecordStore projectionRecords = new RealityProjectionRecordStore();

        // These narrow properties keep the public WorldComponent facade stable while the
        // durable collection ownership lives in the two cohesive stores above.
        private List<RealityRegionDescriptor> regions { get => latentRecords.regions; set => latentRecords.regions = value; }
        private RealityRegionGraphStore graph => latentRecords.graph;
        private List<RealityPopulationRecord> populations { get => latentRecords.populations; set => latentRecords.populations = value; }
        private List<RealityAnchorRecord> anchors { get => latentRecords.anchors; set => latentRecords.anchors = value; }
        private List<RealityConstraint> constraints { get => latentRecords.constraints; set => latentRecords.constraints = value; }
        private List<RealityProcessRecord> processes { get => latentRecords.processes; set => latentRecords.processes = value; }
        private List<RealityObservationRecord> observations { get => latentRecords.observations; set => latentRecords.observations = value; }
        private List<RealityProviderPayload> providerPayloads { get => latentRecords.providerPayloads; set => latentRecords.providerPayloads = value; }
        private List<RealityAppliedOperation> appliedOperations { get => latentRecords.appliedOperations; set => latentRecords.appliedOperations = value; }
        private List<RealityConflictReport> conflicts { get => latentRecords.conflicts; set => latentRecords.conflicts = value; }
        private List<RealityQuarantineRecord> quarantine { get => latentRecords.quarantine; set => latentRecords.quarantine = value; }
        private List<RealityOperationRetentionWatermark> operationWatermarks { get => latentRecords.operationWatermarks; set => latentRecords.operationWatermarks = value; }
        private List<RealityFidelityEscalationRecord> fidelityEscalations { get => latentRecords.fidelityEscalations; set => latentRecords.fidelityEscalations = value; }
        private List<RealityTransferJournalRecord> transferJournals { get => projectionRecords.transferJournals; set => projectionRecords.transferJournals = value; }
        private List<RealityAdjacentMapRecord> adjacentMaps { get => projectionRecords.adjacentMaps; set => projectionRecords.adjacentMaps = value; }
        private List<RealityExcursionTicket> excursions { get => projectionRecords.excursions; set => projectionRecords.excursions = value; }
        private List<RealityMapCreationIntentRecord> mapCreationIntents { get => projectionRecords.mapCreationIntents; set => projectionRecords.mapCreationIntents = value; }
        private List<RealityAdjacentDiagnosticRecord> adjacentDiagnostics { get => projectionRecords.adjacentDiagnostics; set => projectionRecords.adjacentDiagnostics = value; }

        private readonly Dictionary<string, RealityRegionDescriptor> regionById = new Dictionary<string, RealityRegionDescriptor>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityPopulationRecord> populationById = new Dictionary<string, RealityPopulationRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityAnchorRecord> anchorById = new Dictionary<string, RealityAnchorRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityConstraint> constraintById = new Dictionary<string, RealityConstraint>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityProcessRecord> processById = new Dictionary<string, RealityProcessRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityObservationRecord> observationById = new Dictionary<string, RealityObservationRecord>(StringComparer.Ordinal);
        private readonly HashSet<string> appliedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> regionByProjectionMapId = new Dictionary<int, string>();
        private readonly Dictionary<int, RealityAdjacentMapRecord> adjacentMapById = new Dictionary<int, RealityAdjacentMapRecord>();
        private readonly Dictionary<string, RealityExcursionTicket> excursionById = new Dictionary<string, RealityExcursionTicket>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityExcursionTicket> activeExcursionById = new Dictionary<string, RealityExcursionTicket>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityMapCreationIntentRecord> mapCreationIntentById =
            new Dictionary<string, RealityMapCreationIntentRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityFidelityEscalationRecord> fidelityEscalationById =
            new Dictionary<string, RealityFidelityEscalationRecord>(StringComparer.Ordinal);
        private readonly HashSet<string> activeMapCreationTransactions =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityExcursionTicket> pendingExcursions = new Dictionary<string, RealityExcursionTicket>(StringComparer.Ordinal);
        private readonly Dictionary<string, Pawn> pendingExcursionPawns = new Dictionary<string, Pawn>(StringComparer.Ordinal);
        private readonly Dictionary<string, Pawn> excursionPawnInstances = new Dictionary<string, Pawn>(StringComparer.Ordinal);
        private bool mapCreationIntentReconciliationPending;
        private bool initialized;
        private bool indexesReady;
        private bool processScheduleCacheReady;
        private long earliestRunnableProcessDueTick = long.MaxValue;
        private long nextAdjacentMonitorTick;
        private bool storageMaintenanceDirty;
        private long nextStorageMaintenanceTick;
        private int storageMaintenanceMutationCount;
        private RealityCompactionReport lastCompactionReport = new RealityCompactionReport();
        private int unresolvedTransferJournalCount;
        private int revision;

        /// <summary>Returns the active world component, if a world exists.</summary>
        public static DeferredRealityWorldComponent Current => Find.World?.GetComponent<DeferredRealityWorldComponent>();

        /// <summary>Creates the component for a RimWorld world.</summary>
        public DeferredRealityWorldComponent(World world) : base(world) { }

        /// <summary>Current store revision used by read-model caches.</summary>
        public int Revision => revision;

        /// <summary>World seed string used as the root deterministic input.</summary>
        public string WorldSeed
        {
            get
            {
                try { return Find.World?.info?.seedString ?? string.Empty; }
                catch (NullReferenceException) { return string.Empty; }
            }
        }

        /// <summary>Current game tick, or zero during early load.</summary>
        public long Now
        {
            get
            {
                try { return Find.TickManager?.TicksGame ?? 0; }
                catch (NullReferenceException) { return 0; }
            }
        }

        /// <summary>Whether persisted adjacent roles or nonterminal excursions still require safety monitoring.</summary>
        public bool HasAdjacentSafetyWork => adjacentMapById.Count > 0 || activeExcursionById.Count > 0 ||
            mapCreationIntentById.Count > 0 || unresolvedTransferJournalCount > 0;

        /// <summary>Whether bounded storage maintenance is waiting for its next scheduled pass.</summary>
        public bool StorageMaintenanceDirty => storageMaintenanceDirty;

        /// <summary>Next tick at which dirty storage maintenance may run.</summary>
        public long NextStorageMaintenanceTick => nextStorageMaintenanceTick;

        /// <summary>Last deterministic compaction report.</summary>
        public RealityCompactionReport LastCompactionReport => lastCompactionReport;

        /// <inheritdoc />
        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) RunStorageMaintenance(Now, true);
            Scribe_Values.Look(ref revision, "deferredRealityRevision", 0);
            latentRecords.ExposeData();
            projectionRecords.ExposeData();
            Scribe_Values.Look(ref storageMaintenanceDirty, "deferredRealityStorageMaintenanceDirty", false);
            Scribe_Values.Look(ref nextStorageMaintenanceTick, "deferredRealityNextStorageMaintenanceTick", 0L);
            Scribe_Values.Look(ref storageMaintenanceMutationCount, "deferredRealityStorageMaintenanceMutationCount", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                latentRecords.EnsureLists();
                projectionRecords.EnsureLists();
                storageMaintenanceDirty = true;
                nextStorageMaintenanceTick = Now;
                RepairAndIndex();
            }
        }

        /// <inheritdoc />
        public override void WorldComponentTick()
        {
            if (!RealityThreadGuard.IsMainThread) return;
            if (!indexesReady) RepairAndIndex();
            if (!initialized)
            {
                initialized = true;
                RealityProviderRegistry.NotifyWorldReady(this);
                RepairAndIndex();
            }
            long now = Now;
            if (mapCreationIntentReconciliationPending && Find.Maps != null)
            {
                mapCreationIntents = ReconcileMapCreationIntents(mapCreationIntents);
                mapCreationIntentById.Clear();
                foreach (RealityMapCreationIntentRecord intent in mapCreationIntents.Where(item =>
                    item != null && !string.IsNullOrEmpty(item.transactionId)))
                    mapCreationIntentById[intent.transactionId] = intent;
            }
            if (HasRunnableProcessDue(now)) RealityProcessScheduler.RunDue(this, now, RealityProcessRunOptions.Default);
            RunStorageMaintenance(now, false);
            if ((DeferredRealityModSettings.Current.enableAdjacentRegions || HasAdjacentSafetyWork) && now >= nextAdjacentMonitorTick)
            {
                nextAdjacentMonitorTick = now + RealityAdjacentPolicy.MonitorIntervalTicks;
                RealityAdjacentSurfaceService.Monitor(this, now);
            }
        }

        /// <summary>Returns a detached region snapshot.</summary>
        public bool TryGetRegion(RealityRegionId id, out RealityRegionSnapshot snapshot)
        {
            EnsureIndexes();
            if (regionById.TryGetValue(id.ToString(), out RealityRegionDescriptor record))
            {
                snapshot = new RealityRegionSnapshot(record);
                return true;
            }
            snapshot = null;
            return false;
        }

        /// <summary>Creates a region descriptor if needed and returns a detached snapshot.</summary>
        public RealityRegionSnapshot EnsureRegion(RealityRegionId id, string label = null, long now = -1)
        {
            RealityThreadGuard.RequireMainThread();
            if (!id.IsValid) throw new ArgumentException("A valid RealityRegionId is required.", nameof(id));
            EnsureIndexes();
            long tick = now >= 0 ? now : Now;
            if (!regionById.TryGetValue(id.ToString(), out RealityRegionDescriptor record))
            {
                record = new RealityRegionDescriptor
                {
                    regionId = id.ToString(),
                    label = string.IsNullOrEmpty(label) ? id.ToString() : label,
                    stableSeed = RealityDeterminism.Seed(WorldSeed, id.ToString(), "core", "region", 0),
                    createdTick = tick,
                    lastUpdateTick = tick,
                    lastKnownWorldTile = id.WorldTile,
                    fidelity = RealityFidelity.Dormant
                };
                regions.Add(record);
                regionById[record.regionId] = record;
                Touch("region.created", "core", record.regionId, record.regionId);
            }
            else
            {
                if (!string.IsNullOrEmpty(label) && record.label == id.ToString()) record.label = label;
                record.lastUpdateTick = Math.Max(record.lastUpdateTick, tick);
            }
            return new RealityRegionSnapshot(record);
        }

        /// <summary>Imports a provider descriptor without exposing mutable store collections.</summary>
        public bool UpsertRegionDescriptor(RealityRegionDescriptor value)
        {
            RealityThreadGuard.RequireMainThread();
            if (value == null || !RealityRegionId.TryParse(value.regionId, out RealityRegionId id) || !id.IsValid) return false;
            EnsureIndexes();
            RealityRegionDescriptor copy = value.Clone();
            int index = regions.FindIndex(item => item?.regionId == copy.regionId);
            if (index >= 0) regions[index] = copy;
            else regions.Add(copy);
            regionById[copy.regionId] = copy;
            Touch("region.descriptor", "core", copy.regionId, copy.label);
            return true;
        }

        /// <summary>Returns all region snapshots in stable ID order.</summary>
        public IReadOnlyList<RealityRegionSnapshot> RegionSnapshots()
        {
            EnsureIndexes();
            return regions.Where(item => item != null && !string.IsNullOrEmpty(item.regionId))
                .OrderBy(item => item.regionId, StringComparer.Ordinal).Select(item => new RealityRegionSnapshot(item)).ToList();
        }

        /// <summary>Updates low-resolution environment without requiring a live Map.</summary>
        public bool UpdateEnvironment(RealityRegionId id, RealityEnvironmentSummary summary, long now = -1)
        {
            RealityThreadGuard.RequireMainThread();
            RealityRegionDescriptor record = RegionRecord(id.ToString());
            if (record == null || summary == null) return false;
            record.environment = summary.Clone();
            record.lastUpdateTick = Math.Max(record.lastUpdateTick, now >= 0 ? now : Now);
            Touch("environment.changed", "core", record.regionId, record.environment.biomeDefName);
            return true;
        }

        /// <summary>Stores explicitly versioned opaque provider payload data.</summary>
        public bool UpsertProviderPayload(RealityProviderPayload value)
        {
            RealityThreadGuard.RequireMainThread();
            if (value == null || string.IsNullOrEmpty(value.providerId) || string.IsNullOrEmpty(value.payloadId)) return false;
            int index = providerPayloads.FindIndex(item => item?.providerId == value.providerId && item.payloadId == value.payloadId);
            if (index >= 0) providerPayloads[index] = value.Clone();
            else providerPayloads.Add(value.Clone());
            Touch("provider-payload.changed", value.providerId, null, value.payloadId);
            return true;
        }

        /// <summary>Returns detached provider payloads without exposing mutable storage.</summary>
        public IReadOnlyList<RealityProviderPayload> ProviderPayloadSnapshots(string providerId = null)
        {
            return providerPayloads.Where(item => item != null && (string.IsNullOrEmpty(providerId) || item.providerId == providerId))
                .OrderBy(item => item.providerId, StringComparer.Ordinal).ThenBy(item => item.payloadId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        /// <summary>Records or updates one explicit region connection.</summary>
        public bool UpsertConnection(RealityRegionConnection connection)
        {
            RealityThreadGuard.RequireMainThread();
            EnsureIndexes();
            if (!graph.Upsert(connection, out string diagnostic))
            {
                QuarantineInternal("connection", connection?.connectionId, connection?.ownerNamespace ?? "core",
                    diagnostic, GraphFingerprint(connection), Now);
                return false;
            }
            RealityRegionConnection copy = connection.Clone();
            Touch("connection.changed", copy.ownerNamespace ?? "core", copy.sourceRegionId, copy.connectionId);
            return true;
        }

        /// <summary>Returns a detached persisted graph record by its canonical stable ID.</summary>
        public bool TryGetConnection(string connectionId, out RealityRegionConnection connection)
        {
            EnsureIndexes();
            return graph.TryGet(connectionId, out connection);
        }

        /// <summary>Returns all detached connections, including disabled records, in stable ID order.</summary>
        public IReadOnlyList<RealityRegionConnection> ConnectionSnapshots(string regionId = null)
        {
            EnsureIndexes();
            return graph.Snapshots(regionId);
        }

        /// <summary>Returns enabled connections traversable from a region.</summary>
        public IReadOnlyList<RealityRegionConnection> OutgoingConnections(RealityRegionId regionId, string kind = null)
        {
            return OutgoingConnections(regionId.ToString(), kind);
        }

        /// <summary>Returns enabled connections traversable from a serialized region ID.</summary>
        public IReadOnlyList<RealityRegionConnection> OutgoingConnections(string regionId, string kind = null)
        {
            EnsureIndexes();
            return graph.Outgoing(regionId, kind);
        }

        /// <summary>Returns enabled connections that can enter a region.</summary>
        public IReadOnlyList<RealityRegionConnection> IncomingConnections(RealityRegionId regionId, string kind = null)
        {
            return IncomingConnections(regionId.ToString(), kind);
        }

        /// <summary>Returns enabled connections that can enter a serialized region ID.</summary>
        public IReadOnlyList<RealityRegionConnection> IncomingConnections(string regionId, string kind = null)
        {
            EnsureIndexes();
            return graph.Incoming(regionId, kind);
        }

        /// <summary>Returns unique enabled neighboring region identities without performing pathfinding.</summary>
        public IReadOnlyList<RealityRegionId> Neighbors(RealityRegionId regionId, string kind = null)
        {
            return Neighbors(regionId.ToString(), kind);
        }

        /// <summary>Returns unique enabled neighboring serialized region identities without pathfinding.</summary>
        public IReadOnlyList<RealityRegionId> Neighbors(string regionId, string kind = null)
        {
            EnsureIndexes();
            return graph.Neighbors(regionId, kind);
        }

        /// <summary>Returns a detached population record.</summary>
        public bool TryGetPopulation(string populationId, out RealityPopulationSnapshot snapshot)
        {
            EnsureIndexes();
            if (populationById.TryGetValue(populationId ?? string.Empty, out RealityPopulationRecord record))
            {
                snapshot = new RealityPopulationSnapshot(record);
                return true;
            }
            snapshot = null;
            return false;
        }

        /// <summary>Returns a detached mutable copy for provider-side analytical updates.</summary>
        public bool TryGetPopulationRecord(string populationId, out RealityPopulationRecord record)
        {
            EnsureIndexes();
            if (populationById.TryGetValue(populationId ?? string.Empty, out RealityPopulationRecord value))
            {
                record = value.Clone();
                return true;
            }
            record = null;
            return false;
        }

        /// <summary>Returns detached populations filtered by stable provider, kind, or region.</summary>
        public IReadOnlyList<RealityPopulationSnapshot> PopulationSnapshots(string regionId = null, string providerId = null, string kind = null)
        {
            EnsureIndexes();
            return populations.Where(item => item != null &&
                    (string.IsNullOrEmpty(regionId) || item.regionId == regionId) &&
                    (string.IsNullOrEmpty(providerId) || item.providerId == providerId) &&
                    (string.IsNullOrEmpty(kind) || item.kind == kind))
                .OrderBy(item => item.populationId, StringComparer.Ordinal).Select(item => new RealityPopulationSnapshot(item)).ToList();
        }

        /// <summary>Returns a detached anchor record.</summary>
        public IReadOnlyList<RealityAnchorSnapshot> AnchorSnapshots(string regionId = null, string providerId = null)
        {
            EnsureIndexes();
            return anchors.Where(item => item != null && (string.IsNullOrEmpty(regionId) || item.regionId == regionId) &&
                    (string.IsNullOrEmpty(providerId) || item.providerId == providerId))
                .OrderBy(item => item.anchorId, StringComparer.Ordinal).Select(item => new RealityAnchorSnapshot(item)).ToList();
        }

        /// <summary>Returns detached constraints filtered by region.</summary>
        public IReadOnlyList<RealityConstraint> ConstraintSnapshots(string regionId = null)
        {
            EnsureIndexes();
            return constraints.Where(item => item != null && (string.IsNullOrEmpty(regionId) || item.regionId == regionId))
                .OrderByDescending(item => item.priority).ThenBy(item => item.constraintId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        /// <summary>Returns detached scheduled processes.</summary>
        public IReadOnlyList<RealityProcessSnapshot> ProcessSnapshots(string regionId = null)
        {
            EnsureIndexes();
            return processes.Where(item => item != null && (string.IsNullOrEmpty(regionId) || item.regionId == regionId))
                .OrderBy(item => item.nextDueTick).ThenByDescending(item => item.priority)
                .ThenBy(item => item.providerId, StringComparer.Ordinal).ThenBy(item => item.processId, StringComparer.Ordinal)
                .Select(item => new RealityProcessSnapshot(item)).ToList();
        }

        /// <summary>Returns durable fidelity escalation requests in deterministic order.</summary>
        public IReadOnlyList<RealityFidelityEscalationRecord> FidelityEscalationSnapshots(string regionId = null)
        {
            EnsureIndexes();
            return fidelityEscalations.Where(item => item != null &&
                    (string.IsNullOrEmpty(regionId) || item.regionId == regionId))
                .OrderBy(item => item.status).ThenBy(item => item.createdTick)
                .ThenBy(item => item.requestId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        /// <summary>Reads one durable escalation request without exposing mutable store state.</summary>
        public bool TryGetFidelityEscalation(string requestId, out RealityFidelityEscalationRecord record)
        {
            EnsureIndexes();
            if (fidelityEscalationById.TryGetValue(requestId ?? string.Empty, out RealityFidelityEscalationRecord value))
            {
                record = value.Clone();
                return true;
            }
            record = null;
            return false;
        }

        /// <summary>Approves a pending escalation without creating a Map or changing fidelity.</summary>
        public bool ApproveFidelityEscalation(string requestId, string diagnostic = null)
        {
            RealityThreadGuard.RequireMainThread();
            RealityFidelityEscalationRecord record = FidelityEscalationRecord(requestId);
            if (record == null || record.status != RealityFidelityEscalationStatus.Pending) return false;
            record.status = RealityFidelityEscalationStatus.Approved;
            record.updatedTick = Now;
            record.diagnostic = diagnostic;
            Touch("fidelity-escalation.approved", record.providerId, record.regionId, record.requestId);
            return true;
        }

        /// <summary>Resolves an escalation after the host/provider has satisfied or declined its request.</summary>
        public bool ResolveFidelityEscalation(string requestId, RealityFidelityEscalationStatus status,
            string diagnostic = null)
        {
            RealityThreadGuard.RequireMainThread();
            if (status != RealityFidelityEscalationStatus.Satisfied &&
                status != RealityFidelityEscalationStatus.Declined &&
                status != RealityFidelityEscalationStatus.Failed &&
                status != RealityFidelityEscalationStatus.Cancelled) return false;
            RealityFidelityEscalationRecord record = FidelityEscalationRecord(requestId);
            if (record == null || record.status == RealityFidelityEscalationStatus.Satisfied ||
                record.status == RealityFidelityEscalationStatus.Declined ||
                record.status == RealityFidelityEscalationStatus.Failed ||
                record.status == RealityFidelityEscalationStatus.Cancelled) return false;
            if (status == RealityFidelityEscalationStatus.Satisfied)
            {
                if (record.policy != RealityFidelityEscalationPolicy.ProviderMayHandle &&
                    record.status != RealityFidelityEscalationStatus.Approved) return false;
                if (!RealityRegionId.TryParse(record.regionId, out RealityRegionId escalationRegion) ||
                    !TryGetRegion(escalationRegion, out RealityRegionSnapshot snapshot) ||
                    snapshot.fidelity != record.requestedFidelity ||
                    (record.requestedFidelity == RealityFidelity.Materialized &&
                     (snapshot.authority != RealityRegionAuthority.LiveProjection ||
                      snapshot.projectionMapUniqueId < 0))) return false;
            }
            record.status = status;
            record.updatedTick = Now;
            record.resolvedTick = Now;
            record.diagnostic = diagnostic;
            if (status == RealityFidelityEscalationStatus.Satisfied)
                ReactivateFidelityProcesses(record.regionId, record.requestId);
            Touch("fidelity-escalation.resolved", record.providerId, record.regionId, record.requestId);
            return true;
        }

        /// <summary>
        /// Performs a provider-validated latent fidelity transition. Materialized transitions belong to
        /// the transactional map materialization/compression services and are never performed here.
        /// </summary>
        public RealityFidelityTransitionResult TryTransitionFidelity(RealityFidelityTransitionRequest request)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new RealityFidelityTransitionResult { regionId = request?.regionId ?? default(RealityRegionId) };
            if (request == null || !request.IsValid)
            {
                result.error = "A valid region and provider are required.";
                return result;
            }
            if (!TryGetRegion(request.regionId, out RealityRegionSnapshot snapshot))
            {
                result.error = "The requested region is not registered.";
                return result;
            }
            result.fromFidelity = snapshot.fidelity;
            result.toFidelity = request.toFidelity;
            if (snapshot.authority != RealityRegionAuthority.Latent)
            {
                result.error = "Only latent regions can change fidelity without a Map transition.";
                return result;
            }
            if (request.toFidelity == RealityFidelity.Materialized)
            {
                result.error = "Materialized fidelity requires the transactional materialization service.";
                return result;
            }
            if (request.fromFidelity != snapshot.fidelity)
            {
                result.error = "The requested fidelity transition does not match the region's current fidelity.";
                return result;
            }
            if (request.toFidelity == snapshot.fidelity)
            {
                result.succeeded = true;
                return result;
            }
            if (!RealityProviderRegistry.TryGetCapability(request.providerId, out IRealityFidelityProvider provider))
            {
                result.error = "The provider does not expose a fidelity contract.";
                result.vetoes = new[] { new RealityVeto("fidelity.provider-unavailable", result.error, request.providerId, 2) };
                return result;
            }
            var vetoes = new List<RealityVeto>();
            RealityFidelityContract contract;
            try
            {
                contract = provider.DescribeFidelity(new RealityProviderContext(this, request.providerId, request.now), snapshot);
            }
            catch (Exception exception)
            {
                result.error = "Provider fidelity description failed: " + exception.Message;
                result.vetoes = new[] { new RealityVeto("fidelity.contract-exception", exception.Message, request.providerId, 3) };
                return result;
            }
            if (contract == null || contract.currentFidelity != snapshot.fidelity ||
                !contract.Supports(snapshot.fidelity) || !contract.Supports(request.toFidelity) ||
                (!contract.AllowsTransition(snapshot.fidelity, request.toFidelity, RealityFidelityTransitionMechanism.Provider) &&
                 !contract.AllowsTransition(snapshot.fidelity, request.toFidelity, RealityFidelityTransitionMechanism.HostApproval)))
                vetoes.Add(new RealityVeto("fidelity.transition-not-declared",
                    "The provider did not declare this fidelity or transition edge.", request.providerId, 2));
            if (vetoes.Count == 0)
            {
                try
                {
                    if (!provider.CanTransitionFidelity(request, vetoes))
                        vetoes.Add(new RealityVeto("fidelity.provider-veto", "The provider declined the fidelity transition.", request.providerId, 2));
                }
                catch (Exception exception)
                {
                    vetoes.Add(new RealityVeto("fidelity.transition-exception", exception.Message, request.providerId, 3));
                }
            }
            if (vetoes.Count > 0)
            {
                result.error = "Fidelity transition was vetoed.";
                result.vetoes = vetoes;
                return result;
            }
            RealityFidelityEscalationRecord escalation = FidelityEscalationRecord(request.escalationRequestId);
            if (!string.IsNullOrEmpty(request.escalationRequestId) &&
                (escalation == null || escalation.status != RealityFidelityEscalationStatus.Approved))
            {
                result.error = "The escalation request is not approved.";
                result.vetoes = new[] { new RealityVeto("fidelity.escalation-not-approved", result.error, request.providerId, 2) };
                return result;
            }
            RealityWorldState state = CaptureState();
            try
            {
                RealityRegionDescriptor descriptor = RegionRecord(request.regionId.ToString());
                RealityFidelity previousFidelity = descriptor.fidelity;
                descriptor.fidelity = request.toFidelity;
                descriptor.lastUpdateTick = request.now;
                if (escalation != null && !ResolveFidelityEscalation(escalation.requestId,
                    RealityFidelityEscalationStatus.Satisfied, "Fidelity transition committed."))
                    throw new InvalidOperationException("The approved fidelity escalation could not be satisfied by the transition.");
                ReactivateFidelityProcesses(descriptor.regionId, request.escalationRequestId);
                Touch("region.fidelity-changed", request.providerId, descriptor.regionId, request.toFidelity.ToString());
                RealityRegionSnapshot changed = new RealityRegionSnapshot(descriptor);
                try
                {
                    provider.OnFidelityChanged(new RealityProviderContext(this, request.providerId, request.now),
                        changed, previousFidelity, request.toFidelity);
                }
                catch (Exception exception)
                {
                    Quarantine("fidelity-provider-notification", request.regionId.ToString(), request.providerId,
                        exception.Message, request.toFidelity.ToString());
                }
                result.succeeded = true;
                return result;
            }
            catch (Exception exception)
            {
                RestoreState(state);
                result.error = "Fidelity transition rolled back: " + exception.Message;
                return result;
            }
        }

        /// <summary>Returns detached observations, newest first.</summary>
        public IReadOnlyList<RealityObservationRecord> ObservationSnapshots(string regionId = null, string subjectId = null)
        {
            EnsureIndexes();
            return observations.Where(item => item != null && (string.IsNullOrEmpty(regionId) || item.regionId == regionId) &&
                    (string.IsNullOrEmpty(subjectId) || item.subjectId == subjectId))
                .OrderByDescending(item => item.tick).ThenBy(item => item.observationId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        /// <summary>Registers or updates an aggregate population through the generic ledger.</summary>
        public bool UpsertPopulation(RealityPopulationRecord value)
        {
            RealityThreadGuard.RequireMainThread();
            if (value == null || string.IsNullOrEmpty(value.populationId) || string.IsNullOrEmpty(value.regionId) || string.IsNullOrEmpty(value.providerId)) return false;
            EnsureIndexes();
            RealityPopulationRecord copy = value.Clone();
            copy.amount = Math.Max(0f, copy.amount);
            copy.uncertainty = Math.Max(0f, copy.uncertainty);
            copy.carryingCapacity = Math.Max(0f, copy.carryingCapacity);
            copy.habitatSuitability = Clamp01(copy.habitatSuitability);
            int index = populations.FindIndex(item => item?.populationId == copy.populationId);
            if (index >= 0) populations[index] = copy;
            else populations.Add(copy);
            populationById[copy.populationId] = copy;
            Touch("population.changed", copy.providerId, copy.regionId, copy.populationId);
            return true;
        }

        /// <summary>Registers or updates an identity anchor.</summary>
        public bool UpsertAnchor(RealityAnchorRecord value)
        {
            RealityThreadGuard.RequireMainThread();
            if (value == null || string.IsNullOrEmpty(value.anchorId) || string.IsNullOrEmpty(value.regionId) || string.IsNullOrEmpty(value.providerId)) return false;
            EnsureIndexes();
            RealityAnchorRecord copy = value.Clone();
            int index = anchors.FindIndex(item => item?.anchorId == copy.anchorId);
            if (index >= 0) anchors[index] = copy;
            else anchors.Add(copy);
            anchorById[copy.anchorId] = copy;
            Touch("anchor.changed", copy.providerId, copy.regionId, copy.anchorId);
            return true;
        }

        /// <summary>Adds a constraint without replacing an established conflicting fact.</summary>
        public bool AddConstraint(RealityConstraint value)
        {
            RealityThreadGuard.RequireMainThread();
            if (value == null || string.IsNullOrEmpty(value.constraintId) || string.IsNullOrEmpty(value.regionId) || string.IsNullOrEmpty(value.providerId)) return false;
            EnsureIndexes();
            RealityConstraint copy = value.Clone();
            if (constraintById.TryGetValue(copy.constraintId, out RealityConstraint existing))
            {
                if (existing.payload == copy.payload && existing.typeId == copy.typeId) return true;
                AddConflictInternal(copy.regionId, copy.constraintId, existing.constraintId, "Duplicate constraint ID with different established payload.", Now);
                return false;
            }
            constraints.Add(copy);
            constraintById[copy.constraintId] = copy;
            Touch("constraint.added", copy.providerId, copy.regionId, copy.constraintId);
            return true;
        }

        /// <summary>Schedules or updates one stable process.</summary>
        public bool ScheduleProcess(RealityProcessRecord value)
        {
            RealityThreadGuard.RequireMainThread();
            if (value == null || string.IsNullOrEmpty(value.processId) || string.IsNullOrEmpty(value.providerId)) return false;
            EnsureIndexes();
            RealityProcessRecord copy = value.Clone();
            copy.intervalTicks = Math.Max(1, copy.intervalTicks);
            RealityProcessPausePolicy.Normalize(copy, RealityProviderRegistry.TryGet(copy.providerId, out _));
            int index = processes.FindIndex(item => item?.processId == copy.processId);
            if (index >= 0) processes[index] = copy;
            else processes.Add(copy);
            processById[copy.processId] = copy;
            RefreshProcessScheduleCache();
            Touch("process.scheduled", copy.providerId, copy.regionId, copy.processId);
            return true;
        }

        /// <summary>Creates or reuses one durable escalation request for a process output.</summary>
        internal RealityFidelityEscalationRecord RequestFidelityEscalation(RealityProcessRecord process,
            RealityProcessEscalationRequest request, RealityRegionSnapshot region, long now)
        {
            RealityThreadGuard.RequireMainThread();
            if (process == null || request == null || !request.IsValid || region == null ||
                string.IsNullOrEmpty(process.processId) || string.IsNullOrEmpty(process.providerId) ||
                string.IsNullOrEmpty(process.regionId)) return null;
            RealityFidelity requested = request.requestedFidelity;
            if (!RealityFidelityRules.IsHigher(requested, region.fidelity))
                return null;
            string requestId = "fidelity:" + RealityDeterminism.Combine(process.providerId, process.processId,
                process.regionId, requested.ToString(), request.reason.ToString(), request.subjectId ?? string.Empty);
            EnsureIndexes();
            if (fidelityEscalationById.TryGetValue(requestId, out RealityFidelityEscalationRecord existing))
            {
                existing.attemptCount = existing.attemptCount == int.MaxValue ? int.MaxValue : existing.attemptCount + 1;
                existing.updatedTick = now;
                process.pendingEscalationRequestId = existing.status == RealityFidelityEscalationStatus.Pending ||
                    existing.status == RealityFidelityEscalationStatus.Approved ? requestId : null;
                return existing;
            }
            var record = new RealityFidelityEscalationRecord
            {
                requestId = requestId,
                processId = process.processId,
                providerId = process.providerId,
                regionId = process.regionId,
                currentFidelity = region.fidelity,
                requestedFidelity = requested,
                reason = request.reason,
                policy = request.policy,
                status = request.disposition == RealityFidelityEscalationDisposition.Decline
                    ? RealityFidelityEscalationStatus.Declined : RealityFidelityEscalationStatus.Pending,
                subjectId = request.subjectId,
                createdTick = now,
                updatedTick = now,
                attemptCount = 1,
                resolvedTick = request.disposition == RealityFidelityEscalationDisposition.Decline ? now : -1,
                diagnostic = request.disposition == RealityFidelityEscalationDisposition.Decline
                    ? "Provider declined escalation and retained the current abstraction." : null
            };
            fidelityEscalations.Add(record);
            fidelityEscalationById[record.requestId] = record;
            if (record.status == RealityFidelityEscalationStatus.Pending)
                process.pendingEscalationRequestId = record.requestId;
            Touch(record.status == RealityFidelityEscalationStatus.Pending
                ? "fidelity-escalation.pending" : "fidelity-escalation.declined",
                process.providerId, process.regionId, record.requestId);
            return record;
        }

        internal RealityFidelityEscalationRecord FidelityEscalationRecord(string requestId)
        {
            EnsureIndexes();
            return string.IsNullOrEmpty(requestId) ? null :
                fidelityEscalationById.TryGetValue(requestId, out RealityFidelityEscalationRecord record) ? record : null;
        }

        private void ReactivateFidelityProcesses(string regionId, string escalationRequestId)
        {
            foreach (RealityProcessRecord process in processes.Where(item => item != null && !item.cancelled &&
                item.regionId == regionId && item.paused &&
                (string.IsNullOrEmpty(escalationRequestId) || item.pendingEscalationRequestId == escalationRequestId)))
            {
                if (process.pauseReason != RealityProcessPauseReason.FidelityEscalation) continue;
                process.paused = false;
                process.pauseReason = RealityProcessPauseReason.None;
                process.pendingEscalationRequestId = null;
                process.lastError = null;
                process.nextDueTick = Math.Min(process.nextDueTick, Now);
                Touch("process.resumed", process.providerId, process.regionId, process.processId);
            }
            RefreshProcessScheduleCache();
        }

        /// <summary>Updates one process payload in place so a provider can persist an execution stage safely.</summary>
        public bool UpdateProcessPayload(string processId, string payload)
        {
            RealityThreadGuard.RequireMainThread();
            RealityProcessRecord process = ProcessRecord(processId);
            if (process == null || payload == null) return false;
            process.payload = payload;
            RefreshProcessScheduleCache();
            Touch("process.payload", process.providerId, process.regionId, process.processId);
            return true;
        }

        /// <summary>Records an observation without changing objective population data.</summary>
        public bool AddObservation(RealityObservationInput input)
        {
            RealityThreadGuard.RequireMainThread();
            if (input == null || string.IsNullOrEmpty(input.observationId) || string.IsNullOrEmpty(input.regionId) ||
                !Enum.IsDefined(typeof(RealityCompressionSignificance), input.compressionSignificance) ||
                !Enum.IsDefined(typeof(RealityObservationPrecision), input.spatialPrecision) ||
                !Enum.IsDefined(typeof(RealityObservationLifecycle), input.lifecycle) ||
                (input.location != null && !Enum.IsDefined(typeof(RealityObservationPrecision), input.location.precision))) return false;
            EnsureIndexes();
            if (observationById.ContainsKey(input.observationId)) return true;
            RealityLocation location = input.location?.Clone() ?? new RealityLocation();
            if (RealityObservationPrecisionRules.Rank(location.precision) <
                RealityObservationPrecisionRules.Rank(input.spatialPrecision))
                location.precision = input.spatialPrecision;
            var record = new RealityObservationRecord
            {
                observationId = input.observationId,
                observerId = input.observerId,
                source = input.source,
                tick = input.tick,
                subjectId = input.subjectId,
                regionId = input.regionId,
                compressionSignificance = input.compressionSignificance,
                certainty = Clamp01(input.certainty),
                confidence = Clamp01(input.confidence),
                spatialPrecision = input.spatialPrecision,
                location = location,
                facet = input.facet,
                playerObserved = input.playerObserved,
                sensorDerived = input.sensorDerived,
                inferred = input.inferred,
                rumored = input.rumored,
                estimate = input.estimate,
                lifecycle = input.lifecycle,
                supersededByObservationId = input.supersededByObservationId,
                invalidationReason = input.invalidationReason
            };
            observations.Add(record);
            observationById[record.observationId] = record;
            if (observations.Count > MaximumObservationHistory)
            {
                RealityObservationRecord oldest = RealityRetentionPolicy.FindOldestDiscardableObservation(observations);
                if (oldest != null)
                {
                    observations.Remove(oldest);
                    observationById.Remove(oldest.observationId);
                }
            }
            Touch("observation.recorded", input.source, input.regionId, input.observationId);
            foreach (IObservationProvider provider in RealityProviderRegistry.OfType<IObservationProvider>())
            {
                try { provider.OnObservation(record.Clone()); }
                catch (Exception exception) { Log.ErrorOnce("Deferred Reality observation provider failed: " + exception, RealityDeterminism.StableHash("observation:" + input.source)); }
            }
            return true;
        }

        /// <summary>Explicit gameplay invalidation of knowledge; invalidated facts no longer constrain rematerialization.</summary>
        public bool InvalidateObservation(string observationId, string reason, long now = -1L)
        {
            RealityThreadGuard.RequireMainThread();
            EnsureIndexes();
            if (string.IsNullOrEmpty(observationId) || !observationById.TryGetValue(observationId, out RealityObservationRecord record)) return false;
            record.lifecycle = RealityObservationLifecycle.Invalidated;
            record.invalidationReason = reason ?? string.Empty;
            record.supersededByObservationId = null;
            Touch("observation.invalidated", record.source, record.regionId, record.observationId);
            return true;
        }

        /// <summary>Explicit gameplay supersession of knowledge by a newer established fact.</summary>
        public bool SupersedeObservation(string observationId, string supersedingObservationId, string reason = null,
            long now = -1L)
        {
            RealityThreadGuard.RequireMainThread();
            EnsureIndexes();
            if (string.IsNullOrEmpty(observationId) || string.IsNullOrEmpty(supersedingObservationId) ||
                observationId == supersedingObservationId || !observationById.TryGetValue(observationId, out RealityObservationRecord record) ||
                !observationById.ContainsKey(supersedingObservationId)) return false;
            record.lifecycle = RealityObservationLifecycle.Superseded;
            record.supersededByObservationId = supersedingObservationId;
            record.invalidationReason = reason ?? string.Empty;
            Touch("observation.superseded", record.source, record.regionId, record.observationId);
            return true;
        }

        /// <summary>Associates a live Map projection with a durable region identity.</summary>
        public RealityRegionId RegisterMap(Map map)
        {
            RealityThreadGuard.RequireMainThread();
            if (map == null) return default(RealityRegionId);
            EnsureIndexes();
            if (TryRegisterMapFromCreationIntent(map, default(RealityRegionId), out RealityRegionId intendedRegion,
                out bool intentHandled))
                return intendedRegion;
            if (intentHandled) return default(RealityRegionId);
            if (!map.Tile.Valid)
            {
                QuarantineInternal("map", map.uniqueID.ToString(), "core", "Map has no valid world tile; projection ownership cannot be established.", null, Now);
                return default(RealityRegionId);
            }
            if (!TryResolveMapIdentity(map, out RealityRegionId id)) return default(RealityRegionId);
            return RegisterMap(map, id);
        }

        /// <summary>Associates a live Map projection with an explicit provider-owned region identity.</summary>
        public RealityRegionId RegisterMap(Map map, RealityRegionId regionId)
        {
            RealityThreadGuard.RequireMainThread();
            if (map == null || !regionId.IsValid) return default(RealityRegionId);
            EnsureIndexes();
            if (TryRegisterMapFromCreationIntent(map, regionId, out RealityRegionId intendedRegion,
                out bool intentHandled))
                return intendedRegion;
            if (intentHandled) return default(RealityRegionId);
            if (!map.Tile.Valid || map.Tile != (PlanetTile)regionId.WorldTile)
            {
                QuarantineInternal("map", map.uniqueID.ToString(), "core",
                    "Explicit map region does not match the map world tile.", regionId.ToString(), Now);
                return default(RealityRegionId);
            }
            if (TryRegionForProjectionMapId(map.uniqueID, out RealityRegionId previousId) && previousId != regionId)
            {
                QuarantineInternal("map", map.uniqueID.ToString(), regionId.ProviderNamespace,
                    "A live projection binding cannot be remapped to another region.", previousId.ToString(), Now);
                return default(RealityRegionId);
            }
            RealityRegionDescriptor existingRegion = RegionRecord(regionId.ToString());
            if (existingRegion != null && existingRegion.projectionMapUniqueId >= 0 && existingRegion.projectionMapUniqueId != map.uniqueID)
            {
                QuarantineInternal("map", map.uniqueID.ToString(), regionId.ProviderNamespace,
                    "A live map identity is already claimed by another map.", regionId.ToString(), Now);
                return default(RealityRegionId);
            }
            EnsureRegion(regionId, "World tile " + regionId.WorldTile, Now);
            if (!MarkMapActive(regionId, map))
            {
                return default(RealityRegionId);
            }
            Touch("map.mapped", regionId.ProviderNamespace, regionId.ToString(), map.uniqueID.ToString());
            return regionId;
        }

        /// <summary>Releases the live projection binding while preserving latent state.</summary>
        public void UnregisterMap(Map map)
        {
            RealityThreadGuard.RequireMainThread();
            if (map == null || !TryRegionForProjectionMapId(map.uniqueID, out RealityRegionId regionId)) return;
            RealityRegionDescriptor descriptor = RegionRecord(regionId.ToString());
            if (descriptor == null || descriptor.projectionMapUniqueId != map.uniqueID) return;
            descriptor.projectionMapUniqueId = -1;
            descriptor.authority = RealityRegionAuthority.Quarantined == descriptor.authority
                ? descriptor.authority : RealityRegionAuthority.Latent;
            descriptor.fidelity = populations.Any(item => item?.regionId == descriptor.regionId)
                ? RealityFidelity.Statistical : RealityFidelity.Dormant;
            descriptor.lastUpdateTick = Now;
            Touch("map.unmapped", "core", descriptor.regionId, map.uniqueID.ToString());
        }

        /// <summary>Looks up the stable region whose live projection owns a Map.uniqueID.</summary>
        public bool TryRegionForProjectionMapId(int mapId, out RealityRegionId regionId)
        {
            EnsureIndexes();
            regionId = default(RealityRegionId);
            return regionByProjectionMapId.TryGetValue(mapId, out string value) && RealityRegionId.TryParse(value, out regionId);
        }

        /// <summary>Returns whether a mutation operation has already been applied.</summary>
        public bool HasAppliedOperation(string operationId)
        {
            EnsureIndexes();
            return !string.IsNullOrEmpty(operationId) && appliedOperationIds.Contains(operationId);
        }

        /// <summary>Returns whether a sequenced operation may be accepted before state mutation.</summary>
        public bool ValidateOperationSequence(string providerId, string kind, string domainId, long sequence,
            out string diagnostic)
        {
            diagnostic = null;
            string normalizedProvider = providerId?.Trim();
            string normalizedKind = kind?.Trim();
            string normalizedDomain = domainId?.Trim();
            if (sequence < 0)
            {
                bool sequencedDomain = operationWatermarks.Any(item => item != null && item.sequenceMode &&
                    item.providerId == normalizedProvider && item.kind == normalizedKind && item.domainId == normalizedDomain);
                if (!sequencedDomain) return true;
                diagnostic = "The declared operation domain requires a sequence.";
                return false;
            }
            if (string.IsNullOrEmpty(normalizedProvider) || string.IsNullOrEmpty(normalizedKind) ||
                string.IsNullOrEmpty(normalizedDomain))
            {
                diagnostic = "A sequenced operation requires provider, kind, and domain identities.";
                return false;
            }
            RealityOperationRetentionWatermark cursor = operationWatermarks.FirstOrDefault(item => item != null &&
                item.providerId == normalizedProvider && item.kind == normalizedKind && item.domainId == normalizedDomain);
            if (cursor == null || !cursor.sequenceMode)
            {
                diagnostic = "The operation domain has no declared durable sequence cursor.";
                return false;
            }
            if (!RealityExactlyOncePolicy.CanAccept(cursor.sequenceCursor, sequence, cursor.allowGaps))
            {
                diagnostic = cursor.allowGaps
                    ? "The operation sequence is not greater than the accepted cursor."
                    : "The operation sequence is not the next contiguous sequence.";
                return false;
            }
            if (appliedOperations.Any(item => item != null && item.sequence == sequence &&
                item.providerId == normalizedProvider && item.kind == normalizedKind && item.domainId == normalizedDomain))
            {
                diagnostic = "The operation sequence is already represented by a durable marker.";
                return false;
            }
            return true;
        }

        /// <summary>Persists an exactly-once operation marker after sequence validation.</summary>
        public bool RecordAppliedOperation(string operationId, string providerId, string kind, long tick,
            string domainId = null, long sequence = -1)
        {
            RealityThreadGuard.RequireMainThread();
            if (string.IsNullOrEmpty(operationId)) return false;
            EnsureIndexes();
            if (!ValidateOperationSequence(providerId, kind, domainId, sequence, out _)) return false;
            if (!appliedOperationIds.Add(operationId)) return false;
            appliedOperations.Add(new RealityAppliedOperation
            {
                operationId = operationId,
                providerId = providerId,
                domainId = string.IsNullOrWhiteSpace(domainId) ? null : domainId.Trim(),
                sequence = sequence,
                kind = kind,
                tick = tick,

            });
            Touch("operation.applied", providerId, null, operationId);
            MarkStorageMaintenanceDirty(tick);
            return true;
        }

        /// <summary>Publishes provider proof that a replay domain has advanced past a durable tick.</summary>
        public bool UpsertOperationRetentionWatermark(RealityOperationRetentionWatermark value)
        {
            RealityThreadGuard.RequireMainThread();
            if (value == null || string.IsNullOrWhiteSpace(value.providerId) || string.IsNullOrWhiteSpace(value.kind) ||
                string.IsNullOrWhiteSpace(value.domainId) || (!value.sequenceMode && value.safeThroughTick < 0) ||
                (value.sequenceMode && (value.sequenceCursor < -1 || string.IsNullOrWhiteSpace(value.proof)))) return false;
            value.providerId = value.providerId.Trim();
            value.kind = value.kind.Trim();
            value.domainId = value.domainId.Trim();
            value.updatedTick = Math.Max(value.updatedTick, Now);
            int index = operationWatermarks.FindIndex(item => item != null && item.providerId == value.providerId &&
                item.kind == value.kind && item.domainId == value.domainId);
            if (index >= 0)
            {
                RealityOperationRetentionWatermark existing = operationWatermarks[index];
                if (existing.sequenceMode && !value.sequenceMode) return false;
                if (existing.sequenceMode && value.sequenceMode && existing.sequenceCursor > value.sequenceCursor) return false;
                if (!existing.sequenceMode && !value.sequenceMode && existing.safeThroughTick > value.safeThroughTick) return false;
                operationWatermarks[index] = value.Clone();
            }
            else operationWatermarks.Add(value.Clone());
            Touch("operation.watermark", value.providerId, value.kind, value.domainId);
            MarkStorageMaintenanceDirty(value.updatedTick);
            return true;
        }

        /// <summary>Declares a provider/domain sequence cursor. Tick history alone is never upgraded implicitly.</summary>
        public bool DeclareExactlyOnceDomain(string providerId, string kind, string domainId, bool allowGaps, string proof)
        {
            RealityThreadGuard.RequireMainThread();
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(kind) ||
                string.IsNullOrWhiteSpace(domainId) || string.IsNullOrWhiteSpace(proof)) return false;
            if (RealityProviderRegistry.TryGetCapability(providerId.Trim(), out IRealityExactlyOnceProvider descriptor) &&
                (!descriptor.TryDescribeExactlyOnceDomain(kind.Trim(), domainId.Trim(), out RealityExactlyOnceDomain domain) ||
                 domain == null || domain.kind != kind.Trim() || domain.domainId != domainId.Trim() ||
                 domain.allowGaps != allowGaps)) return false;
            RealityOperationRetentionWatermark existing = operationWatermarks.FirstOrDefault(item => item != null &&
                item.providerId == providerId.Trim() && item.kind == kind.Trim() && item.domainId == domainId.Trim());
            if (existing != null && existing.sequenceMode) return true;
            if (existing != null && existing.sequenceCursor >= 0) return false;
            return UpsertOperationRetentionWatermark(new RealityOperationRetentionWatermark
            {
                providerId = providerId.Trim(),
                kind = kind.Trim(),
                domainId = domainId.Trim(),
                sequenceMode = true,
                sequenceCursor = -1,
                allowGaps = allowGaps,
                proof = proof.Trim(),
                updatedTick = Now
            });
        }

        /// <summary>Advances a declared cursor only after its matching sequenced marker is durable.</summary>
        public bool AdvanceExactlyOnceCursor(string providerId, string kind, string domainId, long sequence, string proof)
        {
            RealityThreadGuard.RequireMainThread();
            RealityOperationRetentionWatermark cursor = operationWatermarks.FirstOrDefault(item => item != null &&
                item.providerId == providerId?.Trim() && item.kind == kind?.Trim() && item.domainId == domainId?.Trim());
            if (cursor == null || !cursor.sequenceMode || string.IsNullOrWhiteSpace(proof) || sequence < 0) return false;
            if (sequence == cursor.sequenceCursor) return true;
            if (!RealityExactlyOncePolicy.CanAdvance(cursor.sequenceCursor, sequence, cursor.allowGaps)) return false;
            if (!appliedOperations.Any(item => item != null && item.providerId == cursor.providerId &&
                item.kind == cursor.kind && item.domainId == cursor.domainId && item.sequence == sequence)) return false;
            cursor.sequenceCursor = sequence;
            cursor.safeThroughTick = Math.Max(cursor.safeThroughTick, Now);
            cursor.updatedTick = Now;
            cursor.proof = proof.Trim();
            Touch("operation.cursor", cursor.providerId, cursor.kind, cursor.domainId);
            MarkStorageMaintenanceDirty(Now);
            return true;
        }

        /// <summary>Returns detached, deterministic provider replay-watermark records.</summary>
        public IReadOnlyList<RealityOperationRetentionWatermark> OperationWatermarkSnapshots(string providerId = null)
        {
            return operationWatermarks.Where(item => item != null && (string.IsNullOrEmpty(providerId) || item.providerId == providerId))
                .OrderBy(item => item.providerId, StringComparer.Ordinal).ThenBy(item => item.kind, StringComparer.Ordinal)
                .ThenBy(item => item.domainId, StringComparer.Ordinal).Select(item => item.Clone()).ToList();
        }

        /// <summary>Adds durable audit data for unsupported or corrupted records.</summary>
        public void Quarantine(string recordType, string recordId, string providerId, string reason, string payload = null)
        {
            RealityThreadGuard.RequireMainThread();
            QuarantineInternal(recordType, recordId, providerId, reason, payload, Now);
            Touch("record.quarantined", providerId, null, recordId);
        }

        /// <summary>Returns a detached list of quarantine records.</summary>
        public IReadOnlyList<RealityQuarantineRecord> QuarantineSnapshots()
        {
            return quarantine.Where(item => item != null)
                .OrderByDescending(item => item.detectedTick)
                .ThenBy(item => QuarantineKey(item), StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        /// <summary>Returns detached conflict reports.</summary>
        public IReadOnlyList<RealityConflictReport> ConflictSnapshots()
        {
            return conflicts.Where(item => item != null).OrderByDescending(item => item.detectedTick)
                .ThenBy(item => item.conflictId, StringComparer.Ordinal).Select(item => item.Clone()).ToList();
        }

        /// <summary>Returns detached transfer journals for interrupted-transition recovery.</summary>
        public IReadOnlyList<RealityTransferJournalRecord> TransferJournalSnapshots()
        {
            return transferJournals.Where(item => item != null).OrderByDescending(item => item.updatedTick)
                .ThenBy(item => item.transferId, StringComparer.Ordinal).Select(item => item.Clone()).ToList();
        }

        /// <summary>Finds a detached transfer journal by stable transaction ID.</summary>
        public bool TryGetTransferJournal(string transferId, out RealityTransferJournalRecord journal)
        {
            EnsureIndexes();
            RealityTransferJournalRecord value = transferJournals.FirstOrDefault(item => item?.transferId == transferId);
            journal = value?.Clone();
            return journal != null;
        }

        /// <summary>Creates or updates a durable transfer journal.</summary>
        public bool UpsertTransferJournal(RealityTransferJournalRecord value)
        {
            RealityThreadGuard.RequireMainThread();
            if (value == null || string.IsNullOrEmpty(value.transferId)) return false;
            int index = transferJournals.FindIndex(item => item?.transferId == value.transferId);
            if (index >= 0) transferJournals[index] = value.Clone();
            else transferJournals.Add(value.Clone());
            Touch("transfer.journal", "core", value.sourceRegionId, value.transferId);
            MarkStorageMaintenanceDirty(Now);
            return true;
        }

        /// <summary>Authorizes one provider-owned adjacent map creation before the host starts map generation.</summary>
        internal bool BeginMapCreationIntent(string transactionId, RealityRegionId regionId,
            RealityAdjacentMapMetadata metadata, out string diagnostic)
        {
            RealityThreadGuard.RequireMainThread();
            diagnostic = null;
            if (string.IsNullOrWhiteSpace(transactionId) || !regionId.IsValid || metadata == null ||
                string.IsNullOrWhiteSpace(metadata.providerId) || !metadata.originRegionId.IsValid ||
                metadata.originMapUniqueId < 0 || !RealityProviderRegistry.TryGet(metadata.providerId.Trim(), out _))
            {
                diagnostic = "Adjacent map creation intent has incomplete ownership or origin identity.";
                return false;
            }
            EnsureIndexes();
            string owner = metadata.providerId.Trim();
            string regionKey = regionId.ToString();
            string originKey = metadata.originRegionId.ToString();
            if (mapCreationIntentById.TryGetValue(transactionId, out RealityMapCreationIntentRecord existing))
            {
                if (existing.providerId == owner && existing.regionId == regionKey &&
                    existing.originRegionId == originKey && existing.originMapUniqueId == metadata.originMapUniqueId)
                    return true;
                diagnostic = "A materialization transaction ID is already bound to a different adjacent site.";
                QuarantineInternal("map-creation-intent", transactionId, owner, diagnostic, regionKey, Now);
                return false;
            }
            if (mapCreationIntents.Any(item => item != null && item.providerId == owner && item.regionId == regionKey))
            {
                diagnostic = "Another adjacent map creation intent already owns the requested region and provider.";
                QuarantineInternal("map-creation-intent", transactionId, owner, diagnostic, regionKey, Now);
                return false;
            }
            metadata.transactionId = transactionId;
            if (metadata.createdTick < 0) metadata.createdTick = Now;
            if (!Enum.IsDefined(typeof(RealityAdjacentMapLifecycle), metadata.lifecycle))
                metadata.lifecycle = RealityAdjacentMapLifecycle.Materializing;
            var intent = new RealityMapCreationIntentRecord
            {
                transactionId = transactionId,
                providerId = owner,
                regionId = regionKey,
                originRegionId = originKey,
                originMapUniqueId = metadata.originMapUniqueId,
                preexistingMapUniqueId = (Find.Maps ?? Enumerable.Empty<Map>())
                    .Where(item => item != null && item.Tile.Valid && (int)item.Tile == regionId.WorldTile)
                    .Select(item => item.uniqueID).DefaultIfEmpty(-1).First(),
                createdTick = metadata.createdTick,
                lifecycle = RealityAdjacentMapLifecycle.Materializing
            };
            mapCreationIntents.Add(intent);
            mapCreationIntentById[transactionId] = intent;
            activeMapCreationTransactions.Add(transactionId);
            Touch("adjacent-map.intent-created", owner, regionKey, transactionId);
            return true;
        }

        /// <summary>Binds the intent to the map identity returned by the host factory.</summary>
        internal bool BindMapCreationIntent(string transactionId, Map map)
        {
            RealityThreadGuard.RequireMainThread();
            if (map == null || string.IsNullOrEmpty(transactionId)) return false;
            if (!mapCreationIntentById.TryGetValue(transactionId, out RealityMapCreationIntentRecord intent))
                return IsAdjacentMap(map);
            if (!map.Tile.Valid || !RealityRegionId.TryParse(intent.regionId, out RealityRegionId region) ||
                map.Tile != (PlanetTile)region.WorldTile) return false;
            if (intent.createdMapUniqueId >= 0 && intent.createdMapUniqueId != map.uniqueID) return false;
            intent.createdMapUniqueId = map.uniqueID;
            Touch("adjacent-map.intent-bound", intent.providerId, intent.regionId, map.uniqueID.ToString());
            return true;
        }

        /// <summary>Clears a creation intent after commit, rollback, or explicit recovery.</summary>
        internal void ClearMapCreationIntent(string transactionId, string diagnostic = null, bool quarantine = false)
        {
            RealityThreadGuard.RequireMainThread();
            if (string.IsNullOrEmpty(transactionId) || !mapCreationIntentById.TryGetValue(transactionId,
                out RealityMapCreationIntentRecord intent)) return;
            if (quarantine && !string.IsNullOrEmpty(diagnostic))
                QuarantineInternal("map-creation-intent", transactionId, intent.providerId, diagnostic, intent.regionId, Now);
            mapCreationIntentById.Remove(transactionId);
            activeMapCreationTransactions.Remove(transactionId);
            mapCreationIntents.RemoveAll(item => item != null && item.transactionId == transactionId);
            Touch("adjacent-map.intent-cleared", intent.providerId, intent.regionId, transactionId);
        }

        /// <summary>Returns detached creation intents for diagnostics and deterministic save recovery.</summary>
        public IReadOnlyList<RealityMapCreationIntentRecord> MapCreationIntentSnapshots()
        {
            EnsureIndexes();
            return mapCreationIntents.Where(item => item != null)
                .OrderBy(item => item.transactionId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        private bool TryRegisterMapFromCreationIntent(Map map, RealityRegionId requestedRegion,
            out RealityRegionId regionId, out bool intentHandled)
        {
            regionId = default(RealityRegionId);
            intentHandled = false;
            if (map == null || !map.Tile.Valid) return false;
            List<RealityMapCreationIntentRecord> candidates = mapCreationIntents.Where(item =>
                item != null && RealityRegionId.TryParse(item.regionId, out RealityRegionId expected) &&
                expected.WorldTile == (int)map.Tile).ToList();
            if (candidates.Count == 0) return false;
            intentHandled = true;
            if (candidates.Count != 1)
            {
                string diagnostic = "Multiple adjacent map creation intents matched one map tile.";
                foreach (RealityMapCreationIntentRecord candidate in candidates.ToList())
                    ClearMapCreationIntent(candidate.transactionId, diagnostic, true);
                return false;
            }
            RealityMapCreationIntentRecord intent = candidates[0];
            if (!RealityRegionId.TryParse(intent.regionId, out RealityRegionId expectedRegion) ||
                (requestedRegion.IsValid && requestedRegion != expectedRegion) ||
                (intent.createdMapUniqueId >= 0 && intent.createdMapUniqueId != map.uniqueID) ||
                (intent.preexistingMapUniqueId >= 0 && intent.preexistingMapUniqueId == map.uniqueID &&
                    !adjacentMapById.ContainsKey(map.uniqueID)))
            {
                ClearMapCreationIntent(intent.transactionId,
                    "A map did not match the region or identity recorded by its creation intent.", true);
                return false;
            }
            if (!TryResolveMapIdentity(map, out RealityRegionId resolvedRegion, out RealityMapIdentityClaim claim) ||
                resolvedRegion != expectedRegion ||
                !RealityMapCreationPolicy.IsOwnerClaimCompatible(expectedRegion, intent.providerId, claim) ||
                !RealityMapCreationPolicy.CanClassifyMap(intent.preexistingMapUniqueId == map.uniqueID,
                    adjacentMapById.ContainsKey(map.uniqueID), intent.transactionId, intent.createdMapUniqueId, map.uniqueID))
            {
                ClearMapCreationIntent(intent.transactionId,
                    "A generated map did not provide the expected provider identity claim.", true);
                return false;
            }
            if (!RegisterAdjacentMapFromCreationIntent(map, expectedRegion, intent))
            {
                ClearMapCreationIntent(intent.transactionId,
                    "A generated map could not be atomically classified as its intended adjacent site.", true);
                return false;
            }
            regionId = expectedRegion;
            return true;
        }

        private bool RegisterAdjacentMapFromCreationIntent(Map map, RealityRegionId regionId,
            RealityMapCreationIntentRecord intent)
        {
            if (map == null || intent == null || intent.lifecycle != RealityAdjacentMapLifecycle.Materializing ||
                !regionId.IsValid || intent.createdMapUniqueId >= 0 &&
                intent.createdMapUniqueId != map.uniqueID) return false;
            if (!RealityProviderRegistry.TryGet(intent.providerId, out _)) return false;
            RealityAdjacentMapRecord existing = adjacentMapById.TryGetValue(map.uniqueID, out RealityAdjacentMapRecord value)
                ? value : null;
            if (existing != null && (existing.regionId != regionId.ToString() || existing.providerId != intent.providerId ||
                !string.IsNullOrEmpty(existing.originRegionId) && existing.originRegionId != intent.originRegionId ||
                existing.originMapUniqueId >= 0 && existing.originMapUniqueId != intent.originMapUniqueId)) return false;
            if (TryRegionForProjectionMapId(map.uniqueID, out RealityRegionId mappedRegion) && mappedRegion != regionId) return false;
            RealityRegionDescriptor descriptor = RegionRecord(regionId.ToString());
            if (descriptor != null && descriptor.projectionMapUniqueId >= 0 && descriptor.projectionMapUniqueId != map.uniqueID) return false;
            EnsureRegion(regionId, "World tile " + regionId.WorldTile, Now);
            if (!MarkMapActive(regionId, map))
            {
                return false;
            }
            RealityAdjacentMapRecord record = existing ?? new RealityAdjacentMapRecord { mapUniqueId = map.uniqueID };
            if (string.IsNullOrEmpty(record.transactionId)) record.transactionId = intent.transactionId;
            record.providerId = intent.providerId;
            record.regionId = regionId.ToString();
            record.originRegionId = intent.originRegionId;
            record.originMapUniqueId = intent.originMapUniqueId;
            record.createdTick = intent.createdTick;
            record.lastAccessTick = Now;
            record.lifecycle = RealityAdjacentMapLifecycle.Active;
            record.diagnostic = null;
            if (existing == null) adjacentMaps.Add(record);
            adjacentMapById[record.mapUniqueId] = record;
            mapCreationIntentById.Remove(intent.transactionId);
            activeMapCreationTransactions.Remove(intent.transactionId);
            mapCreationIntents.RemoveAll(item => item != null && item.transactionId == intent.transactionId);
            Touch("adjacent-map.marked", record.providerId, record.regionId, record.mapUniqueId.ToString());
            return true;
        }

        /// <summary>Marks a generated map as a temporary adjacent site using explicit typed metadata.</summary>
        public bool MarkAdjacentMap(Map map, RealityRegionId regionId, RealityAdjacentMapMetadata metadata)
        {
            RealityThreadGuard.RequireMainThread();
            if (map == null || !regionId.IsValid || metadata == null || string.IsNullOrEmpty(metadata.providerId) ||
                !metadata.originRegionId.IsValid || metadata.originMapUniqueId < 0 ||
                !RealityProviderRegistry.TryGet(metadata.providerId.Trim(), out _)) return false;
            EnsureIndexes();
            string owner = metadata.providerId.Trim();
            if (metadata.createdTick < 0) metadata.createdTick = Now;
            if (adjacentMapById.TryGetValue(map.uniqueID, out RealityAdjacentMapRecord existing) &&
                (!string.Equals(existing.regionId, regionId.ToString(), StringComparison.Ordinal) ||
                 !string.Equals(existing.providerId, owner, StringComparison.Ordinal) ||
                 !string.IsNullOrEmpty(existing.originRegionId) &&
                 !string.Equals(existing.originRegionId, metadata.originRegionId.ToString(), StringComparison.Ordinal) ||
                 existing.originMapUniqueId >= 0 && existing.originMapUniqueId != metadata.originMapUniqueId))
            {
                QuarantineInternal("adjacent-map", map.uniqueID.ToString(), owner,
                    "A live map attempted to change its persisted adjacent role.", regionId.ToString(), Now);
                return false;
            }
            if (existing == null && !string.IsNullOrEmpty(metadata.transactionId))
            {
                if (TryRegisterMapFromCreationIntent(map, regionId, out RealityRegionId intended, out bool handled))
                    return intended == regionId;
                if (handled) return false;
            }
            if (existing == null)
            {
                QuarantineInternal("adjacent-map", map.uniqueID.ToString(), owner,
                    "Only the active materialization intent may classify a new map as an adjacent site.", regionId.ToString(), Now);
                return false;
            }
            RealityRegionId mapped = RegisterMap(map, regionId);
            if (mapped != regionId) return false;
            RealityAdjacentMapRecord record = existing ?? new RealityAdjacentMapRecord { mapUniqueId = map.uniqueID };
            record.transactionId = existing == null || string.IsNullOrEmpty(record.transactionId)
                ? metadata.transactionId : record.transactionId;
            record.providerId = owner;
            record.regionId = regionId.ToString();
            record.originRegionId = metadata.originRegionId.ToString();
            record.originMapUniqueId = metadata.originMapUniqueId;
            record.createdTick = existing == null ? metadata.createdTick : record.createdTick;
            record.lastAccessTick = Now;
            record.lifecycle = RealityAdjacentMapLifecycle.Active;
            record.diagnostic = null;
            if (existing == null) adjacentMaps.Add(record);
            adjacentMapById[record.mapUniqueId] = record;
            Touch("adjacent-map.marked", record.providerId, record.regionId, record.mapUniqueId.ToString());
            return true;
        }

        /// <summary>Returns whether a live map has the explicit temporary adjacent role.</summary>
        public bool IsAdjacentMap(Map map)
        {
            return map != null && TryGetAdjacentMapRecord(map.uniqueID, out _);
        }

        /// <summary>Returns a detached adjacent role marker by map ID.</summary>
        public bool TryGetAdjacentMapRecord(int mapUniqueId, out RealityAdjacentMapRecord record)
        {
            EnsureIndexes();
            if (adjacentMapById.TryGetValue(mapUniqueId, out RealityAdjacentMapRecord value) &&
                value.lifecycle != RealityAdjacentMapLifecycle.Retired)
            {
                record = value.Clone();
                return true;
            }
            record = null;
            return false;
        }

        /// <summary>Returns detached adjacent role markers in stable order.</summary>
        public IReadOnlyList<RealityAdjacentMapRecord> AdjacentMapSnapshots()
        {
            EnsureIndexes();
            return adjacentMaps.Where(item => item != null)
                .OrderBy(item => item.mapUniqueId)
                .Select(item => item.Clone()).ToList();
        }

        /// <summary>Updates recency without changing the persisted role or map identity.</summary>
        public void TouchAdjacentMap(int mapUniqueId, long tick = -1)
        {
            EnsureIndexes();
            if (!adjacentMapById.TryGetValue(mapUniqueId, out RealityAdjacentMapRecord record)) return;
            long value = tick >= 0 ? tick : Now;
            if (record.lastAccessTick >= value) return;
            record.lastAccessTick = value;
            Touch("adjacent-map.access", record.providerId, record.regionId, record.mapUniqueId.ToString());
        }

        /// <summary>Marks a successfully removed adjacent map as retired while retaining its audit identity.</summary>
        public bool RetireAdjacentMap(int mapUniqueId, string diagnostic = null)
        {
            RealityThreadGuard.RequireMainThread();
            EnsureIndexes();
            if (!adjacentMapById.TryGetValue(mapUniqueId, out RealityAdjacentMapRecord record)) return false;
            record.lifecycle = RealityAdjacentMapLifecycle.Retired;
            record.diagnostic = diagnostic;
            record.retiredTick = Now;
            adjacentMapById.Remove(mapUniqueId);
            Touch("adjacent-map.retired", record.providerId, record.regionId, mapUniqueId.ToString());
            return true;
        }

        /// <summary>Begins a runtime-only lease. It becomes save-visible only after outbound transfer commit.</summary>
        public bool BeginExcursion(RealityExcursionRequest request, out string excursionId, out string diagnostic)
        {
            RealityThreadGuard.RequireMainThread();
            EnsureIndexes();
            excursionId = null;
            diagnostic = null;
            if (request == null || string.IsNullOrEmpty(request.providerId) || string.IsNullOrEmpty(request.pawnLoadId) ||
                 !request.originRegionId.IsValid || !request.destinationRegionId.IsValid || request.originMapUniqueId < 0 ||
                 request.destinationMapUniqueId < 0)
            {
                diagnostic = "An excursion requires stable provider, pawn, origin, and destination identities.";
                return false;
            }
            excursionId = string.IsNullOrEmpty(request.excursionId)
                ? "excursion:" + RealityDeterminism.Combine(request.providerId, request.pawnLoadId,
                    request.originRegionId.ToString(), request.destinationRegionId.ToString(), request.outboundTransferId)
                : request.excursionId;
            if (excursionById.TryGetValue(excursionId, out RealityExcursionTicket existing))
            {
                if (existing.status == RealityExcursionStatus.Quarantined)
                {
                    diagnostic = "The excursion is quarantined and cannot acquire new ownership.";
                    return false;
                }
                if (!ExcursionMatches(existing, request))
                {
                    diagnostic = "The excursion ID is already owned by a different excursion.";
                    return false;
                }
                return true;
            }
            if (pendingExcursions.TryGetValue(excursionId, out RealityExcursionTicket pending))
            {
                if (!ExcursionMatches(pending, request))
                {
                    diagnostic = "The pending excursion ID is already bound to different origin or destination data.";
                    return false;
                }
                return true;
            }
            if (HasActiveExcursionForPawn(request.pawnLoadId, excursionId))
            {
                diagnostic = "The Pawn already has an active adjacent excursion ticket.";
                return false;
            }
            pendingExcursions[excursionId] = new RealityExcursionTicket
            {
                excursionId = excursionId,
                providerId = request.providerId,
                pawnLoadId = request.pawnLoadId,
                originRegionId = request.originRegionId.ToString(),
                originMapUniqueId = request.originMapUniqueId,
                destinationRegionId = request.destinationRegionId.ToString(),
                destinationMapUniqueId = request.destinationMapUniqueId,
                originCellX = request.originCell.x,
                originCellZ = request.originCell.z,
                 inverseReturnEdge = request.inverseReturnEdge,
                 taskId = string.IsNullOrWhiteSpace(request.taskId) ? excursionId : request.taskId.Trim(),
                outboundTransferId = request.outboundTransferId,
                returnTransferId = request.returnTransferId,
                startTick = request.startTick >= 0 ? request.startTick : Now,
                graceDeadline = request.graceDeadline >= 0 ? request.graceDeadline : Now + RealityAdjacentPolicy.DefaultGraceTicks,
                lastTaskHeartbeat = request.startTick >= 0 ? request.startTick : Now,
                retryTick = Now,
                status = RealityExcursionStatus.Active
            };
            return true;
        }

        /// <summary>Attaches a stable pawn identity to a pending or committed excursion.</summary>
        public bool AttachExcursion(string excursionId, string pawnLoadId, out string diagnostic)
        {
            RealityThreadGuard.RequireMainThread();
            EnsureIndexes();
            diagnostic = null;
            if (string.IsNullOrEmpty(excursionId) || string.IsNullOrEmpty(pawnLoadId))
            {
                diagnostic = "A stable excursion and pawn identity are required.";
                return false;
            }
            if (pendingExcursions.TryGetValue(excursionId, out RealityExcursionTicket pending))
            {
                if (!string.IsNullOrEmpty(pending.pawnLoadId) &&
                    !string.Equals(pending.pawnLoadId, pawnLoadId, StringComparison.Ordinal))
                {
                    diagnostic = "A pending excursion cannot be rebound to a different Pawn.";
                    return false;
                }
                if (pendingExcursionPawns.TryGetValue(excursionId, out Pawn attachedPawn) &&
                    !string.Equals(attachedPawn?.GetUniqueLoadID(), pawnLoadId, StringComparison.Ordinal))
                {
                    diagnostic = "A pending excursion is already attached to a different Pawn instance.";
                    return false;
                }
                if (HasActiveExcursionForPawn(pawnLoadId, excursionId))
                {
                    diagnostic = "The Pawn already has another active adjacent excursion ticket.";
                    return false;
                }
                pending.pawnLoadId = pawnLoadId;
                return true;
            }
            if (!excursionById.TryGetValue(excursionId, out RealityExcursionTicket ticket))
            {
                diagnostic = "No pending or committed excursion has that ID.";
                return false;
            }
            if (ticket.status == RealityExcursionStatus.Quarantined)
            {
                diagnostic = "The excursion is quarantined and cannot be reattached.";
                return false;
            }
            if (!string.Equals(ticket.pawnLoadId, pawnLoadId, StringComparison.Ordinal))
            {
                diagnostic = "The committed excursion pawn identity cannot be changed.";
                return false;
            }
            return true;
        }

        /// <summary>Convenience overload for integrations holding the actual Pawn instance.</summary>
        public bool AttachExcursion(string excursionId, Pawn pawn, out string diagnostic)
        {
            if (!AttachExcursion(excursionId, pawn?.GetUniqueLoadID(), out diagnostic)) return false;
            if (pawn != null && pendingExcursions.ContainsKey(excursionId)) pendingExcursionPawns[excursionId] = pawn;
            return true;
        }

        /// <summary>Creates the durable ticket immediately before the outbound journal is completed.</summary>
        internal bool CommitOutboundExcursion(string excursionId, RealityAdjacentTransferRequest request,
            RealityTransferJournalRecord journal, out string diagnostic)
        {
            RealityThreadGuard.RequireMainThread();
            EnsureIndexes();
            diagnostic = null;
            if (!pendingExcursions.TryGetValue(excursionId ?? string.Empty, out RealityExcursionTicket pending))
            {
                diagnostic = "No pending excursion lease exists for the outbound transfer.";
                return false;
            }
            if (request == null || journal == null || !request.isOutboundExcursion || request.pawns == null ||
                request.pawns.Count != 1 || !string.Equals(journal.transferId, request.transferId, StringComparison.Ordinal) ||
                !string.Equals(journal.excursionId, pending.excursionId, StringComparison.Ordinal) ||
                !string.Equals(request.transferId, pending.outboundTransferId, StringComparison.Ordinal) ||
                !string.Equals(request.providerTaskId ?? string.Empty, pending.taskId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(request.providerId, pending.providerId, StringComparison.Ordinal) ||
                !string.Equals(journal.providerId, pending.providerId, StringComparison.Ordinal) ||
                !string.Equals(journal.providerTaskId ?? string.Empty, pending.taskId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(journal.sourceRegionId, pending.originRegionId, StringComparison.Ordinal) ||
                !string.Equals(journal.destinationRegionId, pending.destinationRegionId, StringComparison.Ordinal) ||
                request.sourceMap == null || request.destinationMap == null ||
                request.sourceMap.uniqueID != pending.originMapUniqueId ||
                request.destinationMap.uniqueID != pending.destinationMapUniqueId ||
                request.sourceRegionId.ToString() != pending.originRegionId ||
                request.destinationRegionId.ToString() != pending.destinationRegionId ||
                journal.sourceMapUniqueId != pending.originMapUniqueId ||
                journal.destinationMapUniqueId != pending.destinationMapUniqueId)
            {
                diagnostic = "The outbound transfer does not match the pending excursion ownership record.";
                return false;
            }
            Pawn pawn = request.pawns[0];
            if (pendingExcursionPawns.TryGetValue(pending.excursionId, out Pawn expectedPawn) &&
                !ReferenceEquals(expectedPawn, pawn))
            {
                diagnostic = "The outbound transfer did not preserve the exact Pawn instance attached to the excursion.";
                return false;
            }
            if (pawn == null || pawn.GetUniqueLoadID() != pending.pawnLoadId)
            {
                diagnostic = "The outbound transfer contains a different Pawn than the excursion ticket.";
                return false;
            }
            if (pawn?.Spawned != true || pawn.Map == null || pawn.Map.uniqueID != pending.destinationMapUniqueId)
            {
                diagnostic = "The outbound transfer did not leave the tracked Pawn instance on the declared destination map.";
                return false;
            }
            pending.status = RealityExcursionStatus.Active;
            pending.lastTaskHeartbeat = Now;
            pending.retryTick = Now;
            if (!excursionById.ContainsKey(pending.excursionId)) excursions.Add(pending);
            excursionById[pending.excursionId] = pending;
            activeExcursionById[pending.excursionId] = pending;
            excursionPawnInstances[pending.excursionId] = pawn;
            pendingExcursions.Remove(pending.excursionId);
            pendingExcursionPawns.Remove(pending.excursionId);
            Touch("excursion.committed", pending.providerId, pending.destinationRegionId, pending.excursionId);
            return true;
        }

        /// <summary>Recreates a save-visible ticket from an actual Pawn found on its exact origin map.</summary>
        internal bool RecoverCompletedExcursion(RealityExcursionRequest request, Pawn pawn, string diagnostic,
            out string error)
        {
            RealityThreadGuard.RequireMainThread();
            error = null;
            if (request == null || pawn == null || pawn.GetUniqueLoadID() != request.pawnLoadId ||
                pawn.Map == null || pawn.Map.uniqueID != request.originMapUniqueId)
            {
                error = "The recovered Pawn is not the declared Pawn on its exact origin map.";
                return false;
            }
            if (!BeginExcursion(request, out string excursionId, out error)) return false;
            if (!pendingExcursions.TryGetValue(excursionId, out RealityExcursionTicket ticket))
            {
                if (excursionById.TryGetValue(excursionId, out RealityExcursionTicket existing) &&
                    RealityRetentionPolicy.IsTerminalExcursion(existing)) return true;
                if (excursionById.TryGetValue(excursionId, out existing) &&
                    pawn.Map.uniqueID == existing.originMapUniqueId)
                {
                    existing.status = RealityExcursionStatus.Completed;
                    existing.retryTick = -1;
                    existing.terminalTick = Now;
                    existing.diagnostic = diagnostic;
                    activeExcursionById.Remove(excursionId);
                    Touch("excursion.recovered", existing.providerId, existing.originRegionId, excursionId);
                    return true;
                }
                error = "The recovered excursion lease was not retained.";
                return false;
            }
            ticket.status = RealityExcursionStatus.Completed;
            ticket.retryTick = -1;
            ticket.terminalTick = Now;
            ticket.diagnostic = diagnostic;
            excursions.Add(ticket);
            excursionById[excursionId] = ticket;
            activeExcursionById.Remove(excursionId);
            pendingExcursions.Remove(excursionId);
            Touch("excursion.recovered", ticket.providerId, ticket.originRegionId, excursionId);
            return true;
        }

        public void CancelPendingExcursion(string excursionId)
        {
            RealityThreadGuard.RequireMainThread();
            if (!string.IsNullOrEmpty(excursionId))
            {
                pendingExcursions.Remove(excursionId);
                pendingExcursionPawns.Remove(excursionId);
            }
        }

        /// <summary>Renews an active task lease without changing pawn ownership.</summary>
        public bool HeartbeatExcursion(string excursionId, long now = -1, long leaseTicks = -1, string diagnostic = null)
        {
            RealityThreadGuard.RequireMainThread();
            if (!excursionById.TryGetValue(excursionId ?? string.Empty, out RealityExcursionTicket ticket) ||
                ticket.status != RealityExcursionStatus.Active) return false;
            long tick = now >= 0 ? now : Now;
            long lease = leaseTicks > 0 ? leaseTicks : RealityAdjacentPolicy.DefaultLeaseTicks;
            ticket.lastTaskHeartbeat = tick;
            ticket.graceDeadline = Math.Max(ticket.graceDeadline, tick + lease);
            ticket.diagnostic = diagnostic;
            if (adjacentMapById.ContainsKey(ticket.destinationMapUniqueId))
                TouchAdjacentMap(ticket.destinationMapUniqueId, tick);
            Touch("excursion.heartbeat", ticket.providerId, ticket.destinationRegionId, ticket.excursionId);
            return true;
        }

        /// <summary>Marks the provider task complete; the monitor performs the actual return.</summary>
        public bool CompleteExcursion(string excursionId, string diagnostic = null)
        {
            return RequestExcursionReturn(excursionId, RealityExcursionStatus.ReturnRequested, diagnostic);
        }

        /// <summary>Cancels the task but still requires the Pawn to return safely before the ticket retires.</summary>
        public bool CancelExcursion(string excursionId, string diagnostic = null)
        {
            return RequestExcursionReturn(excursionId, RealityExcursionStatus.Cancelled, diagnostic);
        }

        /// <summary>Requests the next safe return attempt immediately.</summary>
        public bool RequestImmediateReturn(string excursionId, string diagnostic = null)
        {
            return RequestExcursionReturn(excursionId, RealityExcursionStatus.ReturnRequested, diagnostic);
        }

        private bool RequestExcursionReturn(string excursionId, RealityExcursionStatus status, string diagnostic)
        {
            RealityThreadGuard.RequireMainThread();
            if (!excursionById.TryGetValue(excursionId ?? string.Empty, out RealityExcursionTicket ticket) ||
                RealityRetentionPolicy.IsTerminalExcursion(ticket) || ticket.status == RealityExcursionStatus.Quarantined) return false;
            if (ticket.status == RealityExcursionStatus.Returning)
            {
                if (!string.IsNullOrEmpty(diagnostic)) ticket.diagnostic = diagnostic;
                Touch("excursion.return-requested", ticket.providerId, ticket.destinationRegionId, ticket.excursionId);
                return true;
            }
            ticket.status = status;
            ticket.retryTick = Now;
            ticket.diagnostic = diagnostic;
            if (status == RealityExcursionStatus.ReturnRequested || status == RealityExcursionStatus.Returning ||
                status == RealityExcursionStatus.Cancelled) activeExcursionById[ticket.excursionId] = ticket;
            Touch("excursion.return-requested", ticket.providerId, ticket.destinationRegionId, ticket.excursionId);
            return true;
        }

        internal bool MarkExcursionReturning(string excursionId, long retryTick, string diagnostic)
        {
            RealityThreadGuard.RequireMainThread();
            if (!excursionById.TryGetValue(excursionId ?? string.Empty, out RealityExcursionTicket ticket) ||
                RealityRetentionPolicy.IsTerminalExcursion(ticket) || ticket.status == RealityExcursionStatus.Quarantined) return false;
            ticket.status = RealityExcursionStatus.Returning;
            activeExcursionById[ticket.excursionId] = ticket;
            ticket.retryTick = retryTick;
            ticket.diagnostic = diagnostic;
            Touch("excursion.returning", ticket.providerId, ticket.destinationRegionId, ticket.excursionId);
            return true;
        }

        internal bool MarkExcursionRetry(string excursionId, long retryTick, string diagnostic)
        {
            RealityThreadGuard.RequireMainThread();
            if (!excursionById.TryGetValue(excursionId ?? string.Empty, out RealityExcursionTicket ticket) ||
                RealityRetentionPolicy.IsTerminalExcursion(ticket) || ticket.status == RealityExcursionStatus.Quarantined) return false;
            if (ticket.status != RealityExcursionStatus.Cancelled) ticket.status = RealityExcursionStatus.ReturnRequested;
            activeExcursionById[ticket.excursionId] = ticket;
            ticket.retryTick = retryTick;
            ticket.diagnostic = diagnostic;
            Touch("excursion.retry", ticket.providerId, ticket.destinationRegionId, ticket.excursionId);
            return true;
        }

        internal bool SetExcursionDiagnostic(string excursionId, string diagnostic, long retryTick = -1)
        {
            RealityThreadGuard.RequireMainThread();
            if (!excursionById.TryGetValue(excursionId ?? string.Empty, out RealityExcursionTicket ticket) ||
                RealityRetentionPolicy.IsTerminalExcursion(ticket) || ticket.status == RealityExcursionStatus.Quarantined) return false;
            if (string.Equals(ticket.diagnostic, diagnostic, StringComparison.Ordinal) &&
                (retryTick < 0 || ticket.retryTick == retryTick)) return true;
            ticket.diagnostic = diagnostic;
            if (retryTick >= 0) ticket.retryTick = retryTick;
            Touch("excursion.diagnostic", ticket.providerId, ticket.destinationRegionId, ticket.excursionId);
            return true;
        }

        internal bool MarkExcursionReturned(string excursionId, Pawn pawn, string diagnostic)
        {
            RealityThreadGuard.RequireMainThread();
            if (!excursionById.TryGetValue(excursionId ?? string.Empty, out RealityExcursionTicket ticket) ||
                ticket.status == RealityExcursionStatus.Quarantined || pawn?.GetUniqueLoadID() != ticket.pawnLoadId ||
                pawn.Spawned != true || pawn.Map == null ||
                pawn.Map.uniqueID != ticket.originMapUniqueId) return false;
            if (excursionPawnInstances.TryGetValue(ticket.excursionId, out Pawn expectedPawn) &&
                !ReferenceEquals(expectedPawn, pawn)) return false;
            RealityExcursionStatus returnedStatus = ticket.status == RealityExcursionStatus.Cancelled
                ? RealityExcursionStatus.Cancelled : RealityExcursionStatus.Completed;
            ticket.status = returnedStatus;
            ticket.retryTick = -1;
            ticket.terminalTick = Now;
            ticket.diagnostic = diagnostic;
            excursionPawnInstances.Remove(ticket.excursionId);
            activeExcursionById.Remove(ticket.excursionId);
            if (adjacentMapById.ContainsKey(ticket.originMapUniqueId))
                TouchAdjacentMap(ticket.originMapUniqueId, Now);
            Touch("excursion.completed", ticket.providerId, ticket.originRegionId, ticket.excursionId);
            return true;
        }

        private bool HasActiveExcursionForPawn(string pawnLoadId, string exceptExcursionId)
        {
            return excursions.Any(ticket => ticket != null && ticket.excursionId != exceptExcursionId &&
                ticket.pawnLoadId == pawnLoadId && !RealityRetentionPolicy.IsTerminalExcursion(ticket) &&
                ticket.status != RealityExcursionStatus.Quarantined) ||
                pendingExcursions.Values.Any(ticket => ticket != null && ticket.excursionId != exceptExcursionId &&
                    ticket.pawnLoadId == pawnLoadId);
        }

        private static bool ExcursionMatches(RealityExcursionTicket ticket, RealityExcursionRequest request)
        {
            return ticket != null && request != null && ticket.providerId == request.providerId &&
                ticket.pawnLoadId == request.pawnLoadId && ticket.originRegionId == request.originRegionId.ToString() &&
                ticket.destinationRegionId == request.destinationRegionId.ToString() &&
                ticket.originMapUniqueId == request.originMapUniqueId &&
                ticket.destinationMapUniqueId == request.destinationMapUniqueId &&
                ticket.outboundTransferId == request.outboundTransferId &&
                ticket.returnTransferId == request.returnTransferId &&
                (string.IsNullOrEmpty(request.taskId) || string.IsNullOrEmpty(ticket.taskId) ||
                 ticket.taskId == request.taskId);
        }

        /// <summary>Returns detached tickets for diagnostics and recovery tooling.</summary>
        public IReadOnlyList<RealityExcursionTicket> ExcursionSnapshots()
        {
            EnsureIndexes();
            return excursions.Where(item => item != null).OrderBy(item => item.excursionId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        public bool TryGetExcursion(string excursionId, out RealityExcursionTicket ticket)
        {
            EnsureIndexes();
            if (excursionById.TryGetValue(excursionId ?? string.Empty, out RealityExcursionTicket value))
            {
                ticket = value.Clone();
                return true;
            }
            ticket = null;
            return false;
        }

        /// <summary>Returns detached exactly-once markers; markers without an explicit provider policy remain durable.</summary>
        public IReadOnlyList<RealityAppliedOperation> AppliedOperationSnapshots(string providerId = null)
        {
            return appliedOperations.Where(item => item != null &&
                    (string.IsNullOrEmpty(providerId) || item.providerId == providerId))
                .OrderByDescending(item => item.tick).ThenBy(item => item.operationId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        /// <summary>Runs conservative storage compaction. Interrupted recovery records and unapproved markers are retained.</summary>
        public RealityCompactionReport CompactStorage(long now = -1)
        {
            RealityThreadGuard.RequireMainThread();
            RealityCompactionReport report = CompactStorageInternal(now >= 0 ? now : Now);
            lastCompactionReport = report;
            storageMaintenanceDirty = false;
            storageMaintenanceMutationCount = 0;
            nextStorageMaintenanceTick = (now >= 0 ? now : Now) + StorageMaintenanceIntervalTicks;
            if (report.TotalRemoved > 0) revision++;
            return report;
        }

        /// <summary>Records bounded adjacent diagnostics without making the monitor history unbounded.</summary>
        public void RecordAdjacentDiagnostic(string kind, string providerId, string mapOrExcursionId, string message,
            long tick = -1, bool resolved = false)
        {
            RealityThreadGuard.RequireMainThread();
            long value = tick >= 0 ? tick : Now;
            string diagnosticId = "adjacent:" + RealityDeterminism.Combine(kind, providerId, mapOrExcursionId, message);
            RealityAdjacentDiagnosticRecord record = adjacentDiagnostics.FirstOrDefault(item => item != null &&
                item.diagnosticId == diagnosticId);
            if (record == null)
            {
                record = new RealityAdjacentDiagnosticRecord
                {
                    diagnosticId = diagnosticId,
                    kind = kind,
                    providerId = providerId,
                    mapOrExcursionId = mapOrExcursionId,
                    detectedTick = value,
                    message = message
                };
                adjacentDiagnostics.Add(record);
            }
            else if (resolved) record.resolvedTick = value;
            else record.resolvedTick = -1;
            if (resolved && record.resolvedTick < 0) record.resolvedTick = value;
            Touch("adjacent.diagnostic", providerId, mapOrExcursionId, message);
            MarkStorageMaintenanceDirty(value);
        }

        public IReadOnlyList<RealityAdjacentDiagnosticRecord> AdjacentDiagnosticSnapshots()
        {
            return adjacentDiagnostics.Where(item => item != null).OrderBy(item => item.diagnosticId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        private void MarkStorageMaintenanceDirty(long tick)
        {
            long value = tick >= 0 ? tick : Now;
            storageMaintenanceDirty = true;
            storageMaintenanceMutationCount++;
            if (nextStorageMaintenanceTick <= 0) nextStorageMaintenanceTick = value + StorageMaintenanceIntervalTicks;
            if (storageMaintenanceMutationCount >= StorageMaintenanceMutationThreshold) nextStorageMaintenanceTick = value;
        }

        private void RunStorageMaintenance(long now, bool force)
        {
            if (!force && (!storageMaintenanceDirty ||
                (now < nextStorageMaintenanceTick && storageMaintenanceMutationCount < StorageMaintenanceMutationThreshold))) return;
            RealityCompactionReport report = CompactStorageInternal(now);
            lastCompactionReport = report;
            storageMaintenanceDirty = false;
            storageMaintenanceMutationCount = 0;
            nextStorageMaintenanceTick = now + StorageMaintenanceIntervalTicks;
            if (report.TotalRemoved > 0) revision++;
        }

        internal RealityWorldState CaptureState()
        {
            return new RealityWorldState
            {
                regions = regions.Where(item => item != null).Select(item => item.Clone()).ToList(),
                connections = graph.Capture(),
                populations = populations.Where(item => item != null).Select(item => item.Clone()).ToList(),
                anchors = anchors.Where(item => item != null).Select(item => item.Clone()).ToList(),
                constraints = constraints.Where(item => item != null).Select(item => item.Clone()).ToList(),
                processes = processes.Where(item => item != null).Select(item => item.Clone()).ToList(),
                observations = observations.Where(item => item != null).Select(item => item.Clone()).ToList(),
                providerPayloads = providerPayloads.Where(item => item != null).Select(item => item.Clone()).ToList(),
                appliedOperations = appliedOperations.Where(item => item != null).Select(item => item.Clone()).ToList(),
                conflicts = conflicts.Where(item => item != null).Select(item => item.Clone()).ToList(),
                quarantine = quarantine.Where(item => item != null).Select(item => item.Clone()).ToList(),
                transferJournals = transferJournals.Where(item => item != null).Select(item => item.Clone()).ToList(),
                adjacentMaps = adjacentMaps.Where(item => item != null).Select(item => item.Clone()).ToList(),
                excursions = excursions.Where(item => item != null).Select(item => item.Clone()).ToList(),
                mapCreationIntents = mapCreationIntents.Where(item => item != null).Select(item => item.Clone()).ToList(),
                adjacentDiagnostics = adjacentDiagnostics.Where(item => item != null).Select(item => item.Clone()).ToList(),
                operationWatermarks = operationWatermarks.Where(item => item != null).Select(item => item.Clone()).ToList(),
                fidelityEscalations = fidelityEscalations.Where(item => item != null).Select(item => item.Clone()).ToList()
            };
        }

        internal void RestoreState(RealityWorldState state)
        {
            RealityThreadGuard.RequireMainThread();
            if (state == null) return;
            regions = state.regions ?? new List<RealityRegionDescriptor>();
            graph.Restore(state.connections);
            populations = state.populations ?? new List<RealityPopulationRecord>();
            anchors = state.anchors ?? new List<RealityAnchorRecord>();
            constraints = state.constraints ?? new List<RealityConstraint>();
            processes = state.processes ?? new List<RealityProcessRecord>();
            observations = state.observations ?? new List<RealityObservationRecord>();
            providerPayloads = state.providerPayloads ?? new List<RealityProviderPayload>();
            appliedOperations = state.appliedOperations ?? new List<RealityAppliedOperation>();
            conflicts = state.conflicts ?? new List<RealityConflictReport>();
            quarantine = state.quarantine ?? new List<RealityQuarantineRecord>();
            transferJournals = state.transferJournals ?? new List<RealityTransferJournalRecord>();
            adjacentMaps = state.adjacentMaps ?? new List<RealityAdjacentMapRecord>();
            excursions = state.excursions ?? new List<RealityExcursionTicket>();
            mapCreationIntents = state.mapCreationIntents ?? new List<RealityMapCreationIntentRecord>();
            adjacentDiagnostics = state.adjacentDiagnostics ?? new List<RealityAdjacentDiagnosticRecord>();
            operationWatermarks = state.operationWatermarks ?? new List<RealityOperationRetentionWatermark>();
            fidelityEscalations = state.fidelityEscalations ?? new List<RealityFidelityEscalationRecord>();
            storageMaintenanceDirty = true;
            nextStorageMaintenanceTick = Now;
            storageMaintenanceMutationCount = 0;
            pendingExcursions.Clear();
            pendingExcursionPawns.Clear();
            excursionPawnInstances.Clear();
            activeMapCreationTransactions.Clear();
            mapCreationIntentById.Clear();
            indexesReady = false;
            processScheduleCacheReady = false;
            RepairAndIndex();
            revision++;
        }

        internal RealityRegionDescriptor RegionRecord(string id)
        {
            EnsureIndexes();
            return regionById.TryGetValue(id ?? string.Empty, out RealityRegionDescriptor value) ? value : null;
        }

        internal RealityPopulationRecord PopulationRecord(string id)
        {
            EnsureIndexes();
            return populationById.TryGetValue(id ?? string.Empty, out RealityPopulationRecord value) ? value : null;
        }

        internal RealityProcessRecord ProcessRecord(string id)
        {
            EnsureIndexes();
            return processById.TryGetValue(id ?? string.Empty, out RealityProcessRecord value) ? value : null;
        }

        internal List<RealityProcessRecord> DueProcessRecords(long now)
        {
            EnsureIndexes();
            return RealityProcessScheduling.OrderDue(processes, now).ToList();
        }

        internal bool HasRunnableProcessDue(long now)
        {
            EnsureIndexes();
            EnsureProcessScheduleCache();
            return earliestRunnableProcessDueTick <= now;
        }

        internal void RefreshProcessScheduleCache()
        {
            processScheduleCacheReady = false;
            if (indexesReady) EnsureProcessScheduleCache();
        }

        internal int ReactivateProviderProcesses(string providerId)
        {
            EnsureIndexes();
            int reactivated = 0;
            foreach (RealityProcessRecord process in processes)
            {
                if (!RealityProcessPausePolicy.ReactivateUnavailable(process, providerId)) continue;
                reactivated++;
                Touch("process.resumed", process.providerId, process.regionId, process.processId);
            }
            if (reactivated > 0) RefreshProcessScheduleCache();
            return reactivated;
        }

        internal int ReactivateProjectionProcesses(string regionId)
        {
            EnsureIndexes();
            int reactivated = 0;
            foreach (RealityProcessRecord process in processes)
            {
                if (process == null || process.cancelled || !process.paused ||
                    process.pauseReason != RealityProcessPauseReason.ProjectionAuthoritative &&
                    process.pauseReason != RealityProcessPauseReason.ProjectionTransition ||
                    !string.Equals(process.regionId, regionId, StringComparison.Ordinal)) continue;
                process.paused = false;
                process.pauseReason = RealityProcessPauseReason.None;
                process.lastError = null;
                reactivated++;
                Touch("process.resumed", process.providerId, process.regionId, process.processId);
            }
            if (reactivated > 0) RefreshProcessScheduleCache();
            return reactivated;
        }

        internal RealityConstraint ConstraintRecord(string id)
        {
            EnsureIndexes();
            return constraintById.TryGetValue(id ?? string.Empty, out RealityConstraint value) ? value : null;
        }

        internal void Touch(string kind, string providerId, string regionId, string payload)
        {
            revision++;
            RealityEventBus.Publish(new RealityEvent(revision, "event:" + revision, kind, providerId, regionId, Now, payload));
        }

        internal void AddConflict(string regionId, string firstId, string secondId, string reason, long tick)
        {
            RealityThreadGuard.RequireMainThread();
            AddConflictInternal(regionId, firstId, secondId, reason, tick);
            Touch("constraint.conflict", "core", regionId, firstId + ":" + secondId);
        }

        internal void QuarantineProcess(RealityProcessRecord process, string reason)
        {
            QuarantineInternal("process", process?.processId, process?.providerId, reason, process?.payload, Now);
        }

        private static string GraphFingerprint(RealityRegionConnection connection)
        {
            if (connection == null) return null;
            return (connection.connectionId ?? string.Empty) + "|" + (connection.sourceRegionId ?? string.Empty) + "|" +
                (connection.destinationRegionId ?? string.Empty) + "|" + connection.direction + "|" +
                (connection.kind ?? string.Empty) + "|" + (connection.ownerNamespace ?? string.Empty) + "|" +
                (connection.identityKey ?? string.Empty);
        }

        private bool MarkMapActive(RealityRegionId id, Map map)
        {
            RealityRegionDescriptor record = RegionRecord(id.ToString());
            if (record == null || map == null) return false;
            if (record.authority == RealityRegionAuthority.Quarantined) return false;
            if (record.projectionMapUniqueId >= 0 && record.projectionMapUniqueId != map.uniqueID)
            {
                QuarantineInternal("map", map.uniqueID.ToString(), id.ProviderNamespace,
                    "A second live map attempted to claim an already active region.", id.ToString(), Now);
                return false;
            }
            record.projectionMapUniqueId = map.uniqueID;
            record.lastKnownWorldTile = id.WorldTile;
            record.fidelity = RealityFidelity.Materialized;
            if (record.authority != RealityRegionAuthority.Materializing &&
                record.authority != RealityRegionAuthority.Compressing)
                record.authority = RealityRegionAuthority.LiveProjection;
            record.lastProjectionTick = Now;
            record.lastUpdateTick = Now;
            regionByProjectionMapId[map.uniqueID] = id.ToString();
            return true;
        }

        internal bool BeginMaterialization(RealityRegionId id)
        {
            RealityRegionDescriptor record = RegionRecord(id.ToString());
            if (record == null || record.authority == RealityRegionAuthority.Quarantined ||
                record.authority == RealityRegionAuthority.Materializing ||
                record.authority == RealityRegionAuthority.Compressing) return false;
            record.authority = RealityRegionAuthority.Materializing;
            record.lastUpdateTick = Now;
            return true;
        }

        internal bool BeginCompression(RealityRegionId id)
        {
            RealityRegionDescriptor record = RegionRecord(id.ToString());
            if (record == null || record.authority != RealityRegionAuthority.LiveProjection) return false;
            record.authority = RealityRegionAuthority.Compressing;
            record.lastUpdateTick = Now;
            return true;
        }

        internal void QuarantineProjection(RealityRegionId id, string reason)
        {
            RealityRegionDescriptor record = RegionRecord(id.ToString());
            if (record == null) return;
            record.authority = RealityRegionAuthority.Quarantined;
            record.lastUpdateTick = Now;
            QuarantineInternal("region-projection", record.regionId, id.ProviderNamespace, reason, null, Now);
        }

        internal bool CommitMaterialization(RealityRegionId id, int mapUniqueId, long tick)
        {
            RealityRegionDescriptor record = RegionRecord(id.ToString());
            if (record == null || mapUniqueId < 0 || record.projectionMapUniqueId != mapUniqueId ||
                (record.authority != RealityRegionAuthority.Materializing &&
                 record.authority != RealityRegionAuthority.LiveProjection)) return false;
            record.projectionMapUniqueId = mapUniqueId;
            record.authority = RealityRegionAuthority.LiveProjection;
            record.fidelity = RealityFidelity.Materialized;
            record.lastProjectionTick = tick;
            record.lastUpdateTick = tick;
            regionByProjectionMapId[mapUniqueId] = id.ToString();
            return true;
        }

        internal bool CommitCompression(RealityRegionId id, long tick)
        {
            RealityRegionDescriptor record = RegionRecord(id.ToString());
            if (record == null || record.authority != RealityRegionAuthority.Compressing) return false;
            record.projectionMapUniqueId = -1;
            record.authority = RealityRegionAuthority.Latent;
            record.fidelity = RealityFidelity.Statistical;
            record.lastUpdateTick = tick;
            foreach (int mapId in regionByProjectionMapId.Where(item => item.Value == id.ToString())
                .Select(item => item.Key).ToList()) regionByProjectionMapId.Remove(mapId);
            ReactivateProjectionProcesses(id.ToString());
            return true;
        }

        private bool TryResolveMapIdentity(Map map, out RealityRegionId regionId)
        {
            return TryResolveMapIdentity(map, out regionId, out _);
        }

        private bool TryResolveMapIdentity(Map map, out RealityRegionId regionId, out RealityMapIdentityClaim selectedClaim)
        {
            regionId = default(RealityRegionId);
            selectedClaim = null;
            var claims = new List<RealityMapIdentityClaim>();
            bool invalidClaim = false;
            foreach (IRealityMapIdentityProvider identityProvider in RealityProviderRegistry.OfType<IRealityMapIdentityProvider>())
            {
                string providerId = (identityProvider as IRealityProvider)?.Registration?.providerId;
                try
                {
                    bool claimed = identityProvider.TryClaimMap(map, out RealityMapIdentityClaim claim);
                    if (!claimed && claim == null) continue;
                    if (claim == null || string.IsNullOrEmpty(providerId) || !string.Equals(providerId, claim.providerId, StringComparison.Ordinal) ||
                        !claim.regionId.IsValid || claim.regionId.WorldTile != (int)map.Tile)
                    {
                        invalidClaim = true;
                        QuarantineInternal("map", map.uniqueID.ToString(), providerId ?? "core",
                            "A provider returned an invalid map identity claim.", claim?.StableKey, Now);
                        continue;
                    }
                    if (!claimed) continue;
                    claims.Add(claim);
                }
                catch (Exception exception)
                {
                    invalidClaim = true;
                    QuarantineInternal("map", map.uniqueID.ToString(), providerId ?? "core",
                        "Map identity claim failed: " + exception.Message, null, Now);
                }
            }
            if (invalidClaim) return false;
            if (claims.Count > 0)
            {
                if (!Materialization.RealityMapIdentityPolicy.TrySelectClaim(claims, out RealityMapIdentityClaim selected, out string diagnostic))
                {
                    QuarantineInternal("map", map.uniqueID.ToString(), "core", diagnostic,
                        string.Join(",", claims.Select(item => item.StableKey).ToArray()), Now);
                    return false;
                }
                selectedClaim = selected;
                regionId = selected.regionId;
                return true;
            }
            if (IsStandardSurfaceMap(map))
            {
                regionId = RealityRegionId.Surface((int)map.Tile);
                return true;
            }
            QuarantineInternal("map", map.uniqueID.ToString(), "core",
                "A nonstandard map has no explicit provider identity claim.", null, Now);
            return false;
        }

        private static bool IsStandardSurfaceMap(Map map)
        {
            return map?.Parent != null && map.Parent.GetType().Assembly == typeof(MapParent).Assembly;
        }

        private void RepairAndIndex()
        {
            regionById.Clear();
            populationById.Clear();
            anchorById.Clear();
            constraintById.Clear();
            processById.Clear();
            observationById.Clear();
            appliedOperationIds.Clear();
            regionByProjectionMapId.Clear();
            mapCreationIntentById.Clear();
            activeExcursionById.Clear();
            unresolvedTransferJournalCount = 0;
            RepairList(regions, item => item?.regionId, "region");
            RepairList(populations, item => item?.populationId, "population");
            RepairList(anchors, item => item?.anchorId, "anchor");
            RepairList(constraints, item => item?.constraintId, "constraint");
            RepairList(processes, item => item?.processId, "process");
            foreach (RealityProcessRecord process in processes)
            {
                process.intervalTicks = Math.Max(1, process.intervalTicks);
                RealityProcessPausePolicy.Normalize(process, RealityProviderRegistry.TryGet(process.providerId, out _));
            }
            fidelityEscalations = fidelityEscalations.Where(item => item != null &&
                !string.IsNullOrEmpty(item.requestId) && !string.IsNullOrEmpty(item.regionId) &&
                !string.IsNullOrEmpty(item.providerId) && !string.IsNullOrEmpty(item.processId) &&
                Enum.IsDefined(typeof(RealityFidelity), item.currentFidelity) &&
                Enum.IsDefined(typeof(RealityFidelity), item.requestedFidelity) &&
                RealityFidelityRules.IsHigher(item.requestedFidelity, item.currentFidelity) &&
                Enum.IsDefined(typeof(RealityFidelityEscalationReason), item.reason) &&
                Enum.IsDefined(typeof(RealityFidelityEscalationPolicy), item.policy) &&
                Enum.IsDefined(typeof(RealityFidelityEscalationStatus), item.status)).ToList();
            RepairUnique(fidelityEscalations, item => item?.requestId, "fidelity-escalation");
            RepairList(observations, item => item?.observationId, "observation");
            appliedOperations = appliedOperations.Where(item => item != null && !string.IsNullOrEmpty(item.operationId) &&
                item.sequence >= -1).ToList();
            foreach (RealityGraphRepairIssue issue in graph.RepairAndIndex())
                QuarantineInternal("connection", issue.connectionId, issue.ownerNamespace,
                    issue.reason, issue.payload, Now);
            RepairUnique(appliedOperations, item => item?.operationId, "operation");
            operationWatermarks = operationWatermarks.Where(item => item != null && !string.IsNullOrEmpty(item.providerId) &&
                !string.IsNullOrEmpty(item.kind) && !string.IsNullOrEmpty(item.domainId) &&
                (item.sequenceMode
                    ? item.sequenceCursor >= -1 && !string.IsNullOrEmpty(item.proof)
                    : item.safeThroughTick >= 0)).ToList();
            RepairUnique(operationWatermarks, item => item == null ? null : item.providerId + ":" + item.kind + ":" + item.domainId,
                "operation-watermark");
            adjacentDiagnostics = adjacentDiagnostics.Where(item => item != null && !string.IsNullOrEmpty(item.diagnosticId)).ToList();
            RepairUnique(adjacentDiagnostics, item => item?.diagnosticId, "adjacent-diagnostic");
            RepairUnique(providerPayloads, item => item == null ? null : item.providerId + ":" + item.payloadId, "provider-payload");
            adjacentMaps = RepairAdjacentMaps(adjacentMaps);
            RepairUnique(adjacentMaps, item => item == null ? null : item.mapUniqueId.ToString(), "adjacent-map");
            mapCreationIntents = RepairMapCreationIntents(mapCreationIntents);
            RepairUnique(mapCreationIntents, item => item?.transactionId, "map-creation-intent");
            excursions = RepairExcursions(excursions);
            RepairUnique(excursions, item => item?.excursionId, "excursion");
            RepairDuplicateExcursionPawns();
            conflicts = conflicts.Where(item => item != null && !string.IsNullOrEmpty(item.conflictId)).ToList();
            RepairUnique(conflicts, item => item?.conflictId, "conflict");
            foreach (RealityAppliedOperation operation in appliedOperations) appliedOperationIds.Add(operation.operationId);
            adjacentMapById.Clear();
            foreach (RealityAdjacentMapRecord adjacentMap in adjacentMaps)
                if (adjacentMap != null && adjacentMap.lifecycle != RealityAdjacentMapLifecycle.Retired)
                    adjacentMapById[adjacentMap.mapUniqueId] = adjacentMap;
            mapCreationIntents = ReconcileMapCreationIntents(mapCreationIntents);
            foreach (RealityMapCreationIntentRecord intent in mapCreationIntents)
                if (intent != null && !string.IsNullOrEmpty(intent.transactionId)) mapCreationIntentById[intent.transactionId] = intent;
            excursionById.Clear();
            foreach (RealityExcursionTicket excursion in excursions)
            {
                if (excursion == null || string.IsNullOrEmpty(excursion.excursionId)) continue;
                if (string.IsNullOrEmpty(excursion.taskId)) excursion.taskId = excursion.excursionId;
                if (excursion.status == RealityExcursionStatus.Completed && excursion.terminalTick < 0)
                    excursion.terminalTick = excursion.lastTaskHeartbeat >= 0 ? excursion.lastTaskHeartbeat : excursion.startTick;
                excursionById[excursion.excursionId] = excursion;
                if (!RealityRetentionPolicy.IsTerminalExcursion(excursion))
                    activeExcursionById[excursion.excursionId] = excursion;
            }
            providerPayloads = providerPayloads.Where(item => item != null && !string.IsNullOrEmpty(item.providerId)).ToList();
            quarantine = quarantine.Where(item => item != null).ToList();
            transferJournals = transferJournals.Where(item => item != null && !string.IsNullOrEmpty(item.transferId)).ToList();
            unresolvedTransferJournalCount = transferJournals.Count(item =>
                RealityRetentionPolicy.IsInterruptedTransfer(item) && !string.IsNullOrEmpty(item.excursionId));
            CompactStorageInternal(Now);
            processById.Clear();
            foreach (RealityProcessRecord process in processes.Where(item => item != null && !string.IsNullOrEmpty(item.processId))) processById[process.processId] = process;
            fidelityEscalationById.Clear();
            foreach (RealityFidelityEscalationRecord escalation in fidelityEscalations.Where(item => item != null && !string.IsNullOrEmpty(item.requestId)))
                fidelityEscalationById[escalation.requestId] = escalation;
            regionByProjectionMapId.Clear();
            foreach (RealityRegionDescriptor region in regions.Where(item => item != null && item.projectionMapUniqueId >= 0)
                .OrderBy(item => item.projectionMapUniqueId))
            {
                if (regionByProjectionMapId.ContainsKey(region.projectionMapUniqueId))
                {
                    region.authority = RealityRegionAuthority.Quarantined;
                    QuarantineInternal("region-projection", region.regionId, "core",
                        "Multiple regions claim the same live projection Map.uniqueID.", region.projectionMapUniqueId.ToString(), Now);
                    continue;
                }
                regionByProjectionMapId[region.projectionMapUniqueId] = region.regionId;
            }
            indexesReady = true;
            processScheduleCacheReady = false;
            EnsureProcessScheduleCache();
            storageMaintenanceDirty = false;
            storageMaintenanceMutationCount = 0;
            nextStorageMaintenanceTick = Now + StorageMaintenanceIntervalTicks;
        }

        private void RepairList<T>(List<T> values, Func<T, string> idSelector, string recordType) where T : class
        {
            if (values == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var repaired = new List<T>(values.Count);
            foreach (T value in values.ToList())
            {
                string id = idSelector(value);
                if (value == null || string.IsNullOrEmpty(id))
                {
                    QuarantineInternal(recordType, null, "core", "Record has no stable ID.", null, Now);
                    continue;
                }
                if (!seen.Add(id))
                {
                    QuarantineInternal(recordType, id, "core", "Duplicate stable ID retained only in audit quarantine.", null, Now);
                    int existingIndex = positions[id];
                    if (PreferDuplicate(repaired[existingIndex], value)) repaired[existingIndex] = value;
                    continue;
                }
                positions[id] = repaired.Count;
                repaired.Add(value);
            }
            values.Clear();
            values.AddRange(repaired);
            foreach (T value in values)
            {
                string id = idSelector(value);
                if (value is RealityRegionDescriptor region) regionById[id] = region;
                else if (value is RealityPopulationRecord population) populationById[id] = population;
                else if (value is RealityAnchorRecord anchor) anchorById[id] = anchor;
                else if (value is RealityConstraint constraint) constraintById[id] = constraint;
                else if (value is RealityProcessRecord process) processById[id] = process;
                else if (value is RealityObservationRecord observation) observationById[id] = observation;
            }
        }

        private void EnsureIndexes()
        {
            if (!indexesReady) RepairAndIndex();
        }

        private void EnsureProcessScheduleCache()
        {
            if (processScheduleCacheReady) return;
            earliestRunnableProcessDueTick = RealityProcessScheduling.EarliestRunnableDue(processes);
            processScheduleCacheReady = true;
        }

        private void RepairUnique<T>(List<T> values, Func<T, string> idSelector, string recordType) where T : class
        {
            if (values == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var repaired = new List<T>(values.Count);
            foreach (T value in values.ToList())
            {
                string id = idSelector(value);
                if (value == null || string.IsNullOrEmpty(id))
                {
                    QuarantineInternal(recordType, null, "core", "Record has no stable ID.", null, Now);
                }
                else if (!seen.Add(id))
                {
                    QuarantineInternal(recordType, id, "core", "Duplicate stable ID retained only in audit quarantine.", null, Now);
                    int existingIndex = positions[id];
                    if (PreferDuplicate(repaired[existingIndex], value)) repaired[existingIndex] = value;
                }
                else
                {
                    positions[id] = repaired.Count;
                    repaired.Add(value);
                }
            }
            values.Clear();
            values.AddRange(repaired);
        }

        private List<RealityAdjacentMapRecord> RepairAdjacentMaps(List<RealityAdjacentMapRecord> values)
        {
            var repaired = new List<RealityAdjacentMapRecord>();
            foreach (RealityAdjacentMapRecord value in (values ?? new List<RealityAdjacentMapRecord>()).ToList())
            {
                if (!IsValidAdjacentMapRecord(value))
                {
                    QuarantineInternal("adjacent-map", value?.mapUniqueId.ToString(), value?.providerId ?? "core",
                        "Adjacent map marker has incomplete or invalid persisted identity fields.", value?.regionId, Now);
                    continue;
                }
                repaired.Add(value);
            }
            return repaired;
        }

        private List<RealityMapCreationIntentRecord> RepairMapCreationIntents(List<RealityMapCreationIntentRecord> values)
        {
            var repaired = new List<RealityMapCreationIntentRecord>();
            foreach (RealityMapCreationIntentRecord value in (values ?? new List<RealityMapCreationIntentRecord>()).ToList())
            {
                if (!IsValidMapCreationIntent(value))
                {
                    QuarantineInternal("map-creation-intent", value?.transactionId, value?.providerId ?? "core",
                        "Map creation intent has incomplete or invalid persisted identity fields.", value?.regionId, Now);
                    continue;
                }
                repaired.Add(value);
            }
            return repaired;
        }

        private List<RealityMapCreationIntentRecord> ReconcileMapCreationIntents(
            List<RealityMapCreationIntentRecord> values)
        {
            // A save may be loaded before maps are reconstructed. Keep valid intents until
            // the readiness path can compare them with an actual map identity.
            if (Find.Maps == null)
            {
                mapCreationIntentReconciliationPending = true;
                return values ?? new List<RealityMapCreationIntentRecord>();
            }
            mapCreationIntentReconciliationPending = false;

            var reconciled = new List<RealityMapCreationIntentRecord>();
            List<Map> liveMaps = Find.Maps.Where(item => item != null).OrderBy(item => item.uniqueID).ToList();
            foreach (RealityMapCreationIntentRecord intent in (values ?? new List<RealityMapCreationIntentRecord>())
                .Where(item => item != null).OrderBy(item => item.transactionId, StringComparer.Ordinal).ToList())
            {
                if (activeMapCreationTransactions.Contains(intent.transactionId))
                {
                    reconciled.Add(intent);
                    continue;
                }

                RealityAdjacentMapRecord marker = adjacentMaps.FirstOrDefault(item => item != null &&
                    item.transactionId == intent.transactionId);
                if (marker != null)
                {
                    if (RealityMapCreationPolicy.ResolveDisposition(activeMapCreationTransactions.Contains(intent.transactionId),
                        RealityMapCreationPolicy.ShouldClearAfterLoad(intent, marker), false, false, true) ==
                        RealityMapCreationIntentDisposition.Committed) continue;
                    QuarantineInternal("map-creation-intent", intent.transactionId, intent.providerId,
                        "A creation intent conflicted with its persisted adjacent marker.", intent.regionId, Now);
                    continue;
                }

                bool recoveryReference = transferJournals.Any(journal =>
                    journal != null && RealityRetentionPolicy.IsInterruptedTransfer(journal) &&
                    ((intent.createdMapUniqueId >= 0 &&
                        (journal.sourceMapUniqueId == intent.createdMapUniqueId ||
                         journal.destinationMapUniqueId == intent.createdMapUniqueId)) ||
                     journal.providerId == intent.providerId && journal.sourceRegionId == intent.regionId));

                if (intent.createdMapUniqueId >= 0)
                {
                    Map exactMap = liveMaps.FirstOrDefault(item => item.uniqueID == intent.createdMapUniqueId);
                    bool providerAvailable = RealityProviderRegistry.TryGet(intent.providerId, out _);
                    RealityMapCreationIntentDisposition disposition = RealityMapCreationPolicy.ResolveDisposition(
                        false, false, exactMap != null, recoveryReference, providerAvailable);
                    if (exactMap == null)
                    {
                        if (disposition == RealityMapCreationIntentDisposition.FailedRecovery)
                        {
                            intent.diagnostic = "Creation intent retained for interrupted transfer recovery.";
                            reconciled.Add(intent);
                        }
                        else if (disposition != RealityMapCreationIntentDisposition.Stale)
                        {
                            QuarantineInternal("map-creation-intent", intent.transactionId, intent.providerId,
                                "Creation intent failed without a matching created map.", intent.regionId, Now);
                        }
                        continue;
                    }
                    if (!providerAvailable)
                    {
                        intent.diagnostic = "Creation intent retained until its provider is available.";
                        reconciled.Add(intent);
                        continue;
                    }
                    if (disposition == RealityMapCreationIntentDisposition.ExactCreatedMap &&
                        TryRegisterMapFromCreationIntent(exactMap, default(RealityRegionId), out _, out _))
                        continue;

                    QuarantineInternal("map-creation-intent", intent.transactionId, intent.providerId,
                        "A live map matched the created ID but failed identity or ownership validation.", intent.regionId, Now);
                    continue;
                }

                bool sameTileMap = RealityRegionId.TryParse(intent.regionId, out RealityRegionId expected) &&
                    liveMaps.Any(item => item.Tile.Valid && item.Tile == (PlanetTile)expected.WorldTile);
                RealityMapCreationIntentDisposition unboundDisposition = RealityMapCreationPolicy.ResolveDisposition(
                    false, false, false, recoveryReference, !sameTileMap);
                if (unboundDisposition == RealityMapCreationIntentDisposition.Ambiguous ||
                    unboundDisposition == RealityMapCreationIntentDisposition.FailedRecovery)
                {
                    QuarantineInternal("map-creation-intent", intent.transactionId, intent.providerId,
                        "An unbound creation intent was ambiguous during load reconciliation.", intent.regionId, Now);
                    continue;
                }
                // No active transition, map, or recovery record can still consume this intent.
            }
            return reconciled;
        }

        private List<RealityExcursionTicket> RepairExcursions(List<RealityExcursionTicket> values)
        {
            var repaired = new List<RealityExcursionTicket>();
            foreach (RealityExcursionTicket value in (values ?? new List<RealityExcursionTicket>()).ToList())
            {
                if (!IsValidExcursionTicket(value))
                {
                    QuarantineInternal("excursion", value?.excursionId, value?.providerId ?? "core",
                        "Excursion ticket has incomplete or invalid persisted ownership fields.", value?.pawnLoadId, Now);
                    continue;
                }
                repaired.Add(value);
            }
            return repaired;
        }

        private void RepairDuplicateExcursionPawns()
        {
            var seenPawns = new Dictionary<string, RealityExcursionTicket>(StringComparer.Ordinal);
            var repaired = new List<RealityExcursionTicket>(excursions.Count);
            foreach (RealityExcursionTicket ticket in excursions)
            {
                if (ticket == null)
                {
                    repaired.Add(ticket);
                    continue;
                }
                if (RealityRetentionPolicy.IsTerminalExcursion(ticket))
                {
                    repaired.Add(ticket);
                    continue;
                }
                if (ticket.status == RealityExcursionStatus.Quarantined)
                {
                    if (!seenPawns.ContainsKey(ticket.pawnLoadId)) seenPawns[ticket.pawnLoadId] = ticket;
                    repaired.Add(ticket);
                    continue;
                }
                if (!seenPawns.TryGetValue(ticket.pawnLoadId, out RealityExcursionTicket prior))
                {
                    seenPawns[ticket.pawnLoadId] = ticket;
                    repaired.Add(ticket);
                    continue;
                }
                const string diagnostic = "Duplicate active Pawn ownership was quarantined without selecting an owner.";
                prior.status = RealityExcursionStatus.Quarantined;
                prior.diagnostic = diagnostic;
                ticket.status = RealityExcursionStatus.Quarantined;
                ticket.diagnostic = diagnostic;
                QuarantineInternal("excursion", prior.excursionId, prior.providerId, diagnostic, prior.pawnLoadId, Now);
                QuarantineInternal("excursion", ticket.excursionId, ticket.providerId, diagnostic, ticket.pawnLoadId, Now);
                repaired.Add(ticket);
            }
            excursions = repaired;
        }

        private static bool IsValidAdjacentMapRecord(RealityAdjacentMapRecord value)
        {
            return value != null && value.mapUniqueId >= 0 && value.originMapUniqueId >= 0 &&
                value.mapUniqueId != value.originMapUniqueId && !string.IsNullOrEmpty(value.providerId) &&
                RealityRegionId.TryParse(value.regionId, out RealityRegionId region) &&
                region.IsValid && RealityRegionId.TryParse(value.originRegionId, out RealityRegionId origin) &&
                origin.IsValid && value.createdTick >= 0 && value.lastAccessTick >= 0 &&
                Enum.IsDefined(typeof(RealityAdjacentMapLifecycle), value.lifecycle);
        }

        private static bool IsValidMapCreationIntent(RealityMapCreationIntentRecord value)
        {
            return value != null && !string.IsNullOrEmpty(value.transactionId) && !string.IsNullOrEmpty(value.providerId) &&
                RealityRegionId.TryParse(value.regionId, out RealityRegionId region) &&
                region.IsValid && RealityRegionId.TryParse(value.originRegionId, out RealityRegionId origin) &&
                origin.IsValid && value.originMapUniqueId >= 0 &&
                value.preexistingMapUniqueId >= -1 && value.createdMapUniqueId >= -1 && value.createdTick >= 0 &&
                Enum.IsDefined(typeof(RealityAdjacentMapLifecycle), value.lifecycle) &&
                region.IsValid;
        }

        private static bool IsValidExcursionTicket(RealityExcursionTicket value)
        {
            return value != null && !string.IsNullOrEmpty(value.excursionId) &&
                !string.IsNullOrEmpty(value.providerId) && !string.IsNullOrEmpty(value.pawnLoadId) &&
                value.originMapUniqueId >= 0 && value.destinationMapUniqueId >= 0 &&
                value.originMapUniqueId != value.destinationMapUniqueId &&
                !string.IsNullOrEmpty(value.outboundTransferId) && !string.IsNullOrEmpty(value.returnTransferId) &&
                !string.IsNullOrEmpty(value.inverseReturnEdge) && value.startTick >= 0 &&
                RealityRegionId.TryParse(value.originRegionId, out RealityRegionId origin) && origin.IsValid &&
                RealityRegionId.TryParse(value.destinationRegionId, out RealityRegionId destination) &&
                destination.IsValid &&
                Enum.IsDefined(typeof(RealityExcursionStatus), value.status);
        }

        /// <summary>Stable repair keeps the first serialized slot, replacing it only with an explicitly newer record.</summary>
        private static bool PreferDuplicate(object existing, object candidate)
        {
            return RealityRepairPolicy.ShouldReplaceDuplicate(UpdateTick(existing), UpdateTick(candidate));
        }

        private static long? UpdateTick(object value)
        {
            if (value is RealityRegionDescriptor region) return region.lastUpdateTick;
            if (value is RealityPopulationRecord population) return population.lastUpdateTick;
            if (value is RealityAnchorRecord anchor) return anchor.lastKnownTick;
            if (value is RealityConstraint constraint) return constraint.createdTick;
            if (value is RealityProcessRecord process) return process.lastExecutionTick;
            if (value is RealityObservationRecord observation) return observation.tick;
            if (value is RealityAppliedOperation operation) return operation.tick;
            if (value is RealityConflictReport conflict) return conflict.detectedTick;
            if (value is RealityAdjacentMapRecord adjacentMap) return adjacentMap.lastAccessTick;
            if (value is RealityExcursionTicket excursion) return excursion.terminalTick >= 0 ? excursion.terminalTick : excursion.lastTaskHeartbeat;
            if (value is RealityMapCreationIntentRecord intent) return intent.createdTick;
            if (value is RealityOperationRetentionWatermark watermark) return watermark.updatedTick;
            if (value is RealityAdjacentDiagnosticRecord diagnostic) return diagnostic.resolvedTick >= 0 ? diagnostic.resolvedTick : diagnostic.detectedTick;
            return null;
        }

        private void AddConflictInternal(string regionId, string firstId, string secondId, string reason, long tick)
        {
            string left = string.CompareOrdinal(firstId, secondId) <= 0 ? firstId : secondId;
            string right = left == firstId ? secondId : firstId;
            string conflictId = "conflict:" + RealityDeterminism.Combine(regionId, left, right, reason);
            if (conflicts.Any(item => item?.conflictId == conflictId)) return;
            conflicts.Add(new RealityConflictReport
            {
                conflictId = conflictId,
                regionId = regionId,
                subjectId = firstId,
                constraintIds = new List<string> { left, right },
                reason = reason,
                detectedTick = tick
            });
            if (conflicts.Count > MaximumConflictRecords) CompactAuditRecords();
        }

        private void QuarantineInternal(string recordType, string recordId, string providerId, string reason, string payload, long tick)
        {
            string key = QuarantineKey(recordType, recordId, providerId, reason, payload);
            if (quarantine.Any(item => item != null && QuarantineKey(item) == key)) return;
            quarantine.Add(new RealityQuarantineRecord
            {
                recordType = recordType,
                recordId = recordId,
                providerId = providerId,
                reason = reason,
                payload = payload,
                detectedTick = tick
            });
            if (quarantine.Count > MaximumQuarantineRecords) CompactAuditRecords();
        }

        private RealityCompactionReport CompactStorageInternal(long now)
        {
            RealityCompactionReport report = new RealityCompactionReport();
            int before = quarantine.Count;
            int conflictsBefore = conflicts.Count;
            CompactAuditRecords();
            report.quarantineRemoved += Math.Max(0, before - quarantine.Count);
            report.conflictsRemoved += Math.Max(0, conflictsBefore - conflicts.Count);

            before = observations.Count;
            while (observations.Count > MaximumObservationHistory)
            {
                RealityObservationRecord oldest = RealityRetentionPolicy.FindOldestDiscardableObservation(observations);
                if (oldest == null) break;
                observations.Remove(oldest);
                observationById.Remove(oldest.observationId);
            }
            report.observationsRemoved = Math.Max(0, before - observations.Count);

            before = transferJournals.Count;
            transferJournals = RealityRetentionPolicy.SelectTransferJournals(transferJournals, now).ToList();
            report.transferJournalsRemoved = Math.Max(0, before - transferJournals.Count);

            before = appliedOperations.Count;
            appliedOperations = appliedOperations.Where(operation =>
            {
                if (operation == null) return false;
                if (!RealityProviderRegistry.TryGetRegistration(operation.providerId, out RealityProviderRegistration registration)) return true;
                return !RealityRetentionPolicy.CanExpireOperation(registration, operation, operationWatermarks, now);
            }).ToList();
            report.appliedOperationsRemoved = Math.Max(0, before - appliedOperations.Count);
            appliedOperationIds.Clear();
            foreach (RealityAppliedOperation operation in appliedOperations) appliedOperationIds.Add(operation.operationId);

            report.cancelledProcessesRemoved = CompactCancelledProcesses(now);
            before = excursions.Count;
            excursions = RealityRetentionPolicy.SelectExcursions(excursions, now).ToList();
            report.excursionsRemoved = Math.Max(0, before - excursions.Count);
            excursionById.Clear();
            activeExcursionById.Clear();
            foreach (RealityExcursionTicket ticket in excursions.Where(item => item != null))
            {
                excursionById[ticket.excursionId] = ticket;
                if (!RealityRetentionPolicy.IsTerminalExcursion(ticket)) activeExcursionById[ticket.excursionId] = ticket;
            }
            before = adjacentMaps.Count;
            List<RealityAdjacentMapRecord> retainedAdjacentMaps = RealityRetentionPolicy.SelectRetiredAdjacentMaps(adjacentMaps, now).ToList();
            HashSet<int> retainedAdjacentMapIds = new HashSet<int>(retainedAdjacentMaps.Select(record => record.mapUniqueId));
            foreach (RealityAdjacentMapRecord record in adjacentMaps.Where(item => item != null &&
                item.lifecycle == RealityAdjacentMapLifecycle.Retired && HasAdjacentMapRecoveryReference(item)))
            {
                if (retainedAdjacentMapIds.Add(record.mapUniqueId)) retainedAdjacentMaps.Add(record);
            }
            adjacentMaps = retainedAdjacentMaps.OrderBy(record => record.mapUniqueId).ToList();
            report.retiredAdjacentMapsRemoved = Math.Max(0, before - adjacentMaps.Count);
            before = adjacentDiagnostics.Count;
            adjacentDiagnostics = RealityRetentionPolicy.SelectResolvedAdjacentDiagnostics(adjacentDiagnostics, now).ToList();
            report.adjacentDiagnosticsRemoved = Math.Max(0, before - adjacentDiagnostics.Count);
            regionByProjectionMapId.Clear();
            foreach (RealityRegionDescriptor region in regions.Where(item => item != null && item.projectionMapUniqueId >= 0)
                .OrderBy(item => item.projectionMapUniqueId))
                if (!regionByProjectionMapId.ContainsKey(region.projectionMapUniqueId))
                    regionByProjectionMapId[region.projectionMapUniqueId] = region.regionId;
            unresolvedTransferJournalCount = transferJournals.Count(item =>
                RealityRetentionPolicy.IsInterruptedTransfer(item) && !string.IsNullOrEmpty(item.excursionId));
            return report;
        }

        private bool HasAdjacentMapRecoveryReference(RealityAdjacentMapRecord marker)
        {
            if (marker == null) return false;
            return excursions.Any(ticket => ticket != null &&
                (ticket.originMapUniqueId == marker.mapUniqueId || ticket.destinationMapUniqueId == marker.mapUniqueId ||
                 ticket.originRegionId == marker.regionId || ticket.destinationRegionId == marker.regionId)) ||
                transferJournals.Any(journal => journal != null && RealityRetentionPolicy.IsInterruptedTransfer(journal) &&
                    (journal.sourceMapUniqueId == marker.mapUniqueId || journal.destinationMapUniqueId == marker.mapUniqueId ||
                     journal.sourceRegionId == marker.regionId || journal.destinationRegionId == marker.regionId)) ||
                mapCreationIntents.Any(intent => intent != null && intent.createdMapUniqueId == marker.mapUniqueId);
        }

        private void CompactAuditRecords()
        {
            Dictionary<string, RealityQuarantineRecord> uniqueQuarantine = new Dictionary<string, RealityQuarantineRecord>(StringComparer.Ordinal);
            foreach (RealityQuarantineRecord item in quarantine.Where(value => value != null))
            {
                string key = QuarantineKey(item);
                if (!uniqueQuarantine.TryGetValue(key, out RealityQuarantineRecord existing) ||
                    item.detectedTick > existing.detectedTick) uniqueQuarantine[key] = item;
            }
            quarantine = uniqueQuarantine.Values.OrderByDescending(item => item.detectedTick)
                .ThenBy(item => QuarantineKey(item), StringComparer.Ordinal).Take(MaximumQuarantineRecords).ToList();

            Dictionary<string, RealityConflictReport> uniqueConflicts = new Dictionary<string, RealityConflictReport>(StringComparer.Ordinal);
            foreach (RealityConflictReport item in conflicts.Where(value => value != null && !string.IsNullOrEmpty(value.conflictId)))
            {
                if (!uniqueConflicts.TryGetValue(item.conflictId, out RealityConflictReport existing) ||
                    item.detectedTick > existing.detectedTick) uniqueConflicts[item.conflictId] = item;
            }
            conflicts = uniqueConflicts.Values.OrderByDescending(item => item.detectedTick)
                .ThenBy(item => item.conflictId, StringComparer.Ordinal).Take(MaximumConflictRecords).ToList();
        }

        private int CompactCancelledProcesses(long now)
        {
            List<RealityProcessRecord> candidates = processes.Where(process => CanCompactCancelledProcess(process, now))
                .OrderBy(process => process.cancelledTick).ThenBy(process => process.processId, StringComparer.Ordinal).ToList();
            int overflow = Math.Max(0, processes.Count(process => process != null && process.cancelled) - MaximumCancelledProcesses);
            HashSet<string> removeIds = new HashSet<string>(candidates.Take(overflow).Select(process => process.processId), StringComparer.Ordinal);
            foreach (RealityProcessRecord process in candidates)
                if (process.cancelledTick >= 0 && now - process.cancelledTick >= ProcessRetentionTicks(process)) removeIds.Add(process.processId);
            if (removeIds.Count == 0) return 0;
            int before = processes.Count;
            processes.RemoveAll(process => process != null && removeIds.Contains(process.processId));
            foreach (string id in removeIds) processById.Remove(id);
            processScheduleCacheReady = false;
            return before - processes.Count;
        }

        private bool CanCompactCancelledProcess(RealityProcessRecord process, long now)
        {
            if (process == null || !process.cancelled || process.cancelledTick < 0) return false;
            if (!RealityProviderRegistry.TryGetRegistration(process.providerId, out RealityProviderRegistration registration) ||
                registration.cancelledProcessRetentionTicks <= 0) return false;
            if (now - process.cancelledTick < registration.cancelledProcessRetentionTicks) return false;
            return !HasPersistedReference(process.processId);
        }

        private long ProcessRetentionTicks(RealityProcessRecord process)
        {
            return RealityProviderRegistry.TryGetRegistration(process.providerId, out RealityProviderRegistration registration) &&
                registration.cancelledProcessRetentionTicks > 0
                ? registration.cancelledProcessRetentionTicks : long.MaxValue;
        }

        private bool HasPersistedReference(string id)
        {
            if (string.IsNullOrEmpty(id)) return true;
            if (regions.Any(region => region != null && (Contains(region.regionId, id) || Contains(region.label, id)))) return true;
            if (regions.Any(region => region != null && region.environment != null &&
                (Contains(region.environment.biomeDefName, id) || Contains(region.environment.climateClass, id)))) return true;
            if (regions.Any(region => region != null &&
                (region.environment?.providerFields ?? new List<RealityPayloadField>()).Any(field => field != null &&
                    (Contains(field.key, id) || Contains(field.value, id))))) return true;
            if (regions.Any(region => region != null &&
                (region.providerPayloads ?? new List<RealityProviderPayload>()).Any(payload => payload != null &&
                    (Contains(payload.payloadId, id) || Contains(payload.data, id))))) return true;
            if (graph.References(id)) return true;
            if (populations.Any(population => population != null &&
                (Contains(population.populationId, id) || Contains(population.providerId, id) || Contains(population.kind, id) ||
                 Contains(population.subjectId, id) || Contains(population.regionId, id) || Contains(population.demographicPayload, id) ||
                 Contains(population.anchoredMemberIds, id)))) return true;
            if (anchors.Any(anchor => anchor != null &&
                (Contains(anchor.anchorId, id) || Contains(anchor.providerId, id) || Contains(anchor.typeId, id) ||
                 Contains(anchor.regionId, id) || Contains(anchor.optionalRimWorldLoadId, id) || Contains(anchor.providerPayload, id) ||
                 Contains(anchor.causalProvenance, id)))) return true;
            if (anchors.Any(anchor => anchor != null && anchor.lastKnownLocation != null &&
                (Contains(anchor.lastKnownLocation.edge, id) || Contains(anchor.lastKnownLocation.areaId, id)))) return true;
            if (observations.Any(observation => observation != null &&
                (Contains(observation.observationId, id) || Contains(observation.observerId, id) || Contains(observation.source, id) ||
                 Contains(observation.subjectId, id) || Contains(observation.regionId, id) || Contains(observation.facet, id) ||
                 Contains(observation.estimate, id)))) return true;
            if (processes.Any(process => process != null && process.processId != id &&
                (Contains(process.processId, id) || Contains(process.providerId, id) || Contains(process.regionId, id) ||
                 Contains(process.lastError, id) || Contains(process.payload, id)))) return true;
            if (constraints.Any(constraint => constraint != null &&
                (Contains(constraint.constraintId, id) || Contains(constraint.providerId, id) || Contains(constraint.typeId, id) ||
                 Contains(constraint.regionId, id) || Contains(constraint.observerId, id) || Contains(constraint.source, id) ||
                 Contains(constraint.causalParentIds, id) || Contains(constraint.affectedAnchorIds, id) ||
                 Contains(constraint.affectedPopulationIds, id) || Contains(constraint.payload, id)))) return true;
            if (providerPayloads.Any(payload => payload != null &&
                (Contains(payload.providerId, id) || Contains(payload.payloadId, id) || Contains(payload.data, id)))) return true;
            if (quarantine.Any(item => item != null &&
                (item.recordId == id || Contains(item.recordType, id) || Contains(item.providerId, id) ||
                 Contains(item.reason, id) || Contains(item.payload, id)))) return true;
            if (transferJournals.Any(journal => journal != null &&
                (Contains(journal.transferId, id) || Contains(journal.sourceRegionId, id) || Contains(journal.destinationRegionId, id) ||
                  Contains(journal.edge, id) || Contains(journal.pawnLoadIds, id) || Contains(journal.diagnostic, id)))) return true;
            if (adjacentMaps.Any(marker => marker != null &&
                (Contains(marker.regionId, id) || Contains(marker.providerId, id) || Contains(marker.originRegionId, id) ||
                 Contains(marker.diagnostic, id)))) return true;
            if (excursions.Any(ticket => ticket != null &&
                (Contains(ticket.excursionId, id) || Contains(ticket.providerId, id) || Contains(ticket.pawnLoadId, id) ||
                  Contains(ticket.taskId, id) ||
                  Contains(ticket.originRegionId, id) || Contains(ticket.destinationRegionId, id) ||
                 Contains(ticket.outboundTransferId, id) || Contains(ticket.returnTransferId, id) ||
                 Contains(ticket.diagnostic, id)))) return true;
            if (appliedOperations.Any(operation => operation != null &&
                (Contains(operation.operationId, id) || Contains(operation.providerId, id) || Contains(operation.kind, id) ||
                 Contains(operation.domainId, id)))) return true;
            if (adjacentDiagnostics.Any(diagnostic => diagnostic != null &&
                (Contains(diagnostic.diagnosticId, id) || Contains(diagnostic.kind, id) ||
                 Contains(diagnostic.providerId, id) || Contains(diagnostic.mapOrExcursionId, id) ||
                 Contains(diagnostic.message, id)))) return true;
            if (operationWatermarks.Any(watermark => watermark != null &&
                (Contains(watermark.providerId, id) || Contains(watermark.kind, id) || Contains(watermark.domainId, id) ||
                 Contains(watermark.proof, id)))) return true;
            return conflicts.Any(conflict => conflict != null &&
                (Contains(conflict.conflictId, id) || Contains(conflict.regionId, id) || Contains(conflict.subjectId, id) ||
                 Contains(conflict.constraintIds, id) || Contains(conflict.reason, id)));
        }

        private static bool Contains(string value, string term)
        {
            return !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(term) && value.IndexOf(term, StringComparison.Ordinal) >= 0;
        }

        private static bool Contains(IEnumerable<string> values, string term)
        {
            return values != null && !string.IsNullOrEmpty(term) && values.Any(value => string.Equals(value, term, StringComparison.Ordinal));
        }

        private static string QuarantineKey(RealityQuarantineRecord item)
        {
            return item == null ? string.Empty : QuarantineKey(item.recordType, item.recordId, item.providerId, item.reason, item.payload);
        }

        private static string QuarantineKey(string recordType, string recordId, string providerId, string reason, string payload)
        {
            return (recordType ?? string.Empty) + "\0" + (recordId ?? string.Empty) + "\0" +
                (providerId ?? string.Empty) + "\0" + (reason ?? string.Empty) + "\0" + (payload ?? string.Empty);
        }

        private static float Clamp01(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? 0f : Math.Max(0f, Math.Min(1f, value));
    }

    internal sealed class RealityWorldState
    {
        internal List<RealityRegionDescriptor> regions;
        internal List<RealityRegionConnection> connections;
        internal List<RealityPopulationRecord> populations;
        internal List<RealityAnchorRecord> anchors;
        internal List<RealityConstraint> constraints;
        internal List<RealityProcessRecord> processes;
        internal List<RealityObservationRecord> observations;
        internal List<RealityProviderPayload> providerPayloads;
        internal List<RealityAppliedOperation> appliedOperations;
        internal List<RealityConflictReport> conflicts;
        internal List<RealityQuarantineRecord> quarantine;
        internal List<RealityTransferJournalRecord> transferJournals;
        internal List<RealityAdjacentMapRecord> adjacentMaps;
        internal List<RealityExcursionTicket> excursions;
        internal List<RealityMapCreationIntentRecord> mapCreationIntents;
        internal List<RealityAdjacentDiagnosticRecord> adjacentDiagnostics;
        internal List<RealityOperationRetentionWatermark> operationWatermarks;
        internal List<RealityFidelityEscalationRecord> fidelityEscalations;
    }
}
