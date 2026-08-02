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

Removal of an integration mod does not make framework state unloadable. Its
provider payloads and records remain orphan-inspectable. Reinstalling the mod
allows its migration marker and adapter to resume.

When RimWorld DevBridge is installed, `DevTools/Build-BridgeAdapter.ps1` publishes
an optional typed adapter with `DEFERRED_REALITY`, `DR_REGIONS`, `DR_PROCESSES`,
`DR_DUMP`, `DR_AUDIT`, `DR_COMPRESSION_DRY_RUN`, and bounded `DR_SIMULATE_DAY`
commands. The gameplay mod does not load or require that assembly.
