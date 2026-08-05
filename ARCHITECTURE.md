# Deferred Reality Framework

## Ownership boundary

Deferred Reality Framework is a provider-neutral RimWorld 1.6 framework. It has
no compile-time or runtime knowledge of any consuming gameplay mod. Providers
remain authoritative for their map components, Defs, jobs, Lords, exact
Pawns/Things, gameplay AI, and legacy save keys. The framework owns only generic
regional state, aggregate populations, anchors, constraints, analytical
processes, observations, provider payload envelopes, transfer journals,
adjacent-site records, excursion tickets, and migration/retention metadata.

Provider integrations belong in consuming mods. They register stable
`IRealityProvider` implementations and may translate their own legacy state into
the generic APIs. A provider may be absent; its opaque records remain inspectable,
processes become `ProviderUnavailable`, and no framework fallback invents provider
gameplay or reconstructs provider-owned objects.

## Stable state

`DeferredRealityWorldComponent` is the durable owner of latent framework state.
`RealityRegionId` identifies represented latent state by tile, layer, local
slot/instance, parent, and provider namespace. `Map.uniqueID` is only an active
projection link or migration alias and is never the identity of latent state.

Providers may own an adjacent site representing a region in another namespace,
including a shared `core` surface region. The owner is persisted separately from
the represented region and must be registered and explicitly claimed; the
framework never rewrites a region namespace to match an owner.

## Scheduling and determinism

The world tick uses a runtime earliest-due gate. When no process is due it does
not clone or sort the process collection. Due work is ordered by due tick,
descending priority, provider ID, and process ID. Providers receive continuous
`elapsedTicks`; analytical steps are
`max(1, floor(elapsedTicks / intervalTicks))`, bounded by the run budget.

Seeds, ordering, duplicate repair, retention selection, audit keys, and eviction
ties use stable ordinal identities and do not depend on dictionary enumeration.

## Provider lifecycle

Provider exceptions are isolated. Manual, provider-failure, and provider-requested
pauses require explicit resume. Missing providers suspend processes with
`ProviderUnavailable`; registering that same provider reactivates only those
processes and preserves payload, execution count, RNG epoch, and overdue timing.

Materialization plans are transactional. A provider is tracked before `Prepare`
starts because preparation may partially mutate state and throw. Prepared and
stage-aware providers, transactional anchors, framework state, map-creation
intents, and genuinely created maps compensate in deterministic reverse order.
Framework state is restored after provider rollback and created maps are removed
last. The original error remains primary; rollback failures are retained in the
result and diagnostics. Rollback is idempotent and tolerates missing temporary
state.

Compression selects one explicit owner-scoped provider list and uses that exact
list for `CanCompress`, `Prepare`, `Validate`, `Commit`, and reverse rollback.
Unowned or ambiguous compression fails closed. A provider may also compensate a
committed compression when an outer map-factory removal fails.

## Map identity and readiness

Nonstandard maps require a provider-supplied `IRealityMapIdentityProvider` claim
with a stable layer/custom/instance identity. Standard `Surface(tile)` fallback is
limited to an unambiguous standard surface map. Persisted aliases win during
migration; a second live map or conflicting claim is quarantined and cannot
overwrite an active projection.

Map generation creates a save-safe, transaction-scoped intent before the provider
factory runs. The intent records transaction, expected region, owner, origin,
pre-existing map, and created map identities. It is the only authorization to
classify a newly generated map as an adjacent site. `Map.FinalizeInit` and any
provider map-component callback converge through the same idempotent registration
path. Stale, conflicting, unbound, or ordinary-map intents fail closed and are
reconciled after load. Successful commit and completed rollback clear the intent.

Map readiness is registered through one effective lifecycle path. Worker callbacks
defer through `LongEventHandler` before mutable framework state is touched.
`RealityThreadGuard` is unknown until the explicit RimWorld startup lifecycle
establishes the main-thread ID; mutating calls do not self-initialize.

## Constraints and persistence

Constraint conflicts require intersecting explicit domain keys and incompatible
semantics. Legacy constraints use provider/type plus subject, facet, or region,
never subject alone. Duplicate repair keeps the first serialized valid slot unless
a higher schema version or explicit newer update tick proves a later record newer.

Root save schema 5 adds adjacent diagnostics, operation watermarks, task/terminal
fields, and map-creation intents with empty/-1 defaults for older saves. Unknown
provider payloads and missing-provider records remain durable and inspectable.

Exactly-once marker expiry requires a provider/domain replay boundary. The
framework uses persisted provider/kind/domain sequence watermarks only when the
provider has declared a proof that operations at or below the cursor cannot be
accepted again. Age, aggregate population values, or a legacy operation ID alone
never authorize deletion. Writes mark dirty storage maintenance rather than
running broad compaction on every mutation; save and post-load repair force a
bounded deterministic pass.

## Adjacent sites and excursions

An adjacent marker is a typed temporary-site role applied after map generation.
Construction designators, blueprint placement/spawn/replacement, and frame
completion are rejected on marked maps while generation and deconstruction remain
available. The rejection path is provider-neutral and ordinary maps are
unaffected.

An outbound ticket is written only after a transfer host verifies the same Pawn
instance on the declared destination. It records the exact origin map/cell,
inverse edge, provider task ID, outbound/return journals, heartbeat, grace,
retry, and terminal state. Only one nonterminal ticket may own a Pawn load ID.
Completion requires the same instance on the exact origin map. Unsafe combat,
medical, sleep, carrying, mental, drafted, forced, provider-task, transfer, and
ambiguous ownership states defer return with backoff; missing origins/providers
retain the Pawn, map, journal, and ticket for recovery.

Providers may expose task observation, explicit heartbeat/completion/abandon hooks,
and terminal runtime cleanup. Fresh bounded evidence may renew a lease; unchanged
or absent evidence cannot renew indefinitely. Once a lease expires, only the
provider-neutral safe-idle fallback may request return.

Monitoring is coarse and gated by active maps, nonterminal/recoverable tickets,
pending intents, or unresolved journals. Historical completed tickets alone do
not keep the monitor running. Background inspection reconstructs persisted access
ticks but does not refresh LRU recency. Meaningful creation, transfer, heartbeat,
return, provider access, or explicit map retrieval touches recency.

Warm eviction is real removal, never dropping a dictionary reference. It requires
no active/recoverable ticket, player-owned or unsafe object, construction artifact,
interrupted journal, viewed-map conflict, or provider veto; then provider
compression and the owning `IRealityMapFactory.RemoveMap` must succeed and the map
must disappear from `Find.Maps`. Terminal excursions, retired markers, and
resolved diagnostics use deterministic age/cap retention, while recovery
references override compaction.

The adjacent feature flag remains disabled by default and experimental until the
consumer-provider integration and live RimWorld acceptance checklist pass.
