using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;
using DeferredReality.Materialization;
using DeferredReality.Runtime;
using DeferredReality.Simulation;
using HarmonyLib;
using HorticultureNovelSeeds;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeferredReality.Horticulture
{
    /// <summary>Regional wild-flora adapter. Cultivar and active-plant ownership remains Horticulture-owned.</summary>
    public sealed class HorticultureRealityProvider : IRealityProvider, IRealityProcessProvider,
        IPopulationProvider, IConstraintResolver, IMaterializationProvider, IRealityDiagnosticsProvider
    {
        public const string ProviderId = "lan.horticulture.wild-flora";
        private DeferredRealityWorldComponent attachedWorld;
        private IDisposable eventSubscription;

        public RealityProviderRegistration Registration { get; } = new RealityProviderRegistration
        {
            providerId = ProviderId,
            displayName = "Horticulture regional wild flora",
            semanticApiVersion = 1,
            schemaVersion = 1,
            order = 300,
            capabilities = RealityProviderCapability.Populations | RealityProviderCapability.Processes |
                RealityProviderCapability.Constraints | RealityProviderCapability.Materialization | RealityProviderCapability.Observations |
                RealityProviderCapability.Diagnostics,
            // Event IDs include stable plant identity and cannot legitimately replay after this window.
            operationRetentionTicks = 3600000L,
            compactableOperationKinds = new List<string> { "consume", "release", "active-map-reconcile" }
        };

        public int Order => 300;

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
            return attachedWorld.IsMigrationCommitted(ProviderId, "wild-flora:" + region, 1);
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
            string consumer = "wild-flora:" + region;
            if (world.IsMigrationCommitted(ProviderId, consumer, 1))
            {
                map.GetComponent<HorticultureDeferredProjectionMapComponent>();
                return;
            }
            try
            {
                List<ThingDef> crops = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(NovelSeedUtility.IsGrowableCrop).OrderBy(def => def.defName, StringComparer.Ordinal).ToList();
                foreach (ThingDef crop in crops)
                {
                    string populationId = PopulationId(region, crop.defName);
                    int seed = RealityDeterminism.Seed(world.WorldSeed, region.ToString(), ProviderId, crop.defName, 0);
                    float amount = 2f + (seed % 13);
                    string latentVarieties = LatentVarietyIds(crop);
                    if (!world.TryGetPopulation(populationId, out _))
                    {
                        world.UpsertPopulation(new RealityPopulationRecord
                        {
                            populationId = populationId,
                            providerId = ProviderId,
                            kind = "wild-flora",
                            subjectId = "plant:" + crop.defName,
                            regionId = region.ToString(),
                            amount = amount,
                            uncertainty = Mathf.Max(1f, amount * 0.35f),
                            carryingCapacity = amount * 3f + 6f,
                            habitatSuitability = 1f,
                            established = true,
                            extinct = false,
                            lastUpdateTick = world.Now,
                            demographicPayload = "crop=" + crop.defName + ";seed=" + seed + ";latent-varieties=" + latentVarieties
                        });
                    }
                    EnsureProcess(world, region, populationId);
                }
                world.CommitMigration(ProviderId, consumer, 1, "horticulture-wild-flora-v1");
                map.GetComponent<HorticultureDeferredProjectionMapComponent>();
            }
            catch (Exception exception)
            {
                world.Quarantine("horticulture-wild-flora", map.uniqueID.ToString(), ProviderId, exception.Message);
                Log.Error("Deferred Reality Horticulture migration failed for map " + map.uniqueID + ": " + exception);
            }
        }

        public bool CanExecute(RealityProcessRecord process, RealityProcessExecution execution, IList<RealityVeto> vetoes)
        {
            if (process?.providerId != ProviderId) return false;
            if (string.IsNullOrEmpty(process.payload)) vetoes.Add(new RealityVeto("horticulture.missing-population", "Process has no wild flora target.", ProviderId, 2));
            return vetoes.Count == 0;
        }

        public RealityProcessResult Execute(RealityProcessRecord process, RealityProcessExecution execution)
        {
            if (attachedWorld == null || !attachedWorld.TryGetPopulationRecord(process.payload, out RealityPopulationRecord population))
                return new RealityProcessResult { succeeded = true, pause = true, error = "Wild flora population target is unavailable." };
            if (attachedWorld.TryGetRegion(RealityRegionId.Parse(population.regionId), out RealityRegionSnapshot region) &&
                region.fidelity == RealityFidelity.Materialized)
                return new RealityProcessResult { succeeded = true, nextDelayTicks = process.intervalTicks };
            float days = execution.elapsedTicks / 60000f;
            float capacity = Mathf.Max(1f, population.carryingCapacity);
            float growth = population.amount * 0.02f * days * (1f - population.amount / capacity);
            population.amount = Mathf.Clamp(population.amount + growth * (0.95f + execution.random.NextFloat(0f, 0.1f)), 0f, capacity);
            population.uncertainty = Mathf.Max(0.5f, population.uncertainty * 0.995f);
            population.lastUpdateTick = execution.toTick;
            population.extinct = population.amount < 0.5f;
            attachedWorld.UpsertPopulation(population);
            return new RealityProcessResult { succeeded = true, nextDelayTicks = process.intervalTicks, analyticalSteps = execution.boundedStepCount };
        }

        public bool CanChangePopulation(RealityPopulationRecord population, string operation, IList<RealityVeto> vetoes)
        {
            if (population?.providerId != ProviderId) vetoes.Add(new RealityVeto("horticulture.population-owner", "Population belongs to another provider.", ProviderId));
            return vetoes.Count == 0;
        }

        public void ReconcileActiveMap(RealityProviderContext context, RealityPopulationRecord population, string payload) { }

        public bool CanResolve(RealityConstraint constraint) => constraint?.providerId == ProviderId &&
            (constraint.typeId == "wild-variety-present" || constraint.typeId == "wild-discovery");

        public bool Resolve(RealityProviderContext context, RealityConstraint constraint, IList<RealityVeto> vetoes)
        {
            if (!CanResolve(constraint)) return false;
            return true;
        }

        public void CanMaterialize(RealityMaterializationRequest request, RealityMaterializationPlan plan)
        {
            if (request == null || !request.regionId.IsValid) plan.AddVeto(new RealityVeto("horticulture.invalid-region", "Wild flora requires a valid region.", ProviderId));
            plan.AddStep("horticulture-plan-compatible-wild-plants");
        }

        public void Prepare(RealityMaterializationRequest request, RealityMaterializationPlan plan)
        {
            plan.AddStep("horticulture-prepare-latent-varieties");
        }

        public void Apply(RealityMaterializationContext context)
        {
            // Exact plant creation is intentionally delegated to the host map generator in v1.
            context.Plan.AddStep("horticulture-defer-compatible-plant-placement");
        }

        public void Validate(RealityMaterializationContext context, IList<RealityVeto> vetoes)
        {
            if (context?.World?.PopulationSnapshots(context.Request.regionId.ToString(), ProviderId, "wild-flora").Count > 0)
                vetoes.Add(new RealityVeto("horticulture.materialization-host-required",
                    "Compatible wild-plant placement requires an explicit Horticulture host in framework v1.", ProviderId, 2));
        }

        public void Rollback(RealityMaterializationContext context) { }

        public RealityPopulationMutationResult TryReconcileWildHarvest(Plant plant, int yield)
        {
            if (plant?.Map == null || plant.sown || attachedWorld == null || !IsOwned(plant.Map))
                return new RealityPopulationMutationResult { succeeded = true };
            RealityRegionId region = attachedWorld.RegisterMap(plant.Map);
            string populationId = PopulationId(region, plant.def.defName);
            string operationId = "horticulture:wild-harvest:" + plant.thingIDNumber + ":" + (attachedWorld.Now - 1) + ":" + Mathf.Max(1, yield);
            RealityPopulationMutationResult result = RealityPopulationService.Consume(attachedWorld, populationId, 1f, operationId, attachedWorld.Now, ProviderId);
            if (!result.succeeded && !result.duplicate)
                attachedWorld.Quarantine("horticulture-harvest-pending", operationId, ProviderId,
                    result.error ?? "Wild harvest needs explicit regional reconciliation.", "population=" + populationId);
            return result;
        }

        public void ReconcileWildHarvest(Plant plant, int yield) => TryReconcileWildHarvest(plant, yield);

        public void RecordDiscoveredVariety(ThingDef crop, VarietyRecord variety, Pawn discoverer)
        {
            if (crop == null || variety == null || attachedWorld == null || discoverer?.Map == null) return;
            RealityRegionId region = attachedWorld.RegisterMap(discoverer.Map);
            string anchorId = "horticulture:variety:" + variety.id;
            attachedWorld.UpsertAnchor(new RealityAnchorRecord
            {
                anchorId = anchorId,
                providerId = ProviderId,
                typeId = "wild-variety",
                regionId = region.ToString(),
                lastKnownTick = attachedWorld.Now,
                lastKnownLocation = new RealityLocation { x = -1, z = -1, precision = RealityObservationPrecision.Region },
                importance = 3,
                observationLevel = RealityObservationPrecision.Exact,
                lifecycle = RealityAnchorLifecycle.Present,
                providerPayload = "crop=" + crop.defName + ";variety=" + variety.id + ";traits=" +
                    string.Join(",", (variety.traits ?? new List<VarietyTraitDef>()).Where(item => item != null).Select(item => item.defName).ToArray()),
                causalProvenance = "wild-discovery:" + (discoverer.thingIDNumber)
            });
            attachedWorld.AddConstraint(new RealityConstraint
            {
                constraintId = "horticulture:wild-discovery:" + variety.id + ":" + region,
                providerId = ProviderId,
                typeId = "wild-discovery",
                regionId = region.ToString(),
                createdTick = attachedWorld.Now,
                certainty = 1f,
                observerId = discoverer.GetUniqueLoadID(),
                source = "horticulture-discovery",
                spatialPrecision = RealityObservationPrecision.Region,
                affectedAnchorIds = new List<string> { anchorId },
                priority = 5,
                conflictPolicy = RealityConflictPolicy.PreferEstablished,
                payload = "variety=" + variety.id + ";crop=" + crop.defName
            });
        }

        public IEnumerable<string> DiagnosticLines(RealityDiagnosticsContext context)
        {
            int count = context.World?.PopulationSnapshots(providerId: ProviderId).Count ?? 0;
            int anchors = context.World?.AnchorSnapshots(providerId: ProviderId).Count ?? 0;
            return new[] { "horticulture wild-flora populations=" + count + " discovered-variety-anchors=" + anchors + " cultivar-owner=novel-seeds" };
        }

        private static string PopulationId(RealityRegionId region, string crop) =>
            RealityPopulationService.PopulationId(ProviderId, "wild-flora", region, "plant:" + crop);

        private static string LatentVarietyIds(ThingDef crop)
        {
            GameComponent_NovelSeeds registry = GameComponent_NovelSeeds.Instance;
            if (registry == null) return "seeded";
            return string.Join(",", registry.AllVarieties.Where(item => item?.cropDef == crop && item.originKind == "wild")
                .OrderBy(item => item.id, StringComparer.Ordinal).Select(item => item.id).Take(16).ToArray());
        }

        private static void EnsureProcess(DeferredRealityWorldComponent world, RealityRegionId region, string populationId)
        {
            string processId = "horticulture:wild-flora:" + populationId;
            if (world.ProcessSnapshots(region.ToString()).Any(item => item.record.processId == processId)) return;
            world.ScheduleProcess(new RealityProcessRecord
            {
                processId = processId,
                providerId = ProviderId,
                kind = RealityProcessKind.SeedDispersal,
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
    public static class HorticultureRealityIntegration
    {
        public static readonly HorticultureRealityProvider Provider = new HorticultureRealityProvider();

        static HorticultureRealityIntegration()
        {
            RealityProviderRegistry.Register(Provider);
            new Harmony("lan.deferredreality.horticulture").PatchAll();
        }
    }

    public sealed class HorticultureDeferredProjectionMapComponent : MapComponent
    {
        private int nextMigrationCheck;

        public HorticultureDeferredProjectionMapComponent(Map map) : base(map) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            HorticultureRealityIntegration.Provider.MigrateMap(map);
            nextMigrationCheck = Find.TickManager?.TicksGame ?? 0;
        }

        public override void MapComponentTick()
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now < nextMigrationCheck) return;
            nextMigrationCheck = now + 60000;
            HorticultureRealityIntegration.Provider.MigrateMap(map);
        }
    }

    internal static class HorticultureRealityHooks
    {
        [HarmonyPatch(typeof(HorticultureEventRouter), nameof(HorticultureEventRouter.HarvestCompleted))]
        private static class WildHarvestPatch
        {
            private static bool Prefix(Plant plant, int yield, bool success)
            {
                if (!success || plant?.Map == null || plant.sown || !HorticultureRealityIntegration.Provider.IsOwned(plant.Map)) return true;
                HorticultureRealityIntegration.Provider.TryReconcileWildHarvest(plant, yield);
                return true;
            }
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.PlantCollected))]
        private static class WildHarvestLedgerPatch
        {
            private static bool Prefix(Plant __instance, PlantDestructionMode plantDestructionMode)
            {
                if (__instance?.Map == null || __instance.sown || plantDestructionMode != (PlantDestructionMode)2 ||
                    !__instance.HarvestableNow || __instance.Blighted || !HorticultureRealityIntegration.Provider.IsOwned(__instance.Map)) return true;
                HorticultureRealityIntegration.Provider.TryReconcileWildHarvest(__instance, __instance.YieldNow());
                return true;
            }
        }

        [HarmonyPatch(typeof(GameComponent_NovelSeeds), nameof(GameComponent_NovelSeeds.UnlockVariety))]
        private static class VarietyUnlockPatch
        {
            private static void Postfix(ThingDef cropDef, Pawn discoverer, string originKind, VarietyRecord __result)
            {
                if (originKind == "wild") HorticultureRealityIntegration.Provider.RecordDiscoveredVariety(cropDef, __result, discoverer);
            }
        }
    }
}
