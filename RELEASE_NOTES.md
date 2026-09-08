# Deferred Reality Framework v0.1.0-rc.2

This release candidate hardens the provider-neutral excursion return lifecycle
for RimWorld 1.6. Provider adapters are built, tested, and released by their
consuming mod repositories.

## Changes since rc.1

- Return authorization now crosses a durable `Returning` boundary before any
  inverse transfer. The boundary ends the current monitor invocation.
- An optional `IRealityExcursionReturnGate` provider capability can keep a
  return `Pending`; an absent gate remains `Ready` for compatibility.
- Pending returns retain bounded diagnostics/backoff, transfer failures retain
  the existing `ReturnRequested` retry semantics, and `Returning` survives
  save/load.
- Frontier adds a guarded provider-owned `Ready`/`Pending` return-gate fixture
  and reports its deterministic state in the existing semantic snapshot.

## Compatibility baseline

`v0.1.0-rc.2` preserves the provider-neutral record shape, stable identifiers,
registration contract, and opaque provider payloads across the 0.1.x line,
subject to the repair rules in `SAVE_FORMAT.md`. The provider registration API
version remains `1`; the new return gate is additive and optional.

Providers own provider payload semantics, provider IDs, map-component imports,
gameplay state, and provider-specific migration. DRF preserves missing-provider
records for later recovery and never reconstructs provider-owned gameplay.

## Stable scope

- Deterministic regional simulation, scheduler gating, retention, and
  replay-safe exactly-once cursor APIs.
- Persistence, provider-neutral records and contracts, observations,
  diagnostics, fidelity transitions, and rollback-safe framework services.
- Consumer integration through the public provider registry and capability
  interfaces. Provider registration API version `1` is the supported version.
- Framework-only build, integrity, output-audit, and deterministic packaging
  checks.

## Experimental scope

Adjacent temporary excursion sites remain experimental, opt-in, and disabled by
default. They are not included in the release-candidate guarantee. Live
qualification remains limited to the cases explicitly recorded in the
acceptance report; unexecuted manual cases are not release evidence.

Provider gameplay, active maps, Pawn transfer, and provider-specific rollback
remain the responsibility of consuming repositories.

## Installation

Install Harmony first, then extract the
`DeferredRealityFramework-0.1.0-rc.2.zip` archive's `DeferredRealityFramework`
folder into RimWorld 1.6's `Mods` directory. Enable DRF after Harmony and
before any consuming mod that lists DRF as a dependency. See the
[README installation instructions](README.md#installation).

## Project and license

- Repository: [DeferredRealityFramework on GitHub](https://github.com/lanwoodall423/DeferredRealityFramework)
- License: [LICENSE](LICENSE)

## Qualification summary

The RC2 framework assembly passed source build, repository integrity, output
audit, and the pure regression executable. Frontier's current source assembly
also passed its Release build. The mandatory live matrix remains subject to the
canonical RimTest/RimLiaison workflow and is not claimed here beyond evidence
recorded in `MANUAL_ACCEPTANCE_REPORT.md`.

See [Architecture](ARCHITECTURE.md), [Save Format](SAVE_FORMAT.md),
[Compatibility](COMPATIBILITY.md), and [Provider Guide](PROVIDER_GUIDE.md) for
the public contracts and integration boundaries.
