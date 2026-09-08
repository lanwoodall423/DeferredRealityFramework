using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using DeferredReality.API;
using DeferredReality.Materialization;
using DeferredReality.Simulation;
using HarmonyLib;
using Verse;

namespace DeferredReality.PureTests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveRimWorldAssembly;
                RealityThreadGuard.EstablishMainThread();
                RegionRoundTrip();
                RegionConnectionGraphQueries();
                DeterministicSeedAndStream();
                ProviderScopedFactoryIsolation();
                SchedulerGateAndOrdering();
                CatchUpBoundaries();
                PauseCauseTransitions();
                ProjectionAuthorityContracts();
                CompressionPreservationContracts();
                MaterializationObservationConsistencyContracts();
                FidelityContractsAndEscalationTypes();
                ProviderRegistrationCompatibilityContracts();
                SimpleProviderCompositionContracts();
                RetentionSelection();
                TransitionCompensationSeams();
                PartialPrepareRollbackAndRetention();
                ExactlyOnceSequenceBoundaries();
                MapIdentityClaims();
                MapCreationIntentBoundaries();
                ConstraintDomainsAndFacets();
                DuplicateRepairSemantics();
                DefaultStateContracts();
                AdjacentPolicyBoundaries();
                ExcursionReturnLifecycleContracts();
                HarmonyTargetResolvers();
                HarmonyPatchRegistrationSmoke();
                ThreadGuardDoesNotSelfInitialize();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 1;
            }
        }

        private static Assembly ResolveRimWorldAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            if (string.Equals(name, "0Harmony.dll", StringComparison.OrdinalIgnoreCase))
            {
                string harmonyPath = Environment.GetEnvironmentVariable("DEFERRED_REALITY_HARMONY_PATH");
                return !string.IsNullOrWhiteSpace(harmonyPath) && File.Exists(harmonyPath)
                    ? Assembly.LoadFrom(harmonyPath) : null;
            }
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "1.6", "Assemblies", name));
            if (File.Exists(path)) return Assembly.LoadFrom(path);
            string rimWorldRoot = Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
            if (string.IsNullOrWhiteSpace(rimWorldRoot))
            {
                rimWorldRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "..", ".."));
            }
            string rimWorldPath = Path.Combine(rimWorldRoot, "RimWorldWin64_Data", "Managed", name);
            return File.Exists(rimWorldPath) ? Assembly.LoadFrom(rimWorldPath) : null;
        }

        private static void RegionRoundTrip()
        {
            RealityRegionId original = new RealityRegionId(42, RealityLayer.Custom, "water|north", "body=7",
                "rr-parent", "provider.test", "subterranean");
            RealityRegionId parsed = RealityRegionId.Parse(original.ToString());
            Require(original == parsed, "region identity did not round-trip");
            Require(original.GetHashCode() == parsed.GetHashCode(), "region hash changed after round-trip");
        }

        private static void RegionConnectionGraphQueries()
        {
            RealityRegionId left = RealityRegionId.Surface(101);
            RealityRegionId right = RealityRegionId.Surface(102);
            RealityRegionId remote = RealityRegionId.Surface(103);
            var world = new DeferredRealityWorldComponent(null);
            world.EnsureRegion(left);
            world.EnsureRegion(right);
            world.EnsureRegion(remote);

            string bidirectionalId = RealityRegionConnection.StableId(left, right,
                RealityRegionConnectionDirection.Bidirectional, "pure-edge", "pure-tests", "main");
            Require(world.UpsertConnection(new RealityRegionConnection
            {
                connectionId = bidirectionalId,
                sourceRegionId = left.ToString(),
                destinationRegionId = right.ToString(),
                direction = RealityRegionConnectionDirection.Bidirectional,
                kind = "pure-edge",
                ownerNamespace = "pure-tests",
                identityKey = "main"
            }), "bidirectional connection was rejected");
            Require(world.OutgoingConnections(left).Count == 1 && world.OutgoingConnections(right).Count == 1,
                "bidirectional connection was not indexed from both endpoints");
            Require(world.IncomingConnections(left).Count == 1 && world.IncomingConnections(right).Count == 1,
                "bidirectional connection was not indexed as incoming at both endpoints");
            Require(world.Neighbors(left).Count == 1 && world.Neighbors(left)[0] == right,
                "neighbor query did not return the opposite endpoint");
            Require(world.TryGetConnection(bidirectionalId, out RealityRegionConnection snapshot) &&
                snapshot.sourceRegionId == left.ToString(), "stable connection lookup failed");
            snapshot.lifecycle = RealityRegionConnectionLifecycle.Disabled;
            Require(world.UpsertConnection(snapshot), "disabled connection update was rejected");
            Require(world.ConnectionSnapshots(left.ToString()).Count == 1 &&
                world.OutgoingConnections(left).Count == 0, "disabled connection was not inspectable-but-inactive");

            string directedId = RealityRegionConnection.StableId(right, remote,
                RealityRegionConnectionDirection.Directed, "pure-route", "pure-tests", "directed");
            Require(world.UpsertConnection(new RealityRegionConnection
            {
                connectionId = directedId,
                sourceRegionId = right.ToString(),
                destinationRegionId = remote.ToString(),
                direction = RealityRegionConnectionDirection.Directed,
                kind = "pure-route",
                ownerNamespace = "pure-tests",
                identityKey = "directed"
            }), "directed connection was rejected");
            Require(world.OutgoingConnections(right, "pure-route").Count == 1 &&
                world.OutgoingConnections(remote, "pure-route").Count == 0 &&
                world.IncomingConnections(remote, "pure-route").Count == 1,
                "directed connection query semantics were incorrect");

            string conflictId = RealityRegionConnection.StableId(left, right,
                RealityRegionConnectionDirection.Directed, "pure-conflict", "pure-tests", "same-id");
            Require(world.UpsertConnection(new RealityRegionConnection
            {
                connectionId = conflictId,
                sourceRegionId = left.ToString(),
                destinationRegionId = right.ToString(),
                direction = RealityRegionConnectionDirection.Directed,
                kind = "pure-conflict",
                ownerNamespace = "pure-tests",
                identityKey = "same-id"
            }), "baseline conflict connection was rejected");
            Require(!world.UpsertConnection(new RealityRegionConnection
            {
                connectionId = conflictId,
                sourceRegionId = right.ToString(),
                destinationRegionId = left.ToString(),
                direction = RealityRegionConnectionDirection.Directed,
                kind = "pure-conflict",
                ownerNamespace = "pure-tests",
                identityKey = "same-id"
            }), "conflicting connection ID was accepted");
        }

        private static void DeterministicSeedAndStream()
        {
            int left = RealityDeterminism.Seed("world", "region", "provider", "process", 7);
            int right = RealityDeterminism.Seed("world", "region", "provider", "process", 7);
            Require(left == right, "stable seed changed");
            RealityRandomStream a = new RealityRandomStream(left);
            RealityRandomStream b = new RealityRandomStream(right);
            for (int i = 0; i < 32; i++) Require(a.NextUInt() == b.NextUInt(), "random stream changed at " + i);
        }

        private static void ProviderScopedFactoryIsolation()
        {
            var first = new TestFactory();
            var second = new TestFactory();
            Require(RealityMapFactoryRegistry.Register("pure.first", first), "first factory did not register");
            Require(RealityMapFactoryRegistry.Register("pure.second", second), "second factory did not register");
            Require(RealityMapFactoryRegistry.TryGet("pure.first", out IRealityMapFactory resolvedFirst) &&
                ReferenceEquals(first, resolvedFirst), "first provider factory was not isolated");
            Require(RealityMapFactoryRegistry.TryGet("pure.second", out IRealityMapFactory resolvedSecond) &&
                ReferenceEquals(second, resolvedSecond), "second provider factory was not isolated");
            Require(RealityMapFactoryRegistry.Unregister("pure.first", first), "first factory did not unregister");
            Require(!RealityMapFactoryRegistry.TryGet("pure.first", out _), "unregistered factory remained visible");
            RealityMapFactoryRegistry.Unregister("pure.second", second);
            Require(!RealityMapFactoryRegistry.TryGet("missing", out _),
                "unregistered provider unexpectedly resolved a map factory");
        }

        private static void SchedulerGateAndOrdering()
        {
            var processes = new[]
            {
                new RealityProcessRecord { processId = "future", providerId = "a", nextDueTick = 100 },
                new RealityProcessRecord { processId = "low", providerId = "b", nextDueTick = 10, priority = 1 },
                new RealityProcessRecord { processId = "provider", providerId = "a", nextDueTick = 10, priority = 2 },
                new RealityProcessRecord { processId = "id", providerId = "a", nextDueTick = 10, priority = 2 },
                new RealityProcessRecord { processId = "paused", providerId = "z", nextDueTick = 1, paused = true }
            };
            Require(RealityProcessScheduling.EarliestRunnableDue(processes) == 10, "scheduler gate chose a paused process");
            IReadOnlyList<RealityProcessRecord> due = RealityProcessScheduling.OrderDue(processes, 10);
            Require(due.Count == 3, "scheduler gate returned the wrong due count");
            Require(due[0].processId == "id" && due[1].processId == "provider" && due[2].processId == "low",
                "scheduler ordering is not deterministic");
            processes[1].paused = true;
            processes[2].paused = true;
            processes[3].paused = true;
            Require(RealityProcessScheduling.EarliestRunnableDue(processes) == 100, "scheduler gate did not invalidate paused work");
        }

        private static void CatchUpBoundaries()
        {
            Require(RealityProcessScheduling.RequestedAnalyticalSteps(0, 10) == 1, "first due execution is not one step");
            Require(RealityProcessScheduling.RequestedAnalyticalSteps(10, 10) == 1, "one interval elapsed over-counted");
            Require(RealityProcessScheduling.RequestedAnalyticalSteps(19, 10) == 1, "one tick short of two intervals over-counted");
            Require(RealityProcessScheduling.RequestedAnalyticalSteps(20, 10) == 2, "two complete intervals under-counted");
            Require(RealityProcessScheduling.BoundedAnalyticalSteps(10000, 10, 3) == 3, "catch-up bound was not enforced");
        }

        private static void PauseCauseTransitions()
        {
            var process = new RealityProcessRecord
            {
                processId = "pause", providerId = "provider", nextDueTick = 4,
                executionCount = 8, payload = "opaque", lastExecutionTick = 3
            };
            RealityProcessPausePolicy.SetManual(process);
            Require(process.paused && process.pauseReason == RealityProcessPauseReason.Manual, "manual pause cause was lost");
            Require(!RealityProcessPausePolicy.ReactivateUnavailable(process, "provider"), "manual pause was auto-reactivated");
            RealityProcessPausePolicy.SetProviderUnavailable(process, "missing");
            Require(process.pauseReason == RealityProcessPauseReason.ProviderUnavailable, "missing-provider cause was lost");
            Require(RealityProcessPausePolicy.ReactivateUnavailable(process, "other") == false, "wrong provider reactivated process");
            Require(RealityProcessPausePolicy.ReactivateUnavailable(process, "provider"), "provider re-registration did not reactivate process");
            Require(process.nextDueTick == 4 && process.executionCount == 8 && process.payload == "opaque" && process.lastExecutionTick == 3,
                "provider reactivation changed process state");
            RealityProcessPausePolicy.SetProviderFailure(process, "failed", 99);
            Require(!RealityProcessPausePolicy.ReactivateUnavailable(process, "provider") && process.paused &&
                process.pauseReason == RealityProcessPauseReason.ProviderFailure, "provider failure was silently reactivated");
            Require(RealityProcessPausePolicy.Resume(process, 120) && !process.paused && process.pauseReason == RealityProcessPauseReason.None,
                "explicit resume did not clear the pause");
        }

        private static void ProjectionAuthorityContracts()
        {
            var descriptor = new RealityRegionDescriptor();
            Require(descriptor.authority == RealityRegionAuthority.Latent && descriptor.projectionMapUniqueId == -1,
                "a new region did not default to latent state without a projection binding");
            Require(Enum.IsDefined(typeof(RealityFidelity), "Materialized"),
                "Materialized is not exposed as an explicit fidelity value");
            Require(RealityFidelityRules.Rank(RealityFidelity.Materialized) > RealityFidelityRules.Rank(RealityFidelity.Narrative) &&
                RealityFidelityRules.Rank(RealityFidelity.Narrative) > RealityFidelityRules.Rank(RealityFidelity.Statistical),
                "fidelity ordering is not deterministic");
            var process = new RealityProcessRecord { processId = "projection", providerId = "provider" };
            RealityProcessPausePolicy.SetProjectionAuthoritative(process, "live map owns state");
            Require(process.paused && process.pauseReason == RealityProcessPauseReason.ProjectionAuthoritative,
                "live projection did not pause aggregate work with the authoritative cause");
            RealityProcessPausePolicy.SetProjectionTransition(process, "transition owns state");
            Require(process.paused && process.pauseReason == RealityProcessPauseReason.ProjectionTransition,
                "projection transition did not retain an explicit pause cause");
        }

        private static void CompressionPreservationContracts()
        {
            RealityRegionId region = RealityRegionId.Surface(701);
            var world = new DeferredRealityWorldComponent(null);
            world.EnsureRegion(region, "preservation", 0);
            var population = new RealityPopulationRecord
            {
                populationId = "wildlife:muffalo:701",
                providerId = "wildlife",
                kind = "wildlife",
                subjectId = "species:muffalo",
                regionId = region.ToString(),
                amount = 23f,
                compressionSignificance = RealityCompressionSignificance.Aggregate,
                established = true
            };
            var anchor = new RealityAnchorRecord
            {
                anchorId = "wildlife:muffalo:named-1",
                providerId = "wildlife",
                typeId = "named-animal",
                regionId = region.ToString(),
                compressionSignificance = RealityCompressionSignificance.Identity,
                externalReferenceState = RealityExternalReferenceState.ProviderResolved,
                optionalRimWorldLoadId = "Pawn_NamedMuffalo",
                importance = 3
            };
            population.anchoredMemberIds.Add(anchor.anchorId);
            Require(world.UpsertPopulation(population) && world.UpsertAnchor(anchor),
                "aggregate and identity records were not accepted");
            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "wildlife:observation:named-muffalo",
                regionId = region.ToString(),
                subjectId = anchor.anchorId,
                compressionSignificance = RealityCompressionSignificance.EstablishedFact,
                playerObserved = true,
                certainty = 1f,
                confidence = 1f,
                estimate = "named muffalo is present"
            }), "established observation was not accepted");
            Require(RealityCompressionPreservation.Validate(world, region, "wildlife").Count == 0,
                "safe aggregate, anchor, and established observation state was vetoed");

            world.UpsertRegionDescriptor(new RealityRegionDescriptor
            {
                regionId = region.ToString(),
                fidelity = RealityFidelity.Materialized,
                authority = RealityRegionAuthority.LiveProjection,
                projectionMapUniqueId = 701,
                lastUpdateTick = 1
            });
            Require(world.PopulationSnapshots(region.ToString()).Single().record.amount == 23f,
                "materialization changed the fungible aggregate unexpectedly");
            Require(world.AnchorSnapshots(region.ToString()).Any(item => item.record.anchorId == anchor.anchorId),
                "materialization lost the identity anchor");

            world.UpsertRegionDescriptor(new RealityRegionDescriptor
            {
                regionId = region.ToString(),
                fidelity = RealityFidelity.Statistical,
                authority = RealityRegionAuthority.Latent,
                projectionMapUniqueId = -1,
                lastUpdateTick = 2
            });
            Require(world.PopulationSnapshots(region.ToString()).Single().record.amount == 23f &&
                world.AnchorSnapshots(region.ToString()).Any(item => item.record.anchorId == anchor.anchorId) &&
                world.ObservationSnapshots(region.ToString()).Any(item => item.observationId == "wildlife:observation:named-muffalo"),
                "compression did not preserve aggregate, identity, and established observation state");

            anchor.externalReferenceState = RealityExternalReferenceState.Unknown;
            Require(world.UpsertAnchor(anchor), "unsafe anchor update was rejected before validation");
            IReadOnlyList<RealityVeto> unsafeVetoes = RealityCompressionPreservation.Validate(world, region, "wildlife");
            Require(unsafeVetoes.Any(item => item.code == "compression.unsafe-external-reference"),
                "unknown external identity reference did not veto compression");
            anchor.externalReferenceState = RealityExternalReferenceState.ProviderResolved;
            Require(world.UpsertAnchor(anchor), "resolved anchor update was rejected");
            Require(RealityRetentionPolicy.FindOldestDiscardableObservation(new[]
                {
                    new RealityObservationRecord { observationId = "disposable", tick = 1,
                        compressionSignificance = RealityCompressionSignificance.Disposable },
                    new RealityObservationRecord { observationId = "established", tick = 0,
                        compressionSignificance = RealityCompressionSignificance.EstablishedFact }
                }).observationId == "disposable",
                "retention selected an established fact instead of an explicitly disposable observation");
        }

        private static void MaterializationObservationConsistencyContracts()
        {
            RealityRegionId region = RealityRegionId.Surface(702);
            var world = new DeferredRealityWorldComponent(null);
            world.EnsureRegion(region, "observation-consistency", 0);
            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "rumor-herd",
                regionId = region.ToString(),
                subjectId = "herd:muffalo",
                spatialPrecision = RealityObservationPrecision.Rumor,
                confidence = 0.2f,
                estimate = "muffalo are somewhere in the region"
            }), "rumor observation was not accepted");
            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "habitat-herd",
                regionId = region.ToString(),
                subjectId = "herd:muffalo",
                spatialPrecision = RealityObservationPrecision.Habitat,
                confidence = 0.65f,
                location = new RealityLocation { areaId = "valley", precision = RealityObservationPrecision.Habitat },
                estimate = "muffalo use the valley habitat"
            }), "habitat observation was not accepted");
            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "area-pollution",
                regionId = region.ToString(),
                subjectId = "pollution",
                spatialPrecision = RealityObservationPrecision.Area,
                confidence = 0.8f,
                location = new RealityLocation { areaId = "northwest", precision = RealityObservationPrecision.Area },
                estimate = "pollution is in the northwest"
            }), "area observation was not accepted");
            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "exact-entrance",
                regionId = region.ToString(),
                subjectId = "ruin:entrance",
                facet = "entrance",
                playerObserved = true,
                spatialPrecision = RealityObservationPrecision.Exact,
                confidence = 1f,
                location = new RealityLocation { x = 3, z = 7, precision = RealityObservationPrecision.Exact },
                estimate = "the discovered entrance is here"
            }), "exact observation was not accepted");
            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "invalidated-entrance",
                regionId = region.ToString(),
                subjectId = "ruin:entrance",
                spatialPrecision = RealityObservationPrecision.Exact,
                confidence = 1f,
                lifecycle = RealityObservationLifecycle.Invalidated,
                location = new RealityLocation { x = 99, z = 99, precision = RealityObservationPrecision.Exact }
            }), "invalidated observation was not accepted");

            RealityMaterializationRequest request = new RealityMaterializationRequest
            {
                regionId = region,
                providerId = "wildlife",
                now = 100,
                preserveObservedFacts = true
            };
            RealityMaterializationConsistencyPlan first = RealityMaterializationConsistency.Build(world, request);
            RealityMaterializationConsistencyPlan second = RealityMaterializationConsistency.Build(world, request);
            Require(first != null && first.Observations.Count == 4 && first.Observations.All(item =>
                    item.lifecycle == RealityObservationLifecycle.Active),
                "active observations were not separated from invalidated knowledge");
            RealityMaterializationConstraint rumor = first.MaterializationConstraints.Single(item =>
                item.sourceObservationId == "rumor-herd");
            RealityMaterializationConstraint habitat = first.MaterializationConstraints.Single(item =>
                item.sourceObservationId == "habitat-herd");
            RealityMaterializationConstraint area = first.MaterializationConstraints.Single(item =>
                item.sourceObservationId == "area-pollution");
            RealityMaterializationConstraint exact = first.MaterializationConstraints.Single(item =>
                item.sourceObservationId == "exact-entrance");
            Require(rumor.enforcement == RealityMaterializationConstraintEnforcement.Advisory &&
                rumor.location.x < 0 && string.IsNullOrEmpty(rumor.location.areaId),
                "a rumor was incorrectly promoted to a fixed location");
            Require(habitat.enforcement == RealityMaterializationConstraintEnforcement.Advisory &&
                habitat.location.areaId == "valley",
                "habitat knowledge did not retain its coarse area");
            Require(area.enforcement == RealityMaterializationConstraintEnforcement.Required &&
                area.location.areaId == "northwest",
                "area knowledge did not become a required coarse obligation");
            Require(exact.enforcement == RealityMaterializationConstraintEnforcement.Exact &&
                exact.location.x == 3 && exact.location.z == 7,
                "a high-confidence player exact observation did not constrain placement");

            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "exact-default-location-precision",
                regionId = region.ToString(),
                subjectId = "ruin:beacon",
                playerObserved = true,
                spatialPrecision = RealityObservationPrecision.Exact,
                confidence = 1f,
                location = new RealityLocation { x = 5, z = 9 },
                estimate = "the beacon is at this cell"
            }), "an observation with an implicit location precision was not accepted");
            RealityMaterializationConstraint normalized = RealityMaterializationConsistency.Build(world, request)
                .MaterializationConstraints.Single(item => item.sourceObservationId == "exact-default-location-precision");
            Require(normalized.location.precision == RealityObservationPrecision.Exact,
                "observation location precision was not normalized to its stated precision");
            Require(first.DeterministicStateKey == second.DeterministicStateKey &&
                first.DeterministicSeed == second.DeterministicSeed,
                "materialization consistency inputs were not deterministic");

            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "invalidated-through-api",
                regionId = region.ToString(),
                subjectId = "temporary-sign",
                spatialPrecision = RealityObservationPrecision.Area,
                location = new RealityLocation { areaId = "old-clearing" }
            }), "an observation for explicit invalidation was not accepted");
            Require(world.InvalidateObservation("invalidated-through-api", "gameplay removed the sign"),
                "explicit observation invalidation failed");
            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "superseded-observation",
                regionId = region.ToString(),
                subjectId = "temporary-trail",
                spatialPrecision = RealityObservationPrecision.Area,
                location = new RealityLocation { areaId = "old-trail" }
            }) && world.AddObservation(new RealityObservationInput
            {
                observationId = "superseding-observation",
                regionId = region.ToString(),
                subjectId = "temporary-trail",
                spatialPrecision = RealityObservationPrecision.Area,
                location = new RealityLocation { areaId = "new-trail" }
            }), "observations for explicit supersession were not accepted");
            Require(world.SupersedeObservation("superseded-observation", "superseding-observation", "trail moved"),
                "explicit observation supersession failed");
            RealityMaterializationConsistencyPlan lifecyclePlan = RealityMaterializationConsistency.Build(world, request);
            Require(!lifecyclePlan.Observations.Any(item => item.observationId == "invalidated-through-api") &&
                !lifecyclePlan.Observations.Any(item => item.observationId == "superseded-observation") &&
                lifecyclePlan.Observations.Any(item => item.observationId == "superseding-observation"),
                "invalidated or superseded observations still constrained rematerialization");

            Require(world.AddObservation(new RealityObservationInput
            {
                observationId = "exact-entrance-conflict",
                regionId = region.ToString(),
                subjectId = "ruin:entrance",
                facet = "entrance",
                playerObserved = true,
                spatialPrecision = RealityObservationPrecision.Exact,
                confidence = 1f,
                location = new RealityLocation { x = 4, z = 8, precision = RealityObservationPrecision.Exact },
                estimate = "the entrance is elsewhere"
            }), "conflicting exact observation was not accepted as knowledge");
            RealityMaterializationConsistencyPlan conflicted = RealityMaterializationConsistency.Build(world, request);
            Require(conflicted.Conflicts.Any(item => item.domainKey == "observation:ruin:entrance"),
                "conflicting established observations were not diagnosable");
        }

        private static void FidelityContractsAndEscalationTypes()
        {
            var policy = new RealityProcessFidelityPolicy
            {
                legalFidelities = RealityFidelityMask.Statistical | RealityFidelityMask.Narrative,
                runsWhileLiveProjection = false,
                mayRequestEscalation = true,
                escalationTarget = RealityFidelity.Narrative
            };
            Require(policy.IsLegalAt(RealityFidelity.Statistical) && !policy.IsLegalAt(RealityFidelity.Dormant) &&
                !policy.runsWhileLiveProjection && policy.mayRequestEscalation,
                "process fidelity policy did not express legality and live-map gating");
            var contract = new RealityFidelityContract
            {
                supportedFidelities = RealityFidelityMask.All,
                transitions = new List<RealityFidelityTransitionRule>
                {
                    new RealityFidelityTransitionRule
                    {
                        fromFidelity = RealityFidelity.Statistical,
                        toFidelity = RealityFidelity.Narrative
                    }
                }
            };
            Require(contract.Supports(RealityFidelity.Narrative) &&
                contract.AllowsTransition(RealityFidelity.Statistical, RealityFidelity.Narrative),
                "provider fidelity transition contract did not retain its typed edge");
            Require(!contract.AllowsTransition(RealityFidelity.Statistical, RealityFidelity.Narrative,
                    RealityFidelityTransitionMechanism.Materialization),
                "a materialization edge was confused with a latent transition");
            var downgradeContract = new RealityFidelityContract
            {
                supportedFidelities = RealityFidelityMask.Statistical | RealityFidelityMask.Narrative,
                transitions = new List<RealityFidelityTransitionRule>
                {
                    new RealityFidelityTransitionRule
                    {
                        fromFidelity = RealityFidelity.Narrative,
                        toFidelity = RealityFidelity.Statistical
                    }
                }
            };
            Require(downgradeContract.AllowsTransition(RealityFidelity.Narrative, RealityFidelity.Statistical),
                "provider fidelity contract could not declare a lower latent transition");
            var livePolicy = new RealityProcessFidelityPolicy
            {
                legalFidelities = RealityFidelityMask.Materialized,
                runsWhileLiveProjection = true
            };
            Require(livePolicy.IsLegalAt(RealityFidelity.Materialized) && livePolicy.runsWhileLiveProjection,
                "a process policy could not explicitly opt into safe live-projection execution");
            var pausedProcess = new RealityProcessRecord { processId = "escalation", providerId = "wildlife" };
            RealityProcessPausePolicy.SetFidelityEscalation(pausedProcess, "fidelity:test", "needs detail");
            Require(pausedProcess.paused && pausedProcess.pauseReason == RealityProcessPauseReason.FidelityEscalation &&
                pausedProcess.pendingEscalationRequestId == "fidelity:test",
                "fidelity escalation did not durably own the process pause");
            Require(RealityProcessPausePolicy.Resume(pausedProcess, 20) && !pausedProcess.paused &&
                pausedProcess.pendingEscalationRequestId == null,
                "resuming an escalation-paused process did not clear its durable link");
            var escalation = new RealityProcessEscalationRequest
            {
                requestedFidelity = RealityFidelity.Materialized,
                reason = RealityFidelityEscalationReason.PlayerInteraction,
                disposition = RealityFidelityEscalationDisposition.Request,
                policy = RealityFidelityEscalationPolicy.HostApproval,
                subjectId = "pawn-load-id"
            };
            Require(escalation.IsValid, "typed escalation request was not valid");
            var result = new RealityProcessResult { escalation = escalation };
            Require(result.escalation != null && result.escalation.reason == RealityFidelityEscalationReason.PlayerInteraction,
                "process result did not carry a typed escalation request");
            var registration = new RealityProviderRegistration();
            Require((registration.supportedFidelities & RealityFidelityMask.Materialized) != 0,
                "provider registration did not expose fidelity support");
        }

        private static void ProviderRegistrationCompatibilityContracts()
        {
            Require(DeferredRealityFrameworkInfo.Version == "0.1.0-rc.2" &&
                DeferredRealityFrameworkInfo.SupportedProviderApiVersion == 1 &&
                !string.IsNullOrEmpty(DeferredRealityFrameworkInfo.BuildIdentity),
                "framework identity did not expose the release and provider API versions");

            var acceptedRegistration = new RealityProviderRegistration
            {
                providerId = "pure.registration.accepted",
                semanticApiVersion = DeferredRealityFrameworkInfo.SupportedProviderApiVersion,
                order = 9000
            };
            Require(RealityProviderRegistry.Register(new TestRegistrationProvider(acceptedRegistration)),
                "current provider API version was rejected");

            var unsupportedRegistration = new RealityProviderRegistration
            {
                providerId = "pure.registration.unsupported",
                semanticApiVersion = DeferredRealityFrameworkInfo.SupportedProviderApiVersion + 1
            };
            Require(!RealityProviderRegistry.Register(new TestRegistrationProvider(unsupportedRegistration)),
                "unsupported provider API version was accepted");
            Require(!RealityProviderRegistry.TryGet("pure.registration.unsupported", out _),
                "unsupported provider was partially registered");

            var upstreamRegistration = new RealityProviderRegistration
            {
                providerId = "pure.registration.upstream",
                semanticApiVersion = DeferredRealityFrameworkInfo.SupportedProviderApiVersion,
                capabilities = RealityProviderCapability.Populations,
                order = 9010,
                displayName = "Upstream",
                compactableOperationKinds = new List<string> { "transfer" }
            };
            var downstreamRegistration = new RealityProviderRegistration
            {
                providerId = "pure.registration.downstream",
                semanticApiVersion = DeferredRealityFrameworkInfo.SupportedProviderApiVersion,
                capabilities = RealityProviderCapability.Diagnostics,
                order = 9001,
                dependencies = new List<string> { upstreamRegistration.providerId }
            };
            Require(RealityProviderRegistry.Register(new TestRegistrationProvider(upstreamRegistration)) &&
                RealityProviderRegistry.Register(new TestRegistrationProvider(downstreamRegistration)),
                "provider metadata snapshot setup failed");

            string[] beforeMutation = RealityProviderRegistry.Registrations()
                .Where(item => item.providerId.StartsWith("pure.registration.", StringComparison.Ordinal) &&
                    item.providerId != "pure.registration.accepted")
                .Select(item => item.providerId).ToArray();
            Require(beforeMutation.SequenceEqual(new[]
            {
                "pure.registration.upstream", "pure.registration.downstream"
            }), "provider dependency ordering changed before metadata mutation");

            upstreamRegistration.providerId = "pure.registration.changed";
            upstreamRegistration.order = -1000;
            upstreamRegistration.capabilities = RealityProviderCapability.None;
            upstreamRegistration.dependencies.Clear();
            upstreamRegistration.compactableOperationKinds.Clear();
            downstreamRegistration.order = -2000;
            downstreamRegistration.dependencies.Clear();

            Require(RealityProviderRegistry.TryGetRegistration("pure.registration.upstream",
                out RealityProviderRegistration frozen) &&
                frozen.providerId == "pure.registration.upstream" && frozen.order == 9010 &&
                frozen.capabilities == RealityProviderCapability.Populations &&
                frozen.compactableOperationKinds.SequenceEqual(new[] { "transfer" }),
                "registered provider metadata was not frozen");
            string[] afterMutation = RealityProviderRegistry.Registrations()
                .Where(item => item.providerId.StartsWith("pure.registration.", StringComparison.Ordinal) &&
                    item.providerId != "pure.registration.accepted")
                .Select(item => item.providerId).ToArray();
            Require(beforeMutation.SequenceEqual(afterMutation),
                "provider metadata mutation changed deterministic registry ordering");
        }

        private static void SimpleProviderCompositionContracts()
        {
            int executions = 0;
            SimpleRealityProvider provider = new SimpleRealityProviderBuilder("pure.simple-provider", "Pure simple provider")
                .Configure(registration =>
                {
                    registration.order = 901;
                    registration.defaultFidelity = RealityFidelity.Statistical;
                })
                .WithFidelity(RealityFidelityMask.Statistical | RealityFidelityMask.Narrative)
                .AllowTransition(RealityFidelity.Statistical, RealityFidelity.Narrative)
                .WithAnalyticalProcess(
                    (process, execution, vetoes) => true,
                    (process, execution) =>
                    {
                        executions++;
                        return new RealityProcessResult { succeeded = true };
                    },
                    RealityFidelityMask.Statistical,
                    runsWhileLiveProjection: false)
                .WithPopulations(new SimplePopulationDefinition
                {
                    canChange = (population, operation, vetoes) => true
                })
                .Build();

            Require(provider.Registration.providerId == "pure.simple-provider" &&
                (provider.Registration.capabilities & RealityProviderCapability.Processes) != 0 &&
                (provider.Registration.capabilities & RealityProviderCapability.Populations) != 0,
                "simple provider composition did not advertise configured capabilities");
            Require(provider.ProvidesCapability(typeof(IRealityFidelityProvider)) &&
                provider.ProvidesCapability(typeof(IRealityProcessProvider)) &&
                provider.ProvidesCapability(typeof(IPopulationProvider)) &&
                !provider.ProvidesCapability(typeof(IMaterializationProvider)),
                "simple provider exposed an absent optional capability");
            Require(RealityProviderRegistry.Register(provider), "simple provider did not register");
            bool hasFidelity = RealityProviderRegistry.TryGetCapability("pure.simple-provider", out IRealityFidelityProvider fidelityProvider);
            bool hasProcess = RealityProviderRegistry.TryGetCapability("pure.simple-provider", out IRealityProcessProvider processProvider);
            bool hasPopulation = RealityProviderRegistry.TryGetCapability("pure.simple-provider", out IPopulationProvider populationProvider);
            bool hasMaterialization = RealityProviderRegistry.TryGetCapability("pure.simple-provider", out IMaterializationProvider materializationProvider);
            var transitionVetoes = new List<RealityVeto>();
            Require(hasFidelity && hasProcess && hasPopulation && !hasMaterialization &&
                fidelityProvider.DescribeFidelity(null, null).Supports(RealityFidelity.Narrative) &&
                fidelityProvider.CanTransitionFidelity(new RealityFidelityTransitionRequest
                {
                    providerId = "pure.simple-provider",
                    regionId = RealityRegionId.Surface(901),
                    fromFidelity = RealityFidelity.Statistical,
                    toFidelity = RealityFidelity.Narrative
                }, transitionVetoes),
                "capability-aware registry lookup did not honor simple composition");

            var process = new RealityProcessRecord
            {
                providerId = "pure.simple-provider",
                processId = "pure-process",
                regionId = RealityRegionId.Surface(901).ToString()
            };
            Require(processProvider.CanExecute(process, new RealityProcessExecution(), new List<RealityVeto>()) &&
                processProvider.Execute(process, new RealityProcessExecution()).succeeded && executions == 1,
                "simple process callback was not delegated");
            Require(populationProvider.CanChangePopulation(new RealityPopulationRecord
            {
                providerId = "pure.simple-provider"
            }, "release", new List<RealityVeto>()), "simple population callback was not delegated");
        }

        private static void RetentionSelection()
        {
            var journals = new List<RealityTransferJournalRecord>
            {
                new RealityTransferJournalRecord { transferId = "interrupted", status = RealityTransferStatus.Prepared, updatedTick = 1 },
                new RealityTransferJournalRecord { transferId = "fresh-a", status = RealityTransferStatus.Completed, updatedTick = 950 },
                new RealityTransferJournalRecord { transferId = "fresh-b", status = RealityTransferStatus.RolledBack, updatedTick = 901 },
                new RealityTransferJournalRecord { transferId = "fresh-c", status = RealityTransferStatus.Fallback, updatedTick = 905 },
                new RealityTransferJournalRecord { transferId = "old", status = RealityTransferStatus.Completed, updatedTick = 100 },
                new RealityTransferJournalRecord { transferId = "fresh-a", status = RealityTransferStatus.Completed, updatedTick = 940 },
                new RealityTransferJournalRecord { transferId = "recoverable", status = RealityTransferStatus.Prepared, updatedTick = 10 },
                new RealityTransferJournalRecord { transferId = "recoverable", status = RealityTransferStatus.Completed, updatedTick = 990 }
            };
            IReadOnlyList<RealityTransferJournalRecord> retained = RealityRetentionPolicy.SelectTransferJournals(journals, 1000, 100, 2);
            Require(retained.Count == 4 && retained.Any(item => item.transferId == "interrupted") &&
                retained.Any(item => item.transferId == "fresh-a") && retained.Any(item => item.transferId == "fresh-c") &&
                retained.Any(item => item.transferId == "recoverable" && !RealityRetentionPolicy.IsTerminalTransfer(item)) &&
                !retained.Any(item => item.transferId == "fresh-b") && !retained.Any(item => item.transferId == "old"),
                "transfer compaction selection was unsafe or nondeterministic");
            var registration = new RealityProviderRegistration
            {
                providerId = "retention", operationRetentionTicks = 100,
                compactableOperationKinds = new List<string> { "safe" }
            };
            var first = new RealityObservationRecord { observationId = "z", tick = 4 };
            var second = new RealityObservationRecord { observationId = "a", tick = 4 };
            Require(ReferenceEquals(RealityRetentionPolicy.FindOldestObservation(new[] { first, second }), second),
                "observation eviction did not use a linear deterministic minimum");
        }

        private static void DefaultStateContracts()
        {
            var pausedProcess = new RealityProcessRecord { paused = true };
            Require(pausedProcess.pauseReason == RealityProcessPauseReason.None && pausedProcess.cancelledTick == -1,
                "process defaults are not stable");
            RealityProcessPausePolicy.Normalize(pausedProcess, true);
            Require(pausedProcess.pauseReason == RealityProcessPauseReason.Manual, "an unspecified pause was not normalized to manual");
            var registration = new RealityProviderRegistration();
            Require(registration.operationRetentionTicks == -1 && registration.cancelledProcessRetentionTicks == -1,
                "retention metadata default is not durable");
            var oldConstraint = new RealityConstraint();
            Require(oldConstraint.conflictDomainKeys != null && oldConstraint.conflictFacetKeys != null,
                "constraint conflict-key defaults are not stable");
        }

        private static void AdjacentPolicyBoundaries()
        {
            var ticket = new RealityExcursionTicket
            {
                excursionId = "e1", providerId = "provider", pawnLoadId = "pawn",
                originMapUniqueId = 1, destinationMapUniqueId = 2, graceDeadline = 100,
                lastTaskHeartbeat = 10, retryTick = 0, status = RealityExcursionStatus.Active
            };
            Require(!RealityAdjacentPolicy.IsReturnDue(ticket, 99, false, true, false, false),
                "an excursion returned before its grace deadline");
            Require(RealityAdjacentPolicy.IsReturnDue(ticket, 101, true, true, false, false),
                "explicit completion did not request a safe return");
            Require(!RealityAdjacentPolicy.IsReturnDue(ticket, 101, true, false, true, false),
                "unsafe pawn state bypassed return gating");
            ticket.retryTick = 200;
            Require(!RealityAdjacentPolicy.IsReturnDue(ticket, 101, true, true, false, false),
                "return backoff was ignored");
            Require(RealityAdjacentPolicy.InverseEdge("north") == "south" &&
                RealityAdjacentPolicy.InverseEdge("west") == "east", "inverse return edge was not deterministic");
            IReadOnlyList<string> evictions = RealityAdjacentPolicy.SelectWarmEvictions(new[]
            {
                new KeyValuePair<string, long>("new", 20), new KeyValuePair<string, long>("old", 10),
                new KeyValuePair<string, long>("tie-b", 10), new KeyValuePair<string, long>("tie-a", 10)
            }, 2);
            Require(evictions.SequenceEqual(new[] { "old", "tie-a" }),
                "warm eviction selection did not use recency and stable tie ordering: " + string.Join(",", evictions));
            Require(RealityAdjacentPolicy.HasActiveLease(new[] { ticket }, 2),
                "active excursion did not block map eviction");
            var marker = new RealityAdjacentMapRecord();
            Require(marker.lifecycle == RealityAdjacentMapLifecycle.Materializing,
                "adjacent map marker did not have a stable lifecycle default");
            Require(new RealityExcursionTicket().status == RealityExcursionStatus.Active,
                "excursion ticket did not have a stable active default");
            Require(RealityAdjacentPolicy.IsSafeIdleJob(null, false, false, false, false, false, false, false) &&
                RealityAdjacentPolicy.IsSafeIdleJob("Wait_Wander", true, false, false, false, false, false, false),
                "safe idle classification rejected a no-job or wait state");
            Require(!RealityAdjacentPolicy.IsSafeIdleJob("Hunt", true, false, false, false, false, false, false) &&
                !RealityAdjacentPolicy.IsSafeIdleJob("Wait", true, false, false, false, false, true, false),
                "meaningful or carried jobs were classified as safe idle");
            Require(RealityAdjacentPolicy.IsFreshTaskEvidence(950, 1000) &&
                !RealityAdjacentPolicy.IsFreshTaskEvidence(1100, 1000) &&
                !RealityAdjacentPolicy.IsFreshTaskEvidence(1, 10000),
                "task lease evidence accepted future or stale observations");
            ticket.status = RealityExcursionStatus.Returning;
            ticket.retryTick = 0;
            Require(!RealityAdjacentPolicy.IsReturnDue(ticket, 101, false, true, false, true),
                "provider work bypassed return gating");
            ticket.status = RealityExcursionStatus.Quarantined;
            Require(!RealityAdjacentPolicy.IsReturnDue(ticket, 101, true, true, false, false) &&
                RealityAdjacentPolicy.HasActiveLease(new[] { ticket }, 2),
                "quarantined recovery ownership was either returned automatically or evicted");
            ticket.status = RealityExcursionStatus.Completed;
            Require(!RealityAdjacentPolicy.HasActiveLease(new[] { ticket }, 2),
                "completed excursion continued to block its maps");
            ticket.status = RealityExcursionStatus.Cancelled;
            ticket.terminalTick = -1;
            Require(RealityAdjacentPolicy.HasActiveLease(new[] { ticket }, 2),
                "cancelled excursion awaiting exact return stopped blocking its maps");
            Require(!RealityRetentionPolicy.IsTerminalExcursion(ticket),
                "unresolved cancellation was classified as terminal");
            ticket.terminalTick = 250;
            Require(RealityRetentionPolicy.IsTerminalExcursion(ticket) &&
                !RealityAdjacentPolicy.HasActiveLease(new[] { ticket }, 2),
                "cancelled excursion with verified return remained an active lease");
            ticket.status = RealityExcursionStatus.ReturnRequested;
            ticket.terminalTick = -1;
            Require(!RealityRetentionPolicy.IsTerminalExcursion(ticket) &&
                RealityAdjacentPolicy.HasActiveLease(new[] { ticket }, 2),
                "return-requested excursion was treated as historical");
        }

        private static void ExcursionReturnLifecycleContracts()
        {
            RealityExcursionTicket ticket = NewReturnTicket(RealityExcursionStatus.ReturnRequested);
            Require(RealityAdjacentPolicy.IsReturnDue(ticket, 101, true, true, false, false),
                "ReturnRequested did not authorize the first return boundary");

            ticket.status = RealityExcursionStatus.Returning;
            ticket.retryTick = 300;
            Require(!RealityRetentionPolicy.IsTerminalExcursion(ticket) &&
                RealityAdjacentPolicy.HasActiveLease(new[] { ticket }, ticket.destinationMapUniqueId),
                "Returning did not retain recoverable ownership");

            IReadOnlyList<RealityExcursionTicket> reloaded = RealityRetentionPolicy.SelectExcursions(
                new[] { ticket }, 1000);
            Require(reloaded.Count == 1 && reloaded[0].status == RealityExcursionStatus.Returning &&
                reloaded[0].retryTick == 300,
                "save/load selection changed a durable Returning ticket");

            var pendingGate = new TestReturnGate(RealityExcursionReturnDisposition.Pending, "provider work is still active");
            Require(RealityAdjacentPolicy.EvaluateReturn(pendingGate, ticket, 300, out string pendingDiagnostic) ==
                RealityExcursionReturnDisposition.Pending && pendingDiagnostic == "provider work is still active",
                "a provider Pending return gate was not retained");

            var readyGate = new TestReturnGate(RealityExcursionReturnDisposition.Ready, "provider work complete");
            Require(RealityAdjacentPolicy.EvaluateReturn(readyGate, ticket, 300, out string readyDiagnostic) ==
                RealityExcursionReturnDisposition.Ready && readyDiagnostic == "provider work complete" &&
                RealityAdjacentPolicy.IsReturnDue(ticket, 300, false, true, false, false),
                "a provider Ready return gate did not authorize the inverse-transfer attempt");

            Require(RealityAdjacentPolicy.EvaluateReturn(null, ticket, 300, out string absentDiagnostic) ==
                RealityExcursionReturnDisposition.Ready && absentDiagnostic == null,
                "an absent return gate was not backward-compatible");

            ticket.retryTick = 600;
            IReadOnlyList<RealityExcursionTicket> pendingReload = RealityRetentionPolicy.SelectExcursions(
                new[] { ticket }, 1000);
            Require(pendingReload.Count == 1 && pendingReload[0].status == RealityExcursionStatus.Returning &&
                pendingReload[0].retryTick == 600 && !RealityAdjacentPolicy.IsReturnDue(ticket, 500, false, true, false, false),
                "a Pending gate backoff was not durable across reload");

            ticket.retryTick = 0;
            IReadOnlyList<RealityExcursionTicket> readyReload = RealityRetentionPolicy.SelectExcursions(
                new[] { ticket }, 1000);
            Require(readyReload.Count == 1 && readyReload[0].status == RealityExcursionStatus.Returning &&
                RealityAdjacentPolicy.IsReturnDue(ticket, 1000, false, true, false, false),
                "a Ready gate reload did not leave Returning eligible");

            IReadOnlyList<RealityExcursionTicket> duplicate = RealityRetentionPolicy.SelectExcursions(
                new[] { ticket, NewReturnTicket(RealityExcursionStatus.Completed, ticket.excursionId) }, 1000);
            Require(duplicate.Count == 1 && duplicate[0].status == RealityExcursionStatus.Returning,
                "duplicate monitoring records did not prefer the recoverable Returning ticket");

            ticket.status = RealityExcursionStatus.ReturnRequested;
            ticket.retryTick = 300;
            Require(!RealityAdjacentPolicy.IsReturnDue(ticket, 299, true, true, false, false) &&
                RealityAdjacentPolicy.IsReturnDue(ticket, 300, true, true, false, false),
                "transfer failure retry did not preserve ReturnRequested backoff semantics");

            ticket.status = RealityExcursionStatus.Completed;
            Require(RealityRetentionPolicy.IsTerminalExcursion(ticket) &&
                !RealityAdjacentPolicy.IsReturnDue(ticket, 1000, true, true, false, false),
                "a completed excursion could be replayed by the return monitor");

            ticket = NewReturnTicket(RealityExcursionStatus.Returning);
            Require(RealityAdjacentPolicy.HasActiveLease(new[] { ticket }, ticket.originMapUniqueId),
                "provider removal did not retain the Returning ownership lease");

            var failingGate = new TestReturnGate(RealityExcursionReturnDisposition.Ready, null) { Throw = true };
            Require(RealityAdjacentPolicy.EvaluateReturn(failingGate, ticket, 0, out string failureDiagnostic) ==
                RealityExcursionReturnDisposition.Pending && failureDiagnostic.Contains("failed"),
                "a return-gate failure did not fail closed");
        }

        private static RealityExcursionTicket NewReturnTicket(RealityExcursionStatus status, string id = "return-test")
        {
            return new RealityExcursionTicket
            {
                excursionId = id,
                providerId = "provider",
                pawnLoadId = "pawn:" + id,
                taskId = "task:" + id,
                originRegionId = RealityRegionId.Surface(1).ToString(),
                originMapUniqueId = 1,
                destinationRegionId = RealityRegionId.Surface(2).ToString(),
                destinationMapUniqueId = 2,
                inverseReturnEdge = "south",
                outboundTransferId = "outbound:" + id,
                returnTransferId = "return:" + id,
                startTick = 0,
                graceDeadline = 100,
                lastTaskHeartbeat = 10,
                retryTick = 0,
                status = status,
                terminalTick = status == RealityExcursionStatus.Completed ? 900 : -1
            };
        }

        private static void ThreadGuardDoesNotSelfInitialize()
        {
            bool threw = false;
            Thread worker = new Thread(() =>
            {
                try { RealityThreadGuard.RequireMainThread(); }
                catch (InvalidOperationException) { threw = true; }
            });
            worker.Start();
            worker.Join();
            Require(threw, "a worker thread was allowed to initialize or use the main-thread guard");
        }

        private static void HarmonyTargetResolvers()
        {
            Assembly assembly = typeof(RealityAdjacentConstructionGuards).Assembly;
            foreach (Type type in assembly.GetTypes().Where(item => item.Namespace == "DeferredReality.Materialization"))
            {
                MethodInfo resolver = type.GetMethod("TargetMethods",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (resolver == null) continue;
                try
                {
                    var methods = resolver.Invoke(null, null) as IEnumerable<MethodBase>;
                    Require(methods != null && methods.Any(),
                        "Harmony target resolver returned no methods: " + type.FullName);
                }
                catch (TargetInvocationException exception)
                {
                    throw new InvalidOperationException("Harmony target resolver threw: " + type.FullName,
                        exception.InnerException ?? exception);
                }
            }
        }

        private static void HarmonyPatchRegistrationSmoke()
        {
            const string harmonyId = "lan.deferredreality.puretests";
            var harmony = new Harmony(harmonyId);
            try
            {
                harmony.PatchAll(typeof(RealityAdjacentConstructionGuards).Assembly);
            }
            finally
            {
                harmony.UnpatchAll(harmonyId);
            }
        }

        private static void TransitionCompensationSeams()
        {
            foreach (string failure in new[] { "prepare", "factory", "map", "apply", "constraint", "anchor", "validation" })
            {
                var prepared = new List<FakeStageProvider>();
                var first = new FakeStageProvider("first", failure == "prepare");
                var second = new FakeStageProvider("second", false);
                try
                {
                    foreach (FakeStageProvider provider in new[] { first, second })
                    {
                        prepared.Add(provider);
                        provider.Prepare();
                    }
                    if (failure != "prepare") throw new InvalidOperationException(failure);
                }
                catch
                {
                    foreach (FakeStageProvider provider in RealityTransitionPolicy.ReversePrepared(prepared)) provider.Rollback();
                }
                Require(first.RollbackCount == 1, "prepare-started provider was not included in rollback");
                Require(second.RollbackCount == (failure == "prepare" ? 0 : 1), "" + failure + " did not rollback prepared providers");
                Require(second.RollbackOrder < first.RollbackOrder || first.RollbackOrder == 0,
                    "rollback order was not reverse deterministic");
            }
            var owners = new[] { "provider.alpha", "provider.beta", "provider.alpha" };
            IReadOnlyList<string> selected = RealityTransitionPolicy.SelectOwner(owners, "provider.alpha",
                item => item, item => item == "provider.alpha" ? 1 : 2);
            Require(selected.Count == 2 && selected.All(item => item == "provider.alpha"),
                "compression/provider selection escaped its owner scope");
            Require(RealityTransitionPolicy.SelectOwner(owners, "missing", item => item, item => 0).Count == 0,
                "missing ownership did not fail closed");
        }

        private static void PartialPrepareRollbackAndRetention()
        {
            var partial = new FakeStageProvider("partial", true, true);
            string original = null;
            var rollbackErrors = new List<string>();
            try { partial.Prepare(); }
            catch (Exception exception) { original = exception.Message; }
            try { partial.Rollback(); }
            catch (Exception exception) { rollbackErrors.Add(exception.Message); }
            try { partial.Rollback(); }
            catch (Exception exception) { rollbackErrors.Add(exception.Message); }
            Require(original == "prepare failure" && rollbackErrors.Count == 2 && partial.PrepareStarted,
                "partial preparation did not preserve the original and rollback failures");

            var registration = new RealityProviderRegistration
            {
                providerId = "retention",
                operationRetentionTicks = 100,
                compactableOperationKinds = new List<string> { "demography", "transfer" }
            };
            var operation = new RealityAppliedOperation
            {
                operationId = "op", providerId = "retention", kind = "demography", domainId = "population:1", sequence = 4, tick = 100
            };
            Require(!RealityRetentionPolicy.CanExpireOperation(registration, operation,
                    Array.Empty<RealityOperationRetentionWatermark>(), 300),
                "operation age alone established replay safety");
            Require(RealityRetentionPolicy.CanExpireOperation(registration, operation, new[]
            {
                new RealityOperationRetentionWatermark
                {
                    providerId = "retention", kind = "demography", domainId = "population:1",
                    sequenceMode = true, sequenceCursor = 4, safeThroughTick = 150, proof = "durable-population-cursor"
                }
            }, 300), "a proven operation watermark did not permit safe expiry");
            Require(!RealityRetentionPolicy.CanExpireOperation(registration, operation, new[]
            {
                new RealityOperationRetentionWatermark
                {
                    providerId = "retention", kind = "demography", domainId = "population:2",
                    sequenceMode = true, sequenceCursor = 4, safeThroughTick = 300, proof = "wrong-domain"
                }
            }, 300), "a watermark for another operation domain expired a marker");
            var transferOperation = new RealityAppliedOperation
            {
                operationId = "transfer-op", providerId = "retention", kind = "transfer",
                domainId = "population:1->population:2", sequence = 8, tick = 100
            };
            Require(RealityRetentionPolicy.CanExpireOperation(registration, transferOperation, new[]
            {
                new RealityOperationRetentionWatermark
                {
                    providerId = "retention", kind = "transfer", domainId = "population:1->population:2",
                    sequenceMode = true, sequenceCursor = 8, safeThroughTick = 150, proof = "durable-transfer-cursor"
                }
            }, 300), "a proven transfer watermark did not permit safe expiry");

            var excursions = RealityRetentionPolicy.SelectExcursions(new[]
            {
                new RealityExcursionTicket { excursionId = "active", status = RealityExcursionStatus.Returning },
                new RealityExcursionTicket { excursionId = "old", status = RealityExcursionStatus.Completed, terminalTick = 1 },
                new RealityExcursionTicket { excursionId = "new", status = RealityExcursionStatus.Completed, terminalTick = 950 },
                new RealityExcursionTicket { excursionId = "cancel-pending", status = RealityExcursionStatus.Cancelled, terminalTick = -1 }
            }, 1000, 100, 1);
            Require(excursions.Count == 3 && excursions.Any(item => item.excursionId == "active") &&
                excursions.Any(item => item.excursionId == "cancel-pending") &&
                excursions.Any(item => item.excursionId == "new"), "terminal excursion retention was not bounded safely");
            var retired = RealityRetentionPolicy.SelectRetiredAdjacentMaps(new[]
            {
                new RealityAdjacentMapRecord { mapUniqueId = 1, lifecycle = RealityAdjacentMapLifecycle.Retired, retiredTick = 1 },
                new RealityAdjacentMapRecord { mapUniqueId = 2, lifecycle = RealityAdjacentMapLifecycle.Retired, retiredTick = 950 },
                new RealityAdjacentMapRecord { mapUniqueId = 3, lifecycle = RealityAdjacentMapLifecycle.Active }
            }, 1000, 100, 1);
            Require(retired.Count == 2 && retired.Any(item => item.mapUniqueId == 2) && retired.Any(item => item.mapUniqueId == 3),
                "retired adjacent-map history was not retained deterministically");
            IReadOnlyList<string> firstEviction = RealityAdjacentPolicy.SelectWarmEvictions(new[]
            {
                new KeyValuePair<string, long>("a", 10), new KeyValuePair<string, long>("b", 20),
                new KeyValuePair<string, long>("c", 10)
            }, 1);
            IReadOnlyList<string> secondEviction = RealityAdjacentPolicy.SelectWarmEvictions(new[]
            {
                new KeyValuePair<string, long>("c", 10), new KeyValuePair<string, long>("a", 10),
                new KeyValuePair<string, long>("b", 20)
            }, 1);
            Require(firstEviction.SequenceEqual(secondEviction) && firstEviction[0] == "a",
                "eviction ordering changed with dictionary/input enumeration order");
        }

        private static void ExactlyOnceSequenceBoundaries()
        {
            Require(RealityExactlyOncePolicy.CanAccept(-1, 0, false), "the first contiguous sequence was rejected");
            Require(!RealityExactlyOncePolicy.CanAccept(-1, 1, false), "a strict domain accepted a gap");
            Require(RealityExactlyOncePolicy.CanAccept(0, 1, false), "the next sequence was rejected");
            Require(!RealityExactlyOncePolicy.CanAccept(0, 0, false), "a duplicate sequence was accepted");
            Require(!RealityExactlyOncePolicy.CanAccept(0, -1, false), "a negative sequence was accepted");
            Require(RealityExactlyOncePolicy.CanAccept(0, 4, true), "a gap-tolerant domain rejected a later sequence");
            Require(!RealityExactlyOncePolicy.CanAccept(4, 4, true), "a cursor did not reject replay after marker compaction");

            var operation = new RealityAppliedOperation
            {
                operationId = "sequence-op", providerId = "retention", kind = "demography",
                domainId = "population:1", sequence = 4, tick = 100
            };
            var registration = new RealityProviderRegistration
            {
                providerId = "retention", operationRetentionTicks = 100,
                compactableOperationKinds = new List<string> { "demography" }
            };
            var cursor = new RealityOperationRetentionWatermark
            {
                providerId = "retention", kind = "demography", domainId = "population:1",
                sequenceMode = true, sequenceCursor = 4, proof = "cursor-committed", safeThroughTick = 100
            };
            Require(RealityRetentionPolicy.CanExpireOperation(registration, operation, new[] { cursor }, 300),
                "a proven sequence cursor did not permit safe marker expiry");
            Require(!RealityRetentionPolicy.CanExpireOperation(registration,
                    new RealityAppliedOperation { operationId = "operation-id-only", providerId = "retention", kind = "demography",
                        domainId = "population:1", sequence = -1, tick = 100 }, new[] { cursor }, 300),
                "an operation-ID-only marker was compacted by a sequence cursor");
            var oldSaveCursor = new RealityOperationRetentionWatermark();
            Require(oldSaveCursor.sequenceCursor == -1 && !oldSaveCursor.sequenceMode,
                "old operation watermark defaults were not durable");
            Require(RealityExactlyOncePolicy.CanAccept(cursor.sequenceCursor, 5, cursor.allowGaps),
                "a later operation was not accepted after cursor restoration");
        }

        private static void MapIdentityClaims()
        {
            RealityRegionId surface = RealityRegionId.Surface(7);
            RealityRegionId interior = RealityRegionId.Child(surface, RealityLayer.Interior, "room=1", "map=1", "owner.a");
            RealityMapIdentityClaim explicitClaim = new RealityMapIdentityClaim
            {
                providerId = "owner.a", regionId = interior, identityKey = "room=1"
            };
            Require(RealityMapIdentityPolicy.TrySelectClaim(new[] { explicitClaim }, out RealityMapIdentityClaim selected, out _)
                && selected.regionId == interior, "explicit map identity was not retained");
            Require(!RealityMapIdentityPolicy.TrySelectClaim(new[] { explicitClaim,
                new RealityMapIdentityClaim { providerId = "owner.b", regionId = surface, identityKey = "surface" } },
                out _, out _), "conflicting map claims did not fail closed");
            Require(!RealityMapIdentityPolicy.TrySelectClaim(new[] { explicitClaim,
                new RealityMapIdentityClaim { providerId = "", regionId = surface, identityKey = "invalid" } },
                out _, out _), "invalid map claims did not fail closed");
            Require(!RealityMapIdentityPolicy.TrySelectClaim(new[] { new RealityMapIdentityClaim
                { providerId = "owner.a", regionId = surface, identityKey = "" } }, out _, out _),
                "empty map identity claims did not fail closed");
            Require(RealityMapIdentityPolicy.TrySelectClaim(new[] { new RealityMapIdentityClaim
                { providerId = "owner.a", regionId = surface, identityKey = "layer=surface" } }, out _, out _),
                "a distinct explicit same-tile identity was rejected");
        }

        private static void MapCreationIntentBoundaries()
        {
            RealityRegionId surface = RealityRegionId.Surface(7);
            var claim = new RealityMapIdentityClaim
            {
                providerId = "provider.adjacent", regionId = surface, identityKey = "provider:surface:7"
            };
            Require(RealityMapCreationPolicy.IsOwnerClaimCompatible(surface, "provider.adjacent", claim),
                "core surface region could not be owned by an explicit adjacent provider");
            var intent = new RealityMapCreationIntentRecord
            {
                transactionId = "materialize:test",
                providerId = "provider.adjacent",
                regionId = surface.ToString(),
                originRegionId = RealityRegionId.Surface(6).ToString(),
                originMapUniqueId = 6,
                createdMapUniqueId = 77,
                preexistingMapUniqueId = -1,
                createdTick = 10
            };
            Require(RealityMapCreationPolicy.CanClassifyMap(false, false, intent.transactionId, -1, 77),
                "a newly generated map was not eligible for intent classification");

            Require(!RealityMapCreationPolicy.CanClassifyMap(true, false, intent.transactionId, -1, 77),
                "an existing ordinary map could be reclassified by an intent");
            Require(!RealityMapCreationPolicy.CanClassifyMap(false, false, intent.transactionId, 77, 78),
                "a map with a different bound identity was accepted by an intent");
            var marker = new RealityAdjacentMapRecord
            {
                transactionId = intent.transactionId, mapUniqueId = 77
            };
            Require(RealityMapCreationPolicy.ShouldClearAfterLoad(intent, marker),
                "save/load did not resolve a committed creation intent");
            marker.mapUniqueId = 78;
            Require(!RealityMapCreationPolicy.ShouldClearAfterLoad(intent, marker),
                "save/load cleared an intent for a different map");
            intent.createdMapUniqueId = -1;
            Require(!RealityMapCreationPolicy.ShouldClearAfterLoad(intent, new RealityAdjacentMapRecord
            {
                transactionId = intent.transactionId, mapUniqueId = 77
            }), "an unbound creation intent was cleared by load repair");
            Require(RealityMapCreationPolicy.ResolveDisposition(false, true, false, false, true) ==
                RealityMapCreationIntentDisposition.Committed &&
                RealityMapCreationPolicy.ResolveDisposition(false, false, false, false, true) ==
                RealityMapCreationIntentDisposition.Stale &&
                RealityMapCreationPolicy.ResolveDisposition(false, false, false, true, true) ==
                RealityMapCreationIntentDisposition.FailedRecovery &&
                RealityMapCreationPolicy.ResolveDisposition(false, false, false, false, false) ==
                RealityMapCreationIntentDisposition.Ambiguous,
                "creation-intent load dispositions were not deterministic");
        }

        private static void ConstraintDomainsAndFacets()
        {
            var departed = new RealityConstraint
            {
                providerId = "provider", typeId = "status", regionId = "surface:7",
                conflictDomainKeys = new List<string> { "subject:pawn-1" },
                conflictFacetKeys = new List<string> { "departed:north" }, payload = "north"
            };
            var injured = new RealityConstraint
            {
                providerId = "provider", typeId = "status", regionId = "surface:7",
                conflictDomainKeys = new List<string> { "subject:pawn-1" },
                conflictFacetKeys = new List<string> { "injured" }, payload = "injured"
            };
            Require(RealityConstraintService.ConflictDomainsIntersect(departed, injured), "shared constraint domain was missed");
            Require(!RealityConstraintService.AreSemanticallyIncompatible(departed, injured),
                "compatible facets were treated as a conflict");
            injured.conflictFacetKeys = new List<string> { "departed:north" };
            Require(RealityConstraintService.AreSemanticallyIncompatible(departed, injured),
                "incompatible shared facets were allowed to coexist");
            Require(!RealityConstraintService.AreSemanticallyIncompatible(departed,
                new RealityConstraint { providerId = departed.providerId, typeId = departed.typeId,
                    regionId = departed.regionId, conflictDomainKeys = new List<string>(departed.conflictDomainKeys),
                    conflictFacetKeys = new List<string>(departed.conflictFacetKeys), payload = departed.payload }),
                "identical constraint facts conflicted");
            var implicitNorth = new RealityConstraint
            {
                providerId = "provider", typeId = "status", regionId = "surface:7",
                affectedAnchorIds = new List<string> { "pawn-1" }, conflictFacetKeys = new List<string> { "departed:north" }, payload = "north"
            };
            var implicitInjury = new RealityConstraint
            {
                providerId = "provider", typeId = "status", regionId = "surface:7",
                affectedAnchorIds = new List<string> { "pawn-1" }, conflictFacetKeys = new List<string> { "injured" }, payload = "injured"
            };
            Require(RealityConstraintService.ConflictDomainsIntersect(implicitNorth, implicitInjury) &&
                !RealityConstraintService.AreSemanticallyIncompatible(implicitNorth, implicitInjury),
                "implicit subject domain did not permit independent facets");
        }

        private static void DuplicateRepairSemantics()
        {
            Require(!RealityRepairPolicy.ShouldReplaceDuplicate(null, null),
                "duplicate repair did not retain the first serialized slot");
            Require(RealityRepairPolicy.ShouldReplaceDuplicate(10, 11),
                "newer update tick did not replace an older duplicate");
            Require(!RealityRepairPolicy.ShouldReplaceDuplicate(11, 10),
                "older update tick replaced a newer duplicate");
        }
        private sealed class TestReturnGate : IRealityExcursionReturnGate
        {
            private readonly RealityExcursionReturnDisposition disposition;
            private readonly string diagnostic;
            public bool Throw;

            public TestReturnGate(RealityExcursionReturnDisposition disposition, string diagnostic)
            {
                this.disposition = disposition;
                this.diagnostic = diagnostic;
            }

            public RealityExcursionReturnDisposition EvaluateReturn(RealityExcursionTicket ticket, long now,
                out string diagnostic)
            {
                if (Throw) throw new InvalidOperationException("gate failure");
                diagnostic = this.diagnostic;
                return disposition;
            }
        }

        private sealed class TestRegistrationProvider : IRealityProvider
        {
            public TestRegistrationProvider(RealityProviderRegistration registration)
            {
                Registration = registration;
            }

            public RealityProviderRegistration Registration { get; }

            public void OnRegistered(RealityProviderContext context) { }
        }

        private sealed class FakeStageProvider
        {
            private readonly bool failPrepare;
            private readonly bool failRollback;
            public readonly string Id;
            public int RollbackCount;
            public int RollbackOrder;
            public bool PrepareStarted { get; private set; }
            private static int nextRollbackOrder;

            public FakeStageProvider(string id, bool failPrepare, bool failRollback = false)
            {
                Id = id;
                this.failPrepare = failPrepare;
                this.failRollback = failRollback;
            }

            public void Prepare()
            {
                PrepareStarted = true;
                if (failPrepare) throw new InvalidOperationException("prepare failure");
            }

            public void Rollback()
            {
                if (failRollback) throw new InvalidOperationException("rollback failure");
                RollbackCount++;
                RollbackOrder = ++nextRollbackOrder;
            }
        }

        private sealed class TestFactory : IRealityMapFactory
        {
            public bool TryCreateMap(RealityRegionId regionId, RealityMaterializationPlan plan, out Map map, out string diagnostic)
            {
                map = null;
                diagnostic = "pure test factory";
                return false;
            }

            public void RemoveMap(Map map) { }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
