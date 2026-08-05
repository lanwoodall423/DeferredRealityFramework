using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;
using DeferredReality.Materialization;
using DeferredReality.Runtime;
using DeferredReality.Simulation;
using HarmonyLib;
using Herds;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeferredReality.Wildlife
{
    /// <summary>Wildlife adapter. Regional truth is imported into framework populations; active AI remains Wildlife-owned.</summary>
    public sealed partial class WildlifeRealityProvider : IRealityProvider, IRealityProcessProvider, IPopulationProvider,
        IAnchorProvider, ITransactionalAnchorProvider, ITransactionalAnchorCommitProvider, IRealityMapIdentityProvider, IConstraintResolver,
        IMaterializationProvider, IAdjacentRegionTransferHost, ICompressionProvider, IRealityDiagnosticsProvider,
        IRealityExcursionTaskProvider, IRealityExcursionTaskCleanupProvider
    {
        public const string ProviderId = "lan.wildlife";
        private DeferredRealityWorldComponent attachedWorld;
        private IDisposable eventSubscription;

        public RealityProviderRegistration Registration { get; } = new RealityProviderRegistration
        {
            providerId = ProviderId,
            displayName = "Wildlife regional ecology",
            semanticApiVersion = 1,
            schemaVersion = 1,
            order = 100,
            capabilities = RealityProviderCapability.Populations | RealityProviderCapability.Processes |
                RealityProviderCapability.Anchors | RealityProviderCapability.Constraints |
                RealityProviderCapability.Materialization | RealityProviderCapability.Compression |
                RealityProviderCapability.Diagnostics,
            // These event IDs include stable source identity and cannot legitimately replay after this window.
            operationRetentionTicks = 3600000L,
            compactableOperationKinds = new List<string> { "consume", "release", "active-map-reconcile", "demography", "transfer" }
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
            RealityRegionId id = attachedWorld.RegisterMap(map);
            return attachedWorld.TryGetRegion(id, out RealityRegionSnapshot region) && region != null &&
                attachedWorld.IsMigrationCommitted(ProviderId, "regional:" + id, 1);
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
            RealityThreadGuard.RequireMainThread();
            RealityRegionId regionId = world.RegisterMap(map);
            if (!regionId.IsValid) return;
            if (IsMaterializing(regionId)) return;
            string consumerId = "regional:" + regionId;
            if (world.IsMigrationCommitted(ProviderId, consumerId, 1))
            {
                map.GetComponent<WildlifeDeferredProjectionMapComponent>();
                EnsureAdjacentTopology(world, regionId);
                ReindexAnchors(world, regionId);
                SyncMap(map);
                return;
            }
            RegionalWildlifeMapComponent legacy = map.GetComponent<RegionalWildlifeMapComponent>();
            if (legacy == null) return;
            try
            {
                int orphanIndex = 0;
                foreach (RegionalSpeciesRecord orphan in legacy.OrphanedRecords ?? Array.Empty<RegionalSpeciesRecord>())
                {
                    world.Quarantine("wildlife-regional-orphan", map.uniqueID + ":" + orphanIndex++, ProviderId,
                        "Wildlife species Def is unavailable; regional aggregate was retained for inspection.",
                        "species=" + orphan?.legacySpeciesDefName + ";population=" + orphan?.population.ToString("R") +
                        ";nearby=" + orphan?.nearbyPopulation.ToString("R"));
                }
                int roamingOrphanIndex = 0;
                foreach (RoamingAnimalRecord orphan in legacy.OrphanedRoamingAnimals ?? Array.Empty<RoamingAnimalRecord>())
                {
                    world.Quarantine("wildlife-roaming-orphan", map.uniqueID + ":roaming:" + roamingOrphanIndex++, ProviderId,
                        "Wildlife roaming identity is incomplete or its species Def is unavailable; record was retained for inspection.",
                        "species=" + orphan?.legacySpeciesDefName + ";state=" + orphan?.state + ";tagged=" + orphan?.tagged + ";notable=" + orphan?.notable);
                }
                foreach (RegionalSpeciesRecord source in legacy.Records ?? Array.Empty<RegionalSpeciesRecord>())
                {
                    if (source?.species?.race?.Animal != true) continue;
                    string populationId = ProviderPopulationId(regionId, source.species.defName);
                    if (!world.TryGetPopulation(populationId, out _))
                    {
                        world.UpsertPopulation(new RealityPopulationRecord
                        {
                            populationId = populationId,
                            providerId = ProviderId,
                            kind = "wildlife",
                            subjectId = "species:" + source.species.defName,
                            regionId = regionId.ToString(),
                            amount = Mathf.Max(0f, source.population),
                            uncertainty = Mathf.Max(1f, source.population * (1f - Mathf.Clamp01(source.confidence))),
                            carryingCapacity = Mathf.Max(source.population, source.nearbyPopulation) * 2f + 1f,
                            habitatSuitability = Mathf.Clamp01(legacy.HabitatQuality),
                            pressure = source.consequenceState,
                            established = source.population > 0f,
                            extinct = source.population <= 0f,
                            lastUpdateTick = world.Now,
                            demographicPayload = "legacy-map=" + map.uniqueID + ";confidence=" + source.confidence.ToString("R")
                        });
                    }
                    EnsureProcess(world, regionId, populationId);
                }
                foreach (RoamingAnimalRecord roaming in legacy.RoamingAnimals ?? Enumerable.Empty<RoamingAnimalRecord>())
                {
                    if (roaming?.animal == null || roaming.species == null || (!roaming.tagged && !roaming.notable)) continue;
                    string anchorId = AnchorId(roaming.animal);
                    if (!world.AnchorSnapshots(regionId.ToString(), ProviderId).Any(item => item.record.anchorId == anchorId))
                    {
                        world.UpsertAnchor(new RealityAnchorRecord
                        {
                            anchorId = anchorId,
                            providerId = ProviderId,
                            typeId = roaming.notable ? "notable-animal" : "tagged-animal",
                            regionId = regionId.ToString(),
                            lastKnownTick = roaming.lastSeenTick,
                            lastKnownLocation = new RealityLocation
                            {
                                x = roaming.animal.Spawned ? roaming.animal.Position.x : -1,
                                z = roaming.animal.Spawned ? roaming.animal.Position.z : -1,
                                edge = roaming.direction,
                                precision = roaming.animal.Spawned ? RealityObservationPrecision.Cell : RealityObservationPrecision.Edge
                            },
                            importance = roaming.notable ? 3 : 2,
                            observationLevel = roaming.tagged ? RealityObservationPrecision.Exact : RealityObservationPrecision.Region,
                            optionalRimWorldLoadId = roaming.animal.GetUniqueLoadID(),
                            lifecycle = roaming.state == RoamingAnimalState.Dead ? RealityAnchorLifecycle.Dead : RealityAnchorLifecycle.Present,
                            providerPayload = "species=" + roaming.species.defName + ";state=" + roaming.state + ";reason=" + roaming.reason +
                                ";direction=" + roaming.direction,
                            causalProvenance = "wildlife-legacy-map:" + map.uniqueID
                        });
                    }
                    if (roaming.state != RoamingAnimalState.Present && roaming.state != RoamingAnimalState.Dead)
                    {
                        world.AddConstraint(new RealityConstraint
                        {
                            constraintId = "wildlife:departure:" + anchorId,
                            providerId = ProviderId,
                            typeId = "departure",
                            regionId = regionId.ToString(),
                            createdTick = world.Now,
                            validFromTick = roaming.leftTick,
                            certainty = roaming.tagged || roaming.notable ? 1f : 0.7f,
                            source = "legacy-roaming-record",
                            spatialPrecision = string.IsNullOrEmpty(roaming.direction) ? RealityObservationPrecision.Region : RealityObservationPrecision.Edge,
                            affectedAnchorIds = new List<string> { anchorId },
                            priority = roaming.notable ? 5 : 2,
                            conflictPolicy = RealityConflictPolicy.PreferEstablished,
                            payload = "state=" + roaming.state + ";direction=" + roaming.direction + ";expected=" + roaming.expectedReturnTick
                        });
                    }
                }
                world.CommitMigration(ProviderId, consumerId, 1, "wildlife-regional-v1");
                map.GetComponent<WildlifeDeferredProjectionMapComponent>();
                EnsureAdjacentTopology(world, regionId);
                ReindexAnchors(world, regionId);
            }
            catch (Exception exception)
            {
                world.Quarantine("wildlife-legacy-map", map.uniqueID.ToString(), ProviderId, exception.Message);
                Log.Error("Deferred Reality Wildlife migration failed for map " + map.uniqueID + ": " + exception);
            }
        }

        public void SyncMap(Map map)
        {
            if (map == null || attachedWorld == null) return;
            RealityRegionId regionId = attachedWorld.RegisterMap(map);
            RegionalWildlifeMapComponent legacy = map.GetComponent<RegionalWildlifeMapComponent>();
            if (legacy == null) return;
            EnsureAdjacentTopology(attachedWorld, regionId);
            foreach (RegionalSpeciesRecord row in legacy.Records ?? Array.Empty<RegionalSpeciesRecord>())
            {
                if (row?.species == null) continue;
                string populationId = ProviderPopulationId(regionId, row.species.defName);
                if (!attachedWorld.TryGetPopulation(populationId, out RealityPopulationSnapshot value)) continue;
                row.population = value.record.amount;
                row.previousPopulation = value.record.amount;
                row.nearbyPopulation = Mathf.Max(0f, row.lastLocalCount);
                row.previousNearbyPopulation = row.nearbyPopulation;
                row.lastUpdateTick = (int)Math.Min(int.MaxValue, attachedWorld.Now);
            }
        }

        public bool CanExecute(RealityProcessRecord process, RealityProcessExecution execution, IList<RealityVeto> vetoes)
        {
            if (process == null || process.providerId != ProviderId) return false;
            if (string.IsNullOrEmpty(process.payload)) vetoes.Add(new RealityVeto("wildlife.missing-population", "The process has no population target.", ProviderId, 2));
            return vetoes.Count == 0;
        }

        public RealityProcessResult Execute(RealityProcessRecord process, RealityProcessExecution execution)
        {
            RealityPopulationRecord population = attachedWorld != null && attachedWorld.TryGetPopulationRecord(process.payload, out RealityPopulationRecord value)
                ? value : null;
            if (population == null) return new RealityProcessResult { succeeded = true, pause = true, error = "Population target is unavailable." };
            if (attachedWorld.TryGetRegion(RealityRegionId.Parse(population.regionId), out RealityRegionSnapshot region) &&
                region.fidelity == RealityFidelity.Materialized)
                return new RealityProcessResult { succeeded = true, nextDelayTicks = process.intervalTicks };
            float days = execution.elapsedTicks / 60000f;
            float capacity = Mathf.Max(population.carryingCapacity, population.amount + 1f);
            float growth = Mathf.Clamp(population.amount * 0.035f * days * (1f - population.amount / capacity), -population.amount, capacity);
            float variation = 0.94f + execution.random.NextFloat(0f, 0.12f);
            population.amount = Mathf.Clamp(population.amount + growth * variation, 0f, capacity);
            population.uncertainty = Mathf.Max(0.5f, population.uncertainty * 0.99f + Mathf.Abs(growth) * 0.1f);
            population.extinct = population.amount <= 0.01f;
            population.established = population.established || population.amount > 0f;
            population.lastUpdateTick = execution.toTick;
            attachedWorld.UpsertPopulation(population);
            TryMigratePopulation(population, process, execution);
            return new RealityProcessResult { succeeded = true, nextDelayTicks = process.intervalTicks, analyticalSteps = execution.boundedStepCount };
        }

        public bool CanChangePopulation(RealityPopulationRecord population, string operation, IList<RealityVeto> vetoes)
        {
            if (population?.providerId != ProviderId) vetoes.Add(new RealityVeto("wildlife.population-owner", "Population belongs to another provider.", ProviderId));
            return vetoes.Count == 0;
        }

        public void ReconcileActiveMap(RealityProviderContext context, RealityPopulationRecord population, string payload)
        {
            if (context?.World == null || population == null || !RealityRegionId.TryParse(population.regionId, out RealityRegionId region)) return;
            Map map = Find.Maps?.FirstOrDefault(candidate => candidate != null && candidate.uniqueID ==
                context.World.RegionSnapshots().FirstOrDefault(item => item.id == region)?.activeMapUniqueId);
            RegionalWildlifeMapComponent legacy = map?.GetComponent<RegionalWildlifeMapComponent>();
            RegionalSpeciesRecord row = legacy?.Records?.FirstOrDefault(item =>
                item?.species?.defName == SpeciesFromSubject(population.subjectId));
            if (row == null) return;
            row.population = population.amount;
            row.previousPopulation = population.amount;
            row.lastUpdateTick = (int)Math.Min(int.MaxValue, context.Now);
        }

        public bool ValidateAnchor(RealityAnchorRecord anchor, IList<RealityVeto> vetoes)
        {
            if (anchor == null || anchor.providerId != ProviderId || string.IsNullOrEmpty(anchor.optionalRimWorldLoadId))
                vetoes.Add(new RealityVeto("wildlife.anchor-invalid", "Wildlife anchor has no provider or load identity.", ProviderId));
            return vetoes.Count == 0;
        }

        public void OnAnchorMaterialized(RealityProviderContext context, RealityAnchorRecord anchor) =>
            ReconcileMaterializedAnchor(context, anchor);

        public bool CanResolve(RealityConstraint constraint) => constraint?.providerId == ProviderId && constraint.typeId == "departure";

        public bool Resolve(RealityProviderContext context, RealityConstraint constraint, IList<RealityVeto> vetoes)
        {
            if (!CanResolve(constraint)) return false;
            foreach (string anchorId in constraint.affectedAnchorIds ?? new List<string>())
            {
                RealityAnchorSnapshot snapshot = context.World.AnchorSnapshots(constraint.regionId, ProviderId)
                    .FirstOrDefault(item => item.record.anchorId == anchorId);
                if (snapshot == null) continue;
                RealityAnchorRecord anchor = snapshot.record;
                anchor.lifecycle = RealityAnchorLifecycle.Traveling;
                context.World.UpsertAnchor(anchor);
            }
            return true;
        }

        public IEnumerable<string> DiagnosticLines(RealityDiagnosticsContext context)
        {
            int populations = context.World?.PopulationSnapshots(providerId: ProviderId).Count ?? 0;
            int anchors = context.World?.AnchorSnapshots(providerId: ProviderId).Count ?? 0;
            int links = context.World?.TopologySnapshots().Count(item => item?.kind == "cardinal-surface") ?? 0;
            int materialized = context.World?.RegionSnapshots().Count(item => item?.fidelity == RealityFidelity.Materialized) ?? 0;
            int transfers = context.World?.TransferJournalSnapshots().Count ?? 0;
            return new[]
            {
                "wildlife populations=" + populations + " anchors=" + anchors + " regional-authority=framework",
                "wildlife cardinal-links=" + links + " materialized-regions=" + materialized + " transfer-journals=" + transfers
            };
        }

        internal static string ProviderPopulationId(RealityRegionId regionId, string species) =>
            RealityPopulationService.PopulationId(ProviderId, "wildlife", regionId, "species:" + species);

        private static string AnchorId(Pawn animal) => "wildlife:animal:" + animal.thingIDNumber;

        private static void EnsureProcess(DeferredRealityWorldComponent world, RealityRegionId regionId, string populationId)
        {
            string processId = "wildlife:population:" + populationId;
            if (world.ProcessSnapshots(regionId.ToString()).Any(item => item.record.processId == processId)) return;
            world.ScheduleProcess(new RealityProcessRecord
            {
                processId = processId,
                providerId = ProviderId,
                kind = RealityProcessKind.PopulationGrowth,
                regionId = regionId.ToString(),
                nextDueTick = world.Now + 60000,
                lastExecutionTick = world.Now,
                intervalTicks = 60000,
                payload = populationId
            });
        }

        private void HandleFrameworkEvent(RealityEvent value)
        {
            if (value?.kind == "map.mapped" && Find.Maps != null)
            {
                Map map = Find.Maps.FirstOrDefault(item => item?.uniqueID.ToString() == value.payload);
                if (map != null) MigrateMap(map);
            }
        }
    }

    /// <summary>Registers the adapter without changing the framework's dependency direction.</summary>
    [StaticConstructorOnStartup]
    public static class WildlifeRealityIntegration
    {
        public static readonly WildlifeRealityProvider Provider = new WildlifeRealityProvider();

        static WildlifeRealityIntegration()
        {
            RealityProviderRegistry.Register(Provider);
            RealityMapFactoryRegistry.Register(WildlifeRealityProvider.ProviderId, new WildlifeMapFactory());
            RealityAdjacentSurfaceService.RegisterTransferHost(WildlifeRealityProvider.ProviderId, Provider);
            WildlifeDeferredRealityBridge.MaterializeBeyondMap = Provider.TryMaterializeTrail;
            WildlifeDeferredRealityBridge.AdjacentRegionSummary = Provider.AdjacentRegionSummary;
            new Harmony("lan.deferredreality.wildlife").PatchAll();
        }
    }

    /// <summary>Active-map projection and reconciliation cache.</summary>
    public sealed class WildlifeDeferredProjectionMapComponent : MapComponent
    {
        private int nextSyncTick;

        public WildlifeDeferredProjectionMapComponent(Map map) : base(map) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            WildlifeRealityIntegration.Provider.MigrateMap(map);
            nextSyncTick = Find.TickManager?.TicksGame ?? 0;
        }

        public override void MapComponentTick()
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now < nextSyncTick) return;
            nextSyncTick = now + 60000;
            WildlifeRealityIntegration.Provider.SyncMap(map);
        }
    }

    internal static class WildlifeRealityHooks
    {
        [HarmonyPatch(typeof(RegionalWildlifeMapComponent), "UpdateRegional")]
        private static class RegionalUpdatePatch
        {
            private static bool Prefix(RegionalWildlifeMapComponent __instance) =>
                !WildlifeRealityIntegration.Provider.IsOwned(__instance.ActiveMap);
        }

        [HarmonyPatch(typeof(RegionalWildlifeMapComponent), "UpdateLocalRoaming")]
        private static class LocalRoamingPatch
        {
            private static bool Prefix(RegionalWildlifeMapComponent __instance) =>
                !WildlifeRealityIntegration.Provider.IsOwned(__instance.ActiveMap);
        }

        [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
        private static class AnimalDeparturePatch
        {
            private static void Prefix(Pawn __instance, ref WildlifeRealityProvider.WildlifeAnimalDeparture __state)
            {
                if (__instance?.Spawned != true || __instance.Faction != null || __instance.RaceProps?.Animal != true) return;
                Map map = __instance.Map;
                if (!WildlifeRealityIntegration.Provider.IsOwned(map)) return;
                __state = WildlifeRealityIntegration.Provider.PrepareAnimalDeparture(map, __instance, __instance.Position);
            }

            private static void Postfix(WildlifeRealityProvider.WildlifeAnimalDeparture __state)
            {
                WildlifeRealityIntegration.Provider.CompleteAnimalDeparture(__state);
            }
        }

        [HarmonyPatch(typeof(RegionalWildlifeMapComponent), nameof(RegionalWildlifeMapComponent.NotifyLocalDeath))]
        private static class DeathPatch
        {
            private static bool Prefix(RegionalWildlifeMapComponent __instance, Pawn animal)
            {
                if (animal?.def == null || !WildlifeRealityIntegration.Provider.IsOwned(__instance.ActiveMap)) return true;
                RealityPopulationMutationResult result = Reconcile(__instance.ActiveMap, animal.def, -1f, "death:" + animal.thingIDNumber);
                return !result.duplicate;
            }
        }

        [HarmonyPatch(typeof(RegionalWildlifeMapComponent), nameof(RegionalWildlifeMapComponent.NotifyLocalCapture))]
        private static class CapturePatch
        {
            private static bool Prefix(RegionalWildlifeMapComponent __instance, Pawn animal)
            {
                if (animal?.def == null || !WildlifeRealityIntegration.Provider.IsOwned(__instance.ActiveMap)) return true;
                RealityPopulationMutationResult result = Reconcile(__instance.ActiveMap, animal.def, -1f, "capture:" + animal.thingIDNumber);
                return !result.duplicate;
            }
        }

        [HarmonyPatch(typeof(RegionalWildlifeMapComponent), nameof(RegionalWildlifeMapComponent.NotifyLocalSpawn))]
        private static class SpawnPatch
        {
            private static bool Prefix(RegionalWildlifeMapComponent __instance, Pawn animal, bool respawningAfterLoad)
            {
                if (respawningAfterLoad || animal?.def == null || !WildlifeRealityIntegration.Provider.IsOwned(__instance.ActiveMap)) return true;
                RealityPopulationMutationResult result = WildlifeRealityIntegration.Provider.ReconcileLocalSpawn(__instance.ActiveMap, animal);
                return !result.duplicate;
            }
        }

        [HarmonyPatch(typeof(RegionalWildlifeMapComponent), nameof(RegionalWildlifeMapComponent.ApplyExpeditionImpact))]
        private static class ExpeditionImpactPatch
        {
            private static bool Prefix(RegionalWildlifeMapComponent __instance, ThingDef species,
                float populationDelta, float confidenceGain)
            {
                if (!WildlifeRealityIntegration.Provider.IsOwned(__instance.ActiveMap)) return true;
                RealityPopulationMutationResult result = WildlifeRealityIntegration.Provider.ApplyAggregateImpact(
                    __instance.ActiveMap, species, populationDelta, confidenceGain,
                    __instance.Records?.FirstOrDefault(item => item?.species == species)?.population.ToString("R") + ":" +
                    __instance.Records?.FirstOrDefault(item => item?.species == species)?.confidence.ToString("R"));
                return !(result.succeeded || result.duplicate);
            }
        }

        [HarmonyPatch(typeof(RegionalWildlifeMapComponent), nameof(RegionalWildlifeMapComponent.CanSpawnWildAnimal))]
        private static class SpawnPermissionPatch
        {
            private static bool Prefix(RegionalWildlifeMapComponent __instance, PawnKindDef kind, bool mapInitializing, ref bool __result)
            {
                if (!WildlifeRealityIntegration.Provider.IsOwned(__instance.ActiveMap)) return true;
                if (mapInitializing) return true;
                ThingDef species = kind?.race;
                if (species == null) { __result = false; return false; }
                DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
                RealityRegionId region = world.RegisterMap(__instance.ActiveMap);
                string populationId = WildlifeRealityProvider.ProviderPopulationId(region, species.defName);
                if (!world.TryGetPopulation(populationId, out RealityPopulationSnapshot population)) { __result = false; return false; }
                bool coolingDown = world.ConstraintSnapshots(region.ToString()).Any(item => item.typeId == "local-loss" &&
                    item.affectedPopulationIds.Contains(populationId) && item.IsActiveAt(world.Now));
                __result = !coolingDown && population.record.amount >= 1f && !population.record.extinct;
                return false;
            }
        }

        private static RealityPopulationMutationResult Reconcile(Map map, ThingDef species, float delta, string operation)
        {
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world == null || map == null || species == null) return new RealityPopulationMutationResult { error = "Missing Wildlife reconciliation context." };
            RealityRegionId region = world.RegisterMap(map);
            if (!region.IsValid) return new RealityPopulationMutationResult { error = "Map has no stable region." };
            string populationId = WildlifeRealityProvider.ProviderPopulationId(region, species.defName);
            string operationId = "wildlife:local:" + operation + ":" + region + ":" + species.defName;
            RealityPopulationMutationResult result = delta < 0f
                ? RealityPopulationService.Consume(world, populationId, -delta, operationId, world.Now, WildlifeRealityProvider.ProviderId)
                : RealityPopulationService.Release(world, populationId, delta, operationId, world.Now, WildlifeRealityProvider.ProviderId);
            if (!result.succeeded && !result.duplicate)
                world.Quarantine("wildlife-local-pending", operationId, WildlifeRealityProvider.ProviderId,
                    result.error ?? "Active Wildlife delta needs explicit regional reconciliation.", "population=" + populationId + ";delta=" + delta);
            if (result.succeeded && delta < 0f)
            {
                world.AddConstraint(new RealityConstraint
                {
                    constraintId = "wildlife:local-loss:" + operationId,
                    providerId = WildlifeRealityProvider.ProviderId,
                    typeId = "local-loss",
                    regionId = region.ToString(),
                    createdTick = world.Now,
                    expiryTick = world.Now + 120000,
                    certainty = 1f,
                    source = "active-map-reconciliation",
                    affectedPopulationIds = new List<string> { populationId },
                    conflictPolicy = RealityConflictPolicy.PreferEstablished,
                    payload = "delta=" + delta
                });
            }
            return result;
        }
    }

    internal static class WildlifeRealityProviderExtensions
    {
        public static string ProviderPopulationId(this WildlifeRealityProvider provider, RealityRegionId regionId, string species)
        {
            return RealityPopulationService.PopulationId(WildlifeRealityProvider.ProviderId, "wildlife", regionId, "species:" + species);
        }
    }
}
