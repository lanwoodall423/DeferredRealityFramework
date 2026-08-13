# Save Format

The framework stores one `DeferredRealityWorldComponent` under the world save.
The world component is the only RimWorld serialization owner. DRF has one current
record shape rather than a root/child schema negotiation layer; there are no
pre-release save migrations or compatibility aliases.

## Root collections

- `deferredRealityRegions`: durable region identity, latent fidelity, projection
  authority/binding, observations, environment, and provider blobs.
- `deferredRealityConnections`: persisted provider-neutral region graph
  connections, including endpoint IDs, deterministic identity, direction,
  semantic kind, traversal metadata, owner namespace, opaque payload, and
  enabled/disabled lifecycle.
- `deferredRealityPopulations`: fungible aggregate quantities and uncertainty;
  records persist compression significance and retained anchor membership.
- `deferredRealityAnchors`: identity-bearing objects, compression significance,
  provider-resolved external-reference state, and typed provider payloads.
- `deferredRealityConstraints`: established/disposable facts, unresolved
  outcomes, and causal links.
- `deferredRealityProcesses`: scheduled analytical processes.
- `deferredRealityFidelityEscalations`: durable typed requests for processes
  that need more detail than their current fidelity provides.
- `deferredRealityObservations`: player/sensor/inferred evidence history with
  explicit compression significance. Only `Disposable` observations are
  eligible for age/cap eviction.
- `deferredRealityProviderPayloads`: explicitly opaque provider blobs owned by the
  provider namespace.
- `deferredRealityAppliedOperations`: exactly-once mutation IDs with optional
  provider/domain sequence values. A negative sequence is an operation-ID-only
  marker and is never made compactable without an explicit replay-safety proof.
- `deferredRealityConflicts`: explicit incompatible-fact reports.
- `deferredRealityQuarantine`: invalid, missing-Def, missing-provider, or duplicate records.
- `deferredRealityTransferJournals`: save-safe adjacent transfer transactions.
- `deferredRealityAdjacentMaps`: explicit temporary adjacent-map role markers.
- `deferredRealityExcursions`: durable Pawn ownership and return leases.
- `deferredRealityMapCreationIntents`: transaction-scoped authorization for a
  newly generated adjacent map to become a marked site.
- `deferredRealityAdjacentDiagnostics`: bounded adjacent monitor and eviction
  diagnostics, including resolved history.
- `deferredRealityOperationWatermarks`: provider/domain sequence cursors and proofs
  that markers at or below a durable replay boundary are safe to compact. Sequence
  domains persist `sequenceCursor`, `allowGaps`, and a nonempty proof.

Process records contain `pauseReason` and `cancelledTick`. The runtime scheduler
cache is not serialized; it is rebuilt after load.

Region records store one of the four fidelities: `Dormant` (durable facts with
no recurring simulation), `Statistical` (aggregate analytical evolution),
`Narrative` (bounded abstract events), or `Materialized` (spatial resolution).
Authority is separate: `Latent`, `Materializing`, `LiveProjection`,
`Compressing`, or `Quarantined`. A normal live projection pairs
`Materialized` fidelity with `LiveProjection` authority.

`Map.uniqueID` is only stored as the projection binding for a region. A latent
region ID never depends on it. Region IDs round-trip as `rr1|...` strings and include tile,
layer, local slot, instance, parent, and provider namespace.

Regions describe places. Connections describe topology. Transfers/processes
describe changes across topology. Connection records are not transfer journals,
materialization intents, or process effects.

Fidelity escalation records link a process and region to a typed request for a
higher fidelity. They persist current/requested fidelity, reason, subject,
provider policy, status, attempts, and diagnostics. Pending or approved requests
pause the process until a host/provider resolves them; declined or failed
requests remain inspectable. Approval never creates a map automatically.
Materialization must explicitly satisfy an approved request, and compression
reconciles live state back to latent `Statistical` state.

An adjacent marker's `regionId` identifies the represented region and retains its
own namespace, commonly `core` for `Surface(tile)`. Its `providerId` separately
identifies the integration that owns the temporary site and its lifecycle. A
provider-owned adjacent site may therefore represent a `core` surface region.

Map creation intents persist the materialization transaction, expected region,
adjacent owner, origin, creation tick, lifecycle, and pre-existing/created map
IDs. A readiness callback may classify a map only when the intent, provider
identity claim, tile, and map ID agree. Unbound or conflicting intents are
retained or quarantined rather than guessed. Null collections are repaired to
empty during post-load initialization; map-ID alias matching is never attempted.

## Load repair and resilience

Loading repairs null lists and duplicate IDs deterministically while retaining an
audit quarantine record. Unknown provider data is preserved as a string blob.
Missing Defs do not erase a population or anchor. Missing providers suspend their
processes without changing payload, execution count, deterministic epoch, or
overdue timing; registration resumes only those `ProviderUnavailable` processes.
Manual, provider-failure, and provider-requested pauses require explicit resume.
There is no framework-wide pre-release save migration layer. Providers may import
their own current map-component state during projection registration, but the
framework never infers region identity from an old map-ID alias.

Connection repair validates both endpoint `RealityRegionId` values and the
canonical deterministic connection ID. Invalid records and duplicate IDs are
quarantined; the first valid serialized connection is retained. Missing provider
registrations do not remove connection records.

Constraints may persist optional `conflictDomainKeys` and `conflictFacetKeys`.
When a provider omits them, the current resolver derives a deterministic
conservative domain from provider/type plus subject, facet, or region. Duplicate
repair retains the first serialized valid record; an explicit newer update tick
may replace that value in the first slot, and the displaced duplicate is
quarantined.

Storage compaction is conservative and deterministic. Disposable observation
history is bounded at 8192 records; established observations are protected even
when that cap is reached. Quarantine is capped at 4096 unique records and conflicts at
2048 unique reports. Terminal transfer journals are retained for 600000 ticks
and capped at 256 newest terminal records; interrupted journals are always
retained. Exactly-once operation markers remain durable unless the provider declares
a positive retention window, explicitly lists the operation kind, and declares a
sequence domain. A strict domain accepts only `cursor + 1`; a gap-tolerant domain
accepts a greater sequence but still rejects every sequence at or below the cursor.
The operation is validated against that cursor before population mutation, and the
cursor advances only after the marker and state commit. A marker is removed only
when its sequence is at or below a persisted cursor with a nonempty proof and the
retention age has elapsed. Age, aggregate population values, operation-ID-only markers,
and old tick-only watermarks are never replay-safety proof. Providers without a
proven sequence boundary retain markers indefinitely.
Terminal excursions are retained for 600000 ticks with a deterministic 1024-record
cap, retired adjacent markers for 600000 ticks with a 256-record cap, and resolved
adjacent diagnostics for 600000 ticks with a 2048-record cap. Active, returning,
quarantined, blocked, interrupted, and cancelled tickets awaiting exact return are
never compacted; recovery references override age and cap selection. Storage maintenance is dirty/coarse
rather than a full scan after every operation or transfer-journal mutation; save
and post-load repair still force maintenance.
Cancelled processes are removed only with the corresponding provider policy and
no persisted recovery reference.

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
separate owner/provider and represented region, origin map/region, creation/access
ticks, transaction identity, and lifecycle. Marked
maps are temporary non-buildable work sites; the construction guard rejects build,
install, blueprint, and frame paths while leaving map generation and deconstruction
available. The `enableAdjacentRegions` setting remains opt-in and defaults false.
Excursion leases retain provider `taskId`, grace/deadline, heartbeat, return transfer
ID, retry, terminal tick, status, and diagnostics. Providers may expose fresh task
observations and explicit completion/abandon hooks; without reliable evidence, the
bounded lease expires and only conservative idle fallback may request return. The
world monitor runs at a coarse interval, returns only safely idle Pawns, and uses
retry backoff for unsafe, missing-origin, or unavailable-provider states. Warm-map
eviction is recency ordered and requires returned Pawns, no player
or recovery references, successful provider compression, and a real registered map
factory removal.

Provider-owned payload keys remain opaque to the framework. A live map is bound
only after an explicit provider identity claim (or the unambiguous standard
surface fallback), and conflicting or missing claims are quarantined without
changing an existing projection binding.
