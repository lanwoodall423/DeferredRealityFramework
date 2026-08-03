using System;
using System.Collections.Generic;
using System.Linq;
using AquacultureFishing;
using DeferredReality.API;
using DeferredReality.Runtime;
using DeferredReality.Simulation;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DeferredReality.Aquaculture
{
    /// <summary>Natural-water adapter. Constructed ponds and individual fish remain Aquaculture-owned.</summary>
    public sealed class AquacultureRealityProvider : IRealityProvider, IRealityProcessProvider,
        IPopulationProvider, IRealityDiagnosticsProvider
    {
        public const string ProviderId = "lan.aquaculture.natural-water";
        private DeferredRealityWorldComponent attachedWorld;
        private IDisposable eventSubscription;

        public RealityProviderRegistration Registration { get; } = new RealityProviderRegistration
        {
            providerId = ProviderId,
            displayName = "Aquaculture natural water",
            semanticApiVersion = 1,
            schemaVersion = 1,
            order = 200,
            capabilities = RealityProviderCapability.Regions | RealityProviderCapability.Populations |
                RealityProviderCapability.Processes | RealityProviderCapability.Observations | RealityProviderCapability.Diagnostics
        };

        public void OnRegistered(RealityProviderContext context)
        {
            if (context?.World == null || attachedWorld == context.World) return;
            if (eventSubscription != null) eventSubscription.Dispose();
            attachedWorld = context.World;
            eventSubscription = RealityEventBus.Subscribe(HandleFrameworkEvent);
            foreach (Map map in Find.Maps ?? Enumerable.Empty<Map>()) MigrateMap(map, context.World);
        }

        public bool IsOwned(Map map)
        {
            if (map == null || attachedWorld == null) return false;
            RealityRegionId region = attachedWorld.RegisterMap(map);
            return attachedWorld.IsMigrationCommitted(ProviderId, "natural-water:" + region, 1);
        }

        public void MigrateMap(Map map, DeferredRealityWorldComponent world = null)
        {
            if (map == null) return;
            if (!RealityThreadGuard.IsMainThread)
            {
                RealityMapLifecycle.RunOnMainThread(() => MigrateMap(map, world));
                return;
            }
            world = world ?? attachedWorld ?? DeferredRealityWorldComponent.Current;
            if (world == null) return;
            RealityRegionId region = world.RegisterMap(map);
            if (!region.IsValid) return;
            string consumer = "natural-water:" + region;
            NaturalFishPopulationMapComponent legacy = map.GetComponent<NaturalFishPopulationMapComponent>();
            if (legacy == null || legacy.Populations == null || legacy.Populations.Count == 0)
            {
                map.GetComponent<AquacultureDeferredProjectionMapComponent>();
                return;
            }
            if (world.IsMigrationCommitted(ProviderId, consumer, 1))
            {
                map.GetComponent<AquacultureDeferredProjectionMapComponent>();
                return;
            }
            try
            {
                foreach (NaturalWaterPopulation water in legacy.Populations.OrderBy(item => item.anchor.z).ThenBy(item => item.anchor.x))
                {
                    if (water == null || !water.anchor.IsValid) continue;
                    string waterId = WaterBodyId(region, water);
                    world.UpsertTopology(new RealityTopologyLink
                    {
                        linkId = "water-topology:" + waterId,
                        fromRegionId = region.ToString(),
                        toRegionId = region.ToString(),
                        kind = "water-body",
                        travelCost = 1f,
                        migrationFilter = water.habitat.ToString(),
                        metadata = new List<RealityPayloadField>
                        {
                            new RealityPayloadField { key = "waterBodyId", value = waterId },
                            new RealityPayloadField { key = "anchor", value = water.anchor.x + "," + water.anchor.z },
                            new RealityPayloadField { key = "habitat", value = water.habitat.ToString() }
                        }
                    });
                    foreach (NaturalFishSpeciesPopulation species in water.species ?? new List<NaturalFishSpeciesPopulation>())
                    {
                        if (species == null || string.IsNullOrEmpty(species.fishDefName)) continue;
                        if (species.FishDef == null)
                            world.Quarantine("aquaculture-fish-def", species.fishDefName, ProviderId,
                                "Fish Def is unavailable; aggregate identity was preserved as an orphan subject.");
                        string populationId = PopulationId(region, waterId, species.fishDefName);
                        if (!world.TryGetPopulation(populationId, out _))
                        {
                            world.UpsertPopulation(new RealityPopulationRecord
                            {
                                populationId = populationId,
                                providerId = ProviderId,
                                kind = "natural-water",
                                subjectId = "water:" + waterId + ":fish:" + species.fishDefName,
                                regionId = region.ToString(),
                                amount = Mathf.Max(0f, species.population),
                                uncertainty = Mathf.Max(0.25f, species.population * 0.15f),
                                carryingCapacity = Mathf.Max(species.population, water.carryingCapacity),
                                habitatSuitability = 1f,
                                migrationAllowed = water.habitat == NaturalWaterHabitat.River ||
                                    water.habitat == NaturalWaterHabitat.Coastal || water.habitat == NaturalWaterHabitat.Ocean,
                                established = species.established,
                                extinct = species.population < 0.5f,
                                lastUpdateTick = world.Now,
                                demographicPayload = "water=" + waterId + ";habitat=" + water.habitat + ";cellCount=" + water.cellCount
                            });
                        }
                        EnsureProcess(world, region, populationId);
                    }
                }
                world.CommitMigration(ProviderId, consumer, 1, "aquaculture-natural-water-v1");
                map.GetComponent<AquacultureDeferredProjectionMapComponent>();
            }
            catch (Exception exception)
            {
                world.Quarantine("aquaculture-natural-water", map.uniqueID.ToString(), ProviderId, exception.Message);
                Log.Error("Deferred Reality Aquaculture migration failed for map " + map.uniqueID + ": " + exception);
            }
        }

        public void SyncMap(Map map)
        {
            if (map == null || attachedWorld == null) return;
            RealityRegionId region = attachedWorld.RegisterMap(map);
            NaturalFishPopulationMapComponent legacy = map.GetComponent<NaturalFishPopulationMapComponent>();
            if (legacy == null) return;
            foreach (NaturalWaterPopulation water in legacy.Populations ?? Array.Empty<NaturalWaterPopulation>())
            {
                if (water == null) continue;
                string waterId = WaterBodyId(region, water);
                foreach (NaturalFishSpeciesPopulation species in water.species ?? new List<NaturalFishSpeciesPopulation>())
                {
                    if (species == null) continue;
                    string id = PopulationId(region, waterId, species.fishDefName);
                    if (attachedWorld.TryGetPopulationRecord(id, out RealityPopulationRecord current))
                    {
                        species.population = current.amount;
                        species.established = current.established;
                    }
                }
            }
            foreach (NaturalWaterPopulation refreshed in legacy.Populations ?? Array.Empty<NaturalWaterPopulation>())
                if (refreshed != null) legacy.PopulationAt(refreshed.anchor);
            AquacultureSnapshotCache.Invalidate();
        }

        public bool CanExecute(RealityProcessRecord process, RealityProcessExecution execution, IList<RealityVeto> vetoes)
        {
            if (process?.providerId != ProviderId) return false;
            if (string.IsNullOrEmpty(process.payload)) vetoes.Add(new RealityVeto("aquaculture.missing-water-population", "Process has no water population target.", ProviderId));
            return vetoes.Count == 0;
        }

        public RealityProcessResult Execute(RealityProcessRecord process, RealityProcessExecution execution)
        {
            if (attachedWorld == null || !attachedWorld.TryGetPopulationRecord(process.payload, out RealityPopulationRecord population))
                return new RealityProcessResult { succeeded = true, pause = true, error = "Water population target is unavailable." };
            float days = execution.elapsedTicks / 60000f;
            float capacity = Mathf.Max(1f, population.carryingCapacity);
            float mortality = population.amount * 0.01f * days;
            float breeding = population.amount >= 2f ? population.amount * 0.035f * days * population.habitatSuitability : 0f;
            float amount = Mathf.Clamp(population.amount + (breeding - mortality) * (0.96f + execution.random.NextFloat(0f, 0.08f)), 0f, capacity);
            population.amount = amount;
            population.uncertainty = Mathf.Max(0.1f, population.uncertainty * 0.99f);
            population.extinct = amount < 0.5f;
            population.lastUpdateTick = execution.toTick;
            attachedWorld.UpsertPopulation(population);
            TryMigrate(population, process, execution);
            return new RealityProcessResult { succeeded = true, nextDelayTicks = process.intervalTicks, analyticalSteps = execution.boundedStepCount };
        }

        public bool CanChangePopulation(RealityPopulationRecord population, string operation, IList<RealityVeto> vetoes)
        {
            if (population?.providerId != ProviderId) vetoes.Add(new RealityVeto("aquaculture.population-owner", "Population belongs to another provider.", ProviderId));
            return vetoes.Count == 0;
        }

        public void ReconcileActiveMap(RealityProviderContext context, RealityPopulationRecord population, string payload) { }

        public IEnumerable<string> DiagnosticLines(RealityDiagnosticsContext context)
        {
            int count = context.World?.PopulationSnapshots(providerId: ProviderId).Count ?? 0;
            return new[] { "aquaculture natural-water populations=" + count + " constructed-pond-owner=aquaculture" };
        }

        public RealityPopulationMutationResult TryReconcileCatch(FishingAttemptRecord attempt, CompFishTraits fish = null,
            bool legacyAlreadyConsumed = false)
        {
            if (attempt?.pawn?.Map == null || attempt.FishDef == null || attachedWorld == null)
                return new RealityPopulationMutationResult { error = "Missing natural-water catch context." };
            Map map = attempt.pawn.Map;
            if (!IsOwned(map)) return new RealityPopulationMutationResult { succeeded = true };
            TerrainDef terrain = map.terrainGrid.TerrainAt(attempt.waterCell);
            if (terrain?.IsWater != true || terrain.defName == "AF_Pond") return new RealityPopulationMutationResult { succeeded = true };
            NaturalFishPopulationMapComponent natural = map.GetComponent<NaturalFishPopulationMapComponent>();
            NaturalWaterPopulation water = natural?.PopulationAt(attempt.waterCell);
            if (water == null) return new RealityPopulationMutationResult { error = "Natural water body is unavailable." };
            NaturalFishSpeciesPopulation species = water.species?.FirstOrDefault(item => item?.fishDefName == attempt.FishDef.defName);
            if (species == null) return new RealityPopulationMutationResult { error = "Fish species is not established in this water body." };
            RealityRegionId region = attachedWorld.RegisterMap(map);
            string populationId = PopulationId(region, WaterBodyId(region, water), species.fishDefName);
            if (!attachedWorld.TryGetPopulationRecord(populationId, out _))
                EnsureGenericPopulation(attachedWorld, region, water, species, Mathf.Max(0f, species.population + (legacyAlreadyConsumed ? 1f : 0f)));
            string operationId = "aquaculture:catch:" + (attempt.pawn.thingIDNumber) + ":" + attempt.startedTick + ":" +
                attempt.waterCell.x + ":" + attempt.waterCell.z + ":" + attempt.FishDef.defName;
            RealityPopulationMutationResult result = RealityPopulationService.Consume(attachedWorld, populationId, 1f, operationId, attachedWorld.Now, ProviderId);
            if (!result.succeeded && !result.duplicate)
                attachedWorld.Quarantine("aquaculture-catch-pending", operationId, ProviderId,
                    result.error ?? "Natural-water catch needs explicit reconciliation.", "population=" + populationId + ";amount=1");
            return result;
        }

        public void ReconcileCatch(FishingAttemptRecord attempt, CompFishTraits fish = null) => TryReconcileCatch(attempt, fish, true);

        public void ReconcileRelease(Map map, IntVec3 cell, ThingDef fishDef, float amount)
        {
            if (map == null || fishDef == null || amount <= 0f || attachedWorld == null) return;
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
            if (terrain?.IsWater != true || terrain.defName == "AF_Pond") return;
            NaturalFishPopulationMapComponent natural = map.GetComponent<NaturalFishPopulationMapComponent>();
            NaturalWaterPopulation water = natural?.PopulationAt(cell);
            if (water == null) return;
            RealityRegionId region = attachedWorld.RegisterMap(map);
            string waterId = WaterBodyId(region, water);
            NaturalFishSpeciesPopulation species = water.species?.FirstOrDefault(item => item?.fishDefName == fishDef.defName);
            if (species == null) return;
            string id = PopulationId(region, waterId, fishDef.defName);
            if (!attachedWorld.TryGetPopulationRecord(id, out _))
                EnsureGenericPopulation(attachedWorld, region, water, species, Mathf.Max(0f, species.population - amount));
            string op = "aquaculture:release:" + region + ":" + cell.x + ":" + cell.z + ":" + fishDef.defName + ":" + attachedWorld.Now;
            RealityPopulationMutationResult result = RealityPopulationService.Release(attachedWorld, id, amount, op, attachedWorld.Now, ProviderId);
            if (!result.succeeded && !result.duplicate)
                attachedWorld.Quarantine("aquaculture-release", op, ProviderId, result.error ?? "Natural-water release was not reconciled.");
        }

        private static string WaterBodyId(RealityRegionId region, NaturalWaterPopulation water)
        {
            if (string.IsNullOrEmpty(water.deferredRealityStableId))
                water.deferredRealityStableId = "water:" + region + ":" + water.anchor.x + ":" + water.anchor.z;
            return water.deferredRealityStableId;
        }

        private static string PopulationId(RealityRegionId region, string waterId, string fishDefName) =>
            RealityPopulationService.PopulationId(ProviderId, "natural-water", region, waterId + ":fish:" + fishDefName);

        private static void EnsureGenericPopulation(DeferredRealityWorldComponent world, RealityRegionId region,
            NaturalWaterPopulation water, NaturalFishSpeciesPopulation species, float initialAmount)
        {
            if (world == null || water == null || species == null) return;
            string waterId = WaterBodyId(region, water);
            string id = PopulationId(region, waterId, species.fishDefName);
            world.UpsertPopulation(new RealityPopulationRecord
            {
                populationId = id,
                providerId = ProviderId,
                kind = "natural-water",
                subjectId = "water:" + waterId + ":fish:" + species.fishDefName,
                regionId = region.ToString(),
                amount = Mathf.Max(0f, initialAmount),
                uncertainty = Mathf.Max(0.25f, initialAmount * 0.15f),
                carryingCapacity = Mathf.Max(initialAmount, water.carryingCapacity),
                migrationAllowed = water.habitat == NaturalWaterHabitat.River || water.habitat == NaturalWaterHabitat.Coastal ||
                    water.habitat == NaturalWaterHabitat.Ocean,
                established = species.established,
                extinct = initialAmount < 0.5f,
                lastUpdateTick = world.Now,
                demographicPayload = "water=" + waterId + ";habitat=" + water.habitat + ";cellCount=" + water.cellCount
            });
            EnsureProcess(world, region, id);
        }

        private void TryMigrate(RealityPopulationRecord source, RealityProcessRecord process, RealityProcessExecution execution)
        {
            if (source == null || !source.migrationAllowed || source.amount < 0.5f) return;
            string sourceWater = PayloadValue(source.demographicPayload, "water");
            string sourceHabitat = PayloadValue(source.demographicPayload, "habitat");
            string fish = FishToken(source.subjectId);
            if (string.IsNullOrEmpty(sourceWater) || string.IsNullOrEmpty(fish)) return;
            foreach (RealityTopologyLink link in attachedWorld.TopologySnapshots(source.regionId)
                .Where(item => item.kind == "water-migration").OrderBy(item => item.linkId, StringComparer.Ordinal))
            {
                if (MetadataValue(link.metadata, "waterBodyFrom") != sourceWater) continue;
                string destinationWater = MetadataValue(link.metadata, "waterBodyTo");
                if (string.IsNullOrEmpty(destinationWater)) continue;
                RealityPopulationSnapshot destination = attachedWorld.PopulationSnapshots(link.toRegionId, ProviderId, "natural-water")
                    .FirstOrDefault(item => item.record.migrationAllowed && PayloadValue(item.record.demographicPayload, "water") == destinationWater &&
                        FishToken(item.record.subjectId) == fish && CompatibleHabitat(sourceHabitat,
                            PayloadValue(item.record.demographicPayload, "habitat")));
                if (destination == null) continue;
                float amount = Mathf.Min(source.amount * 0.05f * Mathf.Max(0.1f, execution.elapsedTicks / 60000f), 2f);
                if (amount < 0.1f) return;
                RealityPopulationService.Transfer(attachedWorld, source.populationId, destination.record.populationId, amount,
                    "aquaculture:migration:" + process.processId + ":" + process.executionCount + ":" + destination.record.populationId,
                    execution.toTick, ProviderId);
                return;
            }
        }

        /// <summary>Registers an explicit connected-water portal; disconnected bodies are never merged automatically.</summary>
        public void RegisterWaterConnection(DeferredRealityWorldComponent world, RealityRegionId fromRegion, string fromWater,
            RealityRegionId toRegion, string toWater, bool bidirectional = true)
        {
            AddWaterMigrationLink(world, fromRegion, toRegion, fromWater, toWater);
            if (bidirectional) AddWaterMigrationLink(world, toRegion, fromRegion, toWater, fromWater);
        }

        private static void AddWaterMigrationLink(DeferredRealityWorldComponent world, RealityRegionId fromRegion,
            RealityRegionId toRegion, string from, string to)
        {
            world.UpsertTopology(new RealityTopologyLink
            {
                linkId = "water-migration:" + fromRegion + ":" + from + ":" + toRegion + ":" + to,
                fromRegionId = fromRegion.ToString(),
                toRegionId = toRegion.ToString(),
                kind = "water-migration",
                travelCost = 1f,
                migrationFilter = "natural-water",
                metadata = new List<RealityPayloadField>
                {
                    new RealityPayloadField { key = "waterBodyFrom", value = from },
                    new RealityPayloadField { key = "waterBodyTo", value = to }
                }
            });
        }

        private static string FishToken(string subject)
        {
            int index = subject?.LastIndexOf(":fish:", StringComparison.Ordinal) ?? -1;
            return index < 0 ? null : subject.Substring(index + 6);
        }

        private static bool CompatibleHabitat(string first, string second)
        {
            if (string.Equals(first, "River", StringComparison.Ordinal) || string.Equals(second, "River", StringComparison.Ordinal))
                return string.Equals(first, "River", StringComparison.Ordinal) && string.Equals(second, "River", StringComparison.Ordinal);
            bool firstSalt = string.Equals(first, "Coastal", StringComparison.Ordinal) || string.Equals(first, "Ocean", StringComparison.Ordinal);
            bool secondSalt = string.Equals(second, "Coastal", StringComparison.Ordinal) || string.Equals(second, "Ocean", StringComparison.Ordinal);
            return firstSalt && secondSalt;
        }

        private static string PayloadValue(string payload, string key)
        {
            string prefix = key + "=";
            return (payload ?? string.Empty).Split(';').FirstOrDefault(item => item.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);
        }

        private static string MetadataValue(IEnumerable<RealityPayloadField> fields, string key) =>
            fields?.FirstOrDefault(item => item?.key == key)?.value;

        private static void EnsureProcess(DeferredRealityWorldComponent world, RealityRegionId region, string populationId)
        {
            string processId = "aquaculture:population:" + populationId;
            if (world.ProcessSnapshots(region.ToString()).Any(item => item.record.processId == processId)) return;
            world.ScheduleProcess(new RealityProcessRecord
            {
                processId = processId,
                providerId = ProviderId,
                kind = RealityProcessKind.WaterExchange,
                regionId = region.ToString(),
                nextDueTick = world.Now + 60000,
                lastExecutionTick = world.Now,
                intervalTicks = 60000,
                payload = populationId
            });
        }

        private void HandleFrameworkEvent(RealityEvent value)
        {
            if (value?.kind != "map.mapped" || Find.Maps == null) return;
            Map map = Find.Maps.FirstOrDefault(item => item?.uniqueID.ToString() == value.payload);
            if (map != null) MigrateMap(map);
        }
    }

    [StaticConstructorOnStartup]
    public static class AquacultureRealityIntegration
    {
        public static readonly AquacultureRealityProvider Provider = new AquacultureRealityProvider();

        static AquacultureRealityIntegration()
        {
            RealityProviderRegistry.Register(Provider);
            new Harmony("lan.deferredreality.aquaculture").PatchAll();
        }
    }

    public sealed class AquacultureDeferredProjectionMapComponent : MapComponent
    {
        private int nextSyncTick;

        public AquacultureDeferredProjectionMapComponent(Map map) : base(map) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            AquacultureRealityIntegration.Provider.MigrateMap(map);
            nextSyncTick = Find.TickManager?.TicksGame ?? 0;
        }

        public override void MapComponentTick()
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now < nextSyncTick) return;
            nextSyncTick = now + 60000;
            AquacultureRealityIntegration.Provider.SyncMap(map);
        }
    }

    internal static class AquacultureRealityHooks
    {
        [HarmonyPatch(typeof(FishingProgressionComponent), nameof(FishingProgressionComponent.Complete))]
        private static class CatchLedgerPatch
        {
            private static bool Prefix(FishingAttemptRecord attempt)
            {
                if (attempt?.pawn?.Map == null || !AquacultureRealityIntegration.Provider.IsOwned(attempt.pawn.Map)) return true;
                RealityPopulationMutationResult result = AquacultureRealityIntegration.Provider.TryReconcileCatch(attempt);
                // Let the host finish expertise, attempt cleanup, and journal bookkeeping.
                // A duplicate ledger operation means that bookkeeping was already attempted.
                return true;
            }
        }

        [HarmonyPatch(typeof(NaturalFishPopulationMapComponent), nameof(NaturalFishPopulationMapComponent.MapComponentTick))]
        private static class NaturalTickPatch
        {
            private static bool Prefix(NaturalFishPopulationMapComponent __instance) =>
                !AquacultureRealityIntegration.Provider.IsOwned(__instance.ActiveMap);
        }

        [HarmonyPatch(typeof(AquacultureEventRouter), nameof(AquacultureEventRouter.FishCaught))]
        private static class CatchPatch
        {
            private static void Postfix(FishingAttemptRecord attempt, CompFishTraits fish) =>
                AquacultureRealityIntegration.Provider.ReconcileCatch(attempt, fish);
        }

        [HarmonyPatch(typeof(NaturalFishPopulationMapComponent), nameof(NaturalFishPopulationMapComponent.IntroduceFish))]
        private static class ReleasePatch
        {
            private static void Postfix(NaturalFishPopulationMapComponent __instance, IntVec3 cell, ThingDef fishDef, float amount, bool __result)
            {
                if (__result && AquacultureRealityIntegration.Provider.IsOwned(__instance.ActiveMap))
                    AquacultureRealityIntegration.Provider.ReconcileRelease(__instance.ActiveMap, cell, fishDef, amount);
            }
        }
    }
}
