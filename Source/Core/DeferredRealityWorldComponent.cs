using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using DeferredReality.Simulation;
using Verse;

namespace DeferredReality.API
{
    /// <summary>Authoritative world-level store for latent reality.</summary>
    public sealed class DeferredRealityWorldComponent : WorldComponent
    {
        public const int CurrentSaveSchema = 1;
        private const int MaximumObservationHistory = 8192;

        private int saveSchema = CurrentSaveSchema;
        private List<RealityRegionDescriptor> regions = new List<RealityRegionDescriptor>();
        private List<RealityTopologyLink> topology = new List<RealityTopologyLink>();
        private List<RealityPopulationRecord> populations = new List<RealityPopulationRecord>();
        private List<RealityAnchorRecord> anchors = new List<RealityAnchorRecord>();
        private List<RealityConstraint> constraints = new List<RealityConstraint>();
        private List<RealityProcessRecord> processes = new List<RealityProcessRecord>();
        private List<RealityObservationRecord> observations = new List<RealityObservationRecord>();
        private List<RealityProviderPayload> providerPayloads = new List<RealityProviderPayload>();
        private List<RealityMapAlias> mapAliases = new List<RealityMapAlias>();
        private List<RealityMigrationMarker> migrations = new List<RealityMigrationMarker>();
        private List<RealityAppliedOperation> appliedOperations = new List<RealityAppliedOperation>();
        private List<RealityConflictReport> conflicts = new List<RealityConflictReport>();
        private List<RealityQuarantineRecord> quarantine = new List<RealityQuarantineRecord>();
        private List<RealityTransferJournalRecord> transferJournals = new List<RealityTransferJournalRecord>();

        private readonly Dictionary<string, RealityRegionDescriptor> regionById = new Dictionary<string, RealityRegionDescriptor>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityPopulationRecord> populationById = new Dictionary<string, RealityPopulationRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityAnchorRecord> anchorById = new Dictionary<string, RealityAnchorRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityConstraint> constraintById = new Dictionary<string, RealityConstraint>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityProcessRecord> processById = new Dictionary<string, RealityProcessRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, RealityObservationRecord> observationById = new Dictionary<string, RealityObservationRecord>(StringComparer.Ordinal);
        private readonly HashSet<string> appliedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> regionByLegacyMapId = new Dictionary<int, string>();
        private bool initialized;
        private bool indexesReady;
        private int revision;

        /// <summary>Returns the active world component, if a world exists.</summary>
        public static DeferredRealityWorldComponent Current => Find.World?.GetComponent<DeferredRealityWorldComponent>();

        /// <summary>Creates the component for a RimWorld world.</summary>
        public DeferredRealityWorldComponent(World world) : base(world) { }

        /// <summary>Current store revision used by read-model caches.</summary>
        public int Revision => revision;

        /// <summary>World seed string used as the root deterministic input.</summary>
        public string WorldSeed => Find.World?.info?.seedString ?? string.Empty;

        /// <summary>Current game tick, or zero during early load.</summary>
        public long Now => Find.TickManager?.TicksGame ?? 0;

        /// <inheritdoc />
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref saveSchema, "deferredRealitySaveSchema", CurrentSaveSchema);
            Scribe_Values.Look(ref revision, "deferredRealityRevision", 0);
            Scribe_Collections.Look(ref regions, "deferredRealityRegions", LookMode.Deep);
            Scribe_Collections.Look(ref topology, "deferredRealityTopology", LookMode.Deep);
            Scribe_Collections.Look(ref populations, "deferredRealityPopulations", LookMode.Deep);
            Scribe_Collections.Look(ref anchors, "deferredRealityAnchors", LookMode.Deep);
            Scribe_Collections.Look(ref constraints, "deferredRealityConstraints", LookMode.Deep);
            Scribe_Collections.Look(ref processes, "deferredRealityProcesses", LookMode.Deep);
            Scribe_Collections.Look(ref observations, "deferredRealityObservations", LookMode.Deep);
            Scribe_Collections.Look(ref providerPayloads, "deferredRealityProviderPayloads", LookMode.Deep);
            Scribe_Collections.Look(ref mapAliases, "deferredRealityMapAliases", LookMode.Deep);
            Scribe_Collections.Look(ref migrations, "deferredRealityMigrations", LookMode.Deep);
            Scribe_Collections.Look(ref appliedOperations, "deferredRealityAppliedOperations", LookMode.Deep);
            Scribe_Collections.Look(ref conflicts, "deferredRealityConflicts", LookMode.Deep);
            Scribe_Collections.Look(ref quarantine, "deferredRealityQuarantine", LookMode.Deep);
            Scribe_Collections.Look(ref transferJournals, "deferredRealityTransferJournals", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                regions = regions ?? new List<RealityRegionDescriptor>();
                topology = topology ?? new List<RealityTopologyLink>();
                populations = populations ?? new List<RealityPopulationRecord>();
                anchors = anchors ?? new List<RealityAnchorRecord>();
                constraints = constraints ?? new List<RealityConstraint>();
                processes = processes ?? new List<RealityProcessRecord>();
                observations = observations ?? new List<RealityObservationRecord>();
                providerPayloads = providerPayloads ?? new List<RealityProviderPayload>();
                mapAliases = mapAliases ?? new List<RealityMapAlias>();
                migrations = migrations ?? new List<RealityMigrationMarker>();
                appliedOperations = appliedOperations ?? new List<RealityAppliedOperation>();
                conflicts = conflicts ?? new List<RealityConflictReport>();
                quarantine = quarantine ?? new List<RealityQuarantineRecord>();
                transferJournals = transferJournals ?? new List<RealityTransferJournalRecord>();
                if (saveSchema > CurrentSaveSchema)
                    QuarantineInternal("save", "root", "core", "Save schema is newer than this framework version.", saveSchema.ToString(), Now);
                saveSchema = CurrentSaveSchema;
                RepairAndIndex();
            }
        }

        /// <inheritdoc />
        public override void WorldComponentTick()
        {
            if (!indexesReady) RepairAndIndex();
            if (!initialized)
            {
                RealityThreadGuard.InitializeMainThread();
                initialized = true;
                RealityProviderRegistry.NotifyWorldReady(this);
                RepairAndIndex();
            }
            if (processes.Count > 0) RealityProcessScheduler.RunDue(this, Now, RealityProcessRunOptions.Default);
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

        /// <summary>Records or updates a topology link.</summary>
        public bool UpsertTopology(RealityTopologyLink link)
        {
            RealityThreadGuard.RequireMainThread();
            if (link == null || string.IsNullOrEmpty(link.linkId) || string.IsNullOrEmpty(link.fromRegionId) || string.IsNullOrEmpty(link.toRegionId)) return false;
            EnsureIndexes();
            RealityTopologyLink copy = link.Clone();
            int index = topology.FindIndex(item => item?.linkId == copy.linkId);
            if (index >= 0) topology[index] = copy;
            else topology.Add(copy);
            Touch("topology.changed", "core", copy.fromRegionId, copy.linkId);
            return true;
        }

        /// <summary>Returns detached topology links optionally scoped to a region.</summary>
        public IReadOnlyList<RealityTopologyLink> TopologySnapshots(string regionId = null)
        {
            return topology.Where(item => item != null && (string.IsNullOrEmpty(regionId) || item.fromRegionId == regionId || item.toRegionId == regionId))
                .OrderBy(item => item.linkId, StringComparer.Ordinal).Select(item => item.Clone()).ToList();
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
                .OrderBy(item => item.nextDueTick).ThenByDescending(item => item.priority).ThenBy(item => item.processId, StringComparer.Ordinal)
                .Select(item => new RealityProcessSnapshot(item)).ToList();
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
            int index = processes.FindIndex(item => item?.processId == copy.processId);
            if (index >= 0) processes[index] = copy;
            else processes.Add(copy);
            processById[copy.processId] = copy;
            Touch("process.scheduled", copy.providerId, copy.regionId, copy.processId);
            return true;
        }

        /// <summary>Updates one process payload in place so a provider can persist an execution stage safely.</summary>
        public bool UpdateProcessPayload(string processId, string payload)
        {
            RealityThreadGuard.RequireMainThread();
            RealityProcessRecord process = ProcessRecord(processId);
            if (process == null || payload == null) return false;
            process.payload = payload;
            Touch("process.payload", process.providerId, process.regionId, process.processId);
            return true;
        }

        /// <summary>Records an observation without changing objective population data.</summary>
        public bool AddObservation(RealityObservationInput input)
        {
            RealityThreadGuard.RequireMainThread();
            if (input == null || string.IsNullOrEmpty(input.observationId) || string.IsNullOrEmpty(input.regionId)) return false;
            EnsureIndexes();
            if (observationById.ContainsKey(input.observationId)) return true;
            var record = new RealityObservationRecord
            {
                observationId = input.observationId,
                observerId = input.observerId,
                source = input.source,
                tick = input.tick,
                subjectId = input.subjectId,
                regionId = input.regionId,
                certainty = Clamp01(input.certainty),
                confidence = Clamp01(input.confidence),
                spatialPrecision = input.spatialPrecision,
                facet = input.facet,
                playerObserved = input.playerObserved,
                sensorDerived = input.sensorDerived,
                inferred = input.inferred,
                rumored = input.rumored,
                estimate = input.estimate
            };
            observations.Add(record);
            observationById[record.observationId] = record;
            if (observations.Count > MaximumObservationHistory)
            {
                RealityObservationRecord oldest = observations.OrderBy(item => item.tick).ThenBy(item => item.observationId, StringComparer.Ordinal).FirstOrDefault();
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

        /// <summary>Associates an active map with a stable region and stores only the map ID as a legacy alias/link.</summary>
        public RealityRegionId RegisterMap(Map map)
        {
            RealityThreadGuard.RequireMainThread();
            if (map == null) return default(RealityRegionId);
            EnsureIndexes();
            if (regionByLegacyMapId.TryGetValue(map.uniqueID, out string existingId) && RealityRegionId.TryParse(existingId, out RealityRegionId existing))
            {
                MarkMapActive(existing, map);
                return existing;
            }
            if (!map.Tile.Valid)
            {
                QuarantineInternal("map", map.uniqueID.ToString(), "core", "Map has no valid world tile; stable placement requires migration review.", null, Now);
                return default(RealityRegionId);
            }
            RealityRegionId id = RealityRegionId.Surface((int)map.Tile);
            EnsureRegion(id, "World tile " + id.WorldTile, Now);
            mapAliases.Add(new RealityMapAlias { legacyMapId = map.uniqueID, regionId = id.ToString(), migratedTick = Now });
            regionByLegacyMapId[map.uniqueID] = id.ToString();
            MarkMapActive(id, map);
            Touch("map.mapped", "core", id.ToString(), map.uniqueID.ToString());
            return id;
        }

        /// <summary>Associates a map with an explicit stable region, preserving the legacy tile-based overload.</summary>
        public RealityRegionId RegisterMap(Map map, RealityRegionId regionId)
        {
            RealityThreadGuard.RequireMainThread();
            if (map == null || !regionId.IsValid) return default(RealityRegionId);
            EnsureIndexes();
            if (!map.Tile.Valid || map.Tile != (PlanetTile)regionId.WorldTile)
            {
                QuarantineInternal("map", map.uniqueID.ToString(), "core",
                    "Explicit map region does not match the map world tile.", regionId.ToString(), Now);
                return default(RealityRegionId);
            }
            if (regionByLegacyMapId.TryGetValue(map.uniqueID, out string previousId) && previousId != regionId.ToString())
            {
                RealityRegionDescriptor previous = RegionRecord(previousId);
                if (previous != null && previous.activeMapUniqueId == map.uniqueID)
                {
                    previous.activeMapUniqueId = -1;
                    previous.fidelity = populations.Any(item => item?.regionId == previous.regionId)
                        ? RealityFidelity.Statistical : RealityFidelity.Dormant;
                }
                mapAliases.RemoveAll(item => item != null && item.legacyMapId == map.uniqueID);
            }
            EnsureRegion(regionId, "World tile " + regionId.WorldTile, Now);
            RealityMapAlias alias = mapAliases.FirstOrDefault(item => item != null && item.legacyMapId == map.uniqueID);
            if (alias == null)
            {
                mapAliases.Add(new RealityMapAlias { legacyMapId = map.uniqueID, regionId = regionId.ToString(), migratedTick = Now });
            }
            else
            {
                alias.regionId = regionId.ToString();
                alias.migratedTick = Now;
            }
            regionByLegacyMapId[map.uniqueID] = regionId.ToString();
            MarkMapActive(regionId, map);
            Touch("map.mapped", regionId.ProviderNamespace, regionId.ToString(), map.uniqueID.ToString());
            return regionId;
        }

        /// <summary>Releases the active-map link while preserving latent state for future materialization.</summary>
        public void UnregisterMap(Map map)
        {
            RealityThreadGuard.RequireMainThread();
            if (map == null || !TryRegionForLegacyMap(map.uniqueID, out RealityRegionId regionId)) return;
            RealityRegionDescriptor descriptor = RegionRecord(regionId.ToString());
            if (descriptor == null || descriptor.activeMapUniqueId != map.uniqueID) return;
            descriptor.activeMapUniqueId = -1;
            descriptor.fidelity = populations.Any(item => item?.regionId == descriptor.regionId)
                ? RealityFidelity.Statistical : RealityFidelity.Dormant;
            descriptor.lastUpdateTick = Now;
            Touch("map.unmapped", "core", descriptor.regionId, map.uniqueID.ToString());
        }

        /// <summary>Looks up the stable identity associated with a legacy map ID.</summary>
        public bool TryRegionForLegacyMap(int mapId, out RealityRegionId regionId)
        {
            EnsureIndexes();
            regionId = default(RealityRegionId);
            return regionByLegacyMapId.TryGetValue(mapId, out string value) && RealityRegionId.TryParse(value, out regionId);
        }

        /// <summary>Returns whether a mutation operation has already been applied.</summary>
        public bool HasAppliedOperation(string operationId)
        {
            EnsureIndexes();
            return !string.IsNullOrEmpty(operationId) && appliedOperationIds.Contains(operationId);
        }

        /// <summary>Persists an exactly-once operation marker.</summary>
        public bool RecordAppliedOperation(string operationId, string providerId, string kind, long tick)
        {
            RealityThreadGuard.RequireMainThread();
            if (string.IsNullOrEmpty(operationId)) return false;
            EnsureIndexes();
            if (!appliedOperationIds.Add(operationId)) return false;
            appliedOperations.Add(new RealityAppliedOperation { operationId = operationId, providerId = providerId, kind = kind, tick = tick });
            Touch("operation.applied", providerId, null, operationId);
            return true;
        }

        /// <summary>Returns whether a provider migration version is committed.</summary>
        public bool IsMigrationCommitted(string providerId, string consumerId, int version)
        {
            return migrations.Any(item => item != null && item.providerId == providerId && item.consumerId == consumerId && item.version >= version);
        }

        /// <summary>Commits an idempotent migration marker after provider validation.</summary>
        public bool CommitMigration(string providerId, string consumerId, int version, string checksum)
        {
            RealityThreadGuard.RequireMainThread();
            if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(consumerId) || version < 1) return false;
            RealityMigrationMarker marker = migrations.FirstOrDefault(item => item?.providerId == providerId && item.consumerId == consumerId);
            if (marker != null && marker.version >= version) return true;
            if (marker == null)
            {
                marker = new RealityMigrationMarker { providerId = providerId, consumerId = consumerId };
                migrations.Add(marker);
            }
            marker.version = version;
            marker.checksum = checksum;
            marker.committedTick = Now;
            Touch("migration.committed", providerId, null, consumerId + ":" + version);
            return true;
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
            return quarantine.Where(item => item != null).Select(item => item.Clone()).ToList();
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
            return true;
        }

        internal RealityWorldState CaptureState()
        {
            return new RealityWorldState
            {
                regions = regions.Where(item => item != null).Select(item => item.Clone()).ToList(),
                topology = topology.Where(item => item != null).Select(item => item.Clone()).ToList(),
                populations = populations.Where(item => item != null).Select(item => item.Clone()).ToList(),
                anchors = anchors.Where(item => item != null).Select(item => item.Clone()).ToList(),
                constraints = constraints.Where(item => item != null).Select(item => item.Clone()).ToList(),
                processes = processes.Where(item => item != null).Select(item => item.Clone()).ToList(),
                observations = observations.Where(item => item != null).Select(item => item.Clone()).ToList(),
                providerPayloads = providerPayloads.Where(item => item != null).Select(item => item.Clone()).ToList(),
                mapAliases = mapAliases.Where(item => item != null).Select(item => item.Clone()).ToList(),
                migrations = migrations.Where(item => item != null).Select(item => item.Clone()).ToList(),
                appliedOperations = appliedOperations.Where(item => item != null).Select(item => item.Clone()).ToList(),
                conflicts = conflicts.Where(item => item != null).Select(item => item.Clone()).ToList(),
                quarantine = quarantine.Where(item => item != null).Select(item => item.Clone()).ToList(),
                transferJournals = transferJournals.Where(item => item != null).Select(item => item.Clone()).ToList()
            };
        }

        internal void RestoreState(RealityWorldState state)
        {
            RealityThreadGuard.RequireMainThread();
            if (state == null) return;
            regions = state.regions ?? new List<RealityRegionDescriptor>();
            topology = state.topology ?? new List<RealityTopologyLink>();
            populations = state.populations ?? new List<RealityPopulationRecord>();
            anchors = state.anchors ?? new List<RealityAnchorRecord>();
            constraints = state.constraints ?? new List<RealityConstraint>();
            processes = state.processes ?? new List<RealityProcessRecord>();
            observations = state.observations ?? new List<RealityObservationRecord>();
            providerPayloads = state.providerPayloads ?? new List<RealityProviderPayload>();
            mapAliases = state.mapAliases ?? new List<RealityMapAlias>();
            migrations = state.migrations ?? new List<RealityMigrationMarker>();
            appliedOperations = state.appliedOperations ?? new List<RealityAppliedOperation>();
            conflicts = state.conflicts ?? new List<RealityConflictReport>();
            quarantine = state.quarantine ?? new List<RealityQuarantineRecord>();
            transferJournals = state.transferJournals ?? new List<RealityTransferJournalRecord>();
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

        private void MarkMapActive(RealityRegionId id, Map map)
        {
            RealityRegionDescriptor record = RegionRecord(id.ToString());
            if (record == null) return;
            record.activeMapUniqueId = map.uniqueID;
            record.lastKnownWorldTile = id.WorldTile;
            record.fidelity = RealityFidelity.Materialized;
            record.lifecycle = RealityLifecycleState.Active;
            record.lastUpdateTick = Now;
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
            regionByLegacyMapId.Clear();
            RepairList(regions, item => item?.regionId, "region");
            RepairList(populations, item => item?.populationId, "population");
            RepairList(anchors, item => item?.anchorId, "anchor");
            RepairList(constraints, item => item?.constraintId, "constraint");
            RepairList(processes, item => item?.processId, "process");
            RepairList(observations, item => item?.observationId, "observation");
            mapAliases = mapAliases.Where(item => item != null && item.legacyMapId >= 0 && !string.IsNullOrEmpty(item.regionId)).ToList();
            migrations = migrations.Where(item => item != null && !string.IsNullOrEmpty(item.providerId) && !string.IsNullOrEmpty(item.consumerId)).ToList();
            appliedOperations = appliedOperations.Where(item => item != null && !string.IsNullOrEmpty(item.operationId)).ToList();
            RepairUnique(topology, item => item?.linkId, "topology");
            RepairUnique(mapAliases, item => item == null ? null : item.legacyMapId.ToString(), "map-alias");
            RepairUnique(migrations, item => item == null ? null : item.providerId + ":" + item.consumerId, "migration");
            RepairUnique(appliedOperations, item => item?.operationId, "operation");
            RepairUnique(transferJournals, item => item?.transferId, "transfer-journal");
            RepairUnique(providerPayloads, item => item == null ? null : item.providerId + ":" + item.payloadId, "provider-payload");
            foreach (RealityMapAlias alias in mapAliases.OrderBy(item => item.legacyMapId)) if (!regionByLegacyMapId.ContainsKey(alias.legacyMapId)) regionByLegacyMapId[alias.legacyMapId] = alias.regionId;
            foreach (RealityAppliedOperation operation in appliedOperations) appliedOperationIds.Add(operation.operationId);
            providerPayloads = providerPayloads.Where(item => item != null && !string.IsNullOrEmpty(item.providerId)).ToList();
            conflicts = conflicts.Where(item => item != null && !string.IsNullOrEmpty(item.conflictId)).ToList();
            quarantine = quarantine.Where(item => item != null).ToList();
            transferJournals = transferJournals.Where(item => item != null && !string.IsNullOrEmpty(item.transferId)).ToList();
            indexesReady = true;
        }

        private void RepairList<T>(List<T> values, Func<T, string> idSelector, string recordType) where T : class
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = values.Count - 1; i >= 0; i--)
            {
                T value = values[i];
                string id = idSelector(value);
                if (value == null || string.IsNullOrEmpty(id))
                {
                    QuarantineInternal(recordType, null, "core", "Record has no stable ID.", null, Now);
                    values.RemoveAt(i);
                    continue;
                }
                if (!seen.Add(id))
                {
                    QuarantineInternal(recordType, id, "core", "Duplicate stable ID retained only in audit quarantine.", null, Now);
                    values.RemoveAt(i);
                }
            }
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

        private void RepairUnique<T>(List<T> values, Func<T, string> idSelector, string recordType) where T : class
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = values.Count - 1; i >= 0; i--)
            {
                string id = idSelector(values[i]);
                if (values[i] == null || string.IsNullOrEmpty(id))
                {
                    QuarantineInternal(recordType, null, "core", "Record has no stable ID.", null, Now);
                    values.RemoveAt(i);
                }
                else if (!seen.Add(id))
                {
                    QuarantineInternal(recordType, id, "core", "Duplicate stable ID retained only in audit quarantine.", null, Now);
                    values.RemoveAt(i);
                }
            }
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
        }

        private void QuarantineInternal(string recordType, string recordId, string providerId, string reason, string payload, long tick)
        {
            quarantine.Add(new RealityQuarantineRecord
            {
                recordType = recordType,
                recordId = recordId,
                providerId = providerId,
                reason = reason,
                payload = payload,
                detectedTick = tick
            });
        }

        private static float Clamp01(float value) => Math.Max(0f, Math.Min(1f, value));
    }

    internal sealed class RealityWorldState
    {
        internal List<RealityRegionDescriptor> regions;
        internal List<RealityTopologyLink> topology;
        internal List<RealityPopulationRecord> populations;
        internal List<RealityAnchorRecord> anchors;
        internal List<RealityConstraint> constraints;
        internal List<RealityProcessRecord> processes;
        internal List<RealityObservationRecord> observations;
        internal List<RealityProviderPayload> providerPayloads;
        internal List<RealityMapAlias> mapAliases;
        internal List<RealityMigrationMarker> migrations;
        internal List<RealityAppliedOperation> appliedOperations;
        internal List<RealityConflictReport> conflicts;
        internal List<RealityQuarantineRecord> quarantine;
        internal List<RealityTransferJournalRecord> transferJournals;
    }
}
