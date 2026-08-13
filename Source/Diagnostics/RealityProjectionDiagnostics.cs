using System;
using System.Collections.Generic;
using DeferredReality.API;
using DeferredReality.Materialization;

namespace DeferredReality.Diagnostics
{
    /// <summary>Read-only diagnostics for temporary projections and their recovery policy.</summary>
    internal static class RealityProjectionDiagnostics
    {
        internal static IReadOnlyList<string> Lines(DeferredRealityWorldComponent world = null)
        {
            world = world ?? DeferredRealityWorldComponent.Current;
            if (world == null) return Array.Empty<string>();
            var lines = new List<string>();
            foreach (RealityAdjacentMapRecord marker in world.AdjacentMapSnapshots())
                lines.Add("map|" + marker.mapUniqueId + "|" + marker.regionId + "|" + marker.providerId + "|" + marker.lifecycle +
                    "|origin=" + marker.originRegionId + "@" + marker.originMapUniqueId + "|last=" + marker.lastAccessTick);
            foreach (RealityExcursionTicket ticket in world.ExcursionSnapshots())
                lines.Add("excursion|" + ticket.excursionId + "|" + ticket.providerId + "|" + ticket.pawnLoadId +
                    "|" + ticket.status + "|" + ticket.originMapUniqueId + "->" + ticket.destinationMapUniqueId +
                    "|task=" + (ticket.taskId ?? string.Empty) + "|heartbeat=" + ticket.lastTaskHeartbeat +
                    "|retry=" + ticket.retryTick + "|" + (ticket.diagnostic ?? string.Empty));
            foreach (RealityMapCreationIntentRecord intent in world.MapCreationIntentSnapshots())
                lines.Add("creation-intent|" + intent.transactionId + "|" + intent.providerId + "|" + intent.regionId +
                    "|map=" + intent.createdMapUniqueId + "|preexisting=" + intent.preexistingMapUniqueId +
                    "|lifecycle=" + intent.lifecycle + "|" + (intent.diagnostic ?? string.Empty));
            lines.Add("construction|marked-maps=" + world.AdjacentMapSnapshots().Count + "|rejection=" +
                RealityAdjacentConstructionGuards.RejectionMessage);
            lines.AddRange(RealityProjectionCacheService.EvictionDiagnosticLines());
            return lines;
        }
    }
}
