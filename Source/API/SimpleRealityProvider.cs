using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.Materialization;
using DeferredReality.Simulation;
using Verse;

namespace DeferredReality.API
{
    /// <summary>Internal registry hook used by compositional providers to advertise only configured capabilities.</summary>
    public interface IRealityCapabilitySource
    {
        bool ProvidesCapability(Type capabilityType);
    }

    /// <summary>Small fidelity declaration for the common aggregate-provider path.</summary>
    public sealed class SimpleFidelityDefinition
    {
        public RealityFidelityMask supportedFidelities = RealityFidelityMask.All;
        public List<RealityFidelityTransitionRule> transitions = new List<RealityFidelityTransitionRule>();
        public RealityFidelityMask processLegalFidelities = RealityFidelityMask.Statistical;
        public bool processRunsWhileLiveProjection;
        public bool processMayRequestEscalation = true;
        public RealityFidelity processEscalationTarget = RealityFidelity.Narrative;
        public RealityFidelityEscalationPolicy processEscalationPolicy = RealityFidelityEscalationPolicy.HostApproval;
        public Func<RealityFidelityTransitionRequest, IList<RealityVeto>, bool> validateTransition;
        public Action<RealityProviderContext, RealityRegionSnapshot, RealityFidelity, RealityFidelity> fidelityChanged;
        public Func<RealityProcessRecord, RealityRegionSnapshot, RealityProcessFidelityPolicy> processPolicy;

        internal SimpleFidelityDefinition Clone()
        {
            return new SimpleFidelityDefinition
            {
                supportedFidelities = supportedFidelities,
                transitions = (transitions ?? new List<RealityFidelityTransitionRule>()).Where(item => item != null)
                    .Select(item => new RealityFidelityTransitionRule
                    {
                        fromFidelity = item.fromFidelity,
                        toFidelity = item.toFidelity,
                        mechanism = item.mechanism
                    }).ToList(),
                processLegalFidelities = processLegalFidelities,
                processRunsWhileLiveProjection = processRunsWhileLiveProjection,
                processMayRequestEscalation = processMayRequestEscalation,
                processEscalationTarget = processEscalationTarget,
                processEscalationPolicy = processEscalationPolicy,
                validateTransition = validateTransition,
                fidelityChanged = fidelityChanged,
                processPolicy = processPolicy
            };
        }
    }

    /// <summary>Typed analytical process callbacks; the scheduler still owns bounds, pause, and escalation persistence.</summary>
    public sealed class SimpleProcessDefinition
    {
        public Func<RealityProcessRecord, RealityProcessExecution, IList<RealityVeto>, bool> canExecute;
        public Func<RealityProcessRecord, RealityProcessExecution, RealityProcessResult> execute;
        public Func<RealityProcessRecord, RealityRegionSnapshot, RealityProcessFidelityPolicy> policy;

        internal SimpleProcessDefinition Clone() => new SimpleProcessDefinition
        {
            canExecute = canExecute,
            execute = execute,
            policy = policy
        };
    }

    /// <summary>Typed aggregate population callbacks.</summary>
    public sealed class SimplePopulationDefinition
    {
        public Func<RealityPopulationRecord, string, IList<RealityVeto>, bool> canChange;
        public Action<RealityProviderContext, RealityPopulationRecord, string> reconcileActiveMap;
    }

    /// <summary>Typed identity-anchor validation callback.</summary>
    public sealed class SimpleAnchorDefinition
    {
        public Func<RealityAnchorRecord, IList<RealityVeto>, bool> validate;
    }

    /// <summary>Typed provider-constraint callbacks.</summary>
    public sealed class SimpleConstraintDefinition
    {
        public Func<RealityConstraint, bool> canResolve;
        public Func<RealityProviderContext, RealityConstraint, IList<RealityVeto>, bool> resolve;
    }

    /// <summary>
    /// Composable provider registration for ordinary latent simulation. Optional services are absent unless
    /// configured, while advanced transactional components can be attached without being registered separately.
    /// </summary>
    public sealed class SimpleRealityProviderBuilder
    {
        private readonly RealityProviderRegistration registration;
        private Action<RealityProviderContext> onRegistered;
        private Func<RealityProviderContext, IEnumerable<RealityRegionDescriptor>> regions;
        private SimpleFidelityDefinition fidelity;
        private SimpleProcessDefinition process;
        private SimplePopulationDefinition populations;
        private SimpleAnchorDefinition anchors;
        private SimpleConstraintDefinition constraints;
        private Func<RealityDiagnosticsContext, IEnumerable<string>> diagnostics;
        private Action<RealityObservationRecord> observations;
        private readonly List<object> advanced = new List<object>();

        public SimpleRealityProviderBuilder(string providerId, string displayName = null)
        {
            if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("A provider ID is required.", nameof(providerId));
            registration = new RealityProviderRegistration
            {
                providerId = providerId.Trim(),
                displayName = displayName ?? providerId.Trim(),
                semanticApiVersion = 1,
                defaultFidelity = RealityFidelity.Dormant,
                supportedFidelities = RealityFidelityMask.All
            };
        }

        public SimpleRealityProviderBuilder Configure(Action<RealityProviderRegistration> configure)
        {
            configure?.Invoke(registration);
            return this;
        }

        public SimpleRealityProviderBuilder OnRegistered(Action<RealityProviderContext> callback)
        {
            onRegistered = callback;
            return this;
        }

        public SimpleRealityProviderBuilder WithRegions(Func<RealityProviderContext, IEnumerable<RealityRegionDescriptor>> describe)
        {
            regions = describe;
            registration.capabilities |= RealityProviderCapability.Regions;
            return this;
        }

        public SimpleRealityProviderBuilder WithFidelity(SimpleFidelityDefinition definition)
        {
            fidelity = definition ?? throw new ArgumentNullException(nameof(definition));
            registration.capabilities |= RealityProviderCapability.Fidelity;
            registration.supportedFidelities = definition.supportedFidelities;
            return this;
        }

        /// <summary>Starts a fidelity declaration without exposing transition-record construction.</summary>
        public SimpleRealityProviderBuilder WithFidelity(RealityFidelityMask supportedFidelities)
        {
            return WithFidelity(new SimpleFidelityDefinition { supportedFidelities = supportedFidelities });
        }

        /// <summary>Adds one legal fidelity edge to the simple provider's state machine.</summary>
        public SimpleRealityProviderBuilder AllowTransition(RealityFidelity from, RealityFidelity to,
            RealityFidelityTransitionMechanism mechanism = RealityFidelityTransitionMechanism.Provider)
        {
            if (fidelity == null) throw new InvalidOperationException("Call WithFidelity before AllowTransition.");
            if (!(fidelity.transitions ?? (fidelity.transitions = new List<RealityFidelityTransitionRule>()))
                .Any(item => item != null && item.fromFidelity == from && item.toFidelity == to && item.mechanism == mechanism))
                fidelity.transitions.Add(new RealityFidelityTransitionRule
                {
                    fromFidelity = from,
                    toFidelity = to,
                    mechanism = mechanism
                });
            return this;
        }

        /// <summary>Applies the few provider-specific fidelity settings not covered by the fluent defaults.</summary>
        public SimpleRealityProviderBuilder ConfigureFidelity(Action<SimpleFidelityDefinition> configure)
        {
            if (fidelity == null) throw new InvalidOperationException("Call WithFidelity before ConfigureFidelity.");
            configure?.Invoke(fidelity);
            registration.supportedFidelities = fidelity.supportedFidelities;
            return this;
        }

        public SimpleRealityProviderBuilder WithProcess(SimpleProcessDefinition definition)
        {
            process = definition ?? throw new ArgumentNullException(nameof(definition));
            registration.capabilities |= RealityProviderCapability.Processes | RealityProviderCapability.Fidelity;
            return this;
        }

        /// <summary>Configures the common bounded analytical-process contract in one call.</summary>
        public SimpleRealityProviderBuilder WithAnalyticalProcess(
            Func<RealityProcessRecord, RealityProcessExecution, IList<RealityVeto>, bool> canExecute,
            Func<RealityProcessRecord, RealityProcessExecution, RealityProcessResult> execute,
            RealityFidelityMask legalFidelities = RealityFidelityMask.Statistical,
            bool runsWhileLiveProjection = false,
            bool mayRequestEscalation = true,
            RealityFidelity escalationTarget = RealityFidelity.Narrative,
            RealityFidelityEscalationPolicy escalationPolicy = RealityFidelityEscalationPolicy.HostApproval)
        {
            if (fidelity == null) throw new InvalidOperationException("Call WithFidelity before WithAnalyticalProcess.");
            fidelity.processLegalFidelities = legalFidelities;
            fidelity.processRunsWhileLiveProjection = runsWhileLiveProjection;
            fidelity.processMayRequestEscalation = mayRequestEscalation;
            fidelity.processEscalationTarget = escalationTarget;
            fidelity.processEscalationPolicy = escalationPolicy;
            return WithProcess(new SimpleProcessDefinition { canExecute = canExecute, execute = execute });
        }

        public SimpleRealityProviderBuilder WithPopulations(SimplePopulationDefinition definition)
        {
            populations = definition ?? throw new ArgumentNullException(nameof(definition));
            registration.capabilities |= RealityProviderCapability.Populations;
            return this;
        }

        public SimpleRealityProviderBuilder WithAnchors(SimpleAnchorDefinition definition)
        {
            anchors = definition ?? throw new ArgumentNullException(nameof(definition));
            registration.capabilities |= RealityProviderCapability.Anchors;
            return this;
        }

        public SimpleRealityProviderBuilder WithConstraints(SimpleConstraintDefinition definition)
        {
            constraints = definition ?? throw new ArgumentNullException(nameof(definition));
            registration.capabilities |= RealityProviderCapability.Constraints;
            return this;
        }

        public SimpleRealityProviderBuilder WithDiagnostics(Func<RealityDiagnosticsContext, IEnumerable<string>> lines)
        {
            diagnostics = lines;
            registration.capabilities |= RealityProviderCapability.Diagnostics;
            return this;
        }

        public SimpleRealityProviderBuilder WithObservations(Action<RealityObservationRecord> observe)
        {
            observations = observe;
            registration.capabilities |= RealityProviderCapability.Observations;
            return this;
        }

        /// <summary>Attaches map, compression, transfer, excursion, or other advanced services to this registration.</summary>
        public SimpleRealityProviderBuilder UseAdvanced(params object[] services)
        {
            foreach (object service in services ?? Array.Empty<object>())
            {
                if (service == null || advanced.Contains(service)) continue;
                advanced.Add(service);
                AddCapabilityFlags(service);
            }
            return this;
        }

        public SimpleRealityProvider Build()
        {
            if ((process != null || fidelity != null) && fidelity == null)
                throw new InvalidOperationException("A simple process requires WithFidelity so its legal transitions are explicit.");
            return new SimpleRealityProvider(registration.Clone(), onRegistered, regions,
                fidelity?.Clone(), process?.Clone(), populations, anchors, constraints, diagnostics, observations, advanced.ToList());
        }

        private void AddCapabilityFlags(object service)
        {
            if (service is IRegionDescriptorProvider) registration.capabilities |= RealityProviderCapability.Regions;
            if (service is IRealityProcessProvider) registration.capabilities |= RealityProviderCapability.Processes | RealityProviderCapability.Fidelity;
            else if (service is IRealityFidelityProvider) registration.capabilities |= RealityProviderCapability.Fidelity;
            if (service is IPopulationProvider) registration.capabilities |= RealityProviderCapability.Populations;
            if (service is IAnchorProvider || service is ITransactionalAnchorProvider) registration.capabilities |= RealityProviderCapability.Anchors;
            if (service is IConstraintResolver) registration.capabilities |= RealityProviderCapability.Constraints;
            if (service is IMaterializationProvider || service is IRealityMaterializationConsistencyProvider) registration.capabilities |= RealityProviderCapability.Materialization;
            if (service is ICompressionProvider) registration.capabilities |= RealityProviderCapability.Compression;
            if (service is IObservationProvider) registration.capabilities |= RealityProviderCapability.Observations;
            if (service is IRealityDiagnosticsProvider) registration.capabilities |= RealityProviderCapability.Diagnostics;
        }
    }

    /// <summary>
    /// Registered facade produced by <see cref="SimpleRealityProviderBuilder"/>. It advertises only configured
    /// services to the framework, so an absent optional service cannot silently become a successful no-op provider.
    /// </summary>
    public sealed class SimpleRealityProvider : IRealityProvider, IRealityCapabilitySource,
        IRegionDescriptorProvider, IRealityFidelityProvider, IRealityProcessProvider, IPopulationProvider, IAnchorProvider, IConstraintResolver,
        IMaterializationProvider, IStageAwareMaterializationProvider, ITransactionalAnchorProvider,
        ITransactionalAnchorCommitProvider, IRealityMapIdentityProvider, ICompressionProvider, IObservationProvider,
        IRealityDiagnosticsProvider, IRealityMaterializationConsistencyProvider, IAdjacentRegionTransferHost,
        IRealityExcursionTaskProvider, IRealityExcursionTaskCleanupProvider, IRealityExactlyOnceProvider
    {
        private readonly Action<RealityProviderContext> onRegistered;
        private readonly Func<RealityProviderContext, IEnumerable<RealityRegionDescriptor>> regions;
        private readonly SimpleFidelityDefinition fidelity;
        private readonly SimpleProcessDefinition process;
        private readonly SimplePopulationDefinition populations;
        private readonly SimpleAnchorDefinition anchors;
        private readonly SimpleConstraintDefinition constraints;
        private readonly Func<RealityDiagnosticsContext, IEnumerable<string>> diagnostics;
        private readonly Action<RealityObservationRecord> observations;
        private readonly List<object> advanced;

        internal SimpleRealityProvider(RealityProviderRegistration registration,
            Action<RealityProviderContext> onRegistered,
            Func<RealityProviderContext, IEnumerable<RealityRegionDescriptor>> regions,
            SimpleFidelityDefinition fidelity,
            SimpleProcessDefinition process,
            SimplePopulationDefinition populations,
            SimpleAnchorDefinition anchors,
            SimpleConstraintDefinition constraints,
            Func<RealityDiagnosticsContext, IEnumerable<string>> diagnostics,
            Action<RealityObservationRecord> observations,
            List<object> advanced)
        {
            Registration = registration;
            this.onRegistered = onRegistered;
            this.regions = regions;
            this.fidelity = fidelity;
            this.process = process;
            this.populations = populations;
            this.anchors = anchors;
            this.constraints = constraints;
            this.diagnostics = diagnostics;
            this.observations = observations;
            this.advanced = advanced ?? new List<object>();
        }

        public RealityProviderRegistration Registration { get; }

        public void OnRegistered(RealityProviderContext context)
        {
            if (onRegistered != null) onRegistered(context);
            else Find<IRealityProvider>()?.OnRegistered(context);
        }

        public bool ProvidesCapability(Type capabilityType)
        {
            if (capabilityType == typeof(IRealityProvider)) return true;
            if (capabilityType == typeof(IRegionDescriptorProvider)) return regions != null || Find<IRegionDescriptorProvider>() != null;
            if (capabilityType == typeof(IRealityFidelityProvider)) return fidelity != null || Find<IRealityFidelityProvider>() != null || process != null;
            if (capabilityType == typeof(IRealityProcessProvider)) return process != null || Find<IRealityProcessProvider>() != null;
            if (capabilityType == typeof(IPopulationProvider)) return populations != null || Find<IPopulationProvider>() != null;
            if (capabilityType == typeof(IAnchorProvider)) return anchors != null || Find<IAnchorProvider>() != null;
            if (capabilityType == typeof(IConstraintResolver)) return constraints != null || Find<IConstraintResolver>() != null;
            if (capabilityType == typeof(IRealityDiagnosticsProvider)) return diagnostics != null || Find<IRealityDiagnosticsProvider>() != null;
            if (capabilityType == typeof(IObservationProvider)) return observations != null || Find<IObservationProvider>() != null;
            return advanced.Any(item => capabilityType.IsInstanceOfType(item));
        }

        public IEnumerable<RealityRegionDescriptor> DescribeRegions(RealityProviderContext context)
        {
            if (regions != null) return regions(context) ?? Enumerable.Empty<RealityRegionDescriptor>();
            return Find<IRegionDescriptorProvider>()?.DescribeRegions(context) ?? Enumerable.Empty<RealityRegionDescriptor>();
        }

        public RealityFidelityContract DescribeFidelity(RealityProviderContext context, RealityRegionSnapshot region)
        {
            if (fidelity != null)
                return new RealityFidelityContract
                {
                    currentFidelity = region?.fidelity ?? Registration.defaultFidelity,
                    supportedFidelities = fidelity.supportedFidelities,
                    transitions = (fidelity.transitions ?? new List<RealityFidelityTransitionRule>()).Select(item => new RealityFidelityTransitionRule
                    {
                        fromFidelity = item.fromFidelity, toFidelity = item.toFidelity, mechanism = item.mechanism
                    }).ToList()
                };
            return Find<IRealityFidelityProvider>()?.DescribeFidelity(context, region);
        }

        public RealityProcessFidelityPolicy DescribeProcessFidelity(RealityProcessRecord value, RealityRegionSnapshot region)
        {
            if (process?.policy != null) return process.policy(value, region);
            if (fidelity?.processPolicy != null) return fidelity.processPolicy(value, region);
            if (fidelity != null) return new RealityProcessFidelityPolicy
            {
                legalFidelities = fidelity.processLegalFidelities,
                runsWhileLiveProjection = fidelity.processRunsWhileLiveProjection,
                mayRequestEscalation = fidelity.processMayRequestEscalation,
                escalationTarget = fidelity.processEscalationTarget,
                escalationPolicy = fidelity.processEscalationPolicy
            };
            return Find<IRealityFidelityProvider>()?.DescribeProcessFidelity(value, region);
        }

        public bool CanTransitionFidelity(RealityFidelityTransitionRequest request, IList<RealityVeto> vetoes)
        {
            if (fidelity?.validateTransition != null) return fidelity.validateTransition(request, vetoes);
            if (fidelity != null)
            {
                if (request == null || request.providerId != Registration.providerId)
                    vetoes.Add(new RealityVeto("fidelity.simple-owner", "The simple provider does not own this transition.", Registration.providerId, 2));
                else if (!RealityFidelityRules.Allows(fidelity.supportedFidelities, request.toFidelity))
                    vetoes.Add(new RealityVeto("fidelity.simple-unsupported", "The requested fidelity is not supported by the simple provider.", Registration.providerId, 2));
                else if (!(fidelity.transitions ?? new List<RealityFidelityTransitionRule>()).Any(item => item != null &&
                    item.fromFidelity == request.fromFidelity && item.toFidelity == request.toFidelity &&
                    (item.mechanism == RealityFidelityTransitionMechanism.Provider ||
                     item.mechanism == RealityFidelityTransitionMechanism.HostApproval)))
                    vetoes.Add(new RealityVeto("fidelity.simple-transition", "The simple provider did not declare this transition edge.", Registration.providerId, 2));
                return vetoes.Count == 0;
            }
            return Find<IRealityFidelityProvider>()?.CanTransitionFidelity(request, vetoes) ?? false;
        }

        public void OnFidelityChanged(RealityProviderContext context, RealityRegionSnapshot region,
            RealityFidelity previous, RealityFidelity current)
        {
            if (fidelity?.fidelityChanged != null) fidelity.fidelityChanged(context, region, previous, current);
            else Find<IRealityFidelityProvider>()?.OnFidelityChanged(context, region, previous, current);
        }

        public bool CanExecute(RealityProcessRecord value, RealityProcessExecution execution, IList<RealityVeto> vetoes)
        {
            if (process?.canExecute != null) return process.canExecute(value, execution, vetoes);
            return Find<IRealityProcessProvider>()?.CanExecute(value, execution, vetoes) ?? false;
        }

        public RealityProcessResult Execute(RealityProcessRecord value, RealityProcessExecution execution)
        {
            if (process?.execute != null) return process.execute(value, execution);
            return Find<IRealityProcessProvider>()?.Execute(value, execution) ??
                new RealityProcessResult { succeeded = false, error = "No analytical process service is configured." };
        }

        public bool CanChangePopulation(RealityPopulationRecord value, string operation, IList<RealityVeto> vetoes)
        {
            if (populations?.canChange != null) return populations.canChange(value, operation, vetoes);
            return Find<IPopulationProvider>()?.CanChangePopulation(value, operation, vetoes) ?? false;
        }

        public void ReconcileActiveMap(RealityProviderContext context, RealityPopulationRecord value, string payload)
        {
            if (populations?.reconcileActiveMap != null) populations.reconcileActiveMap(context, value, payload);
            else Find<IPopulationProvider>()?.ReconcileActiveMap(context, value, payload);
        }

        public bool ValidateAnchor(RealityAnchorRecord value, IList<RealityVeto> vetoes)
        {
            if (anchors?.validate != null) return anchors.validate(value, vetoes);
            return Find<IAnchorProvider>()?.ValidateAnchor(value, vetoes) ?? false;
        }

        public bool CanResolve(RealityConstraint value) => constraints?.canResolve != null
            ? constraints.canResolve(value) : Find<IConstraintResolver>()?.CanResolve(value) ?? false;

        public bool Resolve(RealityProviderContext context, RealityConstraint value, IList<RealityVeto> vetoes) => constraints?.resolve != null
            ? constraints.resolve(context, value, vetoes) : Find<IConstraintResolver>()?.Resolve(context, value, vetoes) ?? false;

        public int Order => Find<IMaterializationProvider>()?.Order ?? Find<ICompressionProvider>()?.Order ?? Registration.order;

        public void CanMaterialize(RealityMaterializationRequest request, RealityMaterializationPlan plan) =>
            Find<IMaterializationProvider>()?.CanMaterialize(request, plan);
        public void Prepare(RealityMaterializationRequest request, RealityMaterializationPlan plan) =>
            Find<IMaterializationProvider>()?.Prepare(request, plan);
        public void Apply(RealityMaterializationContext context) => Find<IMaterializationProvider>()?.Apply(context);
        public void Validate(RealityMaterializationContext context, IList<RealityVeto> vetoes) =>
            Find<IMaterializationProvider>()?.Validate(context, vetoes);
        public void Rollback(RealityMaterializationContext context) => Find<IMaterializationProvider>()?.Rollback(context);
        public void Rollback(RealityMaterializationContext context, RealityMaterializationStage stages) =>
            (Find<IStageAwareMaterializationProvider>() as IStageAwareMaterializationProvider)?.Rollback(context, stages);

        public bool PrepareAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor, IList<RealityVeto> vetoes) =>
            Find<ITransactionalAnchorProvider>()?.PrepareAnchor(context, anchor, vetoes) ?? false;
        public bool ApplyAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor, IList<RealityVeto> vetoes) =>
            Find<ITransactionalAnchorProvider>()?.ApplyAnchor(context, anchor, vetoes) ?? false;
        public bool ValidateAnchorMaterialization(RealityMaterializationContext context, RealityAnchorRecord anchor, IList<RealityVeto> vetoes) =>
            Find<ITransactionalAnchorProvider>()?.ValidateAnchorMaterialization(context, anchor, vetoes) ?? false;
        public void RollbackAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor) =>
            Find<ITransactionalAnchorProvider>()?.RollbackAnchor(context, anchor);
        public void CommitAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor) =>
            Find<ITransactionalAnchorCommitProvider>()?.CommitAnchor(context, anchor);

        public bool TryClaimMap(Map map, out RealityMapIdentityClaim claim)
        {
            IRealityMapIdentityProvider provider = Find<IRealityMapIdentityProvider>();
            if (provider != null) return provider.TryClaimMap(map, out claim);
            claim = null;
            return false;
        }

        public void CanCompress(RealityCompressionRequest request, IList<RealityVeto> vetoes) => Find<ICompressionProvider>()?.CanCompress(request, vetoes);
        public void Prepare(RealityCompressionRequest request) => Find<ICompressionProvider>()?.Prepare(request);
        public void Validate(RealityCompressionRequest request, IList<RealityVeto> vetoes) => Find<ICompressionProvider>()?.Validate(request, vetoes);
        public void Commit(RealityCompressionRequest request) => Find<ICompressionProvider>()?.Commit(request);
        public void Rollback(RealityCompressionRequest request) => Find<ICompressionProvider>()?.Rollback(request);

        public void OnObservation(RealityObservationRecord observation)
        {
            if (observations != null) observations(observation);
            else Find<IObservationProvider>()?.OnObservation(observation);
        }

        public IEnumerable<string> DiagnosticLines(RealityDiagnosticsContext context)
        {
            if (diagnostics != null) return diagnostics(context) ?? Enumerable.Empty<string>();
            return Find<IRealityDiagnosticsProvider>()?.DiagnosticLines(context) ?? Enumerable.Empty<string>();
        }

        public void BuildMaterializationConstraints(RealityMaterializationConsistencyContext context,
            RealityMaterializationConsistencyPlan plan) => Find<IRealityMaterializationConsistencyProvider>()?.BuildMaterializationConstraints(context, plan);
        public void ValidateMaterializationConsistency(RealityMaterializationContext context,
            RealityMaterializationConsistencyPlan plan, IList<RealityVeto> vetoes) =>
            Find<IRealityMaterializationConsistencyProvider>()?.ValidateMaterializationConsistency(context, plan, vetoes);

        public bool CanTransfer(RealityAdjacentTransferRequest request, IList<RealityVeto> vetoes) =>
            Find<IAdjacentRegionTransferHost>()?.CanTransfer(request, vetoes) ?? false;
        public bool Prepare(RealityAdjacentTransferRequest request, RealityTransferJournalRecord journal, out string diagnostic)
        {
            IAdjacentRegionTransferHost provider = Find<IAdjacentRegionTransferHost>();
            if (provider != null) return provider.Prepare(request, journal, out diagnostic);
            diagnostic = "No adjacent transfer service is configured.";
            return false;
        }
        public bool Commit(RealityAdjacentTransferRequest request, RealityTransferJournalRecord journal, out string diagnostic)
        {
            IAdjacentRegionTransferHost provider = Find<IAdjacentRegionTransferHost>();
            if (provider != null) return provider.Commit(request, journal, out diagnostic);
            diagnostic = "No adjacent transfer service is configured.";
            return false;
        }
        public void Rollback(RealityAdjacentTransferRequest request, RealityTransferJournalRecord journal) =>
            Find<IAdjacentRegionTransferHost>()?.Rollback(request, journal);

        public bool TryObserveExcursionTask(RealityExcursionTicket ticket, long now, out RealityExcursionTaskObservation observation)
        {
            IRealityExcursionTaskProvider provider = Find<IRealityExcursionTaskProvider>();
            if (provider != null) return provider.TryObserveExcursionTask(ticket, now, out observation);
            observation = null;
            return false;
        }
        public void ForgetExcursionTask(RealityExcursionTicket ticket) => Find<IRealityExcursionTaskCleanupProvider>()?.ForgetExcursionTask(ticket);

        public bool TryDescribeExactlyOnceDomain(string kind, string domainId, out RealityExactlyOnceDomain domain)
        {
            IRealityExactlyOnceProvider provider = Find<IRealityExactlyOnceProvider>();
            if (provider != null) return provider.TryDescribeExactlyOnceDomain(kind, domainId, out domain);
            domain = null;
            return false;
        }

        private T Find<T>() where T : class => advanced.OfType<T>().FirstOrDefault();
    }
}
