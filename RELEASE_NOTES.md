# Deferred Reality Framework v0.1.0-rc.1

This first public release candidate freezes the provider-neutral framework
contract for RimWorld 1.6. Provider adapters are built, tested, and released
by their consuming mod repositories.

## Compatibility baseline

`v0.1.0-rc.1` is the first release with a compatibility guarantee. DRF preserves
its current provider-neutral record shape, stable identifiers, registration
contract, and opaque provider payloads across the 0.1.x line, subject to the
repair rules in `SAVE_FORMAT.md`. There is no root-schema negotiation or
general pre-release migration promise. Pre-release saves are supported only
when they already use the current record shape.

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
default. They are not included in the release-candidate guarantee. No live or
manual acceptance pass is claimed for this release; unexecuted manual cases are
not release evidence.

Provider gameplay, active maps, Pawn transfer, and provider-specific rollback
remain the responsibility of consuming repositories.

## Installation

Install Harmony first, then extract the
`DeferredRealityFramework-0.1.0-rc.1.zip` archive's `DeferredRealityFramework`
folder into RimWorld 1.6's `Mods` directory. Enable DRF after Harmony and
before any consuming mod that lists DRF as a dependency. See the
[README installation instructions](README.md#installation).

## Project and license

- Repository: [DeferredRealityFramework on GitHub](https://github.com/lanwoodall423/DeferredRealityFramework)
- License: [LICENSE](LICENSE)

## Qualification summary

The qualified `DeferredRealityFramework.dll` passed source build, static
validation, fresh deployment, artifact-freshness verification, and the
`deferred-reality-in-game-smoke` runtime test on RimWorld 1.6. The package
contains the qualified framework binary; no full stable live acceptance matrix
is claimed for this release candidate.

See [Architecture](ARCHITECTURE.md), [Save Format](SAVE_FORMAT.md),
[Compatibility](COMPATIBILITY.md), and [Provider Guide](PROVIDER_GUIDE.md) for
the public contracts and integration boundaries.
