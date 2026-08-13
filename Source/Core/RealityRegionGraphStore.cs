using System;
using System.Collections.Generic;
using System.Linq;

namespace DeferredReality.API
{
    /// <summary>
    /// Owns the persisted region-topology records and their read indexes.
    /// The world component remains the serialization and diagnostic owner; this store only
    /// validates, indexes, and returns detached graph records.
    /// </summary>
    internal sealed class RealityRegionGraphStore
    {
        private List<RealityRegionConnection> connections = new List<RealityRegionConnection>();
        private readonly Dictionary<string, RealityRegionConnection> connectionById =
            new Dictionary<string, RealityRegionConnection>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<RealityRegionConnection>> outgoingByRegion =
            new Dictionary<string, List<RealityRegionConnection>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<RealityRegionConnection>> incomingByRegion =
            new Dictionary<string, List<RealityRegionConnection>>(StringComparer.Ordinal);
        private bool indexesReady;

        internal IEnumerable<RealityRegionConnection> Records
        {
            get
            {
                EnsureIndexes();
                return connections.Where(item => item != null);
            }
        }

        internal void ExposeData()
        {
            Verse.Scribe_Collections.Look(ref connections, "deferredRealityConnections", Verse.LookMode.Deep);
            if (Verse.Scribe.mode == Verse.LoadSaveMode.PostLoadInit)
            {
                connections = connections ?? new List<RealityRegionConnection>();
                indexesReady = false;
            }
        }

        internal bool Upsert(RealityRegionConnection connection, out string diagnostic)
        {
            EnsureIndexes();
            if (!Validate(connection, out diagnostic)) return false;
            RealityRegionConnection copy = connection.Clone();
            if (connectionById.TryGetValue(copy.connectionId, out RealityRegionConnection existing) &&
                !SameIdentity(existing, copy))
            {
                diagnostic = "A connection ID is already bound to a different connection identity.";
                return false;
            }
            int index = connections.FindIndex(item => item?.connectionId == copy.connectionId);
            if (index >= 0) connections[index] = copy;
            else connections.Add(copy);
            RebuildIndexes();
            diagnostic = null;
            return true;
        }

        internal bool TryGet(string connectionId, out RealityRegionConnection connection)
        {
            EnsureIndexes();
            if (connectionById.TryGetValue(connectionId ?? string.Empty, out RealityRegionConnection value))
            {
                connection = value.Clone();
                return true;
            }
            connection = null;
            return false;
        }

        internal IReadOnlyList<RealityRegionConnection> Snapshots(string regionId = null)
        {
            EnsureIndexes();
            return connections.Where(item => item != null && (string.IsNullOrEmpty(regionId) ||
                    item.sourceRegionId == regionId || item.destinationRegionId == regionId))
                .OrderBy(item => item.connectionId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        internal IReadOnlyList<RealityRegionConnection> Outgoing(string regionId, string kind = null)
        {
            EnsureIndexes();
            return ForRegion(outgoingByRegion, regionId, kind);
        }

        internal IReadOnlyList<RealityRegionConnection> Incoming(string regionId, string kind = null)
        {
            EnsureIndexes();
            return ForRegion(incomingByRegion, regionId, kind);
        }

        internal IReadOnlyList<RealityRegionId> Neighbors(string regionId, string kind = null)
        {
            EnsureIndexes();
            var result = new HashSet<RealityRegionId>();
            foreach (RealityRegionConnection connection in Outgoing(regionId, kind))
                if (connection.TryGetDestinationRegionId(regionId, out string destination) &&
                    RealityRegionId.TryParse(destination, out RealityRegionId parsed)) result.Add(parsed);
            foreach (RealityRegionConnection connection in Incoming(regionId, kind))
                if (connection.TryGetOtherRegionId(regionId, out string source) &&
                    RealityRegionId.TryParse(source, out RealityRegionId parsed)) result.Add(parsed);
            return result.OrderBy(item => item.ToString(), StringComparer.Ordinal).ToList();
        }

        internal List<RealityRegionConnection> Capture()
        {
            EnsureIndexes();
            return connections.Where(item => item != null).Select(item => item.Clone()).ToList();
        }

        internal void Restore(List<RealityRegionConnection> value)
        {
            connections = value ?? new List<RealityRegionConnection>();
            indexesReady = false;
        }

        internal IReadOnlyList<RealityGraphRepairIssue> RepairAndIndex()
        {
            var repaired = new List<RealityRegionConnection>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var issues = new List<RealityGraphRepairIssue>();
            foreach (RealityRegionConnection connection in (connections ?? new List<RealityRegionConnection>()).ToList())
            {
                if (!Validate(connection, out string diagnostic))
                {
                    issues.Add(Issue(connection, diagnostic));
                    continue;
                }
                if (!seen.Add(connection.connectionId))
                {
                    issues.Add(Issue(connection, "Duplicate connection ID; the first valid serialized connection was retained."));
                    continue;
                }
                repaired.Add(connection);
            }
            connections = repaired;
            RebuildIndexes();
            return issues;
        }

        internal bool References(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return Records.Any(connection =>
                Contains(connection.connectionId, value) || Contains(connection.sourceRegionId, value) ||
                Contains(connection.destinationRegionId, value) || Contains(connection.kind, value) ||
                Contains(connection.identityKey, value) || Contains(connection.ownerNamespace, value) ||
                Contains(connection.providerPayload, value) || Contains(connection.sourceExitEdge, value) ||
                Contains(connection.destinationEntryEdge, value) || Contains(connection.reverseSourceExitEdge, value) ||
                Contains(connection.reverseDestinationEntryEdge, value) ||
                (connection.metadata ?? new List<RealityPayloadField>()).Any(field => field != null &&
                    (Contains(field.key, value) || Contains(field.value, value))));
        }

        private IReadOnlyList<RealityRegionConnection> ForRegion(
            Dictionary<string, List<RealityRegionConnection>> index, string regionId, string kind)
        {
            if (string.IsNullOrEmpty(regionId) || !index.TryGetValue(regionId, out List<RealityRegionConnection> values))
                return Array.Empty<RealityRegionConnection>();
            return values.Where(item => item != null && item.IsEnabled &&
                    (string.IsNullOrEmpty(kind) || string.Equals(item.kind, kind, StringComparison.Ordinal)))
                .OrderBy(item => item.connectionId, StringComparer.Ordinal)
                .Select(item => item.Clone()).ToList();
        }

        private void EnsureIndexes()
        {
            if (!indexesReady) RebuildIndexes();
        }

        private void RebuildIndexes()
        {
            connectionById.Clear();
            outgoingByRegion.Clear();
            incomingByRegion.Clear();
            foreach (RealityRegionConnection connection in (connections ?? new List<RealityRegionConnection>())
                .Where(item => item != null))
            {
                if (string.IsNullOrEmpty(connection.connectionId)) continue;
                connectionById[connection.connectionId] = connection;
                AddIndex(outgoingByRegion, connection.sourceRegionId, connection);
                AddIndex(incomingByRegion, connection.destinationRegionId, connection);
                if (connection.IsBidirectional)
                {
                    AddIndex(outgoingByRegion, connection.destinationRegionId, connection);
                    AddIndex(incomingByRegion, connection.sourceRegionId, connection);
                }
            }
            indexesReady = true;
        }

        private static void AddIndex(Dictionary<string, List<RealityRegionConnection>> index,
            string regionId, RealityRegionConnection connection)
        {
            if (string.IsNullOrEmpty(regionId) || connection == null) return;
            if (!index.TryGetValue(regionId, out List<RealityRegionConnection> values))
            {
                values = new List<RealityRegionConnection>();
                index[regionId] = values;
            }
            values.Add(connection);
        }

        private static bool Validate(RealityRegionConnection connection, out string diagnostic)
        {
            diagnostic = null;
            if (connection == null)
            {
                diagnostic = "A region connection is required.";
                return false;
            }
            if (string.IsNullOrEmpty(connection.connectionId) || string.IsNullOrEmpty(connection.sourceRegionId) ||
                string.IsNullOrEmpty(connection.destinationRegionId) || string.IsNullOrEmpty(connection.kind))
            {
                diagnostic = "A connection requires a stable ID, two region IDs, and a semantic kind.";
                return false;
            }
            if (!RealityRegionId.TryParse(connection.sourceRegionId, out RealityRegionId source) || !source.IsValid ||
                !RealityRegionId.TryParse(connection.destinationRegionId, out RealityRegionId destination) || !destination.IsValid)
            {
                diagnostic = "Connection endpoints must be valid RealityRegionIds.";
                return false;
            }
            if (source == destination)
            {
                diagnostic = "A region connection cannot connect a region to itself.";
                return false;
            }
            if (!Enum.IsDefined(typeof(RealityRegionConnectionDirection), connection.direction) ||
                !Enum.IsDefined(typeof(RealityRegionConnectionLifecycle), connection.lifecycle))
            {
                diagnostic = "Connection direction or lifecycle is not defined by the framework.";
                return false;
            }
            if (connection.traversalCost < -1f || float.IsNaN(connection.traversalCost) || float.IsInfinity(connection.traversalCost) ||
                connection.abstractDistance < -1f || float.IsNaN(connection.abstractDistance) || float.IsInfinity(connection.abstractDistance))
            {
                diagnostic = "Connection traversal cost and abstract distance must be absent or finite and non-negative.";
                return false;
            }
            if (!connection.HasStableIdentity)
            {
                diagnostic = "Connection ID does not match its deterministic endpoint, direction, kind, owner, and identity key.";
                return false;
            }
            return true;
        }

        private static bool SameIdentity(RealityRegionConnection left, RealityRegionConnection right)
        {
            if (left == null || right == null || !string.Equals(left.connectionId, right.connectionId, StringComparison.Ordinal) ||
                left.direction != right.direction || !string.Equals(left.kind, right.kind, StringComparison.Ordinal) ||
                !string.Equals(left.ownerNamespace, right.ownerNamespace, StringComparison.Ordinal) ||
                !string.Equals(left.identityKey, right.identityKey, StringComparison.Ordinal)) return false;
            return string.Equals(left.sourceRegionId, right.sourceRegionId, StringComparison.Ordinal) &&
                string.Equals(left.destinationRegionId, right.destinationRegionId, StringComparison.Ordinal) ||
                left.IsBidirectional && string.Equals(left.sourceRegionId, right.destinationRegionId, StringComparison.Ordinal) &&
                string.Equals(left.destinationRegionId, right.sourceRegionId, StringComparison.Ordinal);
        }

        private static RealityGraphRepairIssue Issue(RealityRegionConnection connection, string reason)
        {
            return new RealityGraphRepairIssue
            {
                connectionId = connection?.connectionId,
                ownerNamespace = connection?.ownerNamespace ?? "core",
                reason = reason,
                payload = Fingerprint(connection)
            };
        }

        private static string Fingerprint(RealityRegionConnection connection)
        {
            if (connection == null) return null;
            return (connection.connectionId ?? string.Empty) + "|" + (connection.sourceRegionId ?? string.Empty) + "|" +
                (connection.destinationRegionId ?? string.Empty) + "|" + connection.direction + "|" +
                (connection.kind ?? string.Empty) + "|" + (connection.ownerNamespace ?? string.Empty) + "|" +
                (connection.identityKey ?? string.Empty);
        }

        private static bool Contains(string value, string term)
        {
            return !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(term) &&
                value.IndexOf(term, StringComparison.Ordinal) >= 0;
        }
    }

    internal sealed class RealityGraphRepairIssue
    {
        internal string connectionId;
        internal string ownerNamespace;
        internal string reason;
        internal string payload;
    }
}
