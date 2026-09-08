# Provider Guide

Deferred Reality providers reference `DeferredRealityFramework.dll` and register a
stable provider facade from a `StaticConstructorOnStartup` initializer. The
framework never references a provider assembly.

Provider projects are not part of the framework's default build or release
package. Build and package an adapter from the consuming mod repository against
the released DRF assembly. The framework's pure/static integrity checks validate
the generic boundary but do not replace provider gameplay or live RimWorld tests.

## Versioning and registration

The release-candidate framework identity is exposed by
`DeferredRealityFrameworkInfo.Version` (`0.1.0-rc.2`), and the exact loaded
assembly identity is available through `BuildIdentity`. Provider registrations
must set `semanticApiVersion` to
`DeferredRealityFrameworkInfo.SupportedProviderApiVersion` (`1`). Other values
are rejected before the provider enters the registry, with a diagnostic naming
the provider and both API versions.

The registry snapshots registration metadata during successful registration.
Later changes to a provider-owned `RealityProviderRegistration` object do not
change ordering, dependencies, capabilities, or retention policy. Re-register
the provider to intentionally replace its snapshot.

## The simple path (recommended)

Most providers need only four things: describe their latent state, run bounded
analytical processes, validate aggregate/identity records, and attach a service
for any map or transfer work. Compose those pieces with
`SimpleRealityProviderBuilder`. It produces one registered facade, advertises
only the capabilities that were actually configured, and leaves scheduling,
projection authority, persistence, exactly-once markers, escalation records,
and transaction rollback to DRF.

```csharp
private static SimpleRealityProvider BuildFrameworkProvider(WildernessGameplay gameplay)
{
    return new SimpleRealityProviderBuilder("example.wilderness", "Example wilderness")
        .Configure(registration =>
        {
            registration.order = 200;
            registration.defaultFidelity = RealityFidelity.Statistical;
            registration.operationRetentionTicks = -1;
        })
        .OnRegistered(gameplay.OnRegistered)
        .WithFidelity(RealityFidelityMask.All)
        .AllowTransition(RealityFidelity.Dormant, RealityFidelity.Statistical)
        .AllowTransition(RealityFidelity.Statistical, RealityFidelity.Materialized,
            RealityFidelityTransitionMechanism.Materialization)
        .AllowTransition(RealityFidelity.Materialized, RealityFidelity.Statistical,
            RealityFidelityTransitionMechanism.Compression)
        .WithAnalyticalProcess(gameplay.CanExecuteProcess, gameplay.ExecuteProcess,
            RealityFidelityMask.Statistical, runsWhileLiveProjection: false)
        .WithPopulations(new SimplePopulationDefinition
        {
            canChange = gameplay.CanChangePopulation,
            reconcileActiveMap = gameplay.ReconcileActiveMap
        })
        .WithAnchors(new SimpleAnchorDefinition { validate = gameplay.ValidateAnchor })
        .WithDiagnostics(gameplay.DiagnosticLines)
        .UseAdvanced(gameplay) // map, compression, transfer, and excursion hooks only
        .Build();
}

[StaticConstructorOnStartup]
public static class ExampleStartup
{
    static ExampleStartup()
    {
        RealityProviderRegistry.Register(BuildFrameworkProvider(new WildernessGameplay()));
    }
}
```

The callback types are deliberately gameplay-shaped: a process receives
elapsed time and a deterministic stream, population callbacks validate a
single aggregate operation, and map reconciliation receives the framework
context. A provider normally uses `RealityPopulationService` for consume,
release, reproduction, mortality, migration, and active-map reconciliation;
`DeferredRealityWorldComponent` for regions, connections, anchors, constraints,
observations, and process records; and `RealityProcessScheduler` indirectly
through the process records it schedules. These helpers keep the common path
out of transaction journals and rollback code.

### Wildlife reference integration

Wildlife is the reference implementation shipped beside this guide. Its
`BuildFrameworkProvider` follows the same composition exactly:

```csharp
new SimpleRealityProviderBuilder(ProviderId, "Wildlife regional ecology")
    .OnRegistered(OnRegistered)
    .WithFidelity(wildlifeFidelity)
    .WithProcess(new SimpleProcessDefinition { canExecute = CanExecute, execute = Execute })
    .WithPopulations(new SimplePopulationDefinition
    {
        canChange = CanChangePopulation,
        reconcileActiveMap = ReconcileActiveMap
    })
    .WithAnchors(new SimpleAnchorDefinition { validate = ValidateAnchor })
    .WithConstraints(new SimpleConstraintDefinition { canResolve = CanResolve, resolve = Resolve })
    .WithDiagnostics(DiagnosticLines)
    .UseAdvanced(this)
    .Build();
```

Its gameplay code then remains ordinary regional simulation:

- latent regions seed one aggregate `RealityPopulationRecord` per species and
  schedule one stable daily process;
- the process receives elapsed days, applies deterministic growth/mortality,
  and calls `RealityPopulationService.Transfer` for eligible graph edges;
- the scheduler pauses that process whenever `LiveProjection` authority owns
  the map, so births and deaths are not counted twice;
- map reconciliation imports ordinary animals as populations and significant
  animals as anchors; compression reconciles survivors and deaths through the
  provider's advanced map/compression service;
- player interaction or an important anchor returns a typed escalation result,
  which DRF persists and pauses without creating a map automatically;
- `DiagnosticLines` reports population, connection, projection, and transfer
  state without exposing rollback machinery to gameplay callers.

Frontier uses the same facade for its regional process, site anchors,
constraints, observations, and diagnostics, while retaining its exploration
map and landmark consistency service as an advanced component.

### What the builder guarantees

`SimpleRealityProvider` is not a second transaction system. It is a capability
adapter. The registry's `TryGetCapability<T>` and `OfType<T>` ignore facade
methods whose service was not configured, so an absent materialization or
compression service cannot accidentally become a successful no-op. Provider
exceptions, bounded scheduler work, live-map gating, typed escalation,
projection binding, and transactional rollback remain framework-owned.

## Advanced path

Use the low-level interfaces below when a provider owns maps, excursions,
transfer journals, provider-specific consistency obligations, or nontrivial
rollback state. Advanced services can be attached to the simple facade with
`UseAdvanced(...)`; they do not need a second registration. A provider may also
implement the interfaces directly when composition is not a useful fit.

The interfaces that deliberately remain low-level are `IMaterializationProvider`
and `IStageAwareMaterializationProvider` (map transaction stages),
`ITransactionalAnchorProvider`/`ITransactionalAnchorCommitProvider` (identity
restoration), `ICompressionProvider` (reconciliation and compensation),
`IAdjacentRegionTransferHost` (exact Pawn movement and journals),
`IRealityExcursionTaskProvider`/`IRealityExcursionTaskCleanupProvider` (leased
runtime tasks), `IRealityMapIdentityProvider` (projection ownership), and
`IRealityMaterializationConsistencyProvider` (provider-specific obligations).
They are low-level because each one owns a separate trust boundary or rollback
obligation; the simple facade can carry them without exposing them to ordinary
latent-simulation code.

## Advanced registration

```csharp
using System.Collections.Generic;

public sealed class ExampleProvider : IRealityProvider
{
    public RealityProviderRegistration Registration { get; } = new RealityProviderRegistration
    {
        providerId = "example.mod",
        semanticApiVersion = DeferredRealityFrameworkInfo.SupportedProviderApiVersion,
        capabilities = RealityProviderCapability.Populations,
        order = 400,
        // -1 means exactly-once markers remain durable forever.
        operationRetentionTicks = -1,
        compactableOperationKinds = new List<string>(),
        // -1 means cancelled process records remain durable forever.
        cancelledProcessRetentionTicks = -1
    };

    public void OnRegistered(RealityProviderContext context) { }
}

[StaticConstructorOnStartup]
public static class ExampleStartup
{
    static ExampleStartup() => RealityProviderRegistry.Register(new ExampleProvider());
}
```

Provider IDs, population IDs, anchor IDs, process IDs, constraint IDs, and
operation IDs are save-format contracts. Use `RealityDeterminism.Seed` with the
world seed, region ID, provider ID, operation ID, and explicit epoch. Do not use
`GetHashCode`, shared `Verse.Rand`, collection iteration order, or UI state.
Registration from a worker or before the framework has established the main
thread is deferred through `LongEventHandler.ExecuteWhenFinished`.

## Capability interfaces

Implement only the interfaces needed by the provider:

- `IRegionDescriptorProvider` describes regions without requiring a Map.
- `IRealityProcessProvider` receives continuous `elapsedTicks`, bounded analytical
  step count, and a deterministic RNG seeded by process execution count.
- `IRealityFidelityProvider` declares supported fidelities, legal transitions,
  per-process fidelity policy, and post-transition notification. Use `Dormant`
  for durable facts without recurring work, `Statistical` for aggregate
  analytical evolution, `Narrative` for bounded abstract events, and
  `Materialized` only for spatial Map resolution. `LiveProjection` remains a
  separate authority state.
- A `RealityProcessResult` may carry a typed `RealityProcessEscalationRequest`.
  A request creates a durable pending record and pauses the process; a provider
  may use `Decline` when it can safely retain the current abstraction. Hosts
  approve and satisfy requests explicitly, and escalation never mass-creates
  maps automatically.
- `IPopulationProvider` validates atomic aggregate changes.
- `IAnchorProvider` validates identity-bearing restoration; it does not imply safe pawn compression.
- `ITransactionalAnchorProvider` may prepare/apply/validate/rollback anchor
  restoration; `ITransactionalAnchorCommitProvider` can release transaction
  bookkeeping after commit. Implement these when anchor materialization mutates
  pawns, Things, maps, or provider-owned state. Anchor restoration belongs in
  these transactional hooks or the provider's materialization `Apply`; there is
  no second post-commit anchor callback.
- `IConstraintResolver` resolves only the provider's payload types.
- `IMaterializationProvider` participates in plan, prepare, apply, validate, and rollback.
- `IRealityMaterializationConsistencyProvider` translates active observations and
  constraints into structured spatial obligations and validates the realized
  map. Its hooks are provider-owned and transactional failure is a veto.
- `IRealityMapIdentityProvider` must claim every nonstandard map with an explicit
  layer/custom/instance/provider identity. Claims are scoped to the registering
  provider and conflicting claims fail closed.
- `ICompressionProvider` must veto unknown or unsafe state and roll back on errors.
- `IObservationProvider` receives detached records and can bridge to a knowledge system.
- `IRealityDiagnosticsProvider` contributes cached diagnostic lines only.

Provider exceptions are isolated. A process failure pauses and quarantines the
process with `ProviderFailure`; a provider veto follows the same cause. A missing
provider uses `ProviderUnavailable`, which is automatically resumed when that
provider registers again. Manual and provider-requested pauses are not cleared by
registration and require explicit resume. A materialization or compression
failure restores the captured latent state.

Projection authority is explicit. `Latent` means the framework's aggregate store
owns the region; `Materializing` and `Compressing` reserve the region for a
transaction; `LiveProjection` makes the bound Map authoritative for spatial state;
and `Quarantined` blocks autonomous transitions. The scheduler pauses analytical
processes for live or transitioning regions, so providers must not apply the same
effect through both a live map and an aggregate process. `RealityFidelity` describes
resolution separately from authority: `Dormant` has no recurring simulation,
`Statistical` evolves aggregates, `Narrative` resolves discrete abstract events,
and `Materialized` supports live spatial state. A live projection normally has
Materialized fidelity, but fidelity alone never claims a Map.

Regions describe places. Connections describe topology. Transfers/processes
describe changes across topology.

Use `RealityRegionConnection` for provider-neutral relationships such as
migration eligibility, expedition reachability, diffusion, or adjacent
materialization hints. Create IDs with `RealityRegionConnection.StableId` using
the endpoint identities, direction, semantic kind, owner namespace, and a
provider-defined identity key. Use one bidirectional record when traversal is
symmetrical; do not create two records for the reverse direction. Connections
are persisted and inspectable through `ConnectionSnapshots`,
`TryGetConnection`, `OutgoingConnections`, `IncomingConnections`, and
`Neighbors`. These APIs expose immediate topology only; they are not a
pathfinding engine. Keep actual population changes, Pawn movement, excursion
ownership, map creation, compression, and rollback in their existing process or
transaction systems.

Connection `ownerNamespace` is informational ownership, not a requirement that
the provider be loaded. Missing providers must leave the opaque connection
record intact. Set `lifecycle` to `Disabled` when a relationship is temporarily
unusable; disabled records are visible in `ConnectionSnapshots` and omitted from
normal directional queries. Provider payloads and metadata are opaque to DRF.

## Retention

Exactly-once operation IDs must remain durable unless replay is impossible by
contract. To opt into compaction, declare an `IRealityExactlyOnceProvider` domain
or call `RealityProviderContext.DeclareExactlyOnceDomain`, then advance its
persisted cursor with `AdvanceExactlyOnceCursor` only after the state and marker
commit. Set a positive `operationRetentionTicks` and list each safe
`compactableOperationKinds`; unlisted kinds remain durable. Strict domains require
contiguous sequences, while gap-tolerant domains still reject every sequence at or
below the cursor. Markers without a provider-declared replay boundary remain
durable.
Cancelled process records use the separate `cancelledProcessRetentionTicks`
opt-in and are removed only when no persisted reference exists. The framework
retains interrupted transfer journals, caps terminal journals deterministically,
and never removes a projection binding while a live map or recovery record can
still refer to it. Providers may opt in only for stable non-replayable event domains with a
durable replay boundary.

## Population rules

Use `RealityPopulationService` for consume, release, reproduction, mortality,
transfer, and active-map reconciliation. Pass a stable operation ID and, for a
declared exactly-once domain, a provider sequence. The framework validates the
sequence before mutating population state; a repeated operation or sequence is
reported as a duplicate and does not change the amount. Objective amount and
observation estimates are separate records.

## Transitions

Planning must not mutate Verse or provider state. A host may register an
`IRealityMapFactory` for normal map creation under its provider ID. A
`RealityMaterializationRequest` should set `providerId` when the region identity
is shared by multiple providers; only that provider's materialization stages are
run and its factory is selected. The framework has no default map factory and
refuses generic compression in v1. Providers must treat unknown components,
active combat, Lords, mental states, jobs, reservations, quests,
player-controlled pawns, and unique Things as veto conditions. Adjacent pawn
 movement is opt-in through `IAdjacentRegionTransferHost`; failed preparation or
 commit must either prove exact rollback before fallback or leave an interrupted
journal and ownership record for recovery.

`RealityRegionId` identifies the represented latent region, not the owner of an
adjacent site. Set both `RealityMaterializationRequest.providerId` and
`adjacentMap.providerId` to the integration responsible for the temporary site
even when the region is a shared `core` `Surface(tile)` identity. Do not rewrite
the region namespace to match the provider. Adjacent materialization requires a
provider-scoped map factory and an explicit provider map-identity claim; there is
no unscoped fallback factory.

The framework creates a transaction-scoped map-creation intent before invoking the
factory. The intent is the only authorization for a newly generated map to be
reclassified. Map readiness and provider map-component callbacks may both call
`RegisterMap`, but they converge through the same intent-aware idempotent path.
Existing ordinary maps, stale intents, and same-tile conflicts fail closed.

`RegisterMap(map, regionId)` is the only explicit projection binding operation.
It validates the map identity and tile, rejects competing bindings, and marks the
region `LiveProjection`. Providers must not invent alias-repair or identity-
rebinding paths around that authority check.

Compression ownership is explicit: `RealityCompressionRequest.providerId`, or the
region provider namespace when omitted, selects one deterministic provider list.
That same list is used for `CanCompress`, `Prepare`, `Validate`, `Commit`, and
reverse rollback. Providers from other adapters are never called. `Rollback` may
also be called after `Commit` if the outer map factory fails to remove the live
map, so provider compression must have an explicit compensation path. A provider
must not claim a map or region ambiguously.

## Adjacent excursions

Adjacent materialization must set `RealityMaterializationRequest.adjacentMap` with
an explicit owner/provider, origin region, origin map ID, and creation tick. Do not infer
the role from `reason` text or from a map merely being non-home. A custom map
parent should implement `IRealityMapIdentityProvider` and return a stable claim
containing its layer/custom/instance/provider identity; the provider-owned map
parent or map component supplies that identity from its own persisted state.

Use the public world lease API to `BeginExcursion`/`AttachExcursion`, call
`HeartbeatExcursion` while a provider task is meaningful, and call
`CompleteExcursion`, `CancelExcursion`, or `RequestImmediateReturn` when the task
ends. The framework does not assume a task-completion callback exists. Without
heartbeats, an expired lease plus conservative idle detection is the fallback;
active combat, drafted/mental/medical/sleep/carried states, player-forced jobs,
and provider-owned jobs must remain unsafe. Provider transfer hosts must preserve
the Pawn instance and implement reverse rollback so return can use the exact
origin map and inverse edge.
When a provider must finish an owned task or unload a provider-side resource
before the inverse leg, it may implement `IRealityExcursionReturnGate`. The
framework enters the durable `Returning` state before calling this gate; a
`Pending` disposition records bounded diagnostic/backoff and performs no
transfer, while `Ready` permits the existing inverse transfer service. An
absent gate is treated as `Ready`, so existing providers retain their contract.


Do not create a durable ticket before outbound commit. The framework creates it
only after the host confirms the exact Pawn on the declared destination. It marks
completion only after the same instance is on the exact origin map. Missing maps,
provider removal, interrupted journals, duplicate load IDs, and world-pawn/caravan
ownership are recovery states: retain the ticket/map and report a diagnostic.
The framework's coarse monitor and real map-factory eviction are safety boundaries;
providers must not bypass them by dropping map references.
Providers that keep runtime task state may optionally implement
`IRealityExcursionTaskCleanupProvider`; the framework calls it only after a ticket
is terminal and no longer recoverable, so task/evidence dictionaries stay bounded.

Only one nonterminal ticket may own a Pawn load ID. If a completed return journal is
loaded beside an existing Returning ticket, the framework finalizes it only after
finding the exact Pawn on its recorded origin map; no summary reconstruction or
alternate colony-map selection is allowed.

`IMaterializationProvider.Rollback` is idempotent and may run after `Prepare` has
partially mutated provider state and thrown. The framework records a provider before
calling `Prepare`, rolls back the throwing provider and earlier providers in reverse
order, and reports rollback failures separately from the original transition error.
Providers must tolerate missing temporary state and must not hide the original
exception.

For recurring demography, transfer, consume, release, and active-map-reconcile
operations, declare the replay threat and advance a persisted provider/kind/domain
watermark only at a durable boundary proving replay is impossible. Retention age
and metadata alone are not proof. Applied-operation and journal writes defer broad
compaction to bounded maintenance; provider mutation calls must remain cheap.

## Population, anchor, and observation rulebook

Use the smallest representation that preserves gameplay meaning:

1. Use `RealityPopulationRecord` for fungible quantities such as ordinary wild
   muffalo, biomass, generic insects, group roles, food, or generic raiders.
   Mark it `compressionSignificance = Aggregate`. If one member becomes named,
   bonded, quest-critical, player-controlled, or otherwise individually
   meaningful, create a `RealityAnchorRecord` and add its ID to the population's
   `anchoredMemberIds`; do not leave that identity only in the amount.
2. Use `RealityAnchorRecord` for a non-fungible identity that must survive
   abstraction. Mark it `Identity`. The provider owns restoration of its payload
   and any RimWorld object. A non-empty `optionalRimWorldLoadId` is safe for
   compression only after the provider sets `externalReferenceState` to
   `ProviderResolved`; unknown, player-controlled, quest-critical, or unsafe
   references veto compression.
3. Use `RealityObservationRecord` for an established fact at a stated precision,
   and mark it `EstablishedFact` when it must constrain rematerialization. Mark
   genuinely disposable rumor/sensor detail `Disposable`. Player-observed facts
   are always established facts. Use `RealityConstraint` for durable causal or
   compatibility rules that must be enforced, not as a hidden identity store.

The framework's `RealityCompressionPreservation.Validate` check is automatic
before provider compression. It rejects unknown significance, non-aggregate
population state, missing identity references, unsafe external references, and
constraints that point to missing records. `PlayerBound` and `QuestBound` are
explicit vetoes. Providers must use `CanCompress`, `Validate`, `Commit`, and
`Rollback` to reconcile live deaths, births, movement, and identity restoration;
the framework does not copy arbitrary live Pawns into latent state.
