# Provider Guide

Deferred Reality providers reference `DeferredRealityFramework.dll` and register a
stable `IRealityProvider` from a `StaticConstructorOnStartup` initializer. The
framework never references a provider assembly.

## Registration

```csharp
using System.Collections.Generic;

public sealed class ExampleProvider : IRealityProvider
{
    public RealityProviderRegistration Registration { get; } = new RealityProviderRegistration
    {
        providerId = "example.mod",
        semanticApiVersion = 1,
        schemaVersion = 1,
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
- `IPopulationProvider` validates atomic aggregate changes.
- `IAnchorProvider` validates identity-bearing restoration; it does not imply safe pawn compression.
- `ITransactionalAnchorProvider` may prepare/apply/validate/rollback anchor
  restoration; `ITransactionalAnchorCommitProvider` can release transaction
  bookkeeping after commit. Implement these when anchor materialization mutates
  pawns, Things, maps, or provider-owned state. Legacy `OnAnchorMaterialized`
  remains a final idempotent notification only.
- `IConstraintResolver` resolves only the provider's payload types.
- `IMaterializationProvider` participates in plan, prepare, apply, validate, and rollback.
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
failure restores the captured latent state. Never remove a legacy owner before a
migration marker is committed.

## Retention

Exactly-once operation IDs must remain durable unless replay is impossible by
contract. To opt into compaction, set a positive `operationRetentionTicks` and
list each safe `compactableOperationKinds`; unlisted kinds remain durable.
Cancelled process records use the separate `cancelledProcessRetentionTicks`
opt-in and are removed only when no persisted reference exists. The framework
retains interrupted transfer journals, caps terminal journals deterministically,
and never removes a map alias while a live map or recovery record can still refer
to it. The bundled providers opt in only for their stable non-replayable event
domains.

## Population rules

Use `RealityPopulationService` for consume, release, reproduction, mortality,
transfer, and active-map reconciliation. Pass a stable operation ID. A repeated
operation is reported as a duplicate and does not change the amount. Objective
amount and observation estimates are separate records.

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

Compression ownership is explicit: `RealityCompressionRequest.providerId`, or the
region provider namespace when omitted, selects one deterministic provider list.
That same list is used for `CanCompress`, `Prepare`, `Validate`, `Commit`, and
reverse rollback. Providers from other adapters are never called. `Rollback` may
also be called after `Commit` if the outer map factory fails to remove the live
map, so provider compression must have an explicit compensation path. A provider
must not claim a map or region ambiguously.

## Adjacent excursions

Adjacent materialization must set `RealityMaterializationRequest.adjacentMap` with
an explicit provider, origin region, origin map ID, and creation tick. Do not infer
the role from `reason` text or from a map merely being non-home. A custom map
parent should implement `IRealityMapIdentityProvider` and return a stable claim
containing its layer/custom/instance/provider identity; `WildlifeDeferredMapParent`
uses its persisted region ID.

Use the public world lease API to `BeginExcursion`/`AttachExcursion`, call
`HeartbeatExcursion` while a provider task is meaningful, and call
`CompleteExcursion`, `CancelExcursion`, or `RequestImmediateReturn` when the task
ends. The framework does not assume a task-completion callback exists. Without
heartbeats, an expired lease plus conservative idle detection is the fallback;
active combat, drafted/mental/medical/sleep/carried states, player-forced jobs,
and provider-owned jobs must remain unsafe. Provider transfer hosts must preserve
the Pawn instance and implement reverse rollback so return can use the exact
origin map and inverse edge.

Do not create a durable ticket before outbound commit. The framework creates it
only after the host confirms the exact Pawn on the declared destination. It marks
completion only after the same instance is on the exact origin map. Missing maps,
provider removal, interrupted journals, duplicate load IDs, and world-pawn/caravan
ownership are recovery states: retain the ticket/map and report a diagnostic.
The framework's coarse monitor and real map-factory eviction are safety boundaries;
providers must not bypass them by dropping map references.

Only one nonterminal ticket may own a Pawn load ID. If a completed return journal is
loaded beside an existing Returning ticket, the framework finalizes it only after
finding the exact Pawn on its recorded origin map; no summary reconstruction or
alternate colony-map selection is allowed.
