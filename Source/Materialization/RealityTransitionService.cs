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
        /// <summary>Optional active factory. Null means framework-only planning is available.</summary>
        public static IRealityMapFactory Factory { get; set; }
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
            plan.AddStep("validate-region");
            Map activeMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID == region.activeMapUniqueId);
            plan.ActiveMap = activeMap;
            if (activeMap != null) plan.AddStep("reuse-active-map");
            List<IMaterializationProvider> providers = RealityProviderRegistry.OfType<IMaterializationProvider>()
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
            bool committedMutation = false;
            var context = new RealityMaterializationContext(world, request, plan, activeMap);
            try
            {
                if (plan.ActiveMap == null)
                {
                    if (RealityMapFactoryRegistry.Factory == null)
                    {
                        result.error = "No map factory is registered for this region.";
                        result.vetoes = new[] { new RealityVeto("materialization.map-factory-unavailable", "The framework does not create maps implicitly.", "core", 2) };
                        result.steps = plan.Steps.ToList();
                        return result;
                    }
                    if (!RealityMapFactoryRegistry.Factory.TryCreateMap(request.regionId, plan, out createdMap, out string diagnostic) || createdMap == null)
                    {
                        result.error = diagnostic ?? "The host map factory could not create a normal Map.";
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
                RealityConstraintService.Resolve(world, request.regionId, request.now);
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
                if (createdMap != null && RealityMapFactoryRegistry.Factory != null)
                {
                    try { RealityMapFactoryRegistry.Factory.RemoveMap(createdMap); } catch (Exception rollbackException) { Log.Error("Deferred Reality map removal rollback failed: " + rollbackException); }
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
            if (provider is IRealityProviderProviderId id) return id.ProviderId;
            return provider?.GetType().FullName ?? string.Empty;
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
                vetoes.Add(new RealityVeto("compression.generic-disabled", "Generic Map compression is not safe in framework v1.", "core", 2));
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
