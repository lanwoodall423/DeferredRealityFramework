using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using DeferredReality.API;
using DeferredReality.Materialization;
using DeferredReality.Simulation;
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
                DeterministicSeedAndStream();
                ProviderScopedFactoryIsolation();
                SchedulerGateAndOrdering();
                CatchUpBoundaries();
                PauseCauseTransitions();
                RetentionSelection();
                TransitionCompensationSeams();
                PartialPrepareRollbackAndRetention();
                ExactlyOnceSequenceBoundaries();
                MapIdentityClaims();
                MapCreationIntentBoundaries();
                ConstraintDomainsAndFacets();
                DuplicateRepairSemantics();
                SaveCompatibleDefaults();
                AdjacentPolicyBoundaries();
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
            var fallback = new TestFactory();
            RealityMapFactoryRegistry.Factory = fallback;
            Require(!RealityMapFactoryRegistry.TryGetScoped("missing", out _),
                "provider-scoped factory resolution used the legacy fallback");
            Require(RealityMapFactoryRegistry.TryGet("missing", out IRealityMapFactory fallbackResolved) &&
                ReferenceEquals(fallback, fallbackResolved), "legacy fallback behavior changed");
            RealityMapFactoryRegistry.Factory = null;
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
            Require(!RealityRetentionPolicy.CanExpireOperation(registration, "safe", 100, 200),
                "operation age alone authorized expiry");
            Require(!RealityRetentionPolicy.CanExpireOperation(registration, "unknown", 1, 1000), "unknown operation domain expired");
            var first = new RealityObservationRecord { observationId = "z", tick = 4 };
            var second = new RealityObservationRecord { observationId = "a", tick = 4 };
            Require(ReferenceEquals(RealityRetentionPolicy.FindOldestObservation(new[] { first, second }), second),
                "observation eviction did not use a linear deterministic minimum");
        }

        private static void SaveCompatibleDefaults()
        {
            var oldProcess = new RealityProcessRecord { paused = true };
            Require(oldProcess.pauseReason == RealityProcessPauseReason.None && oldProcess.cancelledTick == -1,
                "new save fields lack compatible defaults");
            RealityProcessPausePolicy.Normalize(oldProcess, true);
            Require(oldProcess.pauseReason == RealityProcessPauseReason.Manual, "legacy paused process was not repaired as manual");
            var registration = new RealityProviderRegistration();
            Require(registration.operationRetentionTicks == -1 && registration.cancelledProcessRetentionTicks == -1,
                "retention metadata default is not durable");
            var oldConstraint = new RealityConstraint();
            Require(oldConstraint.conflictDomainKeys != null && oldConstraint.conflictFacetKeys != null,
                "constraint conflict-key defaults are not save-compatible");
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
                "adjacent map marker did not have a save-compatible lifecycle default");
            Require(new RealityExcursionTicket().status == RealityExcursionStatus.Active,
                "excursion ticket did not have a save-compatible active default");
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
                    new RealityAppliedOperation { operationId = "legacy", providerId = "retention", kind = "demography",
                        domainId = "population:1", sequence = -1, tick = 100 }, new[] { cursor }, 300),
                "a legacy operation-ID marker was compacted by a sequence cursor");
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
            var legacyNorth = new RealityConstraint
            {
                providerId = "provider", typeId = "status", regionId = "surface:7",
                affectedAnchorIds = new List<string> { "pawn-1" }, conflictFacetKeys = new List<string> { "departed:north" }, payload = "north"
            };
            var legacyInjury = new RealityConstraint
            {
                providerId = "provider", typeId = "status", regionId = "surface:7",
                affectedAnchorIds = new List<string> { "pawn-1" }, conflictFacetKeys = new List<string> { "injured" }, payload = "injured"
            };
            Require(RealityConstraintService.ConflictDomainsIntersect(legacyNorth, legacyInjury) &&
                !RealityConstraintService.AreSemanticallyIncompatible(legacyNorth, legacyInjury),
                "legacy subject fallback did not permit independent facets");
        }

        private static void DuplicateRepairSemantics()
        {
            Require(!RealityRepairPolicy.ShouldReplaceDuplicate(1, 1, null, null),
                "duplicate repair did not retain the first serialized slot");
            Require(RealityRepairPolicy.ShouldReplaceDuplicate(1, 2, null, null),
                "newer schema did not replace an older duplicate");
            Require(RealityRepairPolicy.ShouldReplaceDuplicate(1, 1, 10, 11),
                "newer update tick did not replace an older duplicate");
            Require(!RealityRepairPolicy.ShouldReplaceDuplicate(1, 1, 11, 10),
                "older update tick replaced a newer duplicate");
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
