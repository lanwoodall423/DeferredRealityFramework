using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace DeferredReality.API
{
    /// <summary>Versioned registration metadata for a provider.</summary>
    public sealed class RealityProviderRegistration
    {
        public string providerId;
        public int semanticApiVersion = 1;
        public int schemaVersion = 1;
        public int order;
        public RealityProviderCapability capabilities;
        public List<string> dependencies = new List<string>();
        public List<string> orderingBefore = new List<string>();
        public List<string> orderingAfter = new List<string>();
        /// <summary>Retention window for explicitly safe exactly-once operation kinds; negative means durable forever.</summary>
        public long operationRetentionTicks = -1;
        /// <summary>Operation kinds that are safe to expire under operationRetentionTicks.</summary>
        public List<string> compactableOperationKinds = new List<string>();
        /// <summary>Optional opt-in window for cancelled process records; negative means retain until explicitly recovered.</summary>
        public long cancelledProcessRetentionTicks = -1;
        public string displayName;

        /// <summary>Returns a detached, normalized metadata copy.</summary>
        public RealityProviderRegistration Clone()
        {
            return new RealityProviderRegistration
            {
                providerId = providerId,
                semanticApiVersion = semanticApiVersion,
                schemaVersion = schemaVersion,
                order = order,
                capabilities = capabilities,
                dependencies = new List<string>(dependencies ?? new List<string>()),
                orderingBefore = new List<string>(orderingBefore ?? new List<string>()),
                orderingAfter = new List<string>(orderingAfter ?? new List<string>()),
                operationRetentionTicks = operationRetentionTicks,
                compactableOperationKinds = new List<string>(compactableOperationKinds ?? new List<string>()),
                cancelledProcessRetentionTicks = cancelledProcessRetentionTicks,
                displayName = displayName
            };
        }
    }

    /// <summary>Read-only context supplied while a provider is registered or executed.</summary>
    public sealed class RealityProviderContext
    {
        internal RealityProviderContext(DeferredRealityWorldComponent world, string providerId, long now)
        {
            World = world;
            ProviderId = providerId;
            Now = now;
            WorldSeed = world?.WorldSeed ?? string.Empty;
        }

        /// <summary>Authoritative framework store.</summary>
        public DeferredRealityWorldComponent World { get; }

        /// <summary>Registered provider identity.</summary>
        public string ProviderId { get; }

        /// <summary>Current game tick at context creation.</summary>
        public long Now { get; }

        /// <summary>World seed string used by deterministic provider streams.</summary>
        public string WorldSeed { get; }

        /// <summary>Creates a deterministic stream scoped to this provider and operation.</summary>
        public RealityRandomStream Random(string operationId, long epoch = 0)
        {
            return new RealityRandomStream(RealityDeterminism.Seed(WorldSeed, string.Empty, ProviderId, operationId, epoch));
        }

        /// <summary>Declares a provider/domain sequence boundary used for replay-safe marker retention.</summary>
        public bool DeclareExactlyOnceDomain(string kind, string domainId, bool allowGaps, string proof)
        {
            return World != null && World.DeclareExactlyOnceDomain(ProviderId, kind, domainId, allowGaps, proof);
        }

        /// <summary>Advances a declared provider/domain cursor after its sequenced operation is durably committed.</summary>
        public bool AdvanceExactlyOnceCursor(string kind, string domainId, long sequence, string proof)
        {
            return World != null && World.AdvanceExactlyOnceCursor(ProviderId, kind, domainId, sequence, proof);
        }
    }

    /// <summary>Provider declaration for a durable exactly-once sequence domain.</summary>
    public sealed class RealityExactlyOnceDomain
    {
        public string kind;
        public string domainId;
        public bool allowGaps;
        public string proof;
    }

    /// <summary>Optional provider description for a sequence domain before it is persisted.</summary>
    public interface IRealityExactlyOnceProvider
    {
        bool TryDescribeExactlyOnceDomain(string kind, string domainId, out RealityExactlyOnceDomain domain);
    }

    /// <summary>Public provider extension point. Providers may implement any capability interfaces below.</summary>
    public interface IRealityProvider
    {
        /// <summary>Stable provider metadata.</summary>
        RealityProviderRegistration Registration { get; }

        /// <summary>Called once after registration and again safely after a world is loaded.</summary>
        void OnRegistered(RealityProviderContext context);
    }

    /// <summary>Provider that can describe or seed region descriptors.</summary>
    public interface IRegionDescriptorProvider
    {
        IEnumerable<RealityRegionDescriptor> DescribeRegions(RealityProviderContext context);
    }

    /// <summary>Provider that executes analytical scheduled processes.</summary>
    public interface IRealityProcessProvider
    {
        bool CanExecute(RealityProcessRecord process, RealityProcessExecution execution, IList<RealityVeto> vetoes);
        RealityProcessResult Execute(RealityProcessRecord process, RealityProcessExecution execution);
    }

    /// <summary>Provider-specific policies for aggregate populations.</summary>
    public interface IPopulationProvider
    {
        bool CanChangePopulation(RealityPopulationRecord population, string operation, IList<RealityVeto> vetoes);
        void ReconcileActiveMap(RealityProviderContext context, RealityPopulationRecord population, string payload);
    }

    /// <summary>Provider validation and restoration for identity-bearing anchors.</summary>
    public interface IAnchorProvider
    {
        bool ValidateAnchor(RealityAnchorRecord anchor, IList<RealityVeto> vetoes);
        void OnAnchorMaterialized(RealityProviderContext context, RealityAnchorRecord anchor);
    }

    /// <summary>Provider that resolves its own constraint payloads.</summary>
    public interface IConstraintResolver
    {
        bool CanResolve(RealityConstraint constraint);
        bool Resolve(RealityProviderContext context, RealityConstraint constraint, IList<RealityVeto> vetoes);
    }

    /// <summary>Provider stage in the transactional materialization pipeline.</summary>
    public interface IMaterializationProvider
    {
        int Order { get; }
        void CanMaterialize(RealityMaterializationRequest request, RealityMaterializationPlan plan);
        void Prepare(RealityMaterializationRequest request, RealityMaterializationPlan plan);
        void Apply(RealityMaterializationContext context);
        void Validate(RealityMaterializationContext context, IList<RealityVeto> vetoes);
        /// <summary>May be called after Prepare has started, including after a partial throw; must be idempotent.</summary>
        void Rollback(RealityMaterializationContext context);
    }

    /// <summary>Optional materialization extension that receives the stages that actually began.</summary>
    public interface IStageAwareMaterializationProvider
    {
        void Rollback(RealityMaterializationContext context, RealityMaterializationStage stages);
    }

    /// <summary>Optional transactional anchor owner. Legacy anchor callbacks remain supported as final commit notifications.</summary>
    public interface ITransactionalAnchorProvider
    {
        bool PrepareAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor, IList<RealityVeto> vetoes);
        bool ApplyAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor, IList<RealityVeto> vetoes);
        bool ValidateAnchorMaterialization(RealityMaterializationContext context, RealityAnchorRecord anchor, IList<RealityVeto> vetoes);
        void RollbackAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor);
    }

    /// <summary>Optional cleanup hook after a transactional anchor reaches commit.</summary>
    public interface ITransactionalAnchorCommitProvider
    {
        void CommitAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor);
    }

    /// <summary>Explicit provider-owned identity for a nonstandard map.</summary>
    public sealed class RealityMapIdentityClaim
    {
        public string providerId;
        public RealityRegionId regionId;
        public string identityKey;

        public string StableKey => (providerId ?? string.Empty) + "|" + regionId + "|" + (identityKey ?? string.Empty);
    }

    /// <summary>Optional provider hook used before a map can claim a region.</summary>
    public interface IRealityMapIdentityProvider
    {
        bool TryClaimMap(Map map, out RealityMapIdentityClaim claim);
    }

    /// <summary>Provider stage in the conservative compression pipeline.</summary>
    public interface ICompressionProvider
    {
        int Order { get; }
        void CanCompress(RealityCompressionRequest request, IList<RealityVeto> vetoes);
        void Prepare(RealityCompressionRequest request);
        void Validate(RealityCompressionRequest request, IList<RealityVeto> vetoes);
        void Commit(RealityCompressionRequest request);
        /// <summary>Compensates prepared state, including a committed compression when outer eviction fails.</summary>
        void Rollback(RealityCompressionRequest request);
    }

    /// <summary>Provider hook for translating framework observations to consumer knowledge.</summary>
    public interface IObservationProvider
    {
        void OnObservation(RealityObservationRecord observation);
    }

    /// <summary>Provider hook for adding cached diagnostic rows.</summary>
    public interface IRealityDiagnosticsProvider
    {
        IEnumerable<string> DiagnosticLines(RealityDiagnosticsContext context);
    }

    /// <summary>Idempotent provider migration hook for schema upgrades and legacy imports.</summary>
    public interface IRealityMigrationHandler
    {
        bool TryMigrate(DeferredRealityWorldComponent world, string consumerId, int fromVersion, int toVersion,
            IList<RealityVeto> issues);
    }

    /// <summary>Optional host supplied map factory. The framework never invents a Map implementation.</summary>
    public interface IRealityMapFactory
    {
        bool TryCreateMap(RealityRegionId regionId, RealityMaterializationPlan plan, out Map map, out string diagnostic);
        void RemoveMap(Map map);
    }

    /// <summary>Optional host for safe pawn/group transfer between already materialized adjacent maps.</summary>
    public interface IAdjacentRegionTransferHost
    {
        bool CanTransfer(RealityAdjacentTransferRequest request, IList<RealityVeto> vetoes);
        bool Prepare(RealityAdjacentTransferRequest request, RealityTransferJournalRecord journal, out string diagnostic);
        bool Commit(RealityAdjacentTransferRequest request, RealityTransferJournalRecord journal, out string diagnostic);
        void Rollback(RealityAdjacentTransferRequest request, RealityTransferJournalRecord journal);
    }

    /// <summary>Optional provider observation for a live excursion task. Returning false means no reliable evidence exists.</summary>
    public interface IRealityExcursionTaskProvider
    {
        bool TryObserveExcursionTask(RealityExcursionTicket ticket, long now, out RealityExcursionTaskObservation observation);
    }

    /// <summary>Optional runtime cleanup after an excursion task becomes terminal and unrecoverable.</summary>
    public interface IRealityExcursionTaskCleanupProvider
    {
        void ForgetExcursionTask(RealityExcursionTicket ticket);
    }

    /// <summary>Bounded evidence supplied by an integration for an excursion task or lease.</summary>
    public sealed class RealityExcursionTaskObservation
    {
        public string taskId;
        public bool active;
        public bool completed;
        public bool abandoned;
        public long evidenceTick = -1;
        public string diagnostic;
    }

    /// <summary>Explicit transfer request. No pawn is captured unless a host accepts it.</summary>
    public sealed class RealityAdjacentTransferRequest
    {
        /// <summary>Optional provider owner when the surface region identity is shared.</summary>
        public string providerId;
        public Map sourceMap;
        public Map destinationMap;
        public RealityRegionId sourceRegionId;
        public RealityRegionId destinationRegionId;
        public IntVec3 sourceCell;
        public string entryEdge;
        public IReadOnlyList<Pawn> pawns = Array.Empty<Pawn>();
        public string transferId;
        /// <summary>Optional durable excursion ticket attached to a committed outbound transfer.</summary>
        public string excursionId;
        /// <summary>Optional provider task identity copied into the durable transfer journal.</summary>
        public string providerTaskId;
        /// <summary>Whether this transfer is the outbound leg that creates the ticket.</summary>
        public bool isOutboundExcursion;
    }

    /// <summary>Typed metadata for a temporary adjacent map. It is never inferred from a reason string.</summary>
    public sealed class RealityAdjacentMapMetadata
    {
        /// <summary>Materialization transaction that is authorized to create this temporary site.</summary>
        public string transactionId;
        /// <summary>Integration responsible for the adjacent-site lifecycle; independent of region namespace.</summary>
        public string providerId;
        public RealityRegionId originRegionId;
        public int originMapUniqueId = -1;
        public long createdTick = -1;
        public RealityAdjacentMapLifecycle lifecycle = RealityAdjacentMapLifecycle.Materializing;
    }

    /// <summary>Public input for beginning a runtime excursion lease before its outbound transfer commits.</summary>
    public sealed class RealityExcursionRequest
    {
        public string excursionId;
        public string providerId;
        public string pawnLoadId;
        /// <summary>Provider task/lease identity. It is distinct from the framework excursion ID.</summary>
        public string taskId;
        public RealityRegionId originRegionId;
        public int originMapUniqueId = -1;
        public RealityRegionId destinationRegionId;
        public int destinationMapUniqueId = -1;
        public IntVec3 originCell;
        public string inverseReturnEdge;
        public string outboundTransferId;
        public string returnTransferId;
        public long startTick;
        public long graceDeadline;
    }

    /// <summary>Result of an adjacent transfer attempt, including safe world-travel fallback.</summary>
    public sealed class RealityAdjacentTransferResult
    {
        public bool succeeded;
        public bool rolledBack;
        public bool fallbackToWorldTravel;
        public string diagnostic;
        public IReadOnlyList<RealityVeto> vetoes = Array.Empty<RealityVeto>();
        public IReadOnlyList<string> rollbackErrors = Array.Empty<string>();
    }

    /// <summary>Analytical execution window supplied to a process provider.</summary>
    public sealed class RealityProcessExecution
    {
        public string providerId;
        public string processId;
        public string regionId;
        public long fromTick;
        public long toTick;
        public long elapsedTicks;
        public int executionCount;
        public int boundedStepCount;
        public int maximumStepCount;
        public RealityRandomStream random;
    }

    /// <summary>Result returned by an analytical process.</summary>
    public sealed class RealityProcessResult
    {
        public bool succeeded = true;
        public bool cancel;
        public bool pause;
        public int nextDelayTicks = -1;
        public int analyticalSteps = 1;
        public string error;
    }

    /// <summary>Read-only veto or diagnostic reason.</summary>
    public sealed class RealityVeto
    {
        public readonly string code;
        public readonly string providerId;
        public readonly string message;
        public readonly int severity;

        /// <summary>Creates a structured veto.</summary>
        public RealityVeto(string code, string message, string providerId = null, int severity = 1)
        {
            this.code = code ?? "unknown";
            this.message = message ?? string.Empty;
            this.providerId = providerId;
            this.severity = severity;
        }

        /// <inheritdoc />
        public override string ToString() => code + ": " + message;
    }

    /// <summary>Requested materialization target.</summary>
    public sealed class RealityMaterializationRequest
    {
        public RealityRegionId regionId;
        /// <summary>Optional provider that owns this materialization when the region identity is shared.</summary>
        public string providerId;
        /// <summary>Optional identity-bearing anchor selected for this materialization.</summary>
        public string targetAnchorId;
        public long now;
        public string reason;
        public bool preserveObservedFacts = true;
        public string entryEdge;
        /// <summary>Optional explicit role metadata applied after a host map is generated.</summary>
        public RealityAdjacentMapMetadata adjacentMap;
    }

    /// <summary>Side-effect-free materialization plan.</summary>
    public sealed class RealityMaterializationPlan
    {
        private readonly List<RealityVeto> vetoes = new List<RealityVeto>();
        private readonly List<string> steps = new List<string>();

        /// <summary>Map to use if one is already active, otherwise null until a host factory creates one.</summary>
        public Map ActiveMap { get; internal set; }

        /// <summary>Optional selected anchor copied from the request for host factories.</summary>
        public string TargetAnchorId { get; internal set; }

        /// <summary>Runtime materialization identity used by map factories and readiness callbacks.</summary>
        public string TransactionId { get; internal set; }

        /// <summary>Provider and core plan steps.</summary>
        public IReadOnlyList<string> Steps => steps;

        /// <summary>Vetoes collected during planning.</summary>
        public IReadOnlyList<RealityVeto> Vetoes => vetoes;

        /// <summary>Adds a plan-only step.</summary>
        public void AddStep(string step)
        {
            if (!string.IsNullOrEmpty(step) && !steps.Contains(step)) steps.Add(step);
        }

        /// <summary>Adds a veto.</summary>
        public void AddVeto(RealityVeto veto)
        {
            if (veto != null) vetoes.Add(veto);
        }
    }

    /// <summary>Mutable transaction context visible only after a plan is accepted.</summary>
    public sealed class RealityMaterializationContext
    {
        internal RealityMaterializationContext(DeferredRealityWorldComponent world, RealityMaterializationRequest request,
            RealityMaterializationPlan plan, Map map, string transactionId)
        {
            World = world;
            Request = request;
            Plan = plan;
            Map = map;
            TransactionId = transactionId ?? string.Empty;
        }

        /// <summary>Authoritative framework store.</summary>
        public DeferredRealityWorldComponent World { get; }

        /// <summary>Original request.</summary>
        public RealityMaterializationRequest Request { get; }

        /// <summary>Accepted plan.</summary>
        public RealityMaterializationPlan Plan { get; }

        /// <summary>Host-created RimWorld map, if a host factory supplied one.</summary>
        public Map Map { get; }

        /// <summary>Runtime-only identity for provider compensation state.</summary>
        public string TransactionId { get; }

        /// <summary>Runtime-only provider compensation data; never persisted.</summary>
        public IDictionary<string, object> RuntimeState { get; } = new Dictionary<string, object>();

        /// <summary>Stages that began; providers must not compensate stages outside this mask.</summary>
        public RealityMaterializationStage Stages { get; internal set; }

        internal void MarkStage(RealityMaterializationStage stage) { Stages |= stage; }
    }

    /// <summary>Compression request. Generic compression is intentionally unsupported by default.</summary>
    public sealed class RealityCompressionRequest
    {
        public Map Map;
        public RealityRegionId regionId;
        /// <summary>Explicit compression owner; when omitted the region provider namespace is used.</summary>
        public string providerId;
        public long now;
        public string reason;
        public bool dryRun = true;
    }

    /// <summary>One observation input before it is persisted.</summary>
    public sealed class RealityObservationInput
    {
        public string observationId;
        public string observerId;
        public string source;
        public long tick;
        public string subjectId;
        public string regionId;
        public float certainty;
        public float confidence;
        public RealityObservationPrecision spatialPrecision;
        public string facet;
        public bool playerObserved;
        public bool sensorDerived;
        public bool inferred;
        public bool rumored;
        public string estimate;
    }

    /// <summary>Result of a transactional fidelity transition.</summary>
    public sealed class RealityTransitionResult
    {
        public bool succeeded;
        public bool rolledBack;
        public string error;
        /// <summary>Original transition failure, retained separately from compensation diagnostics.</summary>
        public string originalError;
        /// <summary>Provider/map compensation failures are never discarded.</summary>
        public IReadOnlyList<string> rollbackErrors = Array.Empty<string>();
        public RealityRegionId regionId;
        public IReadOnlyList<RealityVeto> vetoes = Array.Empty<RealityVeto>();
        public IReadOnlyList<string> steps = Array.Empty<string>();
    }

    /// <summary>Read-only region projection used by callers instead of mutable store records.</summary>
    public sealed class RealityRegionSnapshot
    {
        public readonly RealityRegionId id;
        public readonly string label;
        public readonly RealityFidelity fidelity;
        public readonly RealityObservationPrecision observationLevel;
        public readonly RealityLifecycleState lifecycle;
        public readonly int stableSeed;
        public readonly long createdTick;
        public readonly long lastUpdateTick;
        public readonly long lastMaterializedTick;
        public readonly int activeMapUniqueId;
        public readonly int lastKnownWorldTile;
        public readonly RealityEnvironmentSummary environment;

        internal RealityRegionSnapshot(RealityRegionDescriptor source)
        {
            RealityRegionId.TryParse(source?.regionId, out id);
            label = source?.label;
            fidelity = source?.fidelity ?? RealityFidelity.Dormant;
            observationLevel = source?.observationLevel ?? RealityObservationPrecision.Rumor;
            lifecycle = source?.lifecycle ?? RealityLifecycleState.Quarantined;
            stableSeed = source?.stableSeed ?? 0;
            createdTick = source?.createdTick ?? 0;
            lastUpdateTick = source?.lastUpdateTick ?? 0;
            lastMaterializedTick = source?.lastMaterializedTick ?? -1;
            activeMapUniqueId = source?.activeMapUniqueId ?? -1;
            lastKnownWorldTile = source?.lastKnownWorldTile ?? -1;
            environment = source?.environment?.Clone() ?? new RealityEnvironmentSummary();
        }
    }

    /// <summary>Read-only population projection.</summary>
    public sealed class RealityPopulationSnapshot
    {
        public readonly RealityPopulationRecord record;

        internal RealityPopulationSnapshot(RealityPopulationRecord source) { record = source?.Clone(); }
    }

    /// <summary>Read-only anchor projection.</summary>
    public sealed class RealityAnchorSnapshot
    {
        public readonly RealityAnchorRecord record;

        internal RealityAnchorSnapshot(RealityAnchorRecord source) { record = source?.Clone(); }
    }

    /// <summary>Read-only process projection.</summary>
    public sealed class RealityProcessSnapshot
    {
        public readonly RealityProcessRecord record;

        internal RealityProcessSnapshot(RealityProcessRecord source) { record = source?.Clone(); }
    }

    /// <summary>Cached immutable domain event.</summary>
    public sealed class RealityEvent
    {
        public readonly int revision;
        public readonly string eventId;
        public readonly string kind;
        public readonly string providerId;
        public readonly string regionId;
        public readonly long tick;
        public readonly string payload;

        internal RealityEvent(int revision, string eventId, string kind, string providerId, string regionId, long tick, string payload)
        {
            this.revision = revision;
            this.eventId = eventId;
            this.kind = kind;
            this.providerId = providerId;
            this.regionId = regionId;
            this.tick = tick;
            this.payload = payload;
        }
    }

    /// <summary>Context for cached diagnostic providers.</summary>
    public sealed class RealityDiagnosticsContext
    {
        internal RealityDiagnosticsContext(DeferredRealityWorldComponent world, long now) { World = world; Now = now; }

        /// <summary>World store being inspected.</summary>
        public DeferredRealityWorldComponent World { get; }

        /// <summary>Tick at which the snapshot was captured.</summary>
        public long Now { get; }
    }
}
