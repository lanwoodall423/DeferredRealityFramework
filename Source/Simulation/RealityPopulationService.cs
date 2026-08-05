using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;

namespace DeferredReality.Simulation
{
    /// <summary>Result of an atomic aggregate population mutation.</summary>
    public sealed class RealityPopulationMutationResult
    {
        public bool succeeded;
        public bool duplicate;
        public float before;
        public float after;
        public string error;
    }

    /// <summary>Generic population ledger with exactly-once operation support.</summary>
    public static class RealityPopulationService
    {
        /// <summary>Builds the canonical population key from semantic components.</summary>
        public static string PopulationId(string providerId, string kind, RealityRegionId regionId, string subjectId)
        {
            return (providerId ?? string.Empty) + ":" + (kind ?? string.Empty) + ":" + regionId + ":" + (subjectId ?? string.Empty);
        }

        /// <summary>Creates or updates one aggregate population.</summary>
        public static bool Register(DeferredRealityWorldComponent world, RealityPopulationRecord record)
        {
            if (world == null) return false;
            return world.UpsertPopulation(record);
        }

        /// <summary>Consumes an exact amount once. Replaying the same operation ID is a no-op duplicate.</summary>
        public static RealityPopulationMutationResult Consume(DeferredRealityWorldComponent world, string populationId,
            float amount, string operationId, long now, string providerId = null)
        {
            return Mutate(world, populationId, -Math.Abs(amount), operationId, now, providerId, "consume");
        }

        /// <summary>Adds or releases an exact amount once.</summary>
        public static RealityPopulationMutationResult Release(DeferredRealityWorldComponent world, string populationId,
            float amount, string operationId, long now, string providerId = null)
        {
            return Mutate(world, populationId, Math.Abs(amount), operationId, now, providerId, "release");
        }

        /// <summary>Applies reproduction or mortality as one exactly-once aggregate operation.</summary>
        public static RealityPopulationMutationResult ReproduceOrDie(DeferredRealityWorldComponent world, string populationId,
            float births, float deaths, string operationId, long now, string providerId = null)
        {
            return Mutate(world, populationId, births - deaths, operationId, now, providerId, "demography");
        }

        /// <summary>Transfers an aggregate amount between connected populations atomically.</summary>
        public static RealityPopulationMutationResult Transfer(DeferredRealityWorldComponent world, string sourcePopulationId,
            string destinationPopulationId, float amount, string operationId, long now, string providerId = null)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new RealityPopulationMutationResult();
            if (world == null || string.IsNullOrEmpty(sourcePopulationId) || string.IsNullOrEmpty(destinationPopulationId) || amount <= 0f)
            {
                result.error = "Invalid transfer arguments.";
                return result;
            }
            if (world.HasAppliedOperation(operationId)) { result.duplicate = true; result.succeeded = true; return result; }
            RealityPopulationRecord source = world.PopulationRecord(sourcePopulationId)?.Clone();
            RealityPopulationRecord destination = world.PopulationRecord(destinationPopulationId)?.Clone();
            if (source == null || destination == null) { result.error = "Source or destination population is missing."; return result; }
            if (!string.Equals(source.providerId, destination.providerId, StringComparison.Ordinal) ||
                (providerId != null && !string.Equals(providerId, source.providerId, StringComparison.Ordinal)))
            {
                result.error = "Population transfer crosses provider ownership.";
                return result;
            }
            if (!source.migrationAllowed || !destination.migrationAllowed) { result.error = "Population migration is disabled."; return result; }
            if (RealityProviderRegistry.TryGet(source.providerId, out IRealityProvider registered) && registered is IPopulationProvider populationProvider)
            {
                var vetoes = new List<RealityVeto>();
                bool allowed;
                try { allowed = populationProvider.CanChangePopulation(source.Clone(), "transfer", vetoes); }
                catch (Exception exception)
                {
                    allowed = false;
                    vetoes.Add(new RealityVeto("provider.exception", exception.Message, source.providerId, 3));
                }
                if (!allowed || vetoes.Count > 0)
                {
                    result.error = vetoes.Count > 0
                        ? string.Join("; ", vetoes.Select(item => item.ToString()).ToArray())
                        : "Provider vetoed population transfer.";
                    return result;
                }
            }
            if (source.amount < amount) { result.error = "Source population cannot satisfy the exact transfer."; return result; }
            RealityWorldState state = world.CaptureState();
            source.amount -= amount;
            destination.amount += amount;
            source.lastUpdateTick = destination.lastUpdateTick = now;
            source.extinct = source.amount <= 0f;
            destination.extinct = false;
            if (!world.UpsertPopulation(source) || !world.UpsertPopulation(destination) ||
                !world.RecordAppliedOperation(operationId, providerId, "transfer", now,
                    source.populationId + "->" + destination.populationId))
            {
                world.RestoreState(state);
                result.error = "Transfer transaction failed and was rolled back.";
                return result;
            }
            world.UpsertOperationRetentionWatermark(new RealityOperationRetentionWatermark
            {
                providerId = providerId,
                kind = "transfer",
                domainId = source.populationId + "->" + destination.populationId,
                safeThroughTick = now,
                proof = "population-transfer-state-committed:" + operationId,
                updatedTick = now
            });
            result.succeeded = true;
            result.before = source.amount + amount;
            result.after = source.amount;
            return result;
        }

        /// <summary>Records a player or sensor estimate without changing objective amount.</summary>
        public static bool RecordEstimate(DeferredRealityWorldComponent world, RealityObservationInput input)
        {
            return world != null && world.AddObservation(input);
        }

        /// <summary>Reconciles a provider's active-map change through an exactly-once aggregate delta.</summary>
        public static RealityPopulationMutationResult ReconcileActiveMap(DeferredRealityWorldComponent world, string populationId,
            float delta, string operationId, long now, string providerId)
        {
            return Mutate(world, populationId, delta, operationId, now, providerId, "active-map-reconcile");
        }

        private static RealityPopulationMutationResult Mutate(DeferredRealityWorldComponent world, string populationId,
            float delta, string operationId, long now, string providerId, string kind)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new RealityPopulationMutationResult();
            if (world == null || string.IsNullOrEmpty(populationId) || string.IsNullOrEmpty(operationId) || float.IsNaN(delta) || float.IsInfinity(delta))
            {
                result.error = "Invalid population mutation arguments.";
                return result;
            }
            if (world.HasAppliedOperation(operationId)) { result.duplicate = true; result.succeeded = true; return result; }
            RealityPopulationRecord record = world.PopulationRecord(populationId)?.Clone();
            if (record == null) { result.error = "Population is missing."; return result; }
            string effectiveProvider = providerId ?? record.providerId;
            if (RealityProviderRegistry.TryGet(record.providerId, out IRealityProvider registered) && registered is IPopulationProvider populationProvider)
            {
                var vetoes = new List<RealityVeto>();
                bool allowed;
                try { allowed = populationProvider.CanChangePopulation(record.Clone(), kind, vetoes); }
                catch (Exception exception) { allowed = false; vetoes.Add(new RealityVeto("provider.exception", exception.Message, record.providerId, 3)); }
                if (!allowed || vetoes.Count > 0)
                {
                    result.error = vetoes.Count > 0 ? string.Join("; ", vetoes.Select(item => item.ToString()).ToArray()) : "Provider vetoed population change.";
                    return result;
                }
            }
            result.before = record.amount;
            float next = record.amount + delta;
            if (next < 0f) { result.error = "Population cannot become negative."; return result; }
            record.amount = next;
            record.extinct = next <= 0f;
            record.established = record.established || next > 0f;
            record.lastUpdateTick = now;
            RealityWorldState state = world.CaptureState();
            if (!world.UpsertPopulation(record) || !world.RecordAppliedOperation(operationId, effectiveProvider, kind, now,
                record.populationId))
            {
                world.RestoreState(state);
                result.error = "Population mutation failed and was rolled back.";
                return result;
            }
            world.UpsertOperationRetentionWatermark(new RealityOperationRetentionWatermark
            {
                providerId = effectiveProvider,
                kind = kind,
                domainId = record.populationId,
                safeThroughTick = now,
                proof = "population-state-committed:" + operationId,
                updatedTick = now
            });
            if (kind == "active-map-reconcile" && RealityProviderRegistry.TryGet(record.providerId, out IRealityProvider owner) &&
                owner is IPopulationProvider activeMapProvider)
            {
                try { activeMapProvider.ReconcileActiveMap(new RealityProviderContext(world, record.providerId, now), record.Clone(), operationId); }
                catch (Exception exception) { world.Quarantine("population-reconcile", operationId, record.providerId, exception.Message); }
            }
            result.after = next;
            result.succeeded = true;
            return result;
        }
    }
}
