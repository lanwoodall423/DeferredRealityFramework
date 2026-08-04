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
- all analytical simulation uses stable seeded streams and bounded catch-up;
- the world tick uses a runtime earliest-due gate, so a steady-state tick with no
  due process does not clone or sort the process collection;
- due work is ordered by due tick, descending priority, provider ID, and process
  ID. The provider receives the continuous `elapsedTicks` interval, while
  analytical steps are `max(1, floor(elapsedTicks / intervalTicks))`, bounded by
  the run budget.

After preparation begins, materialization compensates only providers that
prepared successfully, in reverse provider order. Framework state is restored
after provider rollback and a newly created map is removed last; original and
compensation failures are both reported. Transactional anchors use
prepare/apply/validate/rollback and commit cleanup. Legacy
`IAnchorProvider.OnAnchorMaterialized` remains a final, idempotent notification
and is never called before validation.

The process pause cause is persisted separately from the paused flag. Manual,
provider-failure, and provider-requested pauses remain paused until an explicit
resume. A missing provider creates a `ProviderUnavailable` suspension; registering
that same provider reactivates only those suspensions and preserves payload,
execution count, deterministic RNG epoch, and overdue timing.

The world component performs conservative retention. Audit records are
deduplicated and capped, observations evict the linear oldest record, terminal
transfer journals use a 600000-tick/256-record policy while interrupted journals
remain recoverable, and exactly-once markers without provider-declared expiry
remain durable. Cancelled processes and legacy map aliases are compacted only
after provider/reference checks prove recovery is unaffected.

RimWorld may invoke `Map.FinalizeInit` and map component initialization from a
`LongEventHandler` worker thread. Framework map registration, de-registration,
and adapter migrations therefore pass through `RealityMapLifecycle`; worker
callbacks are deferred with `LongEventHandler.ExecuteWhenFinished` before any
framework-owned or provider-owned mutable state is touched.

`RealityThreadGuard` starts unknown. Only the explicit `StaticConstructorOnStartup`
framework lifecycle establishes the main-thread ID; mutating calls fail rather
than adopting the first arbitrary caller, and provider registration from an
unknown/worker callback is deferred through RimWorld's lifecycle bridge.

Map identity is claim-based for nonstandard maps. A provider must claim pocket,
interior, underground, vehicle, ship, or other custom maps with a stable region
identity. Automatic `Surface(tile)` mapping is limited to an unambiguous standard
surface map. Persisted aliases win during migration; a second live map or
conflicting provider claims are quarantined and left unclaimed rather than
overwriting `activeMapUniqueId`. The Harmony map-finalization postfix is the sole
map readiness path.

Constraint conflicts use explicit `conflictDomainKeys` and `conflictFacetKeys`.
Only intersecting domains with incompatible semantics conflict. Legacy records
fall back to provider/type plus affected subject, facet, or region, never subject
alone. Duplicate repair keeps the first serialized valid slot, replacing its
value only when a higher schema version or explicit newer update tick proves the
later record newer.

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

## Adjacent excursion boundary

An adjacent map is an explicit, typed temporary-site role. Its marker records the
provider, stable region, origin region/map, creation/access ticks, and lifecycle;
it is applied after map generation so ordinary generation is not constrained by
the construction policy. `WildlifeDeferredMapParent` supplies the provider-scoped
region identity used by map claims. Marked maps are hard non-buildable work sites:
build/install designators, blueprint placement, blueprint replacement, frame
completion, and post-readiness artifact cleanup are guarded, while generation and
deconstruction remain available.

An excursion ticket is written only after a committed outbound transfer verifies
the same Pawn instance on the destination map. It retains exact origin and inverse
return-edge data, outbound/return journal IDs, heartbeat and lease deadlines, and
retry/diagnostic state. The coarse monitor checks only marked maps and ticketed
Pawns. Explicit completion, an expired lease with a meaningful-task-free Pawn, or
an idle fallback can request return; combat, drafting, mental/medical/sleep states,
carrying, player-forced jobs, provider jobs, and unresolved ownership always defer
with backoff. Return uses the provider's reverse transfer and completes only after
the same Pawn is on the exact origin map. Save/load reconciliation uses transfer
journals and quarantine rather than reconstructing or guessing ownership.

Warm adjacent maps are evicted by last access, never by dropping a dictionary
reference. Eviction requires no active excursion, player pawn/prisoner/corpse/
carried pawn/item, or interrupted recovery record, then successful provider
compression and registered factory removal. Viewed maps and every veto remain
diagnostically visible. Adjacent behavior remains behind the opt-in feature flag
until the provider and in-game acceptance suite passes.

Excursion ownership is also unique by Pawn load ID while a ticket is nonterminal.
After load, a completed return journal finalizes an existing Returning ticket only
when the exact Pawn is already on its recorded origin map. Provider removal retains
the live map and ticket until the same scoped transfer host is registered again.
Warm eviction is not a general-purpose map cache: dropping a reference is never
eviction, and the adjacent feature remains experimental until the live acceptance
suite passes.
