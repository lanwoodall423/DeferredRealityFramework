# Compatibility

Deferred Reality Framework targets RimWorld 1.6 and .NET Framework 4.7.2. The
framework has no gameplay dependency on consuming gameplay mods or optional
knowledge bridges.

Consuming mods declare the framework as a dependency and load their own separate
provider assemblies. Provider assemblies are not packaged or built by this
framework project.

The default `DevTools/Build-All.ps1` workflow builds only the framework and
provider-neutral pure tests, then runs the pure executable. Provider repositories
own adapter builds and release packages. `DevTools/Audit-Outputs.ps1` audits only
DRF-owned outputs. Local compilation resolves RimWorld and Harmony through
`RIMWORLD_ROOT`, `DEFERRED_REALITY_HARMONY_PATH`, or the shared MSBuild
properties; the CI integrity workflow performs source and package checks without
proprietary game assemblies.

Knowledge Framework remains optional to the framework. Existing consumer
Knowledge adapters continue to own their domains. Framework observations are
detached records and can be bridged through `IObservationProvider` or
`RealityEventBus` without requiring Knowledge Framework.

The adapter boundary is conservative:

- Provider active maps, jobs, pathfinding, Lords, memories, landscapes, UI, and
  exact pawns remain provider-owned.
- Provider constructed objects, organisms, progression, cultivars, traits, and
  active gameplay remain provider-owned.
- Generic pawn/Thing compression is vetoed.
- The adjacent-region transfer path is opt-in experimental and currently falls
  back unless a safe host map factory and transfer transaction are registered.
- A consuming provider registers its own provider-scoped map factory and adjacent
  transfer host only when its own assembly is loaded; the framework never
  substitutes a fallback provider implementation.
- Adjacent maps are explicitly marked temporary excursion sites. The framework
  rejects construction designators, blueprint placement, and frame completion on
  those maps, but does not block terrain/structure generation or deconstruction.
  Ordinary maps and unrelated providers are not affected. The default setting
  `enableAdjacentRegions` remains false until the full in-game acceptance suite is
  complete.

`RealityRegionId.ProviderNamespace` remains the namespace of represented latent
state. It is not required to equal `RealityAdjacentMapRecord.providerId`:
provider ownership is a separate persisted field so a provider can own a
temporary site representing a `core` `Surface(tile)` region.

## Save compatibility baseline

`v0.1.0-rc.2` is the current public compatibility baseline. Existing/pre-release
saves are supported only when they already use the current DRF field names and
record types. DRF repairs absent optional collections and preserves unknown
provider payloads and missing-provider records, but it has no root schema
negotiation, historical schema 3/4/5 guarantee, pre-release migration layer, or
map-ID alias table.

The RC guarantee covers the current provider-neutral record shape, stable
identifiers, registration contract, and opaque provider payloads across the
0.1.x line, subject to the repair rules in `SAVE_FORMAT.md`.

Providers are responsible for provider payload interpretation, stable provider
identifiers, provider-owned map-component imports, gameplay state, and any
provider-specific migration. DRF preserves provider-owned payloads as opaque
records, keeps missing-provider data inspectable, and does not reconstruct
provider gameplay or objects.

The adjacent path remains experimental, opt-in, and disabled by default until
the live acceptance suite passes. A warm-cache reference is never treated as
map eviction; only the registered provider factory may remove the actual map
after all safety vetoes pass. Excursion tickets retain the exact Pawn load ID,
origin map/region, destination, inverse edge, and transfer journal IDs. Return
is attempted only through the same provider host; provider removal, missing
origins, unsafe jobs, and ambiguous ownership retain the map and surface a
diagnostic instead of teleporting or choosing another map.

The legacy `IAnchorProvider` ABI remains intact. Providers that need reversible
anchor work can additionally implement `ITransactionalAnchorProvider`;
`OnAnchorMaterialized` is a final commit-stage notification. Nonstandard
same-tile maps need an explicit `IRealityMapIdentityProvider` claim; unclaimed
duplicates are quarantined instead of replacing an existing region link. Map
readiness is handled once by the `Map.FinalizeInit` lifecycle hook.

During generation, a transaction-scoped intent is the only compatibility path that
can reclassify a newly created map. The `Map.FinalizeInit` postfix and provider map
component callbacks delegate to the same intent-aware `RegisterMap` logic, so
repeated callbacks cannot create different ordinary or adjacent roles.

Removal of an integration mod does not make framework state unloadable. Its
provider payloads and records remain orphan-inspectable. Reinstalling the mod
allows its migration marker and adapter to resume.

`DevTools/Build-BridgeAdapter.ps1` builds the optional typed adapter pair into
`DevTools/BridgeAdapters` with the `DEFERRED_REALITY`, `DR_REGIONS`,
`DR_PROCESSES`, `DR_DUMP`, `DR_AUDIT`, `DR_COMPRESSION_DRY_RUN`, and bounded
`DR_SIMULATE_DAY` commands. Dev Bridge discovers it only when both mods are
loaded; the gameplay mod does not load or require that assembly.
