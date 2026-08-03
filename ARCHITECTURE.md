# Deferred Reality Framework

## Inspection and ownership baseline

This document records the ownership boundary inspected before framework code is
introduced. The framework is an independent RimWorld 1.6 mod and has no compile
time dependency on Wildlife, AquacultureFishing, Horticulture, or Knowledge
Framework.

### Existing authoritative state

| System | Current owner | Durable keys or identity | First migration boundary |
| --- | --- | --- | --- |
| Wildlife regional ecology | `RegionalWildlifeMapComponent` | `RegionalSpeciesRecord`, roaming records, habitat and seasonal fields | Import regional populations and roaming summaries into stable region records; leave legacy records readable until the import is committed |
| Wildlife herds and packs | `HerdMapComponent`, `PackMapComponent` | hidden pawns, `PackRecord`, herd and pack IDs | Remain active-map owners for v1; only regional/off-map summaries move first |
| Wildlife notable animals | notable/lives/field-journal map components | animal load IDs and existing journal identities | Import observed identity-bearing animals as framework anchors; never compress an unsafe live pawn |
| Wildlife expeditions | `HuntingExpeditionMapComponent` and related components | expedition IDs, world tiles, specialists, history and trail paths | Preserve expedition records; add destination region references without changing active expedition behavior |
| Wildlife presentation | ecology snapshots, event router, narrative, mystery, signal, landscape and UI components | runtime snapshots and bounded event history | Read framework snapshots when available; do not make UI components authoritative |
| Aquaculture natural water | `NaturalFishPopulationMapComponent` | `aquacultureNaturalFishPopulations`, water anchor/cell identity, fish `defName` | Convert each water body to a stable water-body population subject; retain the legacy component for rollback and old-save conversion |
| Aquaculture constructed ponds | `FishPondMapComponent`, `PondEcologyRecord`, `CompFishTraits` | `aquacultureDataVersion`, `pondEcology`, individual fish components | No ownership change in v1 |
| Aquaculture journal and progression | `AquacultureJournalComponent`, progression components | `aquacultureSpeciesJournal`, `aquacultureBreeds`, fishing attempts/progression | Remain authoritative; framework observations are inputs, not a second journal |
| Horticulture cultivars | `GameComponent_NovelSeeds`, `VarietyRecord` | unlocked varieties, IDs, lineage, palettes, grower selections | No ownership change in v1; framework stores only regional wild presence and references |
| Horticulture active plants and produce | plant comps and produce inheritance components | cultivar/trait IDs, packed colors, inherited produce state | No ownership change in v1 |
| Knowledge | Knowledge Framework persistence and each mod's adapter | domain, subject, context and migration IDs | Framework emits read-only observations; optional adapters translate them into Knowledge claims |

### Known save-format constraints

The following identifiers are compatibility boundaries and must not be renamed:

- Wildlife class names, Scribe keys, Def names, and its legacy Knowledge
  migration marker.
- Aquaculture keys `aquacultureNaturalFishPopulations`, `aquacultureDataVersion`,
  `pondEcology`, `aquacultureSpeciesJournal`, and `aquacultureBreeds`.
- Horticulture cultivar IDs, generated trait Def names, packed palette values,
  and the existing `GameComponent_NovelSeeds` fields.
- Knowledge domain and subject IDs, including existing map-context strings while
  they are being migrated.

Existing components may be absent, contain null Def references, contain duplicate
IDs, or refer to a removed integration mod. Conversion therefore treats every
legacy record as untrusted input: validate, quarantine invalid records, preserve
unknown payloads, and commit a per-provider migration marker only after the new
records validate.

## Framework ownership

`DeferredRealityWorldComponent` is the sole owner of latent framework state. It
stores region descriptors, topology, fidelity, timestamps, deterministic seeds,
anchors, aggregate populations, constraints, scheduled processes, observations,
environment summaries, provider payloads, schema versions, and transition
journals. A MapComponent can be a projection or reconciliation cache, but cannot
be the only durable source for an unloaded region.

Stable region identity is composed of world tile, layer, local slot/instance,
parent region, and provider namespace. `Map.uniqueID` is retained only as a
temporary active-map link and legacy migration input. It is never used as the
identity of latent state.

The first implementation is deliberately conservative:

- active maps continue to run ordinary RimWorld systems;
- no unloaded Map is ticked;
- generic pawn and Thing compression is vetoed;
- constructed ponds, colony cultivars, active Wildlife AI, jobs, Lords,
  reservations, combat, and quests remain with their current owners;
- provider payloads are versioned opaque records, not arbitrary Scribe/object
  resurrection;
- materialization and compression use prepare/validate/commit or rollback;
- all analytical simulation uses stable seeded streams and bounded catch-up.

RimWorld may invoke `Map.FinalizeInit` and map component initialization from a
`LongEventHandler` worker thread. Framework map registration, de-registration,
and adapter migrations therefore pass through `RealityMapLifecycle`; worker
callbacks are deferred with `LongEventHandler.ExecuteWhenFinished` before any
framework-owned or provider-owned mutable state is touched.

## Migration sequence

1. Create or resolve a stable region for each active map and record the legacy map
   ID in a migration alias table.
2. Validate and import Wildlife regional species and roaming summaries. Population
   subjects use `RealityRegionId`, not `Map.uniqueID`; observed animals become
   anchors only when their existing identity is reliable.
3. Import Aquaculture natural-water records using a stable water-body identity
   derived from region and topology. Do not merge constructed ponds or unrelated
   water bodies solely because they share a map.
4. Import Horticulture wild regional presence and variety references only. Do not
   move unlocked cultivar ownership or active plants.
5. Emit optional Knowledge observations and preserve existing Knowledge records;
   only remove or rewrite a legacy relation after its replacement is committed.
6. Mark each provider migration version as committed. Keep legacy fields and
   conversion aliases for at least one successful save cycle so interrupted loads
   can retry without loss.

The migration is idempotent. Duplicate stable IDs are repaired deterministically
and reported. Missing Defs remain as unresolved provider payload references rather
than being silently deleted. A provider exception vetoes that provider's cleanup
but does not make the framework save unloadable.

## Current phase boundary

The framework core, provider-scoped map factories, Wildlife regional population
import, roaming anchors, analytical cardinal migration, active-map reconciliation,
and opt-in adjacent transfer/materialization path are implemented. Wildlife still
owns active herds, exact pawns, jobs, Lords, memories, and map AI; the framework
owns only the canonical aggregate and identity projections. No legacy owner is
removed until its replacement has passed validation and a committed migration
marker exists. Frontier and other adapters remain provider-scoped and do not
share Wildlife state.
