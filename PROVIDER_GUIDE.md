# Provider Guide

Deferred Reality providers reference `DeferredRealityFramework.dll` and register a
stable `IRealityProvider` from a `StaticConstructorOnStartup` initializer. The
framework never references a provider assembly.

## Registration

```csharp
public sealed class ExampleProvider : IRealityProvider
{
    public RealityProviderRegistration Registration { get; } = new RealityProviderRegistration
    {
        providerId = "example.mod",
        semanticApiVersion = 1,
        schemaVersion = 1,
        capabilities = RealityProviderCapability.Populations,
        order = 400
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

## Capability interfaces

Implement only the interfaces needed by the provider:

- `IRegionDescriptorProvider` describes regions without requiring a Map.
- `IRealityProcessProvider` receives bounded elapsed time and a deterministic RNG.
- `IPopulationProvider` validates atomic aggregate changes.
- `IAnchorProvider` validates identity-bearing restoration; it does not imply safe pawn compression.
- `IConstraintResolver` resolves only the provider's payload types.
- `IMaterializationProvider` participates in plan, prepare, apply, validate, and rollback.
- `ICompressionProvider` must veto unknown or unsafe state and roll back on errors.
- `IObservationProvider` receives detached records and can bridge to a knowledge system.
- `IRealityDiagnosticsProvider` contributes cached diagnostic lines only.

Provider exceptions are isolated. A process failure pauses and quarantines the
process; a materialization or compression failure restores the captured latent
state. Never remove a legacy owner before a migration marker is committed.

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
commit must leave the journal in safe fallback state.
