using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeferredReality.API;
using DeferredReality.Materialization;
using DeferredReality.Simulation;
using Herds;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DeferredReality.Wildlife
{
    public sealed partial class WildlifeRealityProvider
    {
        private sealed class PreparedTransfer
        {
            public Pawn pawn;
            public Map sourceMap;
            public IntVec3 sourceCell;
            public Map destinationMap;
            public bool moved;
        }

        private sealed class PreparedAnchorMaterialization
        {
            public Pawn pawn;
            public Map map;
            public IntVec3 originalCell;
            public bool wasInWorldPawns;
            public bool spawned;
        }

        private sealed class MaterializationMapState
        {
            public readonly List<WildlifeSpeciesState> species = new List<WildlifeSpeciesState>();
        }

        private sealed class WildlifeSpeciesState
        {
            public RegionalSpeciesRecord record;
            public float population;
            public float previousPopulation;
            public float nearbyPopulation;
            public float previousNearbyPopulation;
            public int lastUpdateTick;
        }

        internal sealed class WildlifeAnimalDeparture
        {
            internal Map sourceMap;
            internal RealityRegionId sourceRegion;
            internal RealityRegionId destinationRegion;
            internal Pawn animal;
            internal IntVec3 edge;
            internal bool transferred;
        }

        private readonly Dictionary<string, List<PreparedTransfer>> preparedTransfers =
            new Dictionary<string, List<PreparedTransfer>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<PreparedAnchorMaterialization>> preparedAnchorMaterializations =
            new Dictionary<string, List<PreparedAnchorMaterialization>>(StringComparer.Ordinal);
        private readonly Dictionary<string, WildlifeTrailLead> excursionTasks =
            new Dictionary<string, WildlifeTrailLead>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> excursionTaskEvidence =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly HashSet<string> MaterializingRegions =
            new HashSet<string>(StringComparer.Ordinal);

        public int Order => 100;

        /// <summary>Observes only provider-owned state changes; unchanged state does not renew a lease indefinitely.</summary>
        public bool TryObserveExcursionTask(RealityExcursionTicket ticket, long now, out RealityExcursionTaskObservation observation)
        {
            observation = null;
            if (ticket == null || string.IsNullOrEmpty(ticket.excursionId) || string.IsNullOrEmpty(ticket.taskId) ||
                !excursionTasks.TryGetValue(ticket.excursionId, out WildlifeTrailLead lead) || lead == null) return false;
            Pawn pawn = lead.tracker;
            if (lead.state != WildlifeTrailState.BeyondMap)
            {
                observation = new RealityExcursionTaskObservation
                {
                    taskId = ticket.taskId,
                    abandoned = true,
                    diagnostic = "The Wildlife trail task is no longer in its adjacent-region state."
                };
                return true;
            }
            bool onDestination = pawn?.Spawned == true && pawn.Map != null && pawn.Map.uniqueID == ticket.destinationMapUniqueId;
            string fingerprint = (pawn?.Map?.uniqueID.ToString() ?? "missing") + "|" +
                (pawn?.Position.ToString() ?? "none") + "|" + (pawn?.CurJobDef?.defName ?? "none") + "|" + lead.state;
            bool changed = !excursionTaskEvidence.TryGetValue(ticket.excursionId, out string previous) || previous != fingerprint;
            if (changed) excursionTaskEvidence[ticket.excursionId] = fingerprint;
            observation = new RealityExcursionTaskObservation
            {
                taskId = ticket.taskId,
                active = onDestination,
                evidenceTick = changed ? now : -1,
                diagnostic = onDestination ? "Wildlife trail state and Pawn placement observed." :
                    "Wildlife trail state is retained while the exact Pawn is not on the destination map."
            };
            return true;
        }

        /// <summary>Explicit integration hook for reliable provider task progress when the Herds bridge has it.</summary>
        public bool HeartbeatExcursionTask(string excursionId, string diagnostic = null)
        {
            return attachedWorld != null && attachedWorld.HeartbeatExcursion(excursionId, attachedWorld.Now,
                RealityAdjacentPolicy.DefaultLeaseTicks, diagnostic ?? "Wildlife provider task heartbeat.");
        }

        /// <summary>Explicit integration hook for completion of a provider-owned Wildlife task.</summary>
        public bool CompleteExcursionTask(string excursionId, string diagnostic = null)
        {
            return attachedWorld != null && attachedWorld.CompleteExcursion(excursionId,
                diagnostic ?? "Wildlife provider task completed.");
        }

        /// <summary>Explicit integration hook for an abandoned or failed provider-owned Wildlife task.</summary>
        public bool AbandonExcursionTask(string excursionId, string diagnostic = null)
        {
            return attachedWorld != null && attachedWorld.CancelExcursion(excursionId,
                diagnostic ?? "Wildlife provider task was abandoned.");
        }

        public void ForgetExcursionTask(RealityExcursionTicket ticket)
        {
            if (ticket == null) return;
            excursionTasks.Remove(ticket.excursionId);
            excursionTaskEvidence.Remove(ticket.excursionId);
        }

        internal string AdjacentRegionSummary(Map map)
        {
            if (map == null || attachedWorld == null || !attachedWorld.TryRegionForLegacyMap(map.uniqueID, out RealityRegionId source)) return null;
            List<RealityTopologyLink> links = attachedWorld.TopologySnapshots(source.ToString())
                .Where(item => item.kind == "cardinal-surface" && item.fromRegionId == source.ToString()).ToList();
            if (links.Count == 0) return null;
            int known = 0;
            int materialized = 0;
            foreach (RealityTopologyLink link in links)
            {
                if (attachedWorld.PopulationSnapshots(link.toRegionId, ProviderId, "wildlife").Count > 0) known++;
                if (attachedWorld.RegionSnapshots().Any(item => item.id.ToString() == link.toRegionId &&
                    item.fidelity == RealityFidelity.Materialized)) materialized++;
            }
            return "Deferred adjacent regions: " + known + "/" + links.Count + " known" +
                (materialized > 0 ? " • " + materialized + " materialized" : "");
        }

        public void CanMaterialize(RealityMaterializationRequest request, RealityMaterializationPlan plan)
        {
            if (request == null || plan == null || !request.regionId.IsValid ||
                !string.Equals(request.providerId, ProviderId, StringComparison.Ordinal))
            {
                plan?.AddVeto(new RealityVeto("wildlife.materialization-owner",
                    "Wildlife materialization requires an explicit Wildlife owner.", ProviderId));
                return;
            }
            if (!DeferredRealityModSettings.Current.enableAdjacentRegions)
                plan.AddVeto(new RealityVeto("wildlife.adjacent-disabled",
                    "Adjacent Wildlife regions are disabled in framework settings.", ProviderId));
            if (attachedWorld == null || !attachedWorld.TryGetRegion(request.regionId, out _))
                plan.AddVeto(new RealityVeto("wildlife.region-missing",
                    "The Wildlife region is not registered in the framework store.", ProviderId));
            if (attachedWorld != null && attachedWorld.PopulationSnapshots(request.regionId.ToString(), ProviderId, "wildlife").Count == 0)
                plan.AddVeto(new RealityVeto("wildlife.population-missing",
                    "The requested region has no canonical Wildlife populations.", ProviderId, 2));
            plan.AddStep("wildlife-plan-adjacent-habitat");
        }

        public void Prepare(RealityMaterializationRequest request, RealityMaterializationPlan plan)
        {
            plan.AddStep("wildlife-prepare-population-projection");
        }

        public void Apply(RealityMaterializationContext context)
        {
            if (context?.World == null || context.Map == null) return;
            RealityRegionId region = context.Request.regionId;
            context.RuntimeState["wildlife.materialization-map"] = CaptureMaterializationMapState(context.Map);
            context.World.RegisterMap(context.Map, region);
            MaterializingRegions.Remove(region.ToString());
            MigrateMap(context.Map, context.World);
            SyncMap(context.Map);
            EnsureAdjacentTopology(context.World, region);
            context.Plan.AddStep("wildlife-apply-active-projection");
        }

        public void Validate(RealityMaterializationContext context, IList<RealityVeto> vetoes)
        {
            if (context?.Map == null)
            {
                vetoes.Add(new RealityVeto("wildlife.materialization-map",
                    "Wildlife requires a normal generated RimWorld map.", ProviderId));
                return;
            }
            RealityRegionId mapped = context.World.RegisterMap(context.Map, context.Request.regionId);
            if (mapped != context.Request.regionId)
                vetoes.Add(new RealityVeto("wildlife.materialization-identity",
                    "The generated map was not associated with the requested Wildlife region.", ProviderId, 2));
            if (context.World.PopulationSnapshots(context.Request.regionId.ToString(), ProviderId, "wildlife").Count == 0)
                vetoes.Add(new RealityVeto("wildlife.materialization-population",
                    "The active Wildlife projection has no canonical population rows.", ProviderId, 2));
        }

        public void Rollback(RealityMaterializationContext context)
        {
            if (context?.Request != null) MaterializingRegions.Remove(context.Request.regionId.ToString());
            if (context != null && context.RuntimeState.TryGetValue("wildlife.materialization-map", out object value) &&
                value is MaterializationMapState state)
            {
                RestoreMaterializationMapState(state);
                context.RuntimeState.Remove("wildlife.materialization-map");
            }
        }

        private static MaterializationMapState CaptureMaterializationMapState(Map map)
        {
            var state = new MaterializationMapState();
            RegionalWildlifeMapComponent legacy = map?.GetComponent<RegionalWildlifeMapComponent>();
            foreach (RegionalSpeciesRecord record in legacy?.Records ?? Enumerable.Empty<RegionalSpeciesRecord>())
            {
                if (record == null) continue;
                state.species.Add(new WildlifeSpeciesState
                {
                    record = record,
                    population = record.population,
                    previousPopulation = record.previousPopulation,
                    nearbyPopulation = record.nearbyPopulation,
                    previousNearbyPopulation = record.previousNearbyPopulation,
                    lastUpdateTick = record.lastUpdateTick
                });
            }
            return state;
        }

        private static void RestoreMaterializationMapState(MaterializationMapState state)
        {
            foreach (WildlifeSpeciesState value in state?.species ?? Enumerable.Empty<WildlifeSpeciesState>())
            {
                if (value?.record == null) continue;
                value.record.population = value.population;
                value.record.previousPopulation = value.previousPopulation;
                value.record.nearbyPopulation = value.nearbyPopulation;
                value.record.previousNearbyPopulation = value.previousNearbyPopulation;
                value.record.lastUpdateTick = value.lastUpdateTick;
            }
        }

        public bool PrepareAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor, IList<RealityVeto> vetoes)
        {
            if (anchor == null || anchor.providerId != ProviderId) return true;
            if (context?.Map == null)
            {
                vetoes.Add(new RealityVeto("wildlife.anchor-map", "Wildlife anchor materialization requires an active map.", ProviderId));
                return false;
            }
            return true;
        }

        public bool ApplyAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor, IList<RealityVeto> vetoes)
        {
            if (anchor == null || anchor.providerId != ProviderId || context?.Map == null) return true;
            if (!string.IsNullOrEmpty(context.Request.targetAnchorId) && anchor.anchorId != context.Request.targetAnchorId) return true;
            if (string.IsNullOrEmpty(anchor.optionalRimWorldLoadId)) return true;
            Pawn pawn = Find.WorldPawns?.AllPawnsAlive?.FirstOrDefault(candidate =>
                candidate?.GetUniqueLoadID() == anchor.optionalRimWorldLoadId);
            if (pawn == null || pawn.Dead) return true;
            if (pawn.Spawned && pawn.Map != context.Map)
            {
                vetoes.Add(new RealityVeto("wildlife.anchor-spawned", "The Wildlife anchor pawn is already spawned on another map.", ProviderId, 3));
                return false;
            }
            if (pawn.Spawned) return true;
            IntVec3 entry = FindEntryCell(context.Map, context.Request.entryEdge, 0);
            if (!entry.IsValid)
            {
                vetoes.Add(new RealityVeto("wildlife.anchor-entry", "No valid entry cell exists for the Wildlife anchor.", ProviderId, 3));
                return false;
            }
            var state = new PreparedAnchorMaterialization
            {
                pawn = pawn,
                map = context.Map,
                originalCell = pawn.Position,
                wasInWorldPawns = Find.WorldPawns.Contains(pawn)
            };
            if (!preparedAnchorMaterializations.TryGetValue(context.TransactionId, out List<PreparedAnchorMaterialization> states))
                preparedAnchorMaterializations[context.TransactionId] = states = new List<PreparedAnchorMaterialization>();
            // Register compensation before the first irreversible operation.
            states.Add(state);
            try
            {
                if (state.wasInWorldPawns) Find.WorldPawns.RemovePawn(pawn);
                GenSpawn.Spawn(pawn, entry, context.Map, Rot4.Random);
                state.spawned = true;
                anchor.lifecycle = RealityAnchorLifecycle.Present;
                anchor.lastKnownTick = context.Request.now;
                anchor.lastKnownLocation = new RealityLocation
                {
                    x = entry.x, z = entry.z, edge = context.Request.entryEdge,
                    precision = RealityObservationPrecision.Cell
                };
                context.World.UpsertAnchor(anchor);
                return true;
            }
            catch (Exception exception)
            {
                // Spawn can throw after changing Pawn state; make that partial mutation visible to compensation.
                state.spawned = pawn.Spawned && pawn.Map == context.Map;
                vetoes.Add(new RealityVeto("wildlife.anchor-apply", exception.Message, ProviderId, 3));
                return false;
            }
        }

        public bool ValidateAnchorMaterialization(RealityMaterializationContext context, RealityAnchorRecord anchor, IList<RealityVeto> vetoes)
        {
            if (anchor == null || anchor.providerId != ProviderId || context?.World == null) return true;
            RealityAnchorSnapshot snapshot = context.World.AnchorSnapshots(context.Request.regionId.ToString(), ProviderId)
                .FirstOrDefault(item => item.record.anchorId == anchor.anchorId);
            if (snapshot == null || !string.IsNullOrEmpty(anchor.optionalRimWorldLoadId) && snapshot.record.lifecycle != RealityAnchorLifecycle.Present)
            {
                vetoes.Add(new RealityVeto("wildlife.anchor-validation", "The Wildlife anchor was not materialized consistently.", ProviderId, 3));
                return false;
            }
            return true;
        }

        public void RollbackAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor)
        {
            if (context == null || !preparedAnchorMaterializations.TryGetValue(context.TransactionId, out List<PreparedAnchorMaterialization> states)) return;
            for (int i = states.Count - 1; i >= 0; i--)
            {
                PreparedAnchorMaterialization state = states[i];
                if (state.pawn == null) continue;
                if (state.spawned && state.pawn.Spawned) state.pawn.DeSpawn(DestroyMode.Vanish);
                if (state.wasInWorldPawns && !Find.WorldPawns.Contains(state.pawn))
                    Find.WorldPawns.PassToWorld(state.pawn, PawnDiscardDecideMode.KeepForever);
            }
            preparedAnchorMaterializations.Remove(context.TransactionId);
        }

        public void CommitAnchor(RealityMaterializationContext context, RealityAnchorRecord anchor)
        {
            if (context != null) preparedAnchorMaterializations.Remove(context.TransactionId);
        }

        public bool CanTransfer(RealityAdjacentTransferRequest request, IList<RealityVeto> vetoes)
        {
            if (request == null || request.sourceMap == null || request.destinationMap == null ||
                request.sourceMap == request.destinationMap)
                vetoes.Add(new RealityVeto("wildlife.transfer-maps", "A Wildlife transfer requires two different maps.", ProviderId));
            if (request?.pawns == null || request.pawns.Count == 0)
                vetoes.Add(new RealityVeto("wildlife.transfer-party", "A Wildlife transfer requires at least one pawn.", ProviderId));
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Pawn pawn in request?.pawns ?? Array.Empty<Pawn>())
            {
                if (pawn == null || !ids.Add(pawn.GetUniqueLoadID()))
                {
                    vetoes.Add(new RealityVeto("wildlife.transfer-identity", "The transfer party contains a missing or duplicate pawn.", ProviderId));
                    continue;
                }
                if (!pawn.Spawned || pawn.Map != request.sourceMap)
                    vetoes.Add(new RealityVeto("wildlife.transfer-source", "Every transfer pawn must be spawned on the source map.", ProviderId));
                if (pawn.Dead || pawn.Downed || pawn.InMentalState)
                    vetoes.Add(new RealityVeto("wildlife.transfer-state", "Downed, dead, or mentally-affected pawns cannot cross an adjacent edge.", ProviderId));
                if (pawn.Drafted)
                    vetoes.Add(new RealityVeto("wildlife.transfer-drafted", "Drafted pawns cannot cross an adjacent edge through the safe transfer host.", ProviderId));
                if (pawn.GetLord() != null)
                    vetoes.Add(new RealityVeto("wildlife.transfer-lord", "Pawns assigned to a Lord cannot be captured by this transfer host.", ProviderId));
            }
            return vetoes.Count == 0;
        }

        public bool Prepare(RealityAdjacentTransferRequest request, RealityTransferJournalRecord journal, out string diagnostic)
        {
            diagnostic = null;
            if (request == null || journal == null)
            {
                diagnostic = "The Wildlife transfer request or journal is missing.";
                return false;
            }
            var states = new List<PreparedTransfer>();
            foreach (Pawn pawn in request.pawns ?? Array.Empty<Pawn>())
                states.Add(new PreparedTransfer
                {
                    pawn = pawn,
                    sourceMap = request.sourceMap,
                    sourceCell = pawn.Position,
                    destinationMap = request.destinationMap
                });
            preparedTransfers[request.transferId] = states;
            journal.diagnostic = "Wildlife transfer prepared for " + states.Count + " pawns.";
            return true;
        }

        public bool Commit(RealityAdjacentTransferRequest request, RealityTransferJournalRecord journal, out string diagnostic)
        {
            diagnostic = null;
            if (request == null || journal == null || !preparedTransfers.TryGetValue(request.transferId, out List<PreparedTransfer> states))
            {
                diagnostic = "The Wildlife transfer was not prepared in this runtime.";
                return false;
            }
            try
            {
                for (int i = 0; i < states.Count; i++)
                {
                    PreparedTransfer state = states[i];
                    if (state.pawn == null || !state.pawn.Spawned || state.pawn.Map != state.sourceMap)
                        throw new InvalidOperationException("A transfer pawn changed state before commit.");
                    state.pawn.DeSpawn(DestroyMode.Vanish);
                    state.moved = true;
                    IntVec3 entry = FindEntryCell(state.destinationMap, request.entryEdge, i);
                    GenSpawn.Spawn(state.pawn, entry, state.destinationMap, Rot4.Random);
                }
                diagnostic = "Wildlife transfer committed for " + states.Count + " pawns.";
                preparedTransfers.Remove(request.transferId);
                return true;
            }
            catch (Exception exception)
            {
                diagnostic = exception.Message;
                return false;
            }
        }

        public void Rollback(RealityAdjacentTransferRequest request, RealityTransferJournalRecord journal)
        {
            if (request == null || journal == null) return;
            if (!preparedTransfers.TryGetValue(request.transferId ?? string.Empty, out List<PreparedTransfer> states))
            {
                states = new List<PreparedTransfer>();
                foreach (string loadId in (journal.pawnLoadIds ?? string.Empty).Split(',')
                    .Where(value => !string.IsNullOrEmpty(value)))
                {
                    Pawn pawn = FindPawn(request.destinationMap, loadId) ?? FindPawn(request.sourceMap, loadId);
                    if (pawn == null) continue;
                    states.Add(new PreparedTransfer
                    {
                        pawn = pawn,
                        sourceMap = request.sourceMap,
                        sourceCell = request.sourceCell.IsValid
                            ? request.sourceCell
                            : new IntVec3(journal.sourceCellX, 0, journal.sourceCellZ),
                        destinationMap = request.destinationMap,
                        moved = pawn.Map == request.destinationMap
                    });
                }
            }
            for (int i = states.Count - 1; i >= 0; i--)
            {
                PreparedTransfer state = states[i];
                try
                {
                    if (state.pawn == null || !state.moved) continue;
                    if (state.pawn.Spawned) state.pawn.DeSpawn(DestroyMode.Vanish);
                    IntVec3 sourceCell = state.sourceCell.IsValid
                        ? state.sourceCell : CellFinder.RandomEdgeCell(state.sourceMap);
                    GenSpawn.Spawn(state.pawn, sourceCell, state.sourceMap, Rot4.Random);
                }
                catch (Exception exception)
                {
                    Log.Error("[DeferredReality] Wildlife transfer pawn rollback failed: " + exception);
                }
            }
            preparedTransfers.Remove(request.transferId);
        }

        private static Pawn FindPawn(Map map, string loadId)
        {
            return map?.mapPawns?.AllPawnsSpawned?.FirstOrDefault(pawn => pawn?.GetUniqueLoadID() == loadId);
        }

        public bool TryMaterializeTrail(Map sourceMap, WildlifeTrailLead lead)
        {
            if (sourceMap == null || lead?.tracker?.Spawned != true || attachedWorld == null) return false;
            RealityRegionId source = attachedWorld.RegisterMap(sourceMap);
            EnsureAdjacentTopology(attachedWorld, source);
            RealityRegionId destination = SelectAdjacentRegion(source, lead.direction);
            if (!destination.IsValid)
            {
                OpenWorldTravelFallback(sourceMap);
                return false;
            }
            RealityTransitionResult materialization = RealityMaterializationService.TryMaterialize(attachedWorld,
                new RealityMaterializationRequest
                {
                    regionId = destination,
                    providerId = ProviderId,
                    targetAnchorId = lead.targetAnimal == null ? null : "wildlife:animal:" + lead.targetAnimal.thingIDNumber,
                    now = attachedWorld.Now,
                    reason = "follow-wildlife-trail",
                    entryEdge = lead.direction,
                    adjacentMap = new RealityAdjacentMapMetadata
                    {
                        providerId = ProviderId,
                        originRegionId = source,
                        originMapUniqueId = sourceMap.uniqueID,
                        createdTick = attachedWorld.Now
                    }
                });
            if (!materialization.succeeded)
            {
                Messages.Message("The adjacent Wildlife region could not be materialized: " + materialization.error,
                    MessageTypeDefOf.RejectInput, false);
                OpenWorldTravelFallback(sourceMap);
                return false;
            }
            Map destinationMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID ==
                attachedWorld.RegionSnapshots().FirstOrDefault(item => item.id == destination)?.activeMapUniqueId);
            if (destinationMap == null)
            {
                OpenWorldTravelFallback(sourceMap);
                return false;
            }
            string pawnLoadId = lead.tracker.GetUniqueLoadID();
            string outboundTransferId = "wildlife:trail:outbound:" + RealityDeterminism.Combine(
                source.ToString(), destination.ToString(), pawnLoadId, lead.createdTick.ToString(CultureInfo.InvariantCulture));
            string taskId = "wildlife:trail:task:" + RealityDeterminism.Combine(
                source.ToString(), destination.ToString(), pawnLoadId, lead.createdTick.ToString(CultureInfo.InvariantCulture));
            if (!attachedWorld.BeginExcursion(new RealityExcursionRequest
            {
                providerId = ProviderId,
                pawnLoadId = pawnLoadId,
                taskId = taskId,
                originRegionId = source,
                originMapUniqueId = sourceMap.uniqueID,
                destinationRegionId = destination,
                destinationMapUniqueId = destinationMap.uniqueID,
                originCell = lead.departureCell.IsValid ? lead.departureCell : lead.tracker.Position,
                inverseReturnEdge = RealityAdjacentPolicy.InverseEdge(lead.direction),
                outboundTransferId = outboundTransferId,
                returnTransferId = "wildlife:trail:return:" + RealityDeterminism.Combine(source.ToString(), destination.ToString(), pawnLoadId),
                startTick = attachedWorld.Now,
                graceDeadline = attachedWorld.Now + RealityAdjacentPolicy.DefaultGraceTicks
            }, out string excursionId, out string excursionDiagnostic))
            {
                Messages.Message("The Wildlife excursion could not be leased safely: " + excursionDiagnostic,
                    MessageTypeDefOf.RejectInput, false);
                OpenWorldTravelFallback(sourceMap);
                return false;
            }
            if (!attachedWorld.AttachExcursion(excursionId, lead.tracker, out string attachDiagnostic))
            {
                attachedWorld.CancelPendingExcursion(excursionId);
                Messages.Message("The Wildlife excursion could not bind the exact tracker Pawn: " + attachDiagnostic,
                    MessageTypeDefOf.RejectInput, false);
                OpenWorldTravelFallback(sourceMap);
                return false;
            }
            RealityAdjacentTransferResult transfer = RealityAdjacentSurfaceService.TryTransfer(new RealityAdjacentTransferRequest
            {
                providerId = ProviderId,
                sourceMap = sourceMap,
                destinationMap = destinationMap,
                sourceCell = lead.departureCell.IsValid ? lead.departureCell : lead.tracker.Position,
                entryEdge = lead.direction,
                pawns = new[] { lead.tracker },
                transferId = outboundTransferId,
                excursionId = excursionId,
                providerTaskId = taskId,
                isOutboundExcursion = true
            });
            if (!transfer.succeeded)
            {
                attachedWorld.CancelPendingExcursion(excursionId);
                Messages.Message("The trail region was materialized, but the tracker could not cross safely. " +
                    (transfer.diagnostic ?? "Ordinary world travel remains available."), MessageTypeDefOf.RejectInput, false);
                OpenWorldTravelFallback(sourceMap);
                return false;
            }
            excursionTasks[excursionId] = lead;
            excursionTaskEvidence.Remove(excursionId);
            lead.state = WildlifeTrailState.BeyondMap;
            lead.lastOutcome = "The tracker crossed into the adjacent region to continue the trail.";
            Messages.Message(lead.tracker.LabelShortCap + " crossed into the adjacent Wildlife region to continue the trail.",
                lead.tracker, MessageTypeDefOf.PositiveEvent, false);
            return true;
        }

        private static void OpenWorldTravelFallback(Map sourceMap)
        {
            if (sourceMap?.GetComponent<HuntingExpeditionMapComponent>() != null &&
                HerdsMod.Settings?.enableOffMapHuntingExpeditions == true)
                Find.WindowStack.Add(new Window_WildlifeExpeditions(sourceMap));
        }

        internal static void BeginMaterialization(RealityRegionId regionId) => MaterializingRegions.Add(regionId.ToString());

        internal static void RollbackMaterialization(RealityRegionId regionId) => MaterializingRegions.Remove(regionId.ToString());

        internal static bool IsMaterializing(RealityRegionId regionId) => MaterializingRegions.Contains(regionId.ToString());

        private void ReconcileMaterializedAnchor(RealityProviderContext context, RealityAnchorRecord anchor)
        {
            if (context?.World == null || anchor?.providerId != ProviderId ||
                !RealityRegionId.TryParse(anchor.regionId, out RealityRegionId region)) return;
            RealityRegionSnapshot snapshot = context.World.RegionSnapshots().FirstOrDefault(item => item.id == region);
            Map map = Find.Maps?.FirstOrDefault(candidate => candidate != null && candidate.uniqueID == snapshot?.activeMapUniqueId);
            Pawn pawn = map?.mapPawns?.AllPawnsSpawned?.FirstOrDefault(candidate =>
                candidate?.GetUniqueLoadID() == anchor.optionalRimWorldLoadId);
            anchor.lastKnownTick = context.Now;
            anchor.lifecycle = pawn == null ? RealityAnchorLifecycle.Missing : RealityAnchorLifecycle.Present;
            if (pawn != null)
            {
                anchor.lastKnownLocation = new RealityLocation
                {
                    x = pawn.Position.x,
                    z = pawn.Position.z,
                    edge = anchor.lastKnownLocation?.edge,
                    precision = RealityObservationPrecision.Cell
                };
            }
            context.World.UpsertAnchor(anchor);
        }

        internal RealityPopulationMutationResult ReconcileLocalSpawn(Map map, Pawn animal)
        {
            if (map == null || animal?.def?.race?.Animal != true || attachedWorld == null)
                return new RealityPopulationMutationResult { error = "Missing Wildlife spawn context." };
            RealityRegionId source = attachedWorld.RegisterMap(map);
            if (!source.IsValid) return new RealityPopulationMutationResult { error = "The spawned animal has no stable region." };
            RealityAnchorSnapshot anchor = attachedWorld.AnchorSnapshots(providerId: ProviderId)
                .FirstOrDefault(item => item.record.optionalRimWorldLoadId == animal.GetUniqueLoadID());
            if (anchor != null && RealityRegionId.TryParse(anchor.record.regionId, out RealityRegionId previous) && previous != source)
            {
                string sourceId = ProviderPopulationId(source, animal.def.defName);
                string previousId = ProviderPopulationId(previous, animal.def.defName);
                RealityPopulationMutationResult moved = RealityPopulationService.Transfer(attachedWorld, previousId, sourceId, 1f,
                    "wildlife:return:" + animal.thingIDNumber + ":" + attachedWorld.Now + ":" + source,
                    attachedWorld.Now, ProviderId);
                if (moved.succeeded || moved.duplicate)
                {
                    MoveAnchorPopulation(anchor.record.anchorId, animal.def.defName, previous, source);
                    RealityAnchorRecord returned = anchor.record;
                    returned.regionId = source.ToString();
                    returned.lifecycle = RealityAnchorLifecycle.Present;
                    returned.lastKnownTick = attachedWorld.Now;
                    returned.lastKnownLocation = new RealityLocation { x = animal.Position.x, z = animal.Position.z, precision = RealityObservationPrecision.Cell };
                    attachedWorld.UpsertAnchor(returned);
                    return moved;
                }
            }
            if (anchor != null && RealityRegionId.TryParse(anchor.record.regionId, out RealityRegionId anchoredRegion) &&
                anchoredRegion == source && anchor.record.lifecycle == RealityAnchorLifecycle.Traveling)
            {
                RealityAnchorRecord materialized = anchor.record;
                materialized.lifecycle = RealityAnchorLifecycle.Present;
                materialized.lastKnownTick = attachedWorld.Now;
                materialized.lastKnownLocation = new RealityLocation
                {
                    x = animal.Position.x,
                    z = animal.Position.z,
                    precision = RealityObservationPrecision.Cell
                };
                attachedWorld.UpsertAnchor(materialized);
                return new RealityPopulationMutationResult { succeeded = true, duplicate = true, before = 0f, after = 0f };
            }
            string populationId = ProviderPopulationId(source, animal.def.defName);
            return RealityPopulationService.Release(attachedWorld, populationId, 1f,
                "wildlife:spawn:" + animal.thingIDNumber + ":" + attachedWorld.Now,
                attachedWorld.Now, ProviderId);
        }

        internal WildlifeAnimalDeparture PrepareAnimalDeparture(Map map, Pawn animal, IntVec3 edge)
        {
            if (map == null || animal?.def?.race?.Animal != true || animal.Faction != null || attachedWorld == null) return null;
            RealityRegionId source = attachedWorld.RegisterMap(map);
            if (!source.IsValid) return null;
            EnsureAdjacentTopology(attachedWorld, source);
            RealityRegionId destination = SelectAdjacentRegion(source, DirectionFor(edge, map));
            if (!destination.IsValid || destination == source) return null;
            string sourceId = ProviderPopulationId(source, animal.def.defName);
            string destinationId = ProviderPopulationId(destination, animal.def.defName);
            if (!attachedWorld.TryGetPopulation(sourceId, out _) || !attachedWorld.TryGetPopulation(destinationId, out _)) return null;
            RealityPopulationMutationResult result = RealityPopulationService.Transfer(attachedWorld, sourceId, destinationId, 1f,
                "wildlife:departure:" + animal.thingIDNumber + ":" + attachedWorld.Now + ":" + destination,
                attachedWorld.Now, ProviderId);
            return new WildlifeAnimalDeparture
            {
                sourceMap = map,
                sourceRegion = source,
                destinationRegion = destination,
                animal = animal,
                edge = edge,
                transferred = result.succeeded || result.duplicate
            };
        }

        internal void CompleteAnimalDeparture(WildlifeAnimalDeparture departure)
        {
            if (departure?.transferred != true || departure.animal == null || attachedWorld == null) return;
            if (!Find.WorldPawns.Contains(departure.animal)) return;
            string anchorId = "wildlife:animal:" + departure.animal.thingIDNumber;
            RealityAnchorRecord anchor = attachedWorld.AnchorSnapshots(providerId: ProviderId)
                .Select(item => item.record).FirstOrDefault(item => item.anchorId == anchorId) ?? new RealityAnchorRecord
                {
                    anchorId = anchorId,
                    providerId = ProviderId,
                    typeId = "roaming-animal",
                    optionalRimWorldLoadId = departure.animal.GetUniqueLoadID(),
                    importance = 1,
                    observationLevel = RealityObservationPrecision.Edge
                };
            anchor.regionId = departure.destinationRegion.ToString();
            anchor.lastKnownTick = attachedWorld.Now;
            anchor.lifecycle = RealityAnchorLifecycle.Traveling;
            anchor.lastKnownLocation = new RealityLocation
            {
                x = departure.edge.x,
                z = departure.edge.z,
                edge = DirectionFor(departure.edge, departure.sourceMap),
                precision = RealityObservationPrecision.Edge
            };
            anchor.providerPayload = "species=" + departure.animal.def.defName + ";state=traveling";
            anchor.causalProvenance = "wildlife-adjacent-departure:" + departure.sourceRegion;
            attachedWorld.UpsertAnchor(anchor);
            MoveAnchorPopulation(anchor.anchorId, departure.animal.def.defName,
                departure.sourceRegion, departure.destinationRegion);
        }

        private void AddAnchorToPopulation(RealityAnchorRecord anchor, string species)
        {
            if (anchor == null || !RealityRegionId.TryParse(anchor.regionId, out RealityRegionId region)) return;
            string populationId = ProviderPopulationId(region, species);
            if (!attachedWorld.TryGetPopulationRecord(populationId, out RealityPopulationRecord population)) return;
            if (population.anchoredMemberIds.Contains(anchor.anchorId)) return;
            population.anchoredMemberIds.Add(anchor.anchorId);
            attachedWorld.UpsertPopulation(population);
        }

        private void MoveAnchorPopulation(string anchorId, string species, RealityRegionId from, RealityRegionId to)
        {
            if (string.IsNullOrEmpty(anchorId) || string.IsNullOrEmpty(species) || attachedWorld == null) return;
            string fromId = ProviderPopulationId(from, species);
            if (attachedWorld.TryGetPopulationRecord(fromId, out RealityPopulationRecord source))
            {
                source.anchoredMemberIds.Remove(anchorId);
                attachedWorld.UpsertPopulation(source);
            }
            string toId = ProviderPopulationId(to, species);
            if (attachedWorld.TryGetPopulationRecord(toId, out RealityPopulationRecord destination) &&
                !destination.anchoredMemberIds.Contains(anchorId))
            {
                destination.anchoredMemberIds.Add(anchorId);
                attachedWorld.UpsertPopulation(destination);
            }
        }

        private void ReindexAnchors(DeferredRealityWorldComponent world, RealityRegionId region)
        {
            if (world == null || !region.IsValid) return;
            foreach (RealityAnchorSnapshot snapshot in world.AnchorSnapshots(region.ToString(), ProviderId))
            {
                string species = PayloadValue(snapshot.record.providerPayload, "species");
                if (!string.IsNullOrEmpty(species)) AddAnchorToPopulation(snapshot.record, species);
            }
        }

        public bool TryClaimMap(Map map, out RealityMapIdentityClaim claim)
        {
            claim = null;
            WildlifeDeferredMapParent parent = map?.Parent as WildlifeDeferredMapParent;
            if (parent == null || string.IsNullOrEmpty(parent.regionId) || !RealityRegionId.TryParse(parent.regionId, out RealityRegionId region)) return false;
            claim = new RealityMapIdentityClaim { providerId = ProviderId, regionId = region, identityKey = parent.regionId };
            return true;
        }

        internal RealityPopulationMutationResult ApplyAggregateImpact(Map map, ThingDef species, float delta,
            float confidenceGain, string operationSuffix = null)
        {
            if (map == null || species?.race?.Animal != true || attachedWorld == null)
                return new RealityPopulationMutationResult { error = "Missing Wildlife aggregate impact context." };
            RealityRegionId region = attachedWorld.RegisterMap(map);
            string populationId = ProviderPopulationId(region, species.defName);
            if (!attachedWorld.TryGetPopulation(populationId, out _))
                EnsureLatentPopulation(attachedWorld, region, species, map.Biome);
            string operation = "wildlife:impact:" + map.uniqueID + ":" + species.defName + ":" +
                (attachedWorld.Now / 60) + ":" + delta.ToString("R", CultureInfo.InvariantCulture) + ":" +
                confidenceGain.ToString("R", CultureInfo.InvariantCulture) + ":" + (operationSuffix ?? string.Empty);
            RealityPopulationMutationResult result = delta < 0f
                ? RealityPopulationService.Consume(attachedWorld, populationId, -delta, operation, attachedWorld.Now, ProviderId)
                : RealityPopulationService.Release(attachedWorld, populationId, delta, operation, attachedWorld.Now, ProviderId);
            if (result.succeeded || result.duplicate)
            {
                RegionalSpeciesRecord legacy = map.GetComponent<RegionalWildlifeMapComponent>()?.Records?
                    .FirstOrDefault(item => item?.species == species);
                if (legacy != null) legacy.confidence = Mathf.Clamp01(legacy.confidence + confidenceGain);
            }
            return result;
        }

        private void EnsureAdjacentTopology(DeferredRealityWorldComponent world, RealityRegionId source)
        {
            if (world == null || !source.IsValid || !DeferredRealityModSettings.Current.enableAdjacentRegions || Find.WorldGrid == null) return;
            List<RealityTopologyLink> existing = world.TopologySnapshots(source.ToString())
                .Where(item => item.kind == "cardinal-surface" && item.fromRegionId == source.ToString()).ToList();
            if (existing.Count > 0)
            {
                foreach (RealityTopologyLink link in existing)
                    if (RealityRegionId.TryParse(link.toRegionId, out RealityRegionId destination)) SeedLatentRegion(world, destination);
                return;
            }
            Map sourceMap = Find.Maps?.FirstOrDefault(map => map != null && map.uniqueID ==
                world.RegionSnapshots().FirstOrDefault(item => item.id == source)?.activeMapUniqueId);
            if (sourceMap == null) return;
            foreach (RealityRegionSnapshot neighbor in RealityAdjacentSurfaceService.EnsureCardinalNeighbors(sourceMap))
                if (neighbor != null) SeedLatentRegion(world, neighbor.id);
        }

        private void SeedLatentRegion(DeferredRealityWorldComponent world, RealityRegionId region)
        {
            if (world == null || !region.IsValid || Find.WorldGrid == null || region.WorldTile < 0) return;
            Tile tile = Find.WorldGrid[(PlanetTile)region.WorldTile];
            BiomeDef biome = tile?.PrimaryBiome;
            if (biome == null) return;
            PawnKindDef firstKind = biome.AllWildAnimals?.FirstOrDefault(candidate => candidate?.race?.race?.Animal == true);
            world.UpdateEnvironment(region, new RealityEnvironmentSummary
            {
                biomeDefName = biome.defName,
                climateClass = biome.label,
                minimumTemperature = tile.temperature - 12f,
                maximumTemperature = tile.temperature + 12f,
                vegetation = 0.5f,
                forage = firstKind == null ? 0.25f : Mathf.Clamp01(biome.CommonalityOfAnimal(firstKind) / 2f),
                shelter = 0.5f,
                waterAvailability = tile.WaterCovered ? 1f : 0.35f
            }, world.Now);
            foreach (PawnKindDef kind in biome.AllWildAnimals ?? Enumerable.Empty<PawnKindDef>())
            {
                ThingDef species = kind?.race;
                if (species?.race?.Animal != true) continue;
                EnsureLatentPopulation(world, region, species, biome);
            }
        }

        private void EnsureLatentPopulation(DeferredRealityWorldComponent world, RealityRegionId region,
            ThingDef species, BiomeDef biome)
        {
            if (world == null || species == null || !region.IsValid) return;
            string populationId = ProviderPopulationId(region, species.defName);
            if (world.TryGetPopulation(populationId, out _))
            {
                EnsureProcess(world, region, populationId);
                return;
            }
            PawnKindDef kind = biome?.AllWildAnimals?.FirstOrDefault(candidate => candidate?.race == species);
            float commonality = kind == null ? 0.2f : Mathf.Max(0.01f, biome.CommonalityOfAnimal(kind));
            int seed = RealityDeterminism.Seed(world.WorldSeed, region.ToString(), ProviderId, species.defName, 0);
            float amount = Mathf.Clamp(2f + commonality * 14f + Math.Abs(seed % 7), 1f, 90f);
            world.UpsertPopulation(new RealityPopulationRecord
            {
                populationId = populationId,
                providerId = ProviderId,
                kind = "wildlife",
                subjectId = "species:" + species.defName,
                regionId = region.ToString(),
                amount = amount,
                uncertainty = Mathf.Max(1f, amount * 0.32f),
                carryingCapacity = Mathf.Max(8f, amount * 2.8f),
                habitatSuitability = 0.5f,
                migrationAllowed = true,
                established = true,
                extinct = false,
                lastUpdateTick = world.Now,
                demographicPayload = "biome=" + biome?.defName + ";predator=" + (species.race.predator ? "true" : "false")
            });
            EnsureProcess(world, region, populationId);
        }

        private void TryMigratePopulation(RealityPopulationRecord source, RealityProcessRecord process,
            RealityProcessExecution execution)
        {
            if (source == null || process == null || attachedWorld == null || !source.migrationAllowed || source.amount < 1f) return;
            float capacity = Mathf.Max(1f, source.carryingCapacity);
            foreach (RealityTopologyLink link in attachedWorld.TopologySnapshots(source.regionId)
                .Where(item => item.kind == "cardinal-surface" && item.fromRegionId == source.regionId)
                .OrderBy(item => item.linkId, StringComparer.Ordinal))
            {
                RealityPopulationSnapshot destination = attachedWorld.PopulationSnapshots(link.toRegionId, ProviderId, "wildlife")
                    .FirstOrDefault(item => item.record.subjectId == source.subjectId && item.record.migrationAllowed);
                if (destination == null) continue;
                float destinationCapacity = Mathf.Max(1f, destination.record.carryingCapacity);
                float pressure = source.amount / capacity - destination.record.amount / destinationCapacity;
                if (pressure < 0.08f) continue;
                float days = Mathf.Max(0.1f, execution.elapsedTicks / 60000f);
                float amount = Mathf.Clamp(source.amount * pressure * 0.025f * days, 0.1f, Mathf.Min(2.5f, source.amount * 0.12f));
                RealityPopulationMutationResult result = RealityPopulationService.Transfer(attachedWorld,
                    source.populationId, destination.record.populationId, amount,
                    "wildlife:migration:" + process.processId + ":" + process.executionCount + ":" + link.linkId,
                    execution.toTick, ProviderId);
                if (result.succeeded || result.duplicate) return;
            }
        }

        private RealityRegionId SelectAdjacentRegion(RealityRegionId source, string direction)
        {
            List<RealityTopologyLink> links = attachedWorld?.TopologySnapshots(source.ToString())
                .Where(item => item.kind == "cardinal-surface" && item.fromRegionId == source.ToString())
                .OrderBy(item => item.linkId, StringComparer.Ordinal).ToList();
            if (links == null || links.Count == 0) return default(RealityRegionId);
            int hash = RealityDeterminism.StableHash(direction ?? string.Empty);
            if (hash == int.MinValue) hash = int.MaxValue;
            int index = Math.Abs(hash) % links.Count;
            return RealityRegionId.TryParse(links[index].toRegionId, out RealityRegionId result) ? result : default(RealityRegionId);
        }

        private static IntVec3 FindEntryCell(Map map, string edge, int offset)
        {
            if (map == null) return IntVec3.Invalid;
            string normalized = (edge ?? string.Empty).Trim().ToLowerInvariant();
            int width = Math.Max(1, map.Size.x);
            int height = Math.Max(1, map.Size.z);
            int attempts = normalized == "east" || normalized == "west" ? height : width;
            int start = Math.Max(0, offset) % Math.Max(1, attempts);
            for (int i = 0; i < attempts; i++)
            {
                int position = (start + i) % attempts;
                IntVec3 cell;
                switch (normalized)
                {
                    case "north": cell = new IntVec3(position, 0, height - 1); break;
                    case "south": cell = new IntVec3(position, 0, 0); break;
                    case "east": cell = new IntVec3(width - 1, 0, position); break;
                    case "west": cell = new IntVec3(0, 0, position); break;
                    default: cell = CellFinder.RandomEdgeCell(map); break;
                }
                if (cell.InBounds(map) && cell.Standable(map)) return cell;
            }
            IntVec3 fallback = CellFinder.RandomClosewalkCellNear(map.Center, map, 20);
            return fallback.IsValid && fallback.Standable(map) ? fallback : map.Center;
        }

        private static string DirectionFor(IntVec3 cell, Map map)
        {
            if (map == null || !cell.IsValid) return "Nearby";
            int west = cell.x;
            int east = map.Size.x - 1 - cell.x;
            int south = cell.z;
            int north = map.Size.z - 1 - cell.z;
            int minimum = Math.Min(Math.Min(west, east), Math.Min(south, north));
            if (minimum == west) return "West";
            if (minimum == east) return "East";
            if (minimum == south) return "South";
            return "North";
        }

        private static string SpeciesFromSubject(string subject)
        {
            const string prefix = "species:";
            return subject != null && subject.StartsWith(prefix, StringComparison.Ordinal)
                ? subject.Substring(prefix.Length) : subject;
        }

        private static string PayloadValue(string payload, string key)
        {
            string prefix = key + "=";
            return (payload ?? string.Empty).Split(';')
                .FirstOrDefault(item => item.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);
        }

        public void CanCompress(RealityCompressionRequest request, IList<RealityVeto> vetoes)
        {
            if (request == null || request.Map == null || request.regionId.ProviderNamespace != ProviderId ||
                !(request.Map.Parent is WildlifeDeferredMapParent parent) ||
                !string.Equals(parent.regionId, request.regionId.ToString(), StringComparison.Ordinal))
            {
                vetoes.Add(new RealityVeto("wildlife.compression-owner", "The map is not an owned Wildlife adjacent region.",
                    ProviderId, 3));
            }
        }

        // Wildlife state is already represented by the framework's persisted regional records. These
        // no-op stages are an explicit safe-compression policy before the factory removes the map.
        public void Prepare(RealityCompressionRequest request) { }
        public void Validate(RealityCompressionRequest request, IList<RealityVeto> vetoes) { }
        public void Commit(RealityCompressionRequest request) { }
        public void Rollback(RealityCompressionRequest request) { }

        internal static string ProviderPopulationIdForRegion(RealityRegionId regionId, string species) =>
            ProviderPopulationId(regionId, species);
    }

    public sealed class WildlifeDeferredMapParent : MapParent
    {
        public string regionId;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref regionId, "wildlifeDeferredRegionId");
        }

        public override string Label => "Wildlife region";
    }

    public sealed class WildlifeMapFactory : IRealityMapFactory
    {
        public bool TryCreateMap(RealityRegionId regionId, RealityMaterializationPlan plan, out Map map, out string diagnostic)
        {
            map = null;
            diagnostic = null;
            if (Find.World == null || Find.WorldObjects == null)
            {
                diagnostic = "No world is available for Wildlife materialization.";
                return false;
            }
            if (regionId.WorldTile < 0)
            {
                diagnostic = "The Wildlife region has no valid world tile.";
                return false;
            }
            PlanetTile tile = new PlanetTile(regionId.WorldTile);
            WildlifeDeferredMapParent existing = Find.WorldObjects.MapParents?.OfType<WildlifeDeferredMapParent>()
                .FirstOrDefault(item => item != null && item.Tile == tile && item.regionId == regionId.ToString());
            if (existing?.Map != null)
            {
                map = existing.Map;
                return true;
            }
            if (Find.WorldObjects.MapParents?.OfType<WildlifeDeferredMapParent>().Any(item => item != null &&
                item.Tile == tile && item.regionId != regionId.ToString()) == true)
            {
                diagnostic = "The target tile already has a different deferred Wildlife region identity.";
                return false;
            }
            if (Find.WorldObjects.ObjectsAt(tile).Any(item => item is Settlement ||
                item is MapParent mapParent && mapParent.Map != null && !(mapParent is WildlifeDeferredMapParent)))
            {
                diagnostic = "The target tile already contains an incompatible world map or settlement.";
                return false;
            }
            WorldObjectDef definition = DefDatabase<WorldObjectDef>.GetNamedSilentFail("Wildlife_DeferredMapParent");
            if (definition == null)
            {
                diagnostic = "Wildlife_DeferredMapParent is not loaded.";
                return false;
            }
            WildlifeDeferredMapParent parent = WorldObjectMaker.MakeWorldObject(definition) as WildlifeDeferredMapParent;
            if (parent == null)
            {
                diagnostic = "Wildlife_DeferredMapParent could not be constructed.";
                return false;
            }
            parent.Tile = tile;
            parent.regionId = regionId.ToString();
            Find.WorldObjects.Add(parent);
            MapGeneratorDef generator = parent.MapGeneratorDef ?? DefDatabase<MapGeneratorDef>.GetNamedSilentFail("Wildlife_DeferredRegionMap");
            if (generator == null)
            {
                Find.WorldObjects.Remove(parent);
                diagnostic = "Wildlife_DeferredRegionMap is not loaded.";
                return false;
            }
            WildlifeRealityProvider.BeginMaterialization(regionId);
            try
            {
                map = MapGenerator.GenerateMap(new IntVec3(200, 1, 200), parent, generator,
                    parent.ExtraGenStepDefs, null, false, false);
                if (map == null)
                {
                    Find.WorldObjects.Remove(parent);
                    diagnostic = "RimWorld returned no map for the deferred Wildlife generator.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                Find.WorldObjects.Remove(parent);
                diagnostic = exception.Message;
                return false;
            }
            finally
            {
                if (map == null) WildlifeRealityProvider.RollbackMaterialization(regionId);
            }
        }

        public void RemoveMap(Map map)
        {
            WildlifeDeferredMapParent parent = map?.Parent as WildlifeDeferredMapParent;
            if (parent != null && !parent.Destroyed && RealityRegionId.TryParse(parent.regionId, out _)) parent.Destroy();
        }
    }
}
