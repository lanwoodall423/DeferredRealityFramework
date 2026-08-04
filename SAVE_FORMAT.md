# Save Format

The framework stores one `DeferredRealityWorldComponent` under the world save.
The root schema key is `deferredRealitySaveSchema`; the current root schema is
`3`. All framework child records carry their own `schemaVersion`.

## Root collections

- `deferredRealityRegions`: descriptors, fidelity, observations, environment, and provider blobs.
- `deferredRealityTopology`: directed or conditional region links.
- `deferredRealityPopulations`: aggregate objective populations and uncertainty.
- `deferredRealityAnchors`: identity-bearing objects and typed provider payloads.
- `deferredRealityConstraints`: unresolved outcomes and causal links.
- `deferredRealityProcesses`: scheduled analytical processes.
- `deferredRealityObservations`: bounded player/sensor/inferred evidence history.
- `deferredRealityProviderPayloads`: explicitly opaque, versioned provider blobs.
- `deferredRealityMapAliases`: legacy `Map.uniqueID` to stable region aliases.
- `deferredRealityMigrations`: idempotent provider migration markers.
- `deferredRealityAppliedOperations`: exactly-once mutation IDs.
- `deferredRealityConflicts`: explicit incompatible-fact reports.
- `deferredRealityQuarantine`: invalid, missing-Def, missing-provider, or duplicate records.
- `deferredRealityTransferJournals`: save-safe adjacent transfer transactions.
- `deferredRealityAdjacentMaps`: explicit temporary adjacent-map role markers.
- `deferredRealityExcursions`: durable Pawn ownership and return leases.

Process records added in root schema 2 contain `pauseReason` and `cancelledTick`.
Missing fields from older saves default to `None` and `-1`. A paused legacy
process is repaired as `Manual` when its provider is present and as
`ProviderUnavailable` when its provider is absent. The runtime scheduler cache
is not serialized; it is rebuilt after load.

`Map.uniqueID` is only stored in an alias or active-map link. A latent region ID
never depends on it. Region IDs round-trip as `rr1|...` strings and include tile,
layer, local slot, instance, parent, and provider namespace.

## Migration and resilience

Loading repairs null lists and duplicate IDs deterministically while retaining an
audit quarantine record. Unknown provider data is preserved as a string blob.
Missing Defs do not erase a population or anchor. Missing providers suspend their
processes without changing payload, execution count, deterministic epoch, or
overdue timing; registration resumes only those `ProviderUnavailable` processes.
Manual, provider-failure, and provider-requested pauses require explicit resume.
A provider-specific migration is committed only after all imported records
validate; repeating a load is safe.

Constraints may persist optional `conflictDomainKeys` and `conflictFacetKeys`.
Missing lists default to empty, preserving legacy fallback matching. A legacy
constraint without keys uses provider/type plus subject, facet, or region as its
conservative domain. Duplicate repair retains the first serialized valid record;
an explicit higher `schemaVersion` or newer update tick may replace that value in
the first slot, and the displaced duplicate is quarantined.

Storage compaction is conservative and deterministic. Observation history is
bounded at 8192 records, quarantine at 4096 unique records, and conflicts at
2048 unique reports. Terminal transfer journals are retained for 600000 ticks
and capped at 256 newest terminal records; interrupted journals are always
retained. Exactly-once operation markers remain durable unless the provider
declares a positive retention window and explicitly lists the operation kind.
Cancelled processes and map aliases are removed only with the corresponding
provider policy and no persisted recovery reference.

Interrupted adjacent transfers remain journaled; completed IDs are idempotent and
incomplete transactions are recovered or rolled back only through the registered
provider host. An outbound excursion ticket is written only after the exact Pawn
instance is confirmed on the declared destination map. Return completion is written
only after that same instance is confirmed on its exact origin map; no Pawn summary
is sufficient for reconciliation. Missing origins, missing providers, world-pawn or
caravan storage, duplicate load IDs, and ambiguous ownership retain the map/ticket
and produce quarantine diagnostics rather than selecting another colony map.

At most one nonterminal excursion ticket may own a Pawn load ID. A completed return
journal can finalize an existing Returning ticket after load only when the exact
Pawn is found on its recorded origin map; otherwise the ticket remains recoverable.
Removing a warm-cache reference alone is never treated as map eviction. Provider
removal retains the map and ticket until the provider is restored.

Adjacent map markers are typed records, not reason-string conventions. They retain
provider, region, origin map/region, creation/access ticks, and lifecycle. Marked
maps are temporary non-buildable work sites; the construction guard rejects build,
install, blueprint, and frame paths while leaving map generation and deconstruction
available. The `enableAdjacentRegions` setting remains opt-in and defaults false.
Excursion leases retain grace/deadline, heartbeat, return transfer ID, retry, status,
and diagnostics. The world monitor runs at a coarse interval, returns only safely
idle Pawns, and uses retry backoff for unsafe, missing-origin, or unavailable-provider
states. Warm-map eviction is recency ordered and requires returned Pawns, no player
or recovery references, successful provider compression, and a real registered map
factory removal.

Legacy Wildlife, Aquaculture, and Horticulture keys remain owned by their original
assemblies. Their Deferred Reality adapters import into new records and do not
rewrite or delete the legacy collections.

Map aliases are authoritative migration records. A live map with no persisted
alias is automatically assigned `Surface(tile)` only when it is an unambiguous
standard surface map. Provider claims are required for custom layers and other
nonstandard maps; conflicting or missing claims are quarantined without changing
an existing active-map identity.
