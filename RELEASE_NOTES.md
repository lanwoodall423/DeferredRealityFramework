# Deferred Reality Framework v0.1.0

This is the first stable Deferred Reality Framework release for RimWorld 1.6.
DRF provides a provider-neutral framework for durable regions, deterministic
deferred outcomes, persistence, diagnostics, fidelity transitions, and
transaction-safe framework services. Provider adapters are built, tested, and
released by their consuming mod repositories.

## Stable scope

- Deterministic regional simulation, scheduler gating, retention, and
  replay-safe exactly-once cursor APIs.
- Persistence, provider-neutral records and contracts, observations,
  diagnostics, fidelity transitions, and rollback-safe framework services.
- Consumer integration through the public provider registry and capability
  interfaces. Provider registration API version `1` is supported.
- Fail-closed map identity and transactional materialization/compression
  boundaries.
- Framework-only build, integrity, output-audit, and deterministic packaging
  checks.

## Compatibility

`v0.1.0` preserves the provider-neutral record shape, stable identifiers,
registration contract, and opaque provider payloads across the 0.1.x line,
subject to the repair rules in `SAVE_FORMAT.md`. The optional
`IRealityExcursionReturnGate` remains additive; providers that do not implement
it retain the default `Ready` return behavior.

Providers own provider payload semantics, provider IDs, map-component imports,
gameplay state, and provider-specific migration. DRF preserves missing-provider
records for later recovery and never reconstructs provider-owned gameplay.
Existing saves are supported only when they already use the current record shape;
this release makes no unlimited historical-save compatibility guarantee.

## Experimental scope

Adjacent temporary excursion sites remain experimental, opt-in, and disabled by
default. They are not part of the stable compatibility guarantee. Provider
gameplay, active maps, Pawn transfer, and provider-specific rollback remain the
responsibility of consuming repositories.

## Acceptance

The supported-scope acceptance matrix completed with live provider integration.
The authoritative results and evidence references are recorded in
`MANUAL_ACCEPTANCE_REPORT.md`, including framework/Frontier smoke, persistence
and return lifecycle, provider absence/restoration, transfer rollback and retry,
identity preservation, temporary-site controls, eviction, same-tile identity
conflict, and partial materialization `Prepare` rollback.

## Installation

Install Harmony first, then extract the
`DeferredRealityFramework-0.1.0.zip` archive's `DeferredRealityFramework`
folder into RimWorld 1.6's `Mods` directory. Enable DRF after Harmony and
before any consuming mod that lists DRF as a dependency. See the
[README installation instructions](README.md#installation).

## Project and license

- Repository: [DeferredRealityFramework on GitHub](https://github.com/lanwoodall423/DeferredRealityFramework)
- License: [LICENSE](LICENSE)
