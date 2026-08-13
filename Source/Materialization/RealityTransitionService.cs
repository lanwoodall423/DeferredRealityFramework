using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;
using DeferredReality.Simulation;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DeferredReality.Materialization
{
    /// <summary>Host-provided factories for normal RimWorld map creation.</summary>
    public static class RealityMapFactoryRegistry
    {
        private static readonly Dictionary<string, IRealityMapFactory> ProviderFactories =
            new Dictionary<string, IRealityMapFactory>(StringComparer.Ordinal);
        /// <summary>Registers a factory for one provider namespace.</summary>
        public static bool Register(string providerId, IRealityMapFactory factory)
        {
            if (string.IsNullOrWhiteSpace(providerId) || factory == null) return false;
            if (!RealityThreadGuard.IsMainThread)
            {
                LongEventHandler.ExecuteWhenFinished(() => Register(providerId, factory));
                return true;
            }
            ProviderFactories[providerId.Trim()] = factory;
            return true;
        }

        /// <summary>Removes a provider-scoped factory if it is the registered instance.</summary>
        public static bool Unregister(string providerId, IRealityMapFactory factory = null)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return false;
            if (!RealityThreadGuard.IsMainThread)
            {
                LongEventHandler.ExecuteWhenFinished(() => Unregister(providerId, factory));
                return true;
            }
            if (!ProviderFactories.TryGetValue(providerId.Trim(), out IRealityMapFactory current)) return false;
            if (factory != null && !ReferenceEquals(factory, current)) return false;
            return ProviderFactories.Remove(providerId.Trim());
        }

        /// <summary>Resolves only the factory owned by the requested provider.</summary>
        public static bool TryGet(string providerId, out IRealityMapFactory factory)
        {
            factory = null;
            return !string.IsNullOrWhiteSpace(providerId) &&
                ProviderFactories.TryGetValue(providerId.Trim(), out factory) && factory != null;
        }
    }

    /// <summary>Transactional materialization of latent regions.</summary>
    public static class RealityMaterializationService
    {
        private static readonly List<string> LastRollbackFailureList = new List<string>();

        /// <summary>Recent rollback failures retained for diagnostics; the original transition error remains primary.</summary>
        public static IReadOnlyList<string> LastRollbackFailures => LastRollbackFailureList.ToList();

        internal static void RememberRollbackFailures(IEnumerable<string> errors)
        {
            LastRollbackFailureList.Clear();
            foreach (string error in (errors ?? Enumerable.Empty<string>()).Where(item => !string.IsNullOrEmpty(item))
                .Distinct(StringComparer.Ordinal).Take(64)) LastRollbackFailureList.Add(error);
        }

        /// <summary>Runs planning, preparation, mutation, validation, commit, or rollback.</summary>
        public static RealityTransitionResult TryMaterialize(DeferredRealityWorldComponent world, RealityMaterializationRequest request)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new RealityTransitionResult { regionId = request?.regionId ?? default(RealityRegionId) };
            if (world == null || request == null || !request.regionId.IsValid)
            {
                result.error = "A valid world and region are required.";
                return result;
            }
            if (!world.TryGetRegion(request.regionId, out RealityRegionSnapshot region))
            {
                result.error = "The requested region is not registered.";
                return result;
            }
            RealityFidelityEscalationRecord escalation = null;
            if (!string.IsNullOrEmpty(request.escalationRequestId))
            {
                if (!world.TryGetFidelityEscalation(request.escalationRequestId, out escalation) ||
                    escalation.status != RealityFidelityEscalationStatus.Approved ||
                    escalation.regionId != request.regionId.ToString() ||
                    escalation.requestedFidelity != RealityFidelity.Materialized)
                {
                    result.error = "The requested materialization escalation is not approved for this region.";
                    result.vetoes = new[] { new RealityVeto("materialization.escalation-not-approved", result.error,
                        request.providerId, 2) };
                    return result;
                }
            }
            if (region.authority == RealityRegionAuthority.Quarantined)
            {
                result.error = "The requested region projection is quarantined.";
                return result;
            }
            if (region.authority == RealityRegionAuthority.LiveProjection)
            {
                Map liveMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID == region.projectionMapUniqueId);
                if (liveMap == null)
                {
                    world.QuarantineProjection(request.regionId, "The persisted live projection binding has no live Map.");
                    result.error = "The region claims a live projection, but its Map is missing.";
                    return result;
                }
                result.succeeded = true;
                result.steps = new[] { "already-live-projection" };
                return result;
            }
            if (region.authority != RealityRegionAuthority.Latent)
            {
                result.error = "The region is already in a projection transition.";
                return result;
            }
            string fidelityOwnerId = string.IsNullOrEmpty(request.providerId)
                ? request.regionId.ProviderNamespace : request.providerId;
            if (!TryValidateFidelityContract(world, region, fidelityOwnerId, RealityFidelity.Materialized,
                RealityFidelityTransitionMechanism.Materialization, out string fidelityDiagnostic))
            {
                result.error = fidelityDiagnostic;
                result.vetoes = new[] { new RealityVeto("materialization.fidelity-contract", fidelityDiagnostic,
                    fidelityOwnerId, 2) };
                return result;
            }
            var plan = new RealityMaterializationPlan();
            string transactionId = "materialize:" + request.regionId + ":" + request.now + ":" + (request.targetAnchorId ?? string.Empty);
            plan.TransactionId = transactionId;
            plan.TargetAnchorId = request.targetAnchorId;
            plan.AddStep("validate-region");
            Map activeMap = null;
            plan.ActiveMap = activeMap;
            if (activeMap != null) plan.AddStep("reuse-active-map");
            plan.SetConsistency(new RealityMaterializationConsistencyPlan(world, request));
            List<IRealityMaterializationConsistencyProvider> consistencyProviders =
                RealityProviderRegistry.OfType<IRealityMaterializationConsistencyProvider>()
                    .Where(provider => string.Equals(ProviderId(provider), fidelityOwnerId, StringComparison.Ordinal))
                    .OrderBy(provider => ProviderId(provider), StringComparer.Ordinal).ToList();
            var consistencyContext = new RealityMaterializationConsistencyContext(world, request, plan.Consistency);
            foreach (IRealityMaterializationConsistencyProvider provider in consistencyProviders)
            {
                try { provider.BuildMaterializationConstraints(consistencyContext, plan.Consistency); }
                catch (Exception exception)
                {
                    plan.AddVeto(new RealityVeto("materialization.consistency-provider-exception", exception.Message,
                        ProviderId(provider), 3));
                }
            }
            plan.Consistency.RefreshDeterminism(world);
            AddConsistencyPlanVetoes(plan, fidelityOwnerId);
            List<IMaterializationProvider> providers = RealityProviderRegistry.OfType<IMaterializationProvider>()
                .Where(provider => AppliesToRequest(provider, request))
                .OrderBy(item => item.Order).ThenBy(item => ProviderId(item), StringComparer.Ordinal).ToList();
            foreach (IMaterializationProvider provider in providers)
            {
                try { provider.CanMaterialize(request, plan); }
                catch (Exception exception) { plan.AddVeto(new RealityVeto("materialization.provider-exception", exception.Message, ProviderId(provider), 3)); }
            }
            if (plan.Vetoes.Count > 0)
            {
                result.error = "Materialization was vetoed during planning.";
                result.originalError = result.error;
                result.vetoes = plan.Vetoes.ToList();
                result.steps = plan.Steps.ToList();
                return result;
            }
            RealityWorldState state = world.CaptureState();
            if (!world.BeginMaterialization(request.regionId))
            {
                result.error = "The region could not enter materialization authority.";
                return result;
            }
            Map createdMap = null;
            IRealityMapFactory selectedFactory = null;
            HashSet<int> liveMapIdsBeforeFactory = new HashSet<int>((Find.Maps ?? Enumerable.Empty<Map>())
                .Where(item => item != null).Select(item => item.uniqueID));
            var context = new RealityMaterializationContext(world, request, plan, activeMap, transactionId);
            List<IMaterializationProvider> preparedProviders = new List<IMaterializationProvider>();
            var providerStages = new Dictionary<IMaterializationProvider, RealityMaterializationStage>();
            List<TransactionalAnchorStage> preparedAnchors = new List<TransactionalAnchorStage>();
            List<TransactionalAnchorStage> appliedAnchors = new List<TransactionalAnchorStage>();
            IReadOnlyList<RealityAnchorSnapshot> anchors = world.AnchorSnapshots(request.regionId.ToString())
                .Where(item => item?.record != null).OrderBy(item => item.record.anchorId, StringComparer.Ordinal).ToList();
            bool mapCreationIntentStarted = false;
            try
            {
                context.MarkStage(RealityMaterializationStage.Preparation);
                foreach (IMaterializationProvider provider in providers)
                {
                    try
                    {
                        providerStages[provider] = RealityMaterializationStage.Preparation;
                        // Prepare may mutate before throwing; register it before invocation so compensation is attempted.
                        preparedProviders.Add(provider);
                        provider.Prepare(request, plan);
                    }
                    catch (Exception exception)
                    {
                        plan.AddVeto(new RealityVeto("materialization.prepare-failed", exception.Message, ProviderId(provider), 3));
                        throw new RealityTransitionException("Materialization preparation failed: " + exception.Message);
                    }
                }
                if (plan.Vetoes.Count > 0) throw new RealityTransitionException("Materialization preparation was vetoed.");
                if (plan.ActiveMap == null)
                {
                    string factoryProviderId = request.adjacentMap?.providerId;
                    if (string.IsNullOrEmpty(factoryProviderId))
                        factoryProviderId = string.IsNullOrEmpty(request.providerId)
                            ? request.regionId.ProviderNamespace : request.providerId;
                    bool factoryFound = RealityMapFactoryRegistry.TryGet(factoryProviderId, out selectedFactory);
                    if (!factoryFound)
                        throw new RealityTransitionException("No map factory is registered for this region.");
                    if (request.adjacentMap != null &&
                        !string.Equals(request.adjacentMap.providerId, factoryProviderId, StringComparison.Ordinal))
                        throw new RealityTransitionException("The adjacent map owner does not match the selected map factory.");
                    if (request.adjacentMap != null)
                    {
                        if (!world.BeginMapCreationIntent(transactionId, request.regionId, request.adjacentMap,
                            out string intentDiagnostic))
                            throw new RealityTransitionException(intentDiagnostic ?? "The adjacent map creation intent was rejected.");
                        mapCreationIntentStarted = true;
                    }
                    context.MarkStage(RealityMaterializationStage.MapAcquisition);
                    bool factorySucceeded = selectedFactory.TryCreateMap(request.regionId, plan, out Map factoryMap, out string diagnostic);
                    if (factoryMap != null && !liveMapIdsBeforeFactory.Contains(factoryMap.uniqueID)) createdMap = factoryMap;
                    if (factoryMap != null && mapCreationIntentStarted && !world.BindMapCreationIntent(transactionId, factoryMap))
                        throw new RealityTransitionException("The generated map did not match its creation intent.");
                    if (!factorySucceeded || factoryMap == null)
                        throw new RealityTransitionException(diagnostic ?? "The host map factory could not create a normal Map.");
                    plan.ActiveMap = factoryMap;
                    RealityMaterializationStage stages = context.Stages;
                    IDictionary<string, object> runtimeState = context.RuntimeState;
                    context = new RealityMaterializationContext(world, request, plan, factoryMap, transactionId);
                    context.Stages = stages;
                    foreach (KeyValuePair<string, object> item in runtimeState)
                        context.RuntimeState[item.Key] = item.Value;
                    plan.AddStep("acquire-map");
                }
                if (request.adjacentMap != null)
                {
                    if (activeMap != null && !world.IsAdjacentMap(activeMap))
                        throw new RealityTransitionException("An existing ordinary map cannot be reclassified as a temporary adjacent site.");
                    if (createdMap == null && !world.IsAdjacentMap(plan.ActiveMap))
                        throw new RealityTransitionException("An existing unmarked map cannot be reclassified as a temporary adjacent site.");
                    if (!world.MarkAdjacentMap(plan.ActiveMap, request.regionId, request.adjacentMap))
                        throw new RealityTransitionException("The generated map could not be marked as the requested temporary adjacent site.");
                    plan.AddStep("mark-adjacent-map");
                }
                foreach (IMaterializationProvider provider in preparedProviders)
                {
                    context.MarkStage(RealityMaterializationStage.ProviderApply);
                    providerStages[provider] = providerStages[provider] | RealityMaterializationStage.ProviderApply;
                    provider.Apply(context);
                }
                plan.AddStep("provider-plans-applied");
                context.MarkStage(RealityMaterializationStage.ConstraintResolution);
                RealityConstraintResolutionResult constraintResult = RealityConstraintService.Resolve(world, request.regionId, request.now);
                if (constraintResult.conflicted > 0)
                    throw new RealityTransitionException("Materialization has unresolved historical constraint conflicts.");
                plan.AddStep("constraints-resolved");
                var consistencyVetoes = new List<RealityVeto>();
                foreach (IRealityMaterializationConsistencyProvider provider in consistencyProviders)
                {
                    try { provider.ValidateMaterializationConsistency(context, plan.Consistency, consistencyVetoes); }
                    catch (Exception exception)
                    {
                        consistencyVetoes.Add(new RealityVeto("materialization.consistency-validation-exception",
                            exception.Message, ProviderId(provider), 3));
                    }
                }
                foreach (RealityVeto veto in consistencyVetoes) plan.AddVeto(veto);
                AddConsistencyPlanVetoes(plan, fidelityOwnerId);
                if (consistencyVetoes.Count > 0 || plan.Consistency.Conflicts.Count > 0)
                    throw new RealityTransitionException("Materialization consistency validation failed.");
                IReadOnlyList<ITransactionalAnchorProvider> transactionalProviders = RealityProviderRegistry.OfType<ITransactionalAnchorProvider>();
                foreach (IAnchorProvider anchorProvider in RealityProviderRegistry.OfType<IAnchorProvider>())
                {
                    string anchorProviderId = (anchorProvider as IRealityProvider)?.Registration?.providerId;
                    foreach (RealityAnchorSnapshot anchor in anchors)
                    {
                        if (!string.IsNullOrEmpty(anchorProviderId) && anchor.record.providerId != anchorProviderId) continue;
                        var vetoes = new List<RealityVeto>();
                        if (!anchorProvider.ValidateAnchor(anchor.record, vetoes) || vetoes.Count > 0)
                            throw new RealityTransitionException("Anchor validation failed: " + string.Join("; ", vetoes.Select(item => item.ToString()).ToArray()));
                    }
                }
                foreach (ITransactionalAnchorProvider anchorProvider in transactionalProviders)
                {
                    string anchorProviderId = (anchorProvider as IRealityProvider)?.Registration?.providerId;
                    foreach (RealityAnchorSnapshot anchor in anchors)
                    {
                        if (!string.IsNullOrEmpty(anchorProviderId) && anchor.record.providerId != anchorProviderId) continue;
                        var vetoes = new List<RealityVeto>();
                        context.MarkStage(RealityMaterializationStage.AnchorPreparation);
                        var anchorStage = new TransactionalAnchorStage(anchorProvider, anchor.record);
                        // Register compensation before invoking an optional provider prepare hook.
                        preparedAnchors.Add(anchorStage);
                        if (!anchorProvider.PrepareAnchor(context, anchor.record, vetoes) || vetoes.Count > 0)
                            throw new RealityTransitionException("Transactional anchor preparation failed: " + string.Join("; ", vetoes.Select(item => item.ToString()).ToArray()));
                    }
                }
                var validationVetoes = new List<RealityVeto>();
                context.MarkStage(RealityMaterializationStage.ProviderValidation);
                foreach (IMaterializationProvider provider in preparedProviders)
                {
                    providerStages[provider] = providerStages[provider] | RealityMaterializationStage.ProviderValidation;
                    provider.Validate(context, validationVetoes);
                }
                if (validationVetoes.Count > 0) throw new RealityTransitionException(string.Join("; ", validationVetoes.Select(item => item.ToString()).ToArray()));
                foreach (TransactionalAnchorStage anchor in preparedAnchors)
                {
                    context.MarkStage(RealityMaterializationStage.AnchorApply);
                    var anchorVetoes = new List<RealityVeto>();
                    // The call itself is a begun mutation stage, even when a provider reports a veto after partial work.
                    appliedAnchors.Add(anchor);
                    if (!anchor.Provider.ApplyAnchor(context, anchor.Record, anchorVetoes) || anchorVetoes.Count > 0)
                        throw new RealityTransitionException("Transactional anchor application failed: " + string.Join("; ", anchorVetoes.Select(item => item.ToString()).ToArray()));
                }
                foreach (TransactionalAnchorStage anchor in appliedAnchors)
                {
                    context.MarkStage(RealityMaterializationStage.AnchorValidation);
                    var anchorVetoes = new List<RealityVeto>();
                    if (!anchor.Provider.ValidateAnchorMaterialization(context, anchor.Record, anchorVetoes) || anchorVetoes.Count > 0)
                        throw new RealityTransitionException("Transactional anchor validation failed: " + string.Join("; ", anchorVetoes.Select(item => item.ToString()).ToArray()));
                }
                RealityRegionDescriptor descriptor = world.RegionRecord(request.regionId.ToString());
                if (descriptor == null) throw new RealityTransitionException("Region disappeared during materialization.");
                context.MarkStage(RealityMaterializationStage.RegionCommit);
                if (!world.CommitMaterialization(request.regionId, plan.ActiveMap.uniqueID, request.now))
                    throw new RealityTransitionException("The region could not commit its live projection binding.");
                if (escalation != null && !world.ResolveFidelityEscalation(escalation.requestId,
                    RealityFidelityEscalationStatus.Satisfied, "Materialization committed the requested fidelity."))
                    throw new RealityTransitionException("The approved fidelity escalation could not be resolved.");
                world.Touch("region.projection-live", "core", descriptor.regionId, request.reason);
                var committedAnchorProviders = new HashSet<ITransactionalAnchorCommitProvider>();
                foreach (TransactionalAnchorStage anchor in appliedAnchors)
                    if (anchor.Provider is ITransactionalAnchorCommitProvider commitProvider && committedAnchorProviders.Add(commitProvider))
                        try
                        {
                            commitProvider.CommitAnchor(context, anchor.Record);
                        }
                        catch (Exception notificationException)
                        {
                            ReportCommitNotificationFailure(world, "transactional-anchor", ProviderId(commitProvider),
                                notificationException);
                        }
                result.succeeded = true;
                if (mapCreationIntentStarted) world.ClearMapCreationIntent(transactionId);
                result.steps = plan.Steps.ToList();
                return result;
            }
            catch (Exception exception)
            {
                var rollbackErrors = new List<string>();
                foreach (TransactionalAnchorStage anchor in RealityTransitionPolicy.ReversePrepared(preparedAnchors))
                {
                    try { anchor.Provider.RollbackAnchor(context, anchor.Record); }
                    catch (Exception rollbackException) { rollbackErrors.Add("anchor " + ProviderId(anchor.Provider) + ": " + rollbackException.Message); }
                }
                foreach (IMaterializationProvider provider in RealityTransitionPolicy.ReversePrepared(preparedProviders))
                {
                    try
                    {
                        if (provider is IStageAwareMaterializationProvider stageAware)
                            stageAware.Rollback(context, providerStages[provider]);
                        else provider.Rollback(context);
                    }
                    catch (Exception rollbackException) { rollbackErrors.Add("provider " + ProviderId(provider) + ": " + rollbackException.Message); }
                }
                try { world.RestoreState(state); }
                catch (Exception restoreException) { rollbackErrors.Add("framework state: " + restoreException.Message); }
                if (mapCreationIntentStarted)
                {
                    try
                    {
                        world.ClearMapCreationIntent(transactionId);
                        world.Quarantine("map-creation-intent", transactionId,
                            request.adjacentMap?.providerId ?? "core",
                            "Adjacent map materialization intent rolled back: " + exception.Message,
                            request.regionId.ToString());
                    }
                    catch (Exception intentException) { rollbackErrors.Add("map creation intent: " + intentException.Message); }
                }
                if (createdMap != null && selectedFactory != null)
                {
                    try { selectedFactory.RemoveMap(createdMap); }
                    catch (Exception rollbackException) { rollbackErrors.Add("map removal: " + rollbackException.Message); }
                }
                result.error = exception.Message;
                result.originalError = exception.Message;
                result.rolledBack = true;
                result.rollbackErrors = rollbackErrors;
                RealityMaterializationService.RememberRollbackFailures(rollbackErrors);
                result.vetoes = plan.Vetoes.Count > 0 ? plan.Vetoes.ToList() :
                    new[] { new RealityVeto("materialization.rollback", exception.Message, "core", 3) };
                result.steps = plan.Steps.ToList();
                return result;
            }
        }

        private sealed class TransactionalAnchorStage
        {
            internal readonly ITransactionalAnchorProvider Provider;
            internal readonly RealityAnchorRecord Record;

            internal TransactionalAnchorStage(ITransactionalAnchorProvider provider, RealityAnchorRecord record)
            {
                Provider = provider;
                Record = record;
            }
        }

        private static string ProviderId(object provider)
        {
            if (provider is IRealityProvider realityProvider)
                return realityProvider.Registration?.providerId ?? provider.GetType().FullName ?? string.Empty;
            if (provider is IRealityProviderProviderId id) return id.ProviderId;
            return provider?.GetType().FullName ?? string.Empty;
        }

        private static void ReportCommitNotificationFailure(DeferredRealityWorldComponent world, string recordType,
            string providerId, Exception exception)
        {
            string diagnostic = "A committed materialization notification failed: " + exception.Message;
            try { world.Quarantine(recordType, null, providerId ?? "core", diagnostic, exception.GetType().FullName); }
            catch { Log.Error("[DeferredReality] " + diagnostic); }
        }

        private static bool AppliesToRequest(IMaterializationProvider provider, RealityMaterializationRequest request)
        {
            if (!(provider is IRealityProvider owner)) return true;
            string ownerId = owner.Registration?.providerId;
            if (string.IsNullOrEmpty(ownerId)) return false;
            string requestOwner = request == null || string.IsNullOrEmpty(request.providerId)
                ? request?.regionId.ProviderNamespace : request.providerId;
            return string.Equals(requestOwner, ownerId, StringComparison.Ordinal);
        }

        private static void AddConsistencyPlanVetoes(RealityMaterializationPlan plan, string providerId)
        {
            if (plan?.Consistency == null) return;
            foreach (RealityMaterializationConsistencyConflict conflict in plan.Consistency.Conflicts)
            {
                if (conflict == null) continue;
                plan.AddVeto(new RealityVeto("materialization.consistency-conflict",
                    conflict.reason + " (" + conflict.leftConstraintId + ", " + conflict.rightConstraintId + ")",
                    providerId, 3));
            }
            foreach (RealityObservationRecord observation in plan.Consistency.Observations)
            {
                if (observation == null || Enum.IsDefined(typeof(RealityObservationPrecision), observation.spatialPrecision)) continue;
                plan.AddVeto(new RealityVeto("materialization.observation-precision-invalid",
                    "Observation " + observation.observationId + " has an undefined spatial precision.", providerId, 3));
            }
            foreach (RealityConstraint constraint in plan.Consistency.Constraints)
            {
                if (constraint == null || Enum.IsDefined(typeof(RealityObservationPrecision), constraint.spatialPrecision)) continue;
                plan.AddVeto(new RealityVeto("materialization.constraint-precision-invalid",
                    "Constraint " + constraint.constraintId + " has an undefined spatial precision.", providerId, 3));
            }
        }

        internal static bool TryValidateFidelityContract(DeferredRealityWorldComponent world,
            RealityRegionSnapshot region, string providerId, RealityFidelity target,
            RealityFidelityTransitionMechanism mechanism, out string diagnostic)
        {
            diagnostic = null;
            if (world == null || region == null || string.IsNullOrEmpty(providerId) ||
                !RealityProviderRegistry.TryGetCapability(providerId, out IRealityFidelityProvider provider))
            {
                diagnostic = "The owning provider does not expose a fidelity contract.";
                return false;
            }
            try
            {
                RealityFidelityContract contract = provider.DescribeFidelity(
                    new RealityProviderContext(world, providerId, world.Now), region);
                if (contract == null || contract.currentFidelity != region.fidelity ||
                    !contract.Supports(region.fidelity) || !contract.Supports(target) ||
                    !contract.AllowsTransition(region.fidelity, target, mechanism))
                {
                    diagnostic = "The owning provider did not declare the requested fidelity transition.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                diagnostic = "The owning provider's fidelity contract failed: " + exception.Message;
                return false;
            }
        }

        private interface IRealityProviderProviderId
        {
            string ProviderId { get; }
        }
    }

    /// <summary>Conservative compression pipeline. It refuses generic state by default.</summary>
    public static class RealityCompressionService
    {
        /// <summary>Collects structured vetoes without removing a Map.</summary>
        public static IReadOnlyList<RealityVeto> CanCompress(DeferredRealityWorldComponent world, RealityCompressionRequest request)
        {
            RealityThreadGuard.RequireMainThread();
            var vetoes = new List<RealityVeto>();
            IReadOnlyList<ICompressionProvider> providers = SelectProviders(request, vetoes);
            AddCompressionVetoes(world, request, providers, vetoes);
            return vetoes;
        }

        private static void AddCompressionVetoes(DeferredRealityWorldComponent world, RealityCompressionRequest request,
            IReadOnlyList<ICompressionProvider> providers, IList<RealityVeto> vetoes)
        {
            if (request == null || request.Map == null)
                vetoes.Add(new RealityVeto("compression.no-map", "No active map was supplied.", "core", 2));
            else if (world == null || !request.regionId.IsValid ||
                !world.TryGetRegion(request.regionId, out RealityRegionSnapshot region))
                vetoes.Add(new RealityVeto("compression.no-region", "The map has no registered region identity.", "core", 3));
            else if (region.authority != RealityRegionAuthority.LiveProjection ||
                region.projectionMapUniqueId != request.Map.uniqueID)
                vetoes.Add(new RealityVeto("compression.not-authoritative",
                    "Compression requires the requested Map to be the region's live projection.", "core", 3));
            else if (!RealityMaterializationService.TryValidateFidelityContract(world, region,
                string.IsNullOrEmpty(request.providerId) ? request.regionId.ProviderNamespace : request.providerId,
                RealityFidelity.Statistical, RealityFidelityTransitionMechanism.Compression,
                out string fidelityDiagnostic))
                vetoes.Add(new RealityVeto("compression.fidelity-contract", fidelityDiagnostic, request.providerId, 2));
            else if (providers.Count > 0)
            {
                foreach (Pawn pawn in request.Map.mapPawns?.AllPawnsSpawned ?? Enumerable.Empty<Pawn>())
                {
                    if (pawn == null) continue;
                    if (pawn.Drafted) vetoes.Add(new RealityVeto("compression.drafted-pawn", "A drafted pawn is active on the map.", "core", 3));
                    if (pawn.InMentalState) vetoes.Add(new RealityVeto("compression-mental-state", "A pawn is in a mental state.", "core", 3));
                    if (pawn.GetLord() != null) vetoes.Add(new RealityVeto("compression-lord", "A pawn belongs to a Lord.", "core", 3));
                    if (pawn.IsColonistPlayerControlled) vetoes.Add(new RealityVeto("compression-player-pawn", "A player-controlled pawn cannot be captured.", "core", 3));
                }
            }
            if (world != null && request != null && request.regionId.IsValid)
            {
                string owner = string.IsNullOrEmpty(request.providerId) ? request.regionId.ProviderNamespace : request.providerId;
                foreach (RealityVeto veto in RealityCompressionPreservation.Validate(world, request.regionId, owner))
                    vetoes.Add(veto);
            }
            foreach (ICompressionProvider provider in providers)
            {
                try { provider.CanCompress(request, vetoes); }
                catch (Exception exception) { vetoes.Add(new RealityVeto("compression.provider-exception", exception.Message, ProviderId(provider), 3)); }
            }
        }

        /// <summary>Attempts provider-backed compression only when every veto is absent.</summary>
        public static RealityTransitionResult TryCompress(DeferredRealityWorldComponent world, RealityCompressionRequest request)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new RealityTransitionResult { regionId = request?.regionId ?? default(RealityRegionId) };
            if (world == null)
            {
                result.error = "A valid world is required for compression.";
                result.originalError = result.error;
                result.vetoes = new[] { new RealityVeto("compression.no-world", result.error, "core", 3) };
                return result;
            }
            var vetoes = new List<RealityVeto>();
            IReadOnlyList<ICompressionProvider> providers = SelectProviders(request, vetoes);
            AddCompressionVetoes(world, request, providers, vetoes);
            result.vetoes = vetoes;
            if (vetoes.Count > 0) { result.error = "Compression was vetoed."; result.originalError = result.error; return result; }
            RealityWorldState state = world.CaptureState();
            var preparedProviders = new List<ICompressionProvider>();
            try
            {
                if (!world.BeginCompression(request.regionId))
                    throw new RealityTransitionException("The region could not enter compression authority.");
                foreach (ICompressionProvider provider in providers)
                {
                    // Compression Prepare may partially mutate before throwing; rollback is idempotent and must see it.
                    preparedProviders.Add(provider);
                    provider.Prepare(request);
                }
                var validationVetoes = new List<RealityVeto>();
                foreach (ICompressionProvider provider in preparedProviders) provider.Validate(request, validationVetoes);
                if (validationVetoes.Count > 0) throw new RealityTransitionException(string.Join("; ", validationVetoes.Select(item => item.ToString()).ToArray()));
                foreach (ICompressionProvider provider in preparedProviders) provider.Commit(request);
                if (!world.CommitCompression(request.regionId, request.now))
                    throw new RealityTransitionException("The region could not reconcile its live projection into latent state.");
                world.Touch("region.projection-compressed", "core", request.regionId.ToString(), request.reason);
                result.succeeded = true;
                return result;
            }
            catch (Exception exception)
            {
                var rollbackErrors = new List<string>();
                foreach (ICompressionProvider provider in RealityTransitionPolicy.ReversePrepared(preparedProviders))
                {
                    try { provider.Rollback(request); }
                    catch (Exception rollbackException) { rollbackErrors.Add(ProviderId(provider) + ": " + rollbackException.Message); }
                }
                try { world.RestoreState(state); }
                catch (Exception restoreException) { rollbackErrors.Add("framework state: " + restoreException.Message); }
                result.error = exception.Message;
                result.originalError = exception.Message;
                result.rollbackErrors = rollbackErrors;
                RealityMaterializationService.RememberRollbackFailures(rollbackErrors);
                result.rolledBack = true;
                result.vetoes = new[] { new RealityVeto("compression.rollback", exception.Message, "core", 3) };
                return result;
            }
        }

        /// <summary>Compensates a successful compression when an outer map eviction cannot remove the live map.</summary>
        internal static IReadOnlyList<string> RollbackCommitted(DeferredRealityWorldComponent world,
            RealityCompressionRequest request, RealityWorldState state)
        {
            var errors = new List<string>();
            IReadOnlyList<ICompressionProvider> providers = SelectProviders(request, new List<RealityVeto>());
            foreach (ICompressionProvider provider in RealityTransitionPolicy.ReversePrepared(providers.ToList()))
            {
                try { provider.Rollback(request); }
                catch (Exception exception) { errors.Add(ProviderId(provider) + ": " + exception.Message); }
            }
            try { if (world != null && state != null) world.RestoreState(state); }
            catch (Exception exception) { errors.Add("framework state: " + exception.Message); }
            RealityMaterializationService.RememberRollbackFailures(errors);
            return errors;
        }

        private static IReadOnlyList<ICompressionProvider> SelectProviders(RealityCompressionRequest request, IList<RealityVeto> vetoes)
        {
            string owner = request == null ? null : (string.IsNullOrEmpty(request.providerId)
                ? request.regionId.ProviderNamespace : request.providerId);
            List<ICompressionProvider> providers = RealityTransitionPolicy.SelectOwner(
                RealityProviderRegistry.OfType<ICompressionProvider>(), owner, ProviderId, provider => provider.Order).ToList();
            if (string.IsNullOrEmpty(owner) || providers.Count == 0)
            {
                vetoes?.Add(new RealityVeto("compression.generic-disabled",
                    "No provider owns a safe compression projection for this region.", "core", 2));
            }
            return providers;
        }

        private static string ProviderId(object provider)
        {
            return (provider as IRealityProvider)?.Registration?.providerId ?? provider?.GetType().FullName ?? string.Empty;
        }
    }

    internal sealed class RealityTransitionException : Exception
    {
        internal RealityTransitionException(string message) : base(message) { }
    }
}
