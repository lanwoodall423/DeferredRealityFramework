# Deferred Reality Framework

Deferred Reality Framework (DRF) is a provider-neutral RimWorld framework for
persistent regions, deterministic aggregate simulation, deferred outcomes,
observations, fidelity transitions, persistence, diagnostics, and integration
contracts.

DRF is a framework for consuming mods, not standalone gameplay content. A
consuming mod supplies its own provider and owns gameplay-specific maps, pawns,
objects, and live-world behavior.

## Compatibility and v0.1.0 scope

- RimWorld **1.6**
- Harmony (**brrainz.harmony**)
- .NET Framework 4.7.2 for development/building

`v0.1.0` is the first stable release of the provider-neutral framework
surface. Provider IDs, persisted identifiers, and provider contracts are public
integration boundaries; downstream providers remain responsible for their own
gameplay and live acceptance coverage.

Live provider integration acceptance is complete for the supported scope recorded
in [the acceptance report](MANUAL_ACCEPTANCE_REPORT.md).

For the complete release scope, compatibility details, architecture, save-format
rules, and consumer setup, see the [Release Notes](RELEASE_NOTES.md),
[Architecture](ARCHITECTURE.md), [Compatibility](COMPATIBILITY.md),
[Save Format](SAVE_FORMAT.md), and [Provider Guide](PROVIDER_GUIDE.md).
The project repository is [DeferredRealityFramework on GitHub](https://github.com/lanwoodall423/DeferredRealityFramework);
the distribution license is [LICENSE](LICENSE).


## Installation

1. Install Harmony (`brrainz.harmony`) and place it before DRF in RimWorld's mod list.
2. Download `DeferredRealityFramework-0.1.0.zip` from the release and
   extract its `DeferredRealityFramework` folder into the game's `Mods` directory.
3. Enable **Deferred Reality Framework** after Harmony.
4. Enable a consuming mod that lists DRF as a dependency.

DRF does not add a standalone gameplay loop without a consuming provider.

## Consumer integration

Reference `DeferredRealityFramework.dll`, register a provider during startup,
and implement only the capabilities your mod owns. Start with the
[Provider Guide](PROVIDER_GUIDE.md). Provider gameplay, live-world behavior,
and live acceptance coverage remain the consuming mod's responsibility.

## Experimental adjacent regions

Adjacent temporary excursion sites are **experimental**, **opt-in**, and
**disabled by default**. They are not part of the v0.1.0 compatibility guarantee.
Repository-only acceptance material is omitted from the end-user package.

