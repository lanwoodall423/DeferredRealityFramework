using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Verse;

namespace DeferredReality.API
{
    /// <summary>One extensible scalar field in an environment or topology summary.</summary>
    public sealed class RealityPayloadField : IExposable
    {
        public string key;
        public string value;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref value, "value");
        }

        internal RealityPayloadField Clone() => new RealityPayloadField { key = key, value = value };
    }

    /// <summary>Explicitly versioned provider data. It is opaque by design and never treated as an object graph.</summary>
    public sealed class RealityProviderPayload : IExposable
    {
        public string providerId;
        public string payloadId;
        public string data;
        public RealityPayloadLifecycle lifecycle = RealityPayloadLifecycle.Active;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref payloadId, "payloadId");
            Scribe_Values.Look(ref data, "data");
            Scribe_Values.Look(ref lifecycle, "lifecycle", RealityPayloadLifecycle.Active);
        }

        internal RealityProviderPayload Clone() => new RealityProviderPayload
        {
            providerId = providerId,
            payloadId = payloadId,
            data = data,
            lifecycle = lifecycle
        };
    }

    /// <summary>Low-resolution environmental state queryable without a Map.</summary>
    public sealed class RealityEnvironmentSummary : IExposable
    {
        public string biomeDefName;
        public string climateClass;
        public float minimumTemperature = -20f;
        public float maximumTemperature = 30f;
        public float precipitation;
        public float droughtPressure;
        public float vegetation;
        public float forage;
        public float shelter;
        public float waterAvailability;
        public float pollution;
        public float humanPressure;
        public float predatorPressure;
        public float preyPressure;
        public int lastFireTick = -1;
        public int lastDisturbanceTick = -1;
        public List<RealityPayloadField> providerFields = new List<RealityPayloadField>();

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref biomeDefName, "biomeDefName");
            Scribe_Values.Look(ref climateClass, "climateClass");
            Scribe_Values.Look(ref minimumTemperature, "minimumTemperature", -20f);
            Scribe_Values.Look(ref maximumTemperature, "maximumTemperature", 30f);
            Scribe_Values.Look(ref precipitation, "precipitation");
            Scribe_Values.Look(ref droughtPressure, "droughtPressure");
            Scribe_Values.Look(ref vegetation, "vegetation");
            Scribe_Values.Look(ref forage, "forage");
            Scribe_Values.Look(ref shelter, "shelter");
            Scribe_Values.Look(ref waterAvailability, "waterAvailability");
            Scribe_Values.Look(ref pollution, "pollution");
            Scribe_Values.Look(ref humanPressure, "humanPressure");
            Scribe_Values.Look(ref predatorPressure, "predatorPressure");
            Scribe_Values.Look(ref preyPressure, "preyPressure");
            Scribe_Values.Look(ref lastFireTick, "lastFireTick", -1);
            Scribe_Values.Look(ref lastDisturbanceTick, "lastDisturbanceTick", -1);
            Scribe_Collections.Look(ref providerFields, "providerFields", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) providerFields = providerFields ?? new List<RealityPayloadField>();
        }

        internal RealityEnvironmentSummary Clone()
        {
            var result = new RealityEnvironmentSummary
            {
                biomeDefName = biomeDefName,
                climateClass = climateClass,
                minimumTemperature = minimumTemperature,
                maximumTemperature = maximumTemperature,
                precipitation = precipitation,
                droughtPressure = droughtPressure,
                vegetation = vegetation,
                forage = forage,
                shelter = shelter,
                waterAvailability = waterAvailability,
                pollution = pollution,
                humanPressure = humanPressure,
                predatorPressure = predatorPressure,
                preyPressure = preyPressure,
                lastFireTick = lastFireTick,
                lastDisturbanceTick = lastDisturbanceTick
            };
            result.providerFields = (providerFields ?? new List<RealityPayloadField>()).Where(item => item != null)
                .Select(item => item.Clone()).ToList();
            return result;
        }
    }

    /// <summary>Persisted descriptor for durable latent state and its optional live projection binding.</summary>
    public sealed class RealityRegionDescriptor : IExposable
    {
        public string regionId;
        public string label;
        public RealityFidelity fidelity = RealityFidelity.Dormant;
        public RealityObservationPrecision observationLevel = RealityObservationPrecision.Rumor;
        public RealityRegionAuthority authority = RealityRegionAuthority.Latent;
        public int stableSeed;
        public long createdTick;
        public long lastUpdateTick;
        public long lastProjectionTick = -1;
        /// <summary>Runtime/persisted binding to the current Map projection. It is never region identity.</summary>
        public int projectionMapUniqueId = -1;
        public int lastKnownWorldTile = -1;
        public RealityEnvironmentSummary environment = new RealityEnvironmentSummary();
        public List<RealityProviderPayload> providerPayloads = new List<RealityProviderPayload>();

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref label, "label");
            Scribe_Values.Look(ref fidelity, "fidelity", RealityFidelity.Dormant);
            Scribe_Values.Look(ref observationLevel, "observationLevel", RealityObservationPrecision.Rumor);
            Scribe_Values.Look(ref authority, "authority", RealityRegionAuthority.Latent);
            Scribe_Values.Look(ref stableSeed, "stableSeed");
            Scribe_Values.Look(ref createdTick, "createdTick");
            Scribe_Values.Look(ref lastUpdateTick, "lastUpdateTick");
            Scribe_Values.Look(ref lastProjectionTick, "lastProjectionTick", -1L);
            Scribe_Values.Look(ref projectionMapUniqueId, "projectionMapUniqueId", -1);
            Scribe_Values.Look(ref lastKnownWorldTile, "lastKnownWorldTile", -1);
            Scribe_Deep.Look(ref environment, "environment");
            Scribe_Collections.Look(ref providerPayloads, "providerPayloads", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                providerPayloads = providerPayloads ?? new List<RealityProviderPayload>();
                environment = environment ?? new RealityEnvironmentSummary();
            }
        }

        internal RealityRegionDescriptor Clone()
        {
            var result = new RealityRegionDescriptor
            {
                regionId = regionId,
                label = label,
                fidelity = fidelity,
                observationLevel = observationLevel,
                authority = authority,
                stableSeed = stableSeed,
                createdTick = createdTick,
                lastUpdateTick = lastUpdateTick,
                lastProjectionTick = lastProjectionTick,
                projectionMapUniqueId = projectionMapUniqueId,
                lastKnownWorldTile = lastKnownWorldTile,
                environment = environment?.Clone() ?? new RealityEnvironmentSummary()
            };
            result.providerPayloads = (providerPayloads ?? new List<RealityProviderPayload>()).Where(item => item != null)
                .Select(item => item.Clone()).ToList();
            return result;
        }
    }

    /// <summary>
    /// Persisted provider-neutral topology between two durable regions. A connection describes eligibility and
    /// metadata only; it does not perform a transfer, materialization, or state mutation across the edge.
    /// </summary>
    public sealed class RealityRegionConnection : IExposable
    {
        public string connectionId;
        public string sourceRegionId;
        public string destinationRegionId;
        public RealityRegionConnectionDirection direction = RealityRegionConnectionDirection.Directed;
        public string kind;
        /// <summary>Provider-defined stable discriminator when several connections share the same semantic kind.</summary>
        public string identityKey;
        public float traversalCost = -1f;
        public float abstractDistance = -1f;
        public string sourceExitEdge;
        public string destinationEntryEdge;
        public string reverseSourceExitEdge;
        public string reverseDestinationEntryEdge;
        /// <summary>Provider namespace/owner. Missing providers do not make this opaque record disposable.</summary>
        public string ownerNamespace;
        /// <summary>Opaque provider-defined connection semantics; the framework never parses this value.</summary>
        public string providerPayload;
        public RealityRegionConnectionLifecycle lifecycle = RealityRegionConnectionLifecycle.Enabled;
        public List<RealityPayloadField> metadata = new List<RealityPayloadField>();

        /// <summary>Whether the connection can be traversed in either direction.</summary>
        public bool IsBidirectional => direction == RealityRegionConnectionDirection.Bidirectional;

        /// <summary>Whether normal graph queries should return this connection.</summary>
        public bool IsEnabled => lifecycle == RealityRegionConnectionLifecycle.Enabled;

        /// <summary>Builds the canonical deterministic ID for a connection identity.</summary>
        public static string StableId(RealityRegionId sourceRegionId, RealityRegionId destinationRegionId,
            RealityRegionConnectionDirection direction, string kind, string ownerNamespace = null, string identityKey = null)
        {
            return StableId(sourceRegionId.ToString(), destinationRegionId.ToString(), direction, kind, ownerNamespace, identityKey);
        }

        /// <summary>Builds a canonical ID from already serialized region identities.</summary>
        public static string StableId(string sourceRegionId, string destinationRegionId,
            RealityRegionConnectionDirection direction, string kind, string ownerNamespace = null, string identityKey = null)
        {
            string left = sourceRegionId ?? string.Empty;
            string right = destinationRegionId ?? string.Empty;
            if (direction == RealityRegionConnectionDirection.Bidirectional &&
                string.CompareOrdinal(left, right) > 0)
            {
                string swap = left;
                left = right;
                right = swap;
            }
            return "connection:" + RealityDeterminism.Combine(left, right, direction.ToString(), kind ?? string.Empty,
                ownerNamespace ?? string.Empty, identityKey ?? string.Empty).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Whether the persisted ID matches the canonical identity fields.</summary>
        public bool HasStableIdentity => string.Equals(connectionId,
            StableId(sourceRegionId, destinationRegionId, direction, kind, ownerNamespace, identityKey),
            StringComparison.Ordinal);

        /// <summary>Returns true when this connection can be traversed from the supplied region.</summary>
        public bool IsOutgoingFrom(string regionId)
        {
            return IsEnabled && (string.Equals(sourceRegionId, regionId, StringComparison.Ordinal) ||
                IsBidirectional && string.Equals(destinationRegionId, regionId, StringComparison.Ordinal));
        }

        /// <summary>Returns true when this connection can be entered at the supplied region.</summary>
        public bool IsIncomingTo(string regionId)
        {
            return IsEnabled && (string.Equals(destinationRegionId, regionId, StringComparison.Ordinal) ||
                IsBidirectional && string.Equals(sourceRegionId, regionId, StringComparison.Ordinal));
        }

        /// <summary>Gets the traversal destination when leaving a supplied endpoint.</summary>
        public bool TryGetDestinationRegionId(string fromRegionId, out string destination)
        {
            destination = null;
            if (!IsOutgoingFrom(fromRegionId)) return false;
            if (string.Equals(sourceRegionId, fromRegionId, StringComparison.Ordinal)) destination = destinationRegionId;
            else destination = sourceRegionId;
            return !string.IsNullOrEmpty(destination);
        }

        /// <summary>Gets the opposite endpoint regardless of direction, for neighborhood inspection.</summary>
        public bool TryGetOtherRegionId(string regionId, out string other)
        {
            other = null;
            if (string.Equals(sourceRegionId, regionId, StringComparison.Ordinal)) other = destinationRegionId;
            else if (string.Equals(destinationRegionId, regionId, StringComparison.Ordinal)) other = sourceRegionId;
            return !string.IsNullOrEmpty(other);
        }

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref connectionId, "connectionId");
            Scribe_Values.Look(ref sourceRegionId, "sourceRegionId");
            Scribe_Values.Look(ref destinationRegionId, "destinationRegionId");
            Scribe_Values.Look(ref direction, "direction", RealityRegionConnectionDirection.Directed);
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref identityKey, "identityKey");
            Scribe_Values.Look(ref traversalCost, "traversalCost", -1f);
            Scribe_Values.Look(ref abstractDistance, "abstractDistance", -1f);
            Scribe_Values.Look(ref sourceExitEdge, "sourceExitEdge");
            Scribe_Values.Look(ref destinationEntryEdge, "destinationEntryEdge");
            Scribe_Values.Look(ref reverseSourceExitEdge, "reverseSourceExitEdge");
            Scribe_Values.Look(ref reverseDestinationEntryEdge, "reverseDestinationEntryEdge");
            Scribe_Values.Look(ref ownerNamespace, "ownerNamespace");
            Scribe_Values.Look(ref providerPayload, "providerPayload");
            Scribe_Values.Look(ref lifecycle, "lifecycle", RealityRegionConnectionLifecycle.Enabled);
            Scribe_Collections.Look(ref metadata, "metadata", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) metadata = metadata ?? new List<RealityPayloadField>();
        }

        internal RealityRegionConnection Clone()
        {
            return new RealityRegionConnection
            {
                connectionId = connectionId,
                sourceRegionId = sourceRegionId,
                destinationRegionId = destinationRegionId,
                direction = direction,
                kind = kind,
                identityKey = identityKey,
                traversalCost = traversalCost,
                abstractDistance = abstractDistance,
                sourceExitEdge = sourceExitEdge,
                destinationEntryEdge = destinationEntryEdge,
                reverseSourceExitEdge = reverseSourceExitEdge,
                reverseDestinationEntryEdge = reverseDestinationEntryEdge,
                ownerNamespace = ownerNamespace,
                providerPayload = providerPayload,
                lifecycle = lifecycle,
                metadata = (metadata ?? new List<RealityPayloadField>()).Where(item => item != null).Select(item => item.Clone()).ToList()
            };
        }
    }

    /// <summary>Generic aggregate population record.</summary>
    public sealed class RealityPopulationRecord : IExposable
    {
        /// <summary>Population is fungible quantity; individual members belong in anchors.</summary>
        public RealityCompressionSignificance compressionSignificance = RealityCompressionSignificance.Aggregate;
        public string populationId;
        public string providerId;
        public string kind;
        public string subjectId;
        public string regionId;
        public float amount;
        public float uncertainty;
        public float carryingCapacity;
        public string demographicPayload;
        public float habitatSuitability = 1f;
        public float pressure;
        public bool migrationAllowed = true;
        public bool established;
        public bool extinct;
        public long lastUpdateTick;
        public List<string> anchoredMemberIds = new List<string>();

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref compressionSignificance, "compressionSignificance", RealityCompressionSignificance.Aggregate);
            Scribe_Values.Look(ref populationId, "populationId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref subjectId, "subjectId");
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref amount, "amount");
            Scribe_Values.Look(ref uncertainty, "uncertainty");
            Scribe_Values.Look(ref carryingCapacity, "carryingCapacity");
            Scribe_Values.Look(ref demographicPayload, "demographicPayload");
            Scribe_Values.Look(ref habitatSuitability, "habitatSuitability", 1f);
            Scribe_Values.Look(ref pressure, "pressure");
            Scribe_Values.Look(ref migrationAllowed, "migrationAllowed", true);
            Scribe_Values.Look(ref established, "established");
            Scribe_Values.Look(ref extinct, "extinct");
            Scribe_Values.Look(ref lastUpdateTick, "lastUpdateTick");
            Scribe_Collections.Look(ref anchoredMemberIds, "anchoredMemberIds", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) anchoredMemberIds = anchoredMemberIds ?? new List<string>();
        }

        internal RealityPopulationRecord Clone()
        {
            var result = (RealityPopulationRecord)MemberwiseClone();
            result.anchoredMemberIds = new List<string>(anchoredMemberIds ?? new List<string>());
            return result;
        }
    }

    /// <summary>Optional spatial location attached to an anchor or observation.</summary>
    public sealed class RealityLocation : IExposable
    {
        public int x = -1;
        public int z = -1;
        public string edge;
        public string areaId;
        public RealityObservationPrecision precision = RealityObservationPrecision.Region;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref x, "x", -1);
            Scribe_Values.Look(ref z, "z", -1);
            Scribe_Values.Look(ref edge, "edge");
            Scribe_Values.Look(ref areaId, "areaId");
            Scribe_Values.Look(ref precision, "precision", RealityObservationPrecision.Region);
        }

        public RealityLocation Clone() => new RealityLocation { x = x, z = z, edge = edge, areaId = areaId, precision = precision };
    }

    /// <summary>Identity-bearing object that must not be replaced by anonymous population detail.</summary>
    public sealed class RealityAnchorRecord : IExposable
    {
        /// <summary>Anchors retain identity across abstraction and projection changes.</summary>
        public RealityCompressionSignificance compressionSignificance = RealityCompressionSignificance.Identity;
        /// <summary>Non-empty load IDs require an explicit provider resolution before compression.</summary>
        public RealityExternalReferenceState externalReferenceState = RealityExternalReferenceState.None;
        public string anchorId;
        public string providerId;
        public string typeId;
        public string regionId;
        public long lastKnownTick;
        public RealityLocation lastKnownLocation = new RealityLocation();
        public int importance;
        public RealityObservationPrecision observationLevel = RealityObservationPrecision.Region;
        public string optionalRimWorldLoadId;
        public RealityAnchorLifecycle lifecycle = RealityAnchorLifecycle.Present;
        public string providerPayload;
        public string causalProvenance;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref compressionSignificance, "compressionSignificance", RealityCompressionSignificance.Identity);
            Scribe_Values.Look(ref externalReferenceState, "externalReferenceState", RealityExternalReferenceState.None);
            Scribe_Values.Look(ref anchorId, "anchorId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref typeId, "typeId");
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref lastKnownTick, "lastKnownTick");
            Scribe_Deep.Look(ref lastKnownLocation, "lastKnownLocation");
            Scribe_Values.Look(ref importance, "importance");
            Scribe_Values.Look(ref observationLevel, "observationLevel", RealityObservationPrecision.Region);
            Scribe_Values.Look(ref optionalRimWorldLoadId, "optionalRimWorldLoadId");
            Scribe_Values.Look(ref lifecycle, "lifecycle", RealityAnchorLifecycle.Present);
            Scribe_Values.Look(ref providerPayload, "providerPayload");
            Scribe_Values.Look(ref causalProvenance, "causalProvenance");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) lastKnownLocation = lastKnownLocation ?? new RealityLocation();
        }

        internal RealityAnchorRecord Clone()
        {
            var result = (RealityAnchorRecord)MemberwiseClone();
            result.lastKnownLocation = lastKnownLocation?.Clone() ?? new RealityLocation();
            return result;
        }
    }

    /// <summary>Deferred fact that future resolution or materialization must honor.</summary>
    public sealed class RealityConstraint : IExposable
    {
        /// <summary>Constraints are established facts unless a provider explicitly vetoes their use.</summary>
        public RealityCompressionSignificance compressionSignificance = RealityCompressionSignificance.EstablishedFact;
        public string constraintId;
        public string providerId;
        public string typeId;
        public string regionId;
        public long createdTick;
        public long validFromTick;
        public long expiryTick = -1;
        public float certainty = 1f;
        public string observerId;
        public string source;
        public RealityObservationPrecision spatialPrecision = RealityObservationPrecision.Region;
        public List<string> affectedAnchorIds = new List<string>();
        public List<string> affectedPopulationIds = new List<string>();
        public List<string> causalParentIds = new List<string>();
        /// <summary>Explicit scopes in which incompatible constraints may conflict.</summary>
        public List<string> conflictDomainKeys = new List<string>();
        /// <summary>Optional semantic facets; disjoint facets are compatible facts.</summary>
        public List<string> conflictFacetKeys = new List<string>();
        public int priority;
        public RealityConflictPolicy conflictPolicy = RealityConflictPolicy.ReportOnly;
        public RealityConstraintStatus status = RealityConstraintStatus.Unresolved;
        public string payload;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref compressionSignificance, "compressionSignificance", RealityCompressionSignificance.EstablishedFact);
            Scribe_Values.Look(ref constraintId, "constraintId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref typeId, "typeId");
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref createdTick, "createdTick");
            Scribe_Values.Look(ref validFromTick, "validFromTick");
            Scribe_Values.Look(ref expiryTick, "expiryTick", -1L);
            Scribe_Values.Look(ref certainty, "certainty", 1f);
            Scribe_Values.Look(ref observerId, "observerId");
            Scribe_Values.Look(ref source, "source");
            Scribe_Values.Look(ref spatialPrecision, "spatialPrecision", RealityObservationPrecision.Region);
            Scribe_Collections.Look(ref affectedAnchorIds, "affectedAnchorIds", LookMode.Value);
            Scribe_Collections.Look(ref affectedPopulationIds, "affectedPopulationIds", LookMode.Value);
            Scribe_Collections.Look(ref causalParentIds, "causalParentIds", LookMode.Value);
            Scribe_Collections.Look(ref conflictDomainKeys, "conflictDomainKeys", LookMode.Value);
            Scribe_Collections.Look(ref conflictFacetKeys, "conflictFacetKeys", LookMode.Value);
            Scribe_Values.Look(ref priority, "priority");
            Scribe_Values.Look(ref conflictPolicy, "conflictPolicy", RealityConflictPolicy.ReportOnly);
            Scribe_Values.Look(ref status, "status", RealityConstraintStatus.Unresolved);
            Scribe_Values.Look(ref payload, "payload");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                affectedAnchorIds = affectedAnchorIds ?? new List<string>();
                affectedPopulationIds = affectedPopulationIds ?? new List<string>();
                causalParentIds = causalParentIds ?? new List<string>();
                conflictDomainKeys = conflictDomainKeys ?? new List<string>();
                conflictFacetKeys = conflictFacetKeys ?? new List<string>();
            }
        }

        internal RealityConstraint Clone()
        {
            var result = (RealityConstraint)MemberwiseClone();
            result.affectedAnchorIds = new List<string>(affectedAnchorIds ?? new List<string>());
            result.affectedPopulationIds = new List<string>(affectedPopulationIds ?? new List<string>());
            result.causalParentIds = new List<string>(causalParentIds ?? new List<string>());
            result.conflictDomainKeys = new List<string>(conflictDomainKeys ?? new List<string>());
            result.conflictFacetKeys = new List<string>(conflictFacetKeys ?? new List<string>());
            return result;
        }

        internal bool AppliesAt(long tick) => (validFromTick <= 0 || tick >= validFromTick) && (expiryTick < 0 || tick <= expiryTick);

        /// <summary>Whether the constraint is valid at a requested analytical tick.</summary>
        public bool IsActiveAt(long tick) => AppliesAt(tick);
    }

    /// <summary>Persisted scheduled process. Providers receive elapsed time analytically.</summary>
    public sealed class RealityProcessRecord : IExposable
    {
        public string processId;
        public string providerId;
        public RealityProcessKind kind = RealityProcessKind.ProviderDefined;
        public string regionId;
        public long nextDueTick;
        public long lastExecutionTick;
        public int intervalTicks = 60000;
        public int priority;
        public int executionCount;
        public bool cancelled;
        public bool paused;
        /// <summary>Persisted pause cause; an unspecified cause is normalized deterministically.</summary>
        public RealityProcessPauseReason pauseReason = RealityProcessPauseReason.None;
        /// <summary>Tick at which cancellation became terminal, or -1 when unknown.</summary>
        public long cancelledTick = -1;
        /// <summary>Durable link to the escalation that currently owns this process's pause.</summary>
        public string pendingEscalationRequestId;
        public string lastError;
        public string payload;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref processId, "processId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref kind, "kind", RealityProcessKind.ProviderDefined);
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref nextDueTick, "nextDueTick");
            Scribe_Values.Look(ref lastExecutionTick, "lastExecutionTick");
            Scribe_Values.Look(ref intervalTicks, "intervalTicks", 60000);
            Scribe_Values.Look(ref priority, "priority");
            Scribe_Values.Look(ref executionCount, "executionCount");
            Scribe_Values.Look(ref cancelled, "cancelled");
            Scribe_Values.Look(ref paused, "paused");
            Scribe_Values.Look(ref pauseReason, "pauseReason", RealityProcessPauseReason.None);
            Scribe_Values.Look(ref cancelledTick, "cancelledTick", -1L);
            Scribe_Values.Look(ref pendingEscalationRequestId, "pendingEscalationRequestId");
            Scribe_Values.Look(ref lastError, "lastError");
            Scribe_Values.Look(ref payload, "payload");
        }

        internal RealityProcessRecord Clone() => (RealityProcessRecord)MemberwiseClone();
    }

    /// <summary>Durable, typed request for a process to run at a higher fidelity.</summary>
    public sealed class RealityFidelityEscalationRecord : IExposable
    {
        public string requestId;
        public string processId;
        public string providerId;
        public string regionId;
        public RealityFidelity currentFidelity = RealityFidelity.Dormant;
        public RealityFidelity requestedFidelity = RealityFidelity.Statistical;
        public RealityFidelityEscalationReason reason = RealityFidelityEscalationReason.InsufficientResolution;
        public RealityFidelityEscalationPolicy policy = RealityFidelityEscalationPolicy.HostApproval;
        public RealityFidelityEscalationStatus status = RealityFidelityEscalationStatus.Pending;
        public string subjectId;
        public long createdTick;
        public long updatedTick;
        public long resolvedTick = -1;
        public int attemptCount;
        public string diagnostic;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref requestId, "requestId");
            Scribe_Values.Look(ref processId, "processId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref currentFidelity, "currentFidelity", RealityFidelity.Dormant);
            Scribe_Values.Look(ref requestedFidelity, "requestedFidelity", RealityFidelity.Statistical);
            Scribe_Values.Look(ref reason, "reason", RealityFidelityEscalationReason.InsufficientResolution);
            Scribe_Values.Look(ref policy, "policy", RealityFidelityEscalationPolicy.HostApproval);
            Scribe_Values.Look(ref status, "status", RealityFidelityEscalationStatus.Pending);
            Scribe_Values.Look(ref subjectId, "subjectId");
            Scribe_Values.Look(ref createdTick, "createdTick");
            Scribe_Values.Look(ref updatedTick, "updatedTick");
            Scribe_Values.Look(ref resolvedTick, "resolvedTick", -1L);
            Scribe_Values.Look(ref attemptCount, "attemptCount");
            Scribe_Values.Look(ref diagnostic, "diagnostic");
        }

        internal RealityFidelityEscalationRecord Clone() => (RealityFidelityEscalationRecord)MemberwiseClone();
    }

    /// <summary>Observation record. It never mutates the objective population value.</summary>
    public sealed class RealityObservationRecord : IExposable
    {
        /// <summary>Established observations constrain rematerialization; disposable observations may age out.</summary>
        public RealityCompressionSignificance compressionSignificance = RealityCompressionSignificance.EstablishedFact;
        public string observationId;
        public string observerId;
        public string source;
        public long tick;
        public string subjectId;
        public string regionId;
        public float certainty;
        public float confidence;
        public RealityObservationPrecision spatialPrecision = RealityObservationPrecision.Region;
        /// <summary>Structured knowledge location; it is not objective state and may be absent at coarse precision.</summary>
        public RealityLocation location = new RealityLocation();
        public string facet;
        public bool playerObserved;
        public bool sensorDerived;
        public bool inferred;
        public bool rumored;
        public string estimate;
        public RealityObservationLifecycle lifecycle = RealityObservationLifecycle.Active;
        public string supersededByObservationId;
        public string invalidationReason;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref compressionSignificance, "compressionSignificance", RealityCompressionSignificance.EstablishedFact);
            Scribe_Values.Look(ref observationId, "observationId");
            Scribe_Values.Look(ref observerId, "observerId");
            Scribe_Values.Look(ref source, "source");
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref subjectId, "subjectId");
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref certainty, "certainty");
            Scribe_Values.Look(ref confidence, "confidence");
            Scribe_Values.Look(ref spatialPrecision, "spatialPrecision", RealityObservationPrecision.Region);
            Scribe_Deep.Look(ref location, "location");
            Scribe_Values.Look(ref facet, "facet");
            Scribe_Values.Look(ref playerObserved, "playerObserved");
            Scribe_Values.Look(ref sensorDerived, "sensorDerived");
            Scribe_Values.Look(ref inferred, "inferred");
            Scribe_Values.Look(ref rumored, "rumored");
            Scribe_Values.Look(ref estimate, "estimate");
            Scribe_Values.Look(ref lifecycle, "lifecycle", RealityObservationLifecycle.Active);
            Scribe_Values.Look(ref supersededByObservationId, "supersededByObservationId");
            Scribe_Values.Look(ref invalidationReason, "invalidationReason");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) location = location ?? new RealityLocation();
        }

        internal RealityObservationRecord Clone()
        {
            var result = (RealityObservationRecord)MemberwiseClone();
            result.location = location?.Clone() ?? new RealityLocation();
            return result;
        }
    }

    /// <summary>Exactly-once mutation marker for catches, kills, releases, and other provider events.</summary>
    public sealed class RealityAppliedOperation : IExposable
    {
        public string operationId;
        public string providerId;
        /// <summary>Optional provider-defined replay domain; null keeps the marker durable.</summary>
        public string domainId;
        /// <summary>Provider/domain sequence. Negative means operation-ID-only durability.</summary>
        public long sequence = -1;
        public long tick;
        public string kind;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref operationId, "operationId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref domainId, "domainId");
            Scribe_Values.Look(ref sequence, "sequence", -1);
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref kind, "kind");
        }

        internal RealityAppliedOperation Clone() => (RealityAppliedOperation)MemberwiseClone();
    }

    /// <summary>Provider proof that exactly-once markers in one domain cannot legitimately replay before this tick.</summary>
    public sealed class RealityOperationRetentionWatermark : IExposable
    {
        public string providerId;
        public string kind;
        public string domainId;
        /// <summary>True only when this record declares a sequence replay boundary.</summary>
        public bool sequenceMode;
        /// <summary>Highest durably accepted sequence in the provider/domain.</summary>
        public long sequenceCursor = -1;
        /// <summary>True permits deterministic out-of-order sequences; false requires cursor+1.</summary>
        public bool allowGaps;
        public long safeThroughTick = -1;
        public long updatedTick;
        public string proof;

        public void ExposeData()
        {
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref domainId, "domainId");
            Scribe_Values.Look(ref sequenceMode, "sequenceMode", false);
            Scribe_Values.Look(ref sequenceCursor, "sequenceCursor", -1);
            Scribe_Values.Look(ref allowGaps, "allowGaps", false);
            Scribe_Values.Look(ref safeThroughTick, "safeThroughTick", -1);
            Scribe_Values.Look(ref updatedTick, "updatedTick");
            Scribe_Values.Look(ref proof, "proof");
        }

        internal RealityOperationRetentionWatermark Clone() => (RealityOperationRetentionWatermark)MemberwiseClone();
    }

    /// <summary>Explicit conflict report retained for diagnostics and future resolution.</summary>
    public sealed class RealityConflictReport : IExposable
    {
        public string conflictId;
        public string regionId;
        public string subjectId;
        public List<string> constraintIds = new List<string>();
        public string reason;
        public long detectedTick;
        public bool acknowledged;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref conflictId, "conflictId");
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref subjectId, "subjectId");
            Scribe_Collections.Look(ref constraintIds, "constraintIds", LookMode.Value);
            Scribe_Values.Look(ref reason, "reason");
            Scribe_Values.Look(ref detectedTick, "detectedTick");
            Scribe_Values.Look(ref acknowledged, "acknowledged");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) constraintIds = constraintIds ?? new List<string>();
        }

        internal RealityConflictReport Clone()
        {
            var result = (RealityConflictReport)MemberwiseClone();
            result.constraintIds = new List<string>(constraintIds ?? new List<string>());
            return result;
        }
    }

    /// <summary>Corrupted or unsupported data kept for inspection instead of being deleted.</summary>
    public sealed class RealityQuarantineRecord : IExposable
    {
        public string recordType;
        public string recordId;
        public string providerId;
        public string reason;
        public string payload;
        public long detectedTick;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref recordType, "recordType");
            Scribe_Values.Look(ref recordId, "recordId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref reason, "reason");
            Scribe_Values.Look(ref payload, "payload");
            Scribe_Values.Look(ref detectedTick, "detectedTick");
        }

        internal RealityQuarantineRecord Clone() => (RealityQuarantineRecord)MemberwiseClone();
    }

    /// <summary>Save-safe role marker for a temporary provider-owned adjacent map.</summary>
    public sealed class RealityAdjacentMapRecord : IExposable
    {
        public int mapUniqueId = -1;
        public string transactionId;
        public string providerId;
        public string regionId;
        public string originRegionId;
        public int originMapUniqueId = -1;
        public long createdTick;
        public long lastAccessTick;
        public long retiredTick = -1;
        public RealityAdjacentMapLifecycle lifecycle = RealityAdjacentMapLifecycle.Materializing;
        public string diagnostic;

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapUniqueId, "mapUniqueId", -1);
            Scribe_Values.Look(ref transactionId, "transactionId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref originRegionId, "originRegionId");
            Scribe_Values.Look(ref originMapUniqueId, "originMapUniqueId", -1);
            Scribe_Values.Look(ref createdTick, "createdTick");
            Scribe_Values.Look(ref lastAccessTick, "lastAccessTick");
            Scribe_Values.Look(ref retiredTick, "retiredTick", -1);
            Scribe_Values.Look(ref lifecycle, "lifecycle", RealityAdjacentMapLifecycle.Materializing);
            Scribe_Values.Look(ref diagnostic, "diagnostic");
        }

        internal RealityAdjacentMapRecord Clone() => (RealityAdjacentMapRecord)MemberwiseClone();
    }

    /// <summary>Save-safe authorization for one materialization transaction to classify one newly created map.</summary>
    public sealed class RealityMapCreationIntentRecord : IExposable
    {
        public string transactionId;
        public string providerId;
        public string regionId;
        public string originRegionId;
        public int originMapUniqueId = -1;
        public int preexistingMapUniqueId = -1;
        public int createdMapUniqueId = -1;
        public long createdTick;
        public RealityAdjacentMapLifecycle lifecycle = RealityAdjacentMapLifecycle.Materializing;
        public string diagnostic;

        public void ExposeData()
        {
            Scribe_Values.Look(ref transactionId, "transactionId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref regionId, "regionId");
            Scribe_Values.Look(ref originRegionId, "originRegionId");
            Scribe_Values.Look(ref originMapUniqueId, "originMapUniqueId", -1);
            Scribe_Values.Look(ref preexistingMapUniqueId, "preexistingMapUniqueId", -1);
            Scribe_Values.Look(ref createdMapUniqueId, "createdMapUniqueId", -1);
            Scribe_Values.Look(ref createdTick, "createdTick");
            Scribe_Values.Look(ref lifecycle, "lifecycle", RealityAdjacentMapLifecycle.Materializing);
            Scribe_Values.Look(ref diagnostic, "diagnostic");
        }

        internal RealityMapCreationIntentRecord Clone() => (RealityMapCreationIntentRecord)MemberwiseClone();
    }

    /// <summary>Durable ownership and return lease for one pawn excursion.</summary>
    public sealed class RealityExcursionTicket : IExposable
    {
        public string excursionId;
        public string providerId;
        public string pawnLoadId;
        public string taskId;
        public string originRegionId;
        public int originMapUniqueId = -1;
        public string destinationRegionId;
        public int destinationMapUniqueId = -1;
        public int originCellX = -1;
        public int originCellZ = -1;
        public string inverseReturnEdge;
        public string outboundTransferId;
        public string returnTransferId;
        public long startTick;
        public long graceDeadline;
        public long lastTaskHeartbeat;
        public long retryTick;
        public long terminalTick = -1;
        public RealityExcursionStatus status = RealityExcursionStatus.Active;
        public string diagnostic;

        public void ExposeData()
        {
            Scribe_Values.Look(ref excursionId, "excursionId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref pawnLoadId, "pawnLoadId");
            Scribe_Values.Look(ref taskId, "taskId");
            Scribe_Values.Look(ref originRegionId, "originRegionId");
            Scribe_Values.Look(ref originMapUniqueId, "originMapUniqueId", -1);
            Scribe_Values.Look(ref destinationRegionId, "destinationRegionId");
            Scribe_Values.Look(ref destinationMapUniqueId, "destinationMapUniqueId", -1);
            Scribe_Values.Look(ref originCellX, "originCellX", -1);
            Scribe_Values.Look(ref originCellZ, "originCellZ", -1);
            Scribe_Values.Look(ref inverseReturnEdge, "inverseReturnEdge");
            Scribe_Values.Look(ref outboundTransferId, "outboundTransferId");
            Scribe_Values.Look(ref returnTransferId, "returnTransferId");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref graceDeadline, "graceDeadline");
            Scribe_Values.Look(ref lastTaskHeartbeat, "lastTaskHeartbeat");
            Scribe_Values.Look(ref retryTick, "retryTick");
            Scribe_Values.Look(ref terminalTick, "terminalTick", -1);
            Scribe_Values.Look(ref status, "status", RealityExcursionStatus.Active);
            Scribe_Values.Look(ref diagnostic, "diagnostic");
        }

        internal RealityExcursionTicket Clone() => (RealityExcursionTicket)MemberwiseClone();
    }

    /// <summary>Bounded diagnostic history for adjacent monitoring and eviction decisions.</summary>
    public sealed class RealityAdjacentDiagnosticRecord : IExposable
    {
        public string diagnosticId;
        public string kind;
        public string providerId;
        public string mapOrExcursionId;
        public long detectedTick;
        public long resolvedTick = -1;
        public string message;

        public void ExposeData()
        {
            Scribe_Values.Look(ref diagnosticId, "diagnosticId");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref mapOrExcursionId, "mapOrExcursionId");
            Scribe_Values.Look(ref detectedTick, "detectedTick");
            Scribe_Values.Look(ref resolvedTick, "resolvedTick", -1);
            Scribe_Values.Look(ref message, "message");
        }

        internal RealityAdjacentDiagnosticRecord Clone() => (RealityAdjacentDiagnosticRecord)MemberwiseClone();
    }

    /// <summary>Save-safe journal for an interrupted adjacent-region transfer.</summary>
    public sealed class RealityTransferJournalRecord : IExposable
    {
        public string transferId;
        public string providerId;
        public string excursionId;
        public string providerTaskId;
        public string sourceRegionId;
        public string destinationRegionId;
        public int sourceMapUniqueId = -1;
        public int destinationMapUniqueId = -1;
        public string edge;
        public string pawnLoadIds;
        public int sourceCellX = -1;
        public int sourceCellZ = -1;
        public long createdTick;
        public long updatedTick;
        public RealityTransferStatus status = RealityTransferStatus.Prepared;
        public string diagnostic;

        /// <inheritdoc />
        public void ExposeData()
        {
            Scribe_Values.Look(ref transferId, "transferId");
            Scribe_Values.Look(ref providerId, "providerId");
            Scribe_Values.Look(ref excursionId, "excursionId");
            Scribe_Values.Look(ref providerTaskId, "providerTaskId");
            Scribe_Values.Look(ref sourceRegionId, "sourceRegionId");
            Scribe_Values.Look(ref destinationRegionId, "destinationRegionId");
            Scribe_Values.Look(ref sourceMapUniqueId, "sourceMapUniqueId", -1);
            Scribe_Values.Look(ref destinationMapUniqueId, "destinationMapUniqueId", -1);
            Scribe_Values.Look(ref edge, "edge");
            Scribe_Values.Look(ref pawnLoadIds, "pawnLoadIds");
            Scribe_Values.Look(ref sourceCellX, "sourceCellX", -1);
            Scribe_Values.Look(ref sourceCellZ, "sourceCellZ", -1);
            Scribe_Values.Look(ref createdTick, "createdTick");
            Scribe_Values.Look(ref updatedTick, "updatedTick");
            Scribe_Values.Look(ref status, "status", RealityTransferStatus.Prepared);
            Scribe_Values.Look(ref diagnostic, "diagnostic");
        }

        internal RealityTransferJournalRecord Clone() => (RealityTransferJournalRecord)MemberwiseClone();
    }
}
