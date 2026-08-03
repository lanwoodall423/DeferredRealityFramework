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
            set { fallbackFactory = value; }
        }

        /// <summary>Registers a factory for one provider namespace without taking the legacy fallback slot.</summary>
        public static bool Register(string providerId, IRealityMapFactory factory)
        {
            if (string.IsNullOrWhiteSpace(providerId) || factory == null) return false;
            ProviderFactories[providerId.Trim()] = factory;
            return true;
        }

        /// <summary>Removes a provider-scoped factory if it is the registered instance.</summary>
        public static bool Unregister(string providerId, IRealityMapFactory factory = null)
        {
            if (string.IsNullOrWhiteSpace(providerId) || !ProviderFactories.TryGetValue(providerId.Trim(), out IRealityMapFactory current)) return false;
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
                result.vetoes = plan.Vetoes.ToList();
                result.steps = plan.Steps.ToList();
                return result;
            }
            RealityWorldState state = world.CaptureState();
            foreach (IMaterializationProvider provider in providers)
            {
                try { provider.Prepare(request, plan); }
                catch (Exception exception)
                {
                    plan.AddVeto(new RealityVeto("materialization.prepare-failed", exception.Message, ProviderId(provider), 3));
                    break;
                }
            }
            if (plan.Vetoes.Count > 0)
            {
                world.RestoreState(state);
                result.error = "Materialization preparation failed.";
                result.rolledBack = true;
                result.vetoes = plan.Vetoes.ToList();
                result.steps = plan.Steps.ToList();
                return result;
            }
            Map createdMap = null;
            IRealityMapFactory selectedFactory = null;
            bool committedMutation = false;
            var context = new RealityMaterializationContext(world, request, plan, activeMap);
            try
            {
                if (plan.ActiveMap == null)
                {
                    string factoryProviderId = string.IsNullOrEmpty(request.providerId)
                        ? request.regionId.ProviderNamespace : request.providerId;
                    if (!RealityMapFactoryRegistry.TryGet(factoryProviderId, out selectedFactory))
                    {
                        world.RestoreState(state);
                        result.error = "No map factory is registered for this region.";
                        result.rolledBack = true;
                        result.vetoes = new[] { new RealityVeto("materialization.map-factory-unavailable", "The framework does not create maps implicitly.", "core", 2) };
                        result.steps = plan.Steps.ToList();
                        return result;
                    }
                    if (!selectedFactory.TryCreateMap(request.regionId, plan, out createdMap, out string diagnostic) || createdMap == null)
                    {
                        world.RestoreState(state);
                        result.error = diagnostic ?? "The host map factory could not create a normal Map.";
                        result.rolledBack = true;
                        result.vetoes = new[] { new RealityVeto("materialization.map-create-failed", result.error, "core", 3) };
                        result.steps = plan.Steps.ToList();
                        return result;
                    }
                    plan.ActiveMap = createdMap;
                    context = new RealityMaterializationContext(world, request, plan, createdMap);
                    plan.AddStep("acquire-map");
                }
                foreach (IMaterializationProvider provider in providers) provider.Apply(context);
                plan.AddStep("provider-plans-applied");
                RealityConstraintResolutionResult constraintResult = RealityConstraintService.Resolve(world, request.regionId, request.now);
                if (constraintResult.conflicted > 0)
                    throw new RealityTransitionException("Materialization has unresolved historical constraint conflicts.");
                plan.AddStep("constraints-resolved");
                foreach (IAnchorProvider anchorProvider in RealityProviderRegistry.OfType<IAnchorProvider>())
                {
                    string anchorProviderId = (anchorProvider as IRealityProvider)?.Registration?.providerId;
                    foreach (RealityAnchorSnapshot anchor in world.AnchorSnapshots(request.regionId.ToString()))
                    {
                        if (!string.IsNullOrEmpty(anchorProviderId) && anchor.record.providerId != anchorProviderId) continue;
                        var vetoes = new List<RealityVeto>();
                        if (!anchorProvider.ValidateAnchor(anchor.record, vetoes) || vetoes.Count > 0)
                            throw new RealityTransitionException("Anchor validation failed: " + string.Join("; ", vetoes.Select(item => item.ToString()).ToArray()));
                        anchorProvider.OnAnchorMaterialized(new RealityProviderContext(world, anchor.record.providerId, request.now), anchor.record);
                    }
                }
                var validationVetoes = new List<RealityVeto>();
                foreach (IMaterializationProvider provider in providers) provider.Validate(context, validationVetoes);
                if (validationVetoes.Count > 0) throw new RealityTransitionException(string.Join("; ", validationVetoes.Select(item => item.ToString()).ToArray()));
                RealityRegionDescriptor descriptor = world.RegionRecord(request.regionId.ToString());
                if (descriptor == null) throw new RealityTransitionException("Region disappeared during materialization.");
                descriptor.fidelity = RealityFidelity.Materialized;
                descriptor.lifecycle = RealityLifecycleState.Active;
                descriptor.activeMapUniqueId = plan.ActiveMap.uniqueID;
                descriptor.lastMaterializedTick = request.now;
                descriptor.lastUpdateTick = request.now;
                world.Touch("region.materialized", "core", descriptor.regionId, request.reason);
                committedMutation = true;
                result.succeeded = true;
                result.steps = plan.Steps.ToList();
                return result;
            }
            catch (Exception exception)
            {
                for (int i = providers.Count - 1; i >= 0; i--)
                {
                    try { providers[i].Rollback(context); } catch (Exception rollbackException) { Log.Error("Deferred Reality materialization rollback failed: " + rollbackException); }
                }
                world.RestoreState(state);
                if (createdMap != null && selectedFactory != null)
                {
                    try { selectedFactory.RemoveMap(createdMap); } catch (Exception rollbackException) { Log.Error("Deferred Reality map removal rollback failed: " + rollbackException); }
                }
                result.error = exception.Message;
                result.rolledBack = !committedMutation;
                result.vetoes = new[] { new RealityVeto("materialization.rollback", exception.Message, "core", 3) };
                result.steps = plan.Steps.ToList();
                return result;
            }
        }

        private static string ProviderId(object provider)
        {
            if (provider is IRealityProvider realityProvider)
                return realityProvider.Registration?.providerId ?? provider.GetType().FullName ?? string.Empty;
            if (provider is IRealityProviderProviderId id) return id.ProviderId;
            return provider?.GetType().FullName ?? string.Empty;
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
            if (request == null || request.Map == null) vetoes.Add(new RealityVeto("compression.no-map", "No active map was supplied.", "core", 2));
            else
            {
                bool providerScoped = RealityProviderRegistry.OfType<IRealityProvider>()
                    .Any(provider => provider.Registration?.providerId == request.regionId.ProviderNamespace && provider is ICompressionProvider);
                if (!providerScoped)
                    vetoes.Add(new RealityVeto("compression.generic-disabled", "No provider owns a safe compression projection for this region.", "core", 2));
                foreach (Pawn pawn in request.Map.mapPawns?.AllPawnsSpawned ?? Enumerable.Empty<Pawn>())
                {
                    if (pawn == null) continue;
                    if (pawn.Drafted) vetoes.Add(new RealityVeto("compression.drafted-pawn", "A drafted pawn is active on the map.", "core", 3));
                    if (pawn.InMentalState) vetoes.Add(new RealityVeto("compression-mental-state", "A pawn is in a mental state.", "core", 3));
                    if (pawn.GetLord() != null) vetoes.Add(new RealityVeto("compression-lord", "A pawn belongs to a Lord.", "core", 3));
                    if (pawn.IsColonistPlayerControlled) vetoes.Add(new RealityVeto("compression-player-pawn", "A player-controlled pawn cannot be captured.", "core", 3));
                }
            }
            foreach (ICompressionProvider provider in RealityProviderRegistry.OfType<ICompressionProvider>())
            {
                try { provider.CanCompress(request, vetoes); }
                catch (Exception exception) { vetoes.Add(new RealityVeto("compression.provider-exception", exception.Message, provider.GetType().FullName, 3)); }
            }
            return vetoes;
        }

        /// <summary>Attempts provider-backed compression only when every veto is absent.</summary>
        public static RealityTransitionResult TryCompress(DeferredRealityWorldComponent world, RealityCompressionRequest request)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new RealityTransitionResult { regionId = request?.regionId ?? default(RealityRegionId) };
            IReadOnlyList<RealityVeto> vetoes = CanCompress(world, request);
            result.vetoes = vetoes;
            if (vetoes.Count > 0) { result.error = "Compression was vetoed."; return result; }
            RealityWorldState state = world.CaptureState();
            List<ICompressionProvider> providers = RealityProviderRegistry.OfType<ICompressionProvider>().OrderBy(item => item.Order).ToList();
            try
            {
                foreach (ICompressionProvider provider in providers) provider.Prepare(request);
                var validationVetoes = new List<RealityVeto>();
                foreach (ICompressionProvider provider in providers) provider.Validate(request, validationVetoes);
                if (validationVetoes.Count > 0) throw new RealityTransitionException(string.Join("; ", validationVetoes.Select(item => item.ToString()).ToArray()));
                foreach (ICompressionProvider provider in providers) provider.Commit(request);
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
                for (int i = providers.Count - 1; i >= 0; i--) try { providers[i].Rollback(request); } catch (Exception rollbackException) { Log.Error("Deferred Reality compression rollback failed: " + rollbackException); }
                world.RestoreState(state);
                result.error = exception.Message;
                result.rolledBack = true;
                result.vetoes = new[] { new RealityVeto("compression.rollback", exception.Message, "core", 3) };
                return result;
            }
        }
    }

    internal sealed class RealityTransitionException : Exception
    {
        internal RealityTransitionException(string message) : base(message) { }
    }
}
