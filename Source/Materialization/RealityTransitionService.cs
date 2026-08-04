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
        private static IRealityMapFactory fallbackFactory;

        /// <summary>Optional legacy fallback factory. Null means framework-only planning is available.</summary>
        public static IRealityMapFactory Factory
        {
            get { return fallbackFactory; }
            set
            {
                RealityThreadGuard.RequireMainThread();
                fallbackFactory = value;
            }
        }

        /// <summary>Registers a factory for one provider namespace without taking the legacy fallback slot.</summary>
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

        /// <summary>Resolves a provider-scoped factory, then the legacy fallback.</summary>
        public static bool TryGet(string providerId, out IRealityMapFactory factory)
        {
            if (!string.IsNullOrWhiteSpace(providerId) && ProviderFactories.TryGetValue(providerId.Trim(), out factory)) return true;
            factory = fallbackFactory;
            return factory != null;
        }
    }

    /// <summary>Transactional materialization of latent regions.</summary>
    public static class RealityMaterializationService
    {
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
            var plan = new RealityMaterializationPlan();
            plan.TargetAnchorId = request.targetAnchorId;
            plan.AddStep("validate-region");
            Map activeMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID == region.activeMapUniqueId);
            plan.ActiveMap = activeMap;
            if (activeMap != null) plan.AddStep("reuse-active-map");
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
            Map createdMap = null;
            IRealityMapFactory selectedFactory = null;
            HashSet<int> liveMapIdsBeforeFactory = new HashSet<int>((Find.Maps ?? Enumerable.Empty<Map>())
                .Where(item => item != null).Select(item => item.uniqueID));
            string transactionId = "materialize:" + request.regionId + ":" + request.now + ":" + (request.targetAnchorId ?? string.Empty);
            var context = new RealityMaterializationContext(world, request, plan, activeMap, transactionId);
            List<IMaterializationProvider> preparedProviders = new List<IMaterializationProvider>();
            var providerStages = new Dictionary<IMaterializationProvider, RealityMaterializationStage>();
            List<TransactionalAnchorStage> preparedAnchors = new List<TransactionalAnchorStage>();
            List<TransactionalAnchorStage> appliedAnchors = new List<TransactionalAnchorStage>();
            IReadOnlyList<RealityAnchorSnapshot> anchors = world.AnchorSnapshots(request.regionId.ToString())
                .Where(item => item?.record != null).OrderBy(item => item.record.anchorId, StringComparer.Ordinal).ToList();
            try
            {
                context.MarkStage(RealityMaterializationStage.Preparation);
                foreach (IMaterializationProvider provider in providers)
                {
                    try
                    {
                        providerStages[provider] = RealityMaterializationStage.Preparation;
                        provider.Prepare(request, plan);
                        preparedProviders.Add(provider);
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
                    string factoryProviderId = string.IsNullOrEmpty(request.providerId)
                        ? request.regionId.ProviderNamespace : request.providerId;
                    if (!RealityMapFactoryRegistry.TryGet(factoryProviderId, out selectedFactory))
                        throw new RealityTransitionException("No map factory is registered for this region.");
                    if (request.adjacentMap != null &&
                        !string.Equals(request.adjacentMap.providerId, factoryProviderId, StringComparison.Ordinal))
                        throw new RealityTransitionException("The adjacent map owner does not match the selected map factory.");
                    context.MarkStage(RealityMaterializationStage.MapAcquisition);
                    bool factorySucceeded = selectedFactory.TryCreateMap(request.regionId, plan, out Map factoryMap, out string diagnostic);
                    if (factoryMap != null && !liveMapIdsBeforeFactory.Contains(factoryMap.uniqueID)) createdMap = factoryMap;
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
                descriptor.fidelity = RealityFidelity.Materialized;
                descriptor.lifecycle = RealityLifecycleState.Active;
                descriptor.activeMapUniqueId = plan.ActiveMap.uniqueID;
                descriptor.lastMaterializedTick = request.now;
                descriptor.lastUpdateTick = request.now;
                world.Touch("region.materialized", "core", descriptor.regionId, request.reason);
                // Legacy callbacks are notifications after the durable region commit. They must not
                // turn an otherwise committed transition into a rollback-capable mutation.
                context.MarkStage(RealityMaterializationStage.LegacyAnchorCommit);
                foreach (IAnchorProvider anchorProvider in RealityProviderRegistry.OfType<IAnchorProvider>()
                    .Where(item => !(item is ITransactionalAnchorProvider)))
                {
                    string anchorProviderId = (anchorProvider as IRealityProvider)?.Registration?.providerId;
                    foreach (RealityAnchorSnapshot anchor in anchors)
                    {
                        if (!string.IsNullOrEmpty(anchorProviderId) && anchor.record.providerId != anchorProviderId) continue;
                        try
                        {
                            anchorProvider.OnAnchorMaterialized(new RealityProviderContext(world, anchor.record.providerId, request.now), anchor.record);
                        }
                        catch (Exception notificationException)
                        {
                            ReportCommitNotificationFailure(world, "legacy-anchor", anchorProviderId,
                                notificationException);
                        }
                    }
                }
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
                if (createdMap != null && selectedFactory != null)
                {
                    try { selectedFactory.RemoveMap(createdMap); }
                    catch (Exception rollbackException) { rollbackErrors.Add("map removal: " + rollbackException.Message); }
                }
                result.error = exception.Message;
                result.originalError = exception.Message;
                result.rolledBack = true;
                result.rollbackErrors = rollbackErrors;
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
                foreach (ICompressionProvider provider in providers)
                {
                    provider.Prepare(request);
                    preparedProviders.Add(provider);
                }
                var validationVetoes = new List<RealityVeto>();
                foreach (ICompressionProvider provider in preparedProviders) provider.Validate(request, validationVetoes);
                if (validationVetoes.Count > 0) throw new RealityTransitionException(string.Join("; ", validationVetoes.Select(item => item.ToString()).ToArray()));
                foreach (ICompressionProvider provider in preparedProviders) provider.Commit(request);
                if (world.TryGetRegion(request.regionId, out RealityRegionSnapshot region))
                {
                    RealityRegionDescriptor descriptor = world.RegionRecord(request.regionId.ToString());
                    descriptor.fidelity = RealityFidelity.Statistical;
                    descriptor.activeMapUniqueId = -1;
                    descriptor.lastUpdateTick = request.now;
                    world.Touch("region.compressed", "core", descriptor.regionId, request.reason);
                }
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
