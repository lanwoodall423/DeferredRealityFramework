# Compatibility

Deferred Reality Framework targets RimWorld 1.6 and .NET Framework 4.7.2. The
Horticulture adapter targets .NET Framework 4.8 to match its host assembly. The
framework has no gameplay dependency on Wildlife, AquacultureFishing,
Horticulture, or Knowledge Framework.

The consuming mods declare the framework as a dependency and load separate
adapter assemblies:

- `Wildlife/1.6/Assemblies/DeferredReality.Wildlife.dll`
- `AquacultureFishing/1.6/Assemblies/DeferredReality.Aquaculture.dll`
- `Horticulture - Novel Seeds/1.6/Assemblies/DeferredReality.Horticulture.dll`

Knowledge Framework remains optional to the framework. Existing consumer
Knowledge adapters continue to own their domains. Framework observations are
detached records and can be bridged through `IObservationProvider` or
`RealityEventBus` without requiring Knowledge Framework.

The adapter boundary is conservative:

- Wildlife active herds, packs, jobs, pathfinding, Lords, memories, landscapes,
  UI, and exact pawns remain Wildlife-owned.
- Aquaculture constructed ponds, schools, hunger, breeding, organisms, and exact
  trait-bearing fish remain Aquaculture-owned.
- Horticulture unlocked cultivars, `VarietyRecord`, palettes, active plants,
  mutation, cross-pollination, and produce inheritance remain Horticulture-owned.
- Generic pawn/Thing compression is vetoed.
- The adjacent-region transfer path is opt-in experimental and currently falls
  back unless a safe host map factory and transfer transaction are registered.
- Wildlife registers its provider-scoped map factory and adjacent transfer host
  only when `DeferredReality.Wildlife.dll` is loaded; Frontier retains its own
  provider-scoped factory and does not replace Wildlife's registration.
- Adjacent maps are explicitly marked temporary excursion sites. The framework
  rejects construction designators, blueprint placement, and frame completion on
  those maps, but does not block terrain/structure generation or deconstruction.
  Ordinary maps and unrelated providers are not affected. The default setting
  `enableAdjacentRegions` remains false until the full in-game acceptance suite is
  complete.

The adjacent path is not described as production-ready before that live acceptance
suite passes. A warm-cache reference is never treated as map eviction; only the
registered provider factory may remove the actual map after all safety vetoes pass.
- Excursion tickets retain the exact Pawn load ID, origin map/region, destination,
  inverse edge, and transfer journal IDs. Return is attempted only through the
  same provider host; provider removal, missing origins, unsafe jobs, and ambiguous
  ownership retain the map and surface a diagnostic instead of teleporting or
  choosing another map.

The legacy `IAnchorProvider` ABI remains intact. Providers that need reversible
anchor work can additionally implement `ITransactionalAnchorProvider`; legacy
`OnAnchorMaterialized` is now a final commit-stage notification. Existing map
aliases remain authoritative during migration. Nonstandard same-tile maps need an
explicit `IRealityMapIdentityProvider` claim; unclaimed duplicates are quarantined
instead of replacing an existing region link. Map readiness is handled once by
the `Map.FinalizeInit` lifecycle hook.

Removal of an integration mod does not make framework state unloadable. Its
provider payloads and records remain orphan-inspectable. Reinstalling the mod
allows its migration marker and adapter to resume.

When RimWorld DevBridge is installed, `DevTools/Build-BridgeAdapter.ps1` publishes
an optional typed adapter with `DEFERRED_REALITY`, `DR_REGIONS`, `DR_PROCESSES`,
`DR_DUMP`, `DR_AUDIT`, `DR_COMPRESSION_DRY_RUN`, and bounded `DR_SIMULATE_DAY`
commands. The gameplay mod does not load or require that assembly.
