# Deferred Reality Framework

## Ownership boundary

Deferred Reality Framework is a provider-neutral RimWorld 1.6 framework. It has
no compile-time or runtime knowledge of any consuming gameplay mod. Providers
remain authoritative for their map components, Defs, jobs, Lords, exact
Pawns/Things, gameplay AI, and provider save keys. The framework owns only generic
regional state, aggregate populations, anchors, constraints, analytical
processes, observations, provider payload envelopes, transfer journals,
adjacent-site records, excursion tickets, exactly-once markers, and retention
metadata.

Provider integrations belong in consuming mods. Ordinary providers should
compose a `SimpleRealityProvider` with `SimpleRealityProviderBuilder` and
translate their current projection state into the generic APIs. Advanced
providers may still implement the capability interfaces directly or attach
transactional services with `UseAdvanced(...)`. A provider may be absent; its
opaque records remain inspectable, processes become `ProviderUnavailable`, and
no framework fallback invents provider gameplay or reconstructs provider-owned
objects.

The simple facade is an ergonomic boundary, not a weaker transaction model.
`TryGetCapability<T>` and `OfType<T>` expose only configured capabilities, while
the scheduler, projection authority, deterministic streams, escalation records,
map intents, transfer journals, compression checks, and rollback remain DRF
responsibilities. Wildlife is the canonical reference integration; Frontier
uses the same composition for its ordinary regional callbacks and attaches its
landmark/map consistency service as an advanced component.

## Build and release boundary

The default `DevTools/Build-All.ps1` workflow builds only the framework and its
provider-neutral pure tests, then runs those tests. Provider adapters are built
from their consuming repositories or by explicitly invoked adapter projects;
they are not framework release inputs or package contents. `Audit-Outputs.ps1`
and `Check-RepositoryIntegrity.ps1` audit only framework-owned outputs and fail
if provider DLLs or provider assembly references appear in the DRF package.
Local compilation requires a configured RimWorld 1.6 root and Harmony path via
`RIMWORLD_ROOT`/`DEFERRED_REALITY_HARMONY_PATH` or the documented MSBuild
properties. The CI integrity workflow performs legal source/package checks
without proprietary RimWorld binaries.

## Stable state

`DeferredRealityWorldComponent` is the durable owner of latent framework state.
`RealityRegionId` identifies represented latent state by tile, layer, local
slot/instance, parent, and provider namespace. `Map.uniqueID` is only the
runtime/persisted binding for an active map projection and is never the identity
of latent state.

The world component remains the single RimWorld serialization and transaction
boundary, but its durable collections are grouped behind cohesive stores:
`RealityLatentRecordStore` owns regions, graph connections, populations, anchors,
constraints, processes, observations, provider payloads, exactly-once records,
quarantine, conflicts, watermarks, and escalation requests. `RealityProjectionRecordStore`
owns transfer journals, temporary projection markers, map-creation intents,
excursion leases, and bounded projection diagnostics. Runtime graph indexes,
projection caches, topology discovery, transition orchestration, and retention
remain separate services; the public world facade does not expose that internal
decomposition.

Each region has one explicit `RealityRegionAuthority`:

- `Latent`: the world store is authoritative and no live projection is bound.
- `Materializing`: a transaction is moving latent state into a projection.
- `LiveProjection`: the bound map is authoritative for state represented spatially.
- `Compressing`: a transaction is reconciling the projection back into latent state.
- `Quarantined`: the framework refuses autonomous transition or simulation.

`RealityFidelity` describes resolution independently of authority: `Dormant`
stores durable facts without recurring simulation, `Statistical` evolves
aggregates analytically, `Narrative` resolves discrete abstract events, and
`Materialized` represents spatial resolution suitable for a live Map.
`projectionMapUniqueId` remains only a projection binding. A normal live
invariant is `LiveProjection` authority plus `Materialized` fidelity; fidelity
alone never claims a Map.

Regions describe places. Connections describe topology. Transfers/processes
describe changes across topology.

`RealityRegionConnection` is the persisted provider-neutral graph edge. It
contains source and destination region identities, a deterministic connection
ID, directed or bidirectional semantics, provider-defined kind/identity,
optional traversal cost/distance and entry/exit edge metadata, an optional
owner namespace, opaque provider payload, and an enabled/disabled lifecycle.
Bidirectional topology is one record and is indexed from both endpoints;
disabled records remain inspectable but are excluded from traversal helpers.
The framework exposes stable-ID lookup, outgoing/incoming edges, and immediate
neighbors. It deliberately does not provide global pathfinding. A connection
never transfers a Pawn, mutates a population, creates a Map, or replaces a
transfer/materialization transaction.

Providers may own an adjacent site representing a region in another namespace,
including a shared `core` surface region. The owner is persisted separately from
the represented region and must be registered and explicitly claimed; the
framework never rewrites a region namespace to match an owner.

## Aggregate, identity, and knowledge state

The framework keeps three different meanings explicit:

- `RealityPopulationRecord` is fungible quantity. Its
  `compressionSignificance` must be `Aggregate`; it may reference retained
  identity anchors through `anchoredMemberIds`, but it must never be the only
  representation of an individual that gameplay cares about.
- `RealityAnchorRecord` is non-fungible identity. Its significance must be
  `Identity`, and provider-owned external references must be marked
  `ProviderResolved` before compression. Named pawns, bonded animals, faction
  leaders, quest entities, unique Things, and excursion-owned identities belong
  here, not solely in a population amount.
- `RealityConstraint` and an established `RealityObservationRecord` are facts
  that constrain later analytical resolution and materialization. They use
  `EstablishedFact`; deliberately ephemeral evidence may use `Disposable`.

`Unknown`, `PlayerBound`, `QuestBound`, and `UnsafeReference` are fail-closed
significance values. Compression vetoes them, as well as missing anchors or
populations referenced by a retained constraint. The framework never serializes
arbitrary live Pawns, Things, jobs, needs, or object graphs. Providers restore
provider-owned identities and must prove that external references are safe.
Transactional compression captures and restores all framework records around
provider stages, so a preservation veto or provider failure leaves the live
projection and latent state unchanged.

## Observation consistency and rematerialization

An observation is knowledge, not objective reality. `RealityObservationRecord`
therefore carries its own optional `RealityLocation`, active/invalidated/
superseded lifecycle, confidence, and `RealityObservationPrecision`; it does not
overwrite populations or anchors. Providers may explicitly call
`InvalidateObservation` or `SupersedeObservation` for a legitimate gameplay
event. Inactive observations remain inspectable but do not constrain a new map.

The framework builds a deterministic `RealityMaterializationConsistencyPlan`
before map creation. It contains detached active observations, active
constraints, and provider-translated `RealityMaterializationConstraint`
obligations. Providers implementing `IRealityMaterializationConsistencyProvider`
translate their own knowledge and payload semantics and validate the realized
map after provider application. `IRealityMapFactory` receives the same plan
through `RealityMaterializationPlan`; DRF never generates provider-specific
terrain, herds, ruins, or landmarks.

Precision is a constraint strength, not a promise that every detail is frozen:

- `Rumor` is advisory and may be placed anywhere compatible with the region.
- `Region` and `Habitat` preserve coarse presence or ecological suitability;
  placement remains free within that scope.
- `Area` and `Edge` preserve a named/coarse area or boundary relationship.
- `Cell` is required when the provider considers the confidence sufficient.
- `Exact` is an expensive exact obligation for sufficiently confident player
  knowledge; providers may downgrade weaker/non-player evidence explicitly.

The default translation omits disposable evidence, rejects undefined precision,
and reports incompatible translated obligations as explicit
`RealityMaterializationConsistencyConflict` diagnostics. A conflict or provider
validation failure aborts the transaction; map creation, provider state,
anchors, and framework records roll back together. The deterministic plan key
and seed derive from world seed, region, provider, and the complete active
observation/constraint state, so providers can regenerate free details
reproducibly while honoring established obligations.

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

Analytical processes are also gated by region authority. `LiveProjection` pauses
aggregate work with `ProjectionAuthoritative`; `Materializing` and `Compressing`
pause it with `ProjectionTransition`. This prevents background effects from being
applied a second time while the live map or a transition owns the state. Each
process also declares legal fidelities and whether it can run while a live
projection is authoritative. A process that cannot resolve its event safely at
the current fidelity returns a typed escalation request; the framework persists
and pauses that request for explicit approval and never creates a Map
automatically.

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
limited to an unambiguous standard surface map. A second live map or conflicting
claim is quarantined and cannot overwrite an active projection. There is no
persisted alias table or provider migration hook.

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
semantics. When a provider omits domain keys, the current resolver derives a
deterministic conservative domain from provider/type plus subject, facet, or
region; subject identity is never used without that provider/type scope.
Duplicate repair keeps the first serialized valid slot unless an explicit newer
update tick proves a later current record newer. There is no root or child save
schema negotiation: DRF has one current record shape, frozen as the
`v0.1.0-rc.2` compatibility baseline. Pre-release saves are supported only when
they already use this current shape; DRF makes no historical schema 3/4/5
guarantee or general pre-release migration promise. Invalid or conflicting
records are quarantined deterministically. Unknown provider payloads and
missing-provider records remain durable and inspectable.

Exactly-once marker expiry requires a provider/domain replay boundary. The
framework uses persisted provider/kind/domain sequence watermarks only when the
provider has declared a proof that operations at or below the cursor cannot be
accepted again. Age, aggregate population values, or an operation ID alone
never authorize deletion. Writes mark dirty storage maintenance rather than
running broad compaction on every mutation; save and post-load repair force a
bounded deterministic pass.

Connection records are repaired deterministically: invalid records and duplicate
IDs are quarantined, and the first valid serialized record wins. Missing provider
registrations preserve connection data and produce diagnostics; they do not erase
opaque topology.

## Projections, transfers, and excursions

The former adjacent-surface concern is split by ownership. `RealityRegionTopologyService`
projects loaded map adjacency into general region connections; it does not create
maps or move entities. `RealityProjectionCacheService` owns warm projection
references and real factory eviction. `RealityRegionTransferService` owns
provider-host registration, durable transfer stages, rollback, and exact Pawn
ownership checks. `RealityAdjacentSurfaceService` is only the coarse monitor and
excursion-reconciliation facade, while `RealityProjectionDiagnostics` formats
bounded read-only diagnostics. Temporary-site settings live in the mod-settings
service. These services share the world transaction boundary but do not duplicate
topology or transfer responsibilities.

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
Return authorization has an explicit durable boundary: a due `ReturnRequested`
or expired `Active` ticket is first persisted as `Returning`, and that monitor
invocation ends before any inverse transfer is attempted. Later passes process
only `Returning`. An optional provider `IRealityExcursionReturnGate` may keep
that state `Pending` with bounded backoff; `Ready` or an absent gate permits the
existing transactional inverse transfer. Transfer failure deliberately returns
to `ReturnRequested` through the existing retry path.


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
