using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace DeferredReality.API
{
    /// <summary>Registration metadata for a provider.</summary>
    public sealed class RealityProviderRegistration
    {
        public string providerId;
        /// <summary>Provider registration API version; the registry accepts only the framework-supported version.</summary>
        public int semanticApiVersion = DeferredRealityFrameworkInfo.SupportedProviderApiVersion;
        public int order;
        public RealityProviderCapability capabilities;
        /// <summary>Fidelities this provider can represent for its regions.</summary>
        public RealityFidelityMask supportedFidelities = RealityFidelityMask.All;
        /// <summary>Fidelity used when a provider first establishes a latent region.</summary>
        public RealityFidelity defaultFidelity = RealityFidelity.Dormant;
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
                order = order,
                capabilities = capabilities,
                supportedFidelities = supportedFidelities,
                defaultFidelity = defaultFidelity,
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

    /// <summary>Provider-declared fidelity and transition contract for a region.</summary>
    public sealed class RealityFidelityContract
    {
        public RealityFidelity currentFidelity = RealityFidelity.Dormant;
        public RealityFidelityMask supportedFidelities = RealityFidelityMask.All;
        public List<RealityFidelityTransitionRule> transitions = new List<RealityFidelityTransitionRule>();

        public bool Supports(RealityFidelity fidelity) => RealityFidelityRules.Allows(supportedFidelities, fidelity);

        public bool AllowsTransition(RealityFidelity from, RealityFidelity to)
        {
            return (transitions ?? new List<RealityFidelityTransitionRule>()).Any(item => item != null &&
                item.fromFidelity == from && item.toFidelity == to);
        }

        public bool AllowsTransition(RealityFidelity from, RealityFidelity to,
            RealityFidelityTransitionMechanism mechanism)
        {
            return (transitions ?? new List<RealityFidelityTransitionRule>()).Any(item => item != null &&
                item.fromFidelity == from && item.toFidelity == to && item.mechanism == mechanism);
        }
    }

    /// <summary>One typed edge in a provider's fidelity state machine.</summary>
    public sealed class RealityFidelityTransitionRule
    {
        public RealityFidelity fromFidelity;
        public RealityFidelity toFidelity;
        public RealityFidelityTransitionMechanism mechanism = RealityFidelityTransitionMechanism.Provider;
    }

    /// <summary>Process-specific declaration of legal fidelity and live-map behavior.</summary>
    public sealed class RealityProcessFidelityPolicy
    {
        public RealityFidelityMask legalFidelities = RealityFidelityMask.Statistical;
        public bool runsWhileLiveProjection;
        public bool mayRequestEscalation;
        public RealityFidelity escalationTarget = RealityFidelity.Narrative;
        public RealityFidelityEscalationPolicy escalationPolicy = RealityFidelityEscalationPolicy.HostApproval;

        public bool IsLegalAt(RealityFidelity fidelity) => RealityFidelityRules.Allows(legalFidelities, fidelity);
    }

    /// <summary>Typed input to a provider- or host-owned latent fidelity transition.</summary>
    public sealed class RealityFidelityTransitionRequest
    {
        public RealityRegionId regionId;
        public string providerId;
        public RealityFidelity fromFidelity;
        public RealityFidelity toFidelity;
        public string processId;
        public string escalationRequestId;
        public long now;

        public bool IsValid => regionId.IsValid && !string.IsNullOrEmpty(providerId) &&
            Enum.IsDefined(typeof(RealityFidelity), fromFidelity) &&
            Enum.IsDefined(typeof(RealityFidelity), toFidelity);
    }

    /// <summary>Structured result from a latent fidelity transition attempt.</summary>
    public sealed class RealityFidelityTransitionResult
    {
        public bool succeeded;
        public RealityRegionId regionId;
        public RealityFidelity fromFidelity;
        public RealityFidelity toFidelity;
        public string error;
        public IReadOnlyList<RealityVeto> vetoes = Array.Empty<RealityVeto>();
    }

    /// <summary>Typed process output requesting more detail without creating a Map automatically.</summary>
    public sealed class RealityProcessEscalationRequest
    {
        public RealityFidelity requestedFidelity = RealityFidelity.Narrative;
        public RealityFidelityEscalationReason reason = RealityFidelityEscalationReason.InsufficientResolution;
        public RealityFidelityEscalationDisposition disposition = RealityFidelityEscalationDisposition.Request;
        public RealityFidelityEscalationPolicy policy = RealityFidelityEscalationPolicy.HostApproval;
        public string subjectId;

        public bool IsValid => Enum.IsDefined(typeof(RealityFidelity), requestedFidelity) &&
            Enum.IsDefined(typeof(RealityFidelityEscalationReason), reason) &&
            Enum.IsDefined(typeof(RealityFidelityEscalationDisposition), disposition) &&
            Enum.IsDefined(typeof(RealityFidelityEscalationPolicy), policy);
    }

    /// <summary>Provider fidelity contract required by every analytical process provider.</summary>
    public interface IRealityFidelityProvider
    {
        RealityFidelityContract DescribeFidelity(RealityProviderContext context, RealityRegionSnapshot region);
        RealityProcessFidelityPolicy DescribeProcessFidelity(RealityProcessRecord process, RealityRegionSnapshot region);
        bool CanTransitionFidelity(RealityFidelityTransitionRequest request, IList<RealityVeto> vetoes);
        /// <summary>Notifies the provider after a committed latent fidelity change so it can schedule only legal work.</summary>
        void OnFidelityChanged(RealityProviderContext context, RealityRegionSnapshot region,
            RealityFidelity previous, RealityFidelity current);
    }

    /// <summary>Provider that executes analytical scheduled processes.</summary>
    public interface IRealityProcessProvider : IRealityFidelityProvider
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

    /// <summary>Optional transactional anchor owner.</summary>
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

    /// <summary>Optional host supplied map factory. The framework never invents a Map implementation.</summary>
    public interface IRealityMapFactory
    {
        bool TryCreateMap(RealityRegionId regionId, RealityMaterializationPlan plan, out Map map, out string diagnostic);
        void RemoveMap(Map map);
    }

    /// <summary>Provider-defined translation of an established observation or constraint into map obligations.</summary>
    public sealed class RealityMaterializationConstraint
    {
        public string constraintId;
        public string providerId;
        public string sourceObservationId;
        public string sourceConstraintId;
        public string regionId;
        public string subjectId;
        public string kind;
        public RealityObservationPrecision spatialPrecision = RealityObservationPrecision.Region;
        public RealityMaterializationConstraintEnforcement enforcement = RealityMaterializationConstraintEnforcement.Advisory;
        public float certainty = 1f;
        public float confidence = 1f;
        public RealityLocation location = new RealityLocation();
        public string conflictDomainKey;
        public string conflictFacetKey;
        public string payload;

        public RealityMaterializationConstraint Clone()
        {
            return new RealityMaterializationConstraint
            {
                constraintId = constraintId,
                providerId = providerId,
                sourceObservationId = sourceObservationId,
                sourceConstraintId = sourceConstraintId,
                regionId = regionId,
                subjectId = subjectId,
                kind = kind,
                spatialPrecision = spatialPrecision,
                enforcement = enforcement,
                certainty = certainty,
                confidence = confidence,
                location = location?.Clone() ?? new RealityLocation(),
                conflictDomainKey = conflictDomainKey,
                conflictFacetKey = conflictFacetKey,
                payload = payload
            };
        }

        internal bool SemanticallyEquals(RealityMaterializationConstraint other)
        {
            if (other == null) return false;
            return string.Equals(providerId, other.providerId, System.StringComparison.Ordinal) &&
                string.Equals(sourceObservationId, other.sourceObservationId, System.StringComparison.Ordinal) &&
                string.Equals(sourceConstraintId, other.sourceConstraintId, System.StringComparison.Ordinal) &&
                string.Equals(regionId, other.regionId, System.StringComparison.Ordinal) &&
                string.Equals(subjectId, other.subjectId, System.StringComparison.Ordinal) &&
                string.Equals(kind, other.kind, System.StringComparison.Ordinal) &&
                spatialPrecision == other.spatialPrecision && enforcement == other.enforcement &&
                System.Math.Abs(certainty - other.certainty) < 0.0001f &&
                System.Math.Abs(confidence - other.confidence) < 0.0001f &&
                LocationsEqual(location, other.location) &&
                string.Equals(conflictDomainKey, other.conflictDomainKey, System.StringComparison.Ordinal) &&
                string.Equals(conflictFacetKey, other.conflictFacetKey, System.StringComparison.Ordinal) &&
                string.Equals(payload, other.payload, System.StringComparison.Ordinal);
        }

        internal bool ConflictsWith(RealityMaterializationConstraint other)
        {
            if (other == null || string.IsNullOrEmpty(conflictDomainKey) ||
                !string.Equals(conflictDomainKey, other.conflictDomainKey, System.StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(conflictFacetKey) && !string.IsNullOrEmpty(other.conflictFacetKey) &&
                !string.Equals(conflictFacetKey, other.conflictFacetKey, System.StringComparison.Ordinal)) return false;
            return !SemanticallyEquals(other);
        }

        private static bool LocationsEqual(RealityLocation left, RealityLocation right)
        {
            if (left == null || right == null) return left == right;
            return left.x == right.x && left.z == right.z &&
                string.Equals(left.edge, right.edge, System.StringComparison.Ordinal) &&
                string.Equals(left.areaId, right.areaId, System.StringComparison.Ordinal) &&
                left.precision == right.precision;
        }
    }

    /// <summary>Builds the deterministic, provider-neutral consistency input without creating a Map.</summary>
    public static class RealityMaterializationConsistency
    {
        public static RealityMaterializationConsistencyPlan Build(DeferredRealityWorldComponent world,
            RealityMaterializationRequest request)
        {
            if (world == null || request == null || !request.regionId.IsValid) return null;
            return new RealityMaterializationConsistencyPlan(world, request);
        }
    }

    /// <summary>Explicit conflict between provider-derived rematerialization obligations.</summary>
    public sealed class RealityMaterializationConsistencyConflict
    {
        public string conflictId;
        public string regionId;
        public string domainKey;
        public string leftConstraintId;
        public string rightConstraintId;
        public string reason;

        internal RealityMaterializationConsistencyConflict Clone() => (RealityMaterializationConsistencyConflict)MemberwiseClone();
    }

    /// <summary>
    /// Deterministic, side-effect-free input supplied to map factories and providers.
    /// Observations are knowledge; translated constraints are the provider's materialization obligations.
    /// </summary>
    public sealed class RealityMaterializationConsistencyPlan
    {
        private readonly List<RealityObservationRecord> observations;
        private readonly List<RealityConstraint> constraints;
        private readonly List<RealityMaterializationConstraint> materializationConstraints = new List<RealityMaterializationConstraint>();
        private readonly List<RealityMaterializationConsistencyConflict> conflicts = new List<RealityMaterializationConsistencyConflict>();

        internal RealityMaterializationConsistencyPlan(DeferredRealityWorldComponent world, RealityMaterializationRequest request)
        {
            string regionKey = request.regionId.ToString();
            observations = request.preserveObservedFacts
                ? world.ObservationSnapshots(regionKey).Where(item => item != null &&
                    item.lifecycle == RealityObservationLifecycle.Active)
                    .OrderBy(item => item.observationId, System.StringComparer.Ordinal).ToList()
                : new List<RealityObservationRecord>();
            constraints = world.ConstraintSnapshots(regionKey).Where(item => item != null && item.AppliesAt(request.now) &&
                    item.status != RealityConstraintStatus.Expired && item.status != RealityConstraintStatus.Conflicted &&
                    item.status != RealityConstraintStatus.Quarantined)
                .OrderBy(item => item.constraintId, System.StringComparer.Ordinal).ToList();
            RegionId = regionKey;
            ProviderId = string.IsNullOrEmpty(request.providerId) ? request.regionId.ProviderNamespace : request.providerId;
            PreserveObservedFacts = request.preserveObservedFacts;
            BuildDefaultConstraints();
            RefreshDeterminism(world);
        }

        public string RegionId { get; }
        public string ProviderId { get; }
        public bool PreserveObservedFacts { get; }
        public int DeterministicSeed { get; private set; }
        public string DeterministicStateKey { get; private set; }
        public IReadOnlyList<RealityObservationRecord> Observations => observations;
        public IReadOnlyList<RealityConstraint> Constraints => constraints;
        public IReadOnlyList<RealityMaterializationConstraint> MaterializationConstraints => materializationConstraints;
        public IReadOnlyList<RealityMaterializationConsistencyConflict> Conflicts => conflicts;

        /// <summary>
        /// Adds the conservative framework translation. Providers may add richer obligations
        /// from their own payloads, but the framework never turns a rumor into an exact claim.
        /// </summary>
        internal void BuildDefaultConstraints()
        {
            foreach (RealityObservationRecord observation in observations)
            {
                if (observation == null || observation.compressionSignificance == RealityCompressionSignificance.Disposable) continue;
                if (!System.Enum.IsDefined(typeof(RealityObservationPrecision), observation.spatialPrecision)) continue;
                var translated = new RealityMaterializationConstraint
                {
                    constraintId = "observation:" + observation.observationId,
                    providerId = ProviderId,
                    sourceObservationId = observation.observationId,
                    regionId = RegionId,
                    subjectId = observation.subjectId,
                    kind = "observation-fact",
                    spatialPrecision = observation.spatialPrecision,
                    enforcement = RealityObservationPrecisionRules.DefaultEnforcement(observation.spatialPrecision,
                        observation.confidence, observation.playerObserved),
                    certainty = observation.certainty,
                    confidence = observation.confidence,
                    location = observation.location?.Clone() ?? new RealityLocation { precision = observation.spatialPrecision },
                    conflictDomainKey = "observation:" + (observation.subjectId ?? string.Empty),
                    conflictFacetKey = observation.facet,
                    payload = observation.estimate
                };
                AddConstraint(translated);
            }

            foreach (RealityConstraint constraint in constraints)
            {
                if (constraint == null || constraint.compressionSignificance == RealityCompressionSignificance.Disposable ||
                    !System.Enum.IsDefined(typeof(RealityObservationPrecision), constraint.spatialPrecision)) continue;
                var translated = new RealityMaterializationConstraint
                {
                    constraintId = "constraint:" + constraint.constraintId,
                    providerId = constraint.providerId ?? ProviderId,
                    sourceConstraintId = constraint.constraintId,
                    regionId = RegionId,
                    kind = constraint.typeId ?? "constraint-fact",
                    spatialPrecision = constraint.spatialPrecision,
                    enforcement = RealityObservationPrecisionRules.DefaultEnforcement(constraint.spatialPrecision),
                    certainty = constraint.certainty,
                    location = new RealityLocation { precision = constraint.spatialPrecision },
                    conflictDomainKey = constraint.conflictDomainKeys?.FirstOrDefault(),
                    conflictFacetKey = constraint.conflictFacetKeys?.FirstOrDefault(),
                    payload = constraint.payload
                };
                AddConstraint(translated);
            }
        }

        /// <summary>Adds one provider translation, retaining deterministic first-writer semantics.</summary>
        public bool AddConstraint(RealityMaterializationConstraint value)
        {
            if (value == null || string.IsNullOrEmpty(value.constraintId) || string.IsNullOrEmpty(value.regionId) ||
                !string.Equals(value.regionId, RegionId, System.StringComparison.Ordinal) ||
                !System.Enum.IsDefined(typeof(RealityObservationPrecision), value.spatialPrecision) ||
                !System.Enum.IsDefined(typeof(RealityMaterializationConstraintEnforcement), value.enforcement)) return false;
            RealityMaterializationConstraint copy = value.Clone();
            RealityMaterializationConstraint existing = materializationConstraints.FirstOrDefault(item =>
                string.Equals(item.constraintId, copy.constraintId, System.StringComparison.Ordinal));
            if (existing != null)
            {
                if (!existing.SemanticallyEquals(copy)) AddConflict(existing, copy, "Duplicate translated constraint ID has different meaning.");
                return false;
            }
            foreach (RealityMaterializationConstraint other in materializationConstraints)
                if (copy.ConflictsWith(other)) AddConflict(other, copy, "Translated obligations occupy one incompatible materialization domain.");
            materializationConstraints.Add(copy);
            materializationConstraints.Sort((left, right) => string.CompareOrdinal(left.constraintId, right.constraintId));
            return true;
        }

        internal void AddConflict(RealityMaterializationConstraint left, RealityMaterializationConstraint right, string reason)
        {
            string leftId = left?.constraintId ?? string.Empty;
            string rightId = right?.constraintId ?? string.Empty;
            string first = string.CompareOrdinal(leftId, rightId) <= 0 ? leftId : rightId;
            string second = string.CompareOrdinal(leftId, rightId) <= 0 ? rightId : leftId;
            string id = "materialization-conflict:" + RealityDeterminism.Combine(RegionId, first, second,
                left?.conflictDomainKey ?? right?.conflictDomainKey ?? string.Empty).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (conflicts.Any(item => item != null && item.conflictId == id)) return;
            conflicts.Add(new RealityMaterializationConsistencyConflict
            {
                conflictId = id,
                regionId = RegionId,
                domainKey = left?.conflictDomainKey ?? right?.conflictDomainKey,
                leftConstraintId = first,
                rightConstraintId = second,
                reason = reason
            });
            conflicts.Sort((a, b) => string.CompareOrdinal(a?.conflictId, b?.conflictId));
        }

        internal void RefreshDeterminism(DeferredRealityWorldComponent world)
        {
            var parts = new List<string>
            {
                world?.WorldSeed ?? string.Empty,
                RegionId,
                ProviderId,
                PreserveObservedFacts.ToString()
            };
            foreach (RealityObservationRecord item in observations)
                parts.Add("observation|" + item.observationId + "|" + item.observerId + "|" +
                    item.source + "|" + item.tick + "|" + item.subjectId + "|" + item.regionId + "|" +
                    item.spatialPrecision + "|" + item.certainty.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    "|" + item.confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + item.compressionSignificance +
                    "|" + item.playerObserved + ":" + item.sensorDerived + ":" + item.inferred + ":" + item.rumored + "|" +
                    item.location?.x + ":" + item.location?.z + ":" + item.location?.edge + ":" + item.location?.areaId + ":" + item.location?.precision +
                    "|" + item.facet + "|" + item.estimate + "|" + item.lifecycle + "|" + item.supersededByObservationId +
                    "|" + item.invalidationReason);
            foreach (RealityConstraint item in constraints)
                parts.Add("constraint|" + item.constraintId + "|" + item.providerId + "|" + item.typeId + "|" +
                    item.regionId + "|" + item.createdTick + "|" + item.validFromTick + "|" + item.expiryTick + "|" +
                    item.certainty.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + item.observerId + "|" + item.source +
                    "|" + item.spatialPrecision + "|" + item.compressionSignificance + "|" + item.priority + "|" + item.conflictPolicy + "|" + item.status +
                    "|" + string.Join(",", (item.affectedAnchorIds ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal)) +
                    "|" + string.Join(",", (item.affectedPopulationIds ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal)) +
                    "|" + string.Join(",", (item.causalParentIds ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal)) +
                    "|" + string.Join(",", (item.conflictDomainKeys ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal)) +
                    "|" + string.Join(",", (item.conflictFacetKeys ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal)) +
                    "|" + item.payload);
            foreach (RealityMaterializationConstraint item in materializationConstraints)
                parts.Add("translated|" + item.constraintId + "|" + item.providerId + "|" + item.sourceObservationId + "|" +
                    item.sourceConstraintId + "|" + item.regionId + "|" + item.subjectId + "|" + item.kind + "|" +
                    item.spatialPrecision + "|" + item.enforcement + "|" +
                    item.certainty.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" +
                    item.confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" +
                    item.location?.x + ":" + item.location?.z + ":" + item.location?.edge + ":" + item.location?.areaId + ":" + item.location?.precision + "|" +
                    item.conflictDomainKey + "|" + item.conflictFacetKey + "|" + item.payload);
            foreach (RealityMaterializationConsistencyConflict conflict in conflicts)
                parts.Add("conflict|" + conflict.conflictId + "|" + conflict.domainKey + "|" + conflict.leftConstraintId + "|" +
                    conflict.rightConstraintId + "|" + conflict.reason);
            DeterministicStateKey = RealityDeterminism.Combine(parts.ToArray()).ToString(System.Globalization.CultureInfo.InvariantCulture);
            DeterministicSeed = RealityDeterminism.Seed(world?.WorldSeed ?? string.Empty, RegionId, ProviderId,
                DeterministicStateKey, 0);
        }
    }

    /// <summary>Read-only provider input for translating knowledge into spatial obligations.</summary>
    public sealed class RealityMaterializationConsistencyContext
    {
        internal RealityMaterializationConsistencyContext(DeferredRealityWorldComponent world,
            RealityMaterializationRequest request, RealityMaterializationConsistencyPlan plan)
        {
            World = world;
            Request = request;
            Plan = plan;
        }

        public DeferredRealityWorldComponent World { get; }
        public RealityMaterializationRequest Request { get; }
        public RealityMaterializationConsistencyPlan Plan { get; }
        public int DeterministicSeed => Plan?.DeterministicSeed ?? 0;

        public RealityRandomStream Random(string operationId)
        {
            return new RealityRandomStream(RealityDeterminism.Seed(World?.WorldSeed ?? string.Empty,
                Plan?.RegionId ?? string.Empty, Plan?.ProviderId ?? string.Empty, operationId, 0));
        }
    }

    /// <summary>Optional provider translation/validation layer for rematerialization consistency.</summary>
    public interface IRealityMaterializationConsistencyProvider
    {
        void BuildMaterializationConstraints(RealityMaterializationConsistencyContext context,
            RealityMaterializationConsistencyPlan plan);
        void ValidateMaterializationConsistency(RealityMaterializationContext context,
            RealityMaterializationConsistencyPlan plan, IList<RealityVeto> vetoes);
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
        /// <summary>Optional typed request. The scheduler persists and gates it; it never auto-materializes.</summary>
        public RealityProcessEscalationRequest escalation;
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

    /// <summary>
    /// Framework-level compression preservation checks. This validates the
    /// representation boundary without serializing live Pawns, Things, jobs,
    /// or other RimWorld object graphs.
    /// </summary>
    public static class RealityCompressionPreservation
    {
        public static IReadOnlyList<RealityVeto> Validate(DeferredRealityWorldComponent world,
            RealityRegionId regionId, string providerId = null)
        {
            var vetoes = new List<RealityVeto>();
            if (world == null || !regionId.IsValid)
            {
                vetoes.Add(new RealityVeto("compression.preservation-no-region",
                    "Compression preservation could not resolve a valid region.", providerId ?? "core", 3));
                return vetoes;
            }

            string regionKey = regionId.ToString();
            string owner = string.IsNullOrEmpty(providerId) ? regionId.ProviderNamespace : providerId;
            var populations = world.PopulationSnapshots(regionKey).Where(item => item?.record != null)
                .Select(item => item.record).ToList();
            var anchors = world.AnchorSnapshots().Where(item => item?.record != null)
                .Select(item => item.record).ToList();
            var anchorsById = anchors.Where(item => !string.IsNullOrEmpty(item.anchorId))
                .GroupBy(item => item.anchorId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var populationsById = world.PopulationSnapshots().Where(item => item?.record != null)
                .Select(item => item.record).Where(item => !string.IsNullOrEmpty(item.populationId))
                .GroupBy(item => item.populationId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (RealityPopulationRecord population in populations)
            {
                if (population.compressionSignificance != RealityCompressionSignificance.Aggregate)
                {
                    Add(vetoes, "compression.population-significance", population.providerId,
                        "Population " + population.populationId + " is not explicitly fungible aggregate state.");
                }
                foreach (string anchorId in population.anchoredMemberIds ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(anchorId) || !anchorsById.TryGetValue(anchorId, out RealityAnchorRecord anchor))
                    {
                        Add(vetoes, "compression.population-missing-anchor", population.providerId,
                            "Population " + population.populationId + " references an identity that is not retained.");
                        continue;
                    }
                    if (anchor.compressionSignificance != RealityCompressionSignificance.Identity ||
                        RealityCompressionSignificanceRules.IsVeto(anchor.compressionSignificance))
                    {
                        Add(vetoes, "compression.anchor-significance", anchor.providerId,
                            "Population " + population.populationId + " references an anchor that cannot survive compression.");
                    }
                }
            }

            foreach (RealityAnchorRecord anchor in anchors.Where(item => item.regionId == regionKey))
            {
                if (anchor.compressionSignificance != RealityCompressionSignificance.Identity ||
                    RealityCompressionSignificanceRules.IsVeto(anchor.compressionSignificance))
                {
                    Add(vetoes, "compression.anchor-significance", anchor.providerId,
                        "Identity anchor " + anchor.anchorId + " is not declared retainable.");
                }
                if (!RealityCompressionSignificanceRules.IsDefined(anchor.externalReferenceState) ||
                    RealityCompressionSignificanceRules.IsVeto(anchor.externalReferenceState))
                {
                    Add(vetoes, "compression.unsafe-external-reference", anchor.providerId,
                        "Identity anchor " + anchor.anchorId + " has an unknown or unsafe external reference.");
                }
                else if (!string.IsNullOrEmpty(anchor.optionalRimWorldLoadId) &&
                    anchor.externalReferenceState != RealityExternalReferenceState.ProviderResolved)
                {
                    Add(vetoes, "compression.unresolved-external-reference", anchor.providerId,
                        "Identity anchor " + anchor.anchorId + " names a RimWorld object that its provider has not resolved.");
                }
            }

            foreach (RealityConstraint constraint in world.ConstraintSnapshots(regionKey).Where(item => item != null))
            {
                if (constraint.compressionSignificance != RealityCompressionSignificance.EstablishedFact &&
                    constraint.compressionSignificance != RealityCompressionSignificance.Disposable)
                {
                    Add(vetoes, "compression.constraint-significance", constraint.providerId,
                        "Constraint " + constraint.constraintId + " has no safe compression disposition.");
                }
                foreach (string anchorId in constraint.affectedAnchorIds ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(anchorId) || !anchorsById.ContainsKey(anchorId))
                        Add(vetoes, "compression.constraint-missing-anchor", constraint.providerId,
                            "Constraint " + constraint.constraintId + " references an unknown identity anchor.");
                }
                foreach (string populationId in constraint.affectedPopulationIds ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(populationId) || !populationsById.ContainsKey(populationId))
                        Add(vetoes, "compression.constraint-missing-population", constraint.providerId,
                            "Constraint " + constraint.constraintId + " references an unknown aggregate population.");
                }
            }

            foreach (RealityObservationRecord observation in world.ObservationSnapshots(regionKey).Where(item => item != null))
            {
                if (observation.compressionSignificance != RealityCompressionSignificance.EstablishedFact &&
                    observation.compressionSignificance != RealityCompressionSignificance.Disposable)
                {
                    Add(vetoes, "compression.observation-significance", owner,
                        "Observation " + observation.observationId + " has no safe compression disposition.");
                }
                if (observation.playerObserved && observation.compressionSignificance != RealityCompressionSignificance.EstablishedFact)
                {
                    Add(vetoes, "compression.player-observation-discard", owner,
                        "Player-observed fact " + observation.observationId + " cannot be discarded during compression.");
                }
            }
            return vetoes;
        }

        private static void Add(IList<RealityVeto> vetoes, string code, string providerId, string message)
        {
            vetoes.Add(new RealityVeto(code, message, providerId, 3));
        }
    }

    /// <summary>Requested materialization target.</summary>
    public sealed class RealityMaterializationRequest
    {
        public RealityRegionId regionId;
        /// <summary>Optional provider that owns this materialization when the region identity is shared.</summary>
        public string providerId;
        /// <summary>Optional identity-bearing anchor selected for this materialization.</summary>
        public string targetAnchorId;
        /// <summary>Approved durable escalation being satisfied by this materialization.</summary>
        public string escalationRequestId;
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
        private RealityMaterializationConsistencyPlan consistency;

        /// <summary>Map to use if one is already active, otherwise null until a host factory creates one.</summary>
        public Map ActiveMap { get; internal set; }

        /// <summary>Optional selected anchor copied from the request for host factories.</summary>
        public string TargetAnchorId { get; internal set; }

        /// <summary>Runtime materialization identity used by map factories and readiness callbacks.</summary>
        public string TransactionId { get; internal set; }

        /// <summary>
        /// Deterministic provider-neutral knowledge input for this projection. It contains
        /// observations as knowledge and translated obligations as separate collections.
        /// </summary>
        public RealityMaterializationConsistencyPlan Consistency => consistency;

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

        internal void SetConsistency(RealityMaterializationConsistencyPlan value) { consistency = value; }
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
        public RealityCompressionSignificance compressionSignificance = RealityCompressionSignificance.EstablishedFact;
        public string observationId;
        public string observerId;
        public string source;
        public long tick;
        public string subjectId;
        public string regionId;
        public float certainty;
        public float confidence;
        public RealityObservationPrecision spatialPrecision = RealityObservationPrecision.Region;
        public RealityLocation location = new RealityLocation { precision = RealityObservationPrecision.Region };
        public string facet;
        public bool playerObserved;
        public bool sensorDerived;
        public bool inferred;
        public bool rumored;
        public string estimate;
        public RealityObservationLifecycle lifecycle = RealityObservationLifecycle.Active;
        public string supersededByObservationId;
        public string invalidationReason;
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
        public readonly RealityRegionAuthority authority;
        public readonly int stableSeed;
        public readonly long createdTick;
        public readonly long lastUpdateTick;
        public readonly long lastProjectionTick;
        public readonly int projectionMapUniqueId;
        public readonly int lastKnownWorldTile;
        public readonly RealityEnvironmentSummary environment;

        internal RealityRegionSnapshot(RealityRegionDescriptor source)
        {
            RealityRegionId.TryParse(source?.regionId, out id);
            label = source?.label;
            fidelity = source?.fidelity ?? RealityFidelity.Dormant;
            observationLevel = source?.observationLevel ?? RealityObservationPrecision.Rumor;
            authority = source?.authority ?? RealityRegionAuthority.Quarantined;
            stableSeed = source?.stableSeed ?? 0;
            createdTick = source?.createdTick ?? 0;
            lastUpdateTick = source?.lastUpdateTick ?? 0;
            lastProjectionTick = source?.lastProjectionTick ?? -1;
            projectionMapUniqueId = source?.projectionMapUniqueId ?? -1;
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
