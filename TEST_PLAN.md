# Test Plan

## Pure automated checks

`Tests/DeferredReality.PureTests.csproj` runs without a RimWorld world and verifies
stable region serialization, deterministic seeds, deterministic RNG streams, and
the pure scheduler/retention seams.

Focused pure checks cover:

- earliest-due scheduler gating and deterministic due ordering;
- first-due, one-interval, one-tick-before-two-intervals, exactly-two-intervals,
  and maximum-step catch-up boundaries;
- distinct manual, provider-unavailable, and provider-failure pause transitions;
- provider removal/re-registration behavior with payload/timing/execution state;
- terminal/interrupted transfer-journal compaction, operation expiry opt-in, and
  linear deterministic observation eviction;
- backward-compatible defaults for new process and provider retention fields.
- first-slot duplicate repair, schema/update-tick preference, and save-compatible
  constraint domain/facet defaults;
- transactional fake-provider failures at preparation, missing factory, map
  creation, apply, constraint, anchor, and validation stages, including reverse
  rollback order and preservation of original/rollback errors;
- transactional anchor compensation and final-only legacy notifications;
- compression owner isolation, missing/ambiguous ownership, and prepared-only
  rollback;
- same-tile standard-map collisions, explicit provider map claims, conflicting
  claims, persisted-alias authority, and single map-readiness handling;
- core `Surface(tile)` region identity with a separate Wildlife adjacent owner;
  creation-intent binding, pre-existing-map rejection, stale/conflicting intent
  rejection, load-time intent clearing, and generated-map reclassification;
- compatible and incompatible constraint facets such as `departed north` and
  `injured` sharing a subject without a false conflict.
- adjacent return grace/completion/unsafe-state/backoff boundaries, inverse edges,
  recency/tie-stable warm eviction, active-lease blocking, marker/ticket defaults,
  and construction rejection policy.
- partial `Prepare` mutation followed by a thrown provider, reverse rollback of the
  throwing provider, idempotent repeated recovery, and preserved rollback errors;
- terminal excursion/retired-marker/resolved-diagnostic age and cap selection,
  recoverable-record retention, and repeated-monitor recency stability;
- exactly-once demography/transfer/consume/release/active-map-reconcile markers
  retained without a watermark and removed only with a matching domain watermark;
- provider task IDs, bounded fresh task evidence, explicit completion/abandonment,
  lease-expiry safe-idle fallback, and dirty/coarse maintenance scheduling.

Pure adjacent checks also cover safe-idle classification and rejection of worker
threads by the explicit main-thread guard.

## Framework checks

- repeat analytical process execution with the same seed and explicit epoch;
- compare concise snapshots before and after save/load;
- verify process priority/provider/ID ordering and bounded catch-up;
- verify a no-due world tick does not enumerate/sort the full process collection;
- isolate provider exceptions and retain paused process payloads;
- verify provider re-registration resumes only `ProviderUnavailable` processes;
- preserve unknown providers and missing Def references;
- migrate schemas idempotently and quarantine duplicate IDs;
- detect incompatible constraints without silently selecting one;
- force materialization failure and verify latent rollback;
- verify compression vetoes and rollback;
- round-trip topology and map aliases;
- capture diagnostics repeatedly without changing revisions.
- verify quarantine/conflict caps, terminal journal retention, safe cancelled-process
  selection, alias reference checks, and durable operation markers without policy;
- load a new game through `LongEventHandler` map initialization and verify that
  deferred map registration and all installed adapter migrations execute on the
  main thread without blocking map readiness.
- fail a generated adjacent transition at factory, provider, validation, and
  intent-marking stages and verify framework/map/intent rollback in diagnostics.

## Provider and in-game checks

Wildlife: old regional records, roaming identity, exact-once local kill/birth,
cooldown after hunting, expedition population target, trail causality, cardinal
topology seeding, latent population migration, adjacent materialization,
atomic pawn transfer/rollback, safe fallback, and no duplicate regional ticking.
Adjacent in-game cases must cover outbound transfer, idle return, explicit task
completion, save/load during outbound and return, missing origin, unsafe pawn state,
duplicate monitor ticks, provider removal/re-registration, construction designator
and blueprint/frame defense-in-depth, and warm eviction vetoes/successful factory
removal. Verify the exact Pawn instance retains inventory/equipment/apparel/health/
relations/needs and that no alternate colony map is selected.
The `Create Wildlife Adjacent Site` developer action exercises the real Wildlife
factory and verifies the core region identity, `lan.wildlife` owner, adjacent
marker, construction rejection, and registration/intent cleanup.

Aquaculture: natural river/coast migration links, closed-water isolation, catch
exactness, species diversity/rarity, stable water IDs after topology rebuild,
and unchanged constructed ponds.

Horticulture: deterministic latent flora, discovered variety anchors, existing
`VarietyRecord` identity after reload, one-time wild harvest reconciliation,
palette/trait stability, and unchanged colony crops.

Compatibility: framework alone, each adapter alone, all adapters together,
provider-scoped factory load order and shared-region materialization,
Knowledge Framework present/absent where optional, DevBridge absent/present,
old saves, multiple maps, unsupported components, and removed providers.

Dev actions are exposed under `Deferred Reality`: inspector, one-day/quadrum/year
catch-up, audit, concise dump, compression dry-run, adjacent diagnostics, an
immediate adjacent safety monitor, an eviction attempt, `Create and verify Wildlife
adjacent site`, `Transfer selected pawn to Wildlife adjacent site`, `Heartbeat
selected Wildlife excursion`, `Complete and return selected Wildlife excursion`,
and `Run adjacent release-readiness checklist`. The transfer, heartbeat, and
completion actions use the public lease/transfer APIs and log exact Pawn/ticket/
return-journal results; they do not fake save/load or map deinitialization.

Manual live checklist:

1. Create or load a home map and enable the experimental adjacent-region setting.
2. Run `Create and verify Wildlife adjacent site`; verify the core `Surface(tile)`
   region, `lan.wildlife` marker owner, temporary lifecycle, intent cleanup, and
   build rejection while ordinary-map construction remains available.
3. Run `Transfer selected pawn to Wildlife adjacent site`; inspect the exact Pawn
   load ID, origin map, task ID, and durable outbound ticket in the diagnostics dump.
4. Save and reload while resident; run `Heartbeat selected Wildlife excursion` or
   the Wildlife integration callback, then run `Complete and return selected Wildlife excursion`.
5. Save and reload during return, run the monitor twice, and verify one completed
   return journal, the same Pawn instance on the origin map, and no duplicate ticket.
6. Attempt buildings, walls, furniture, floors, reinstall, blueprints, and frames
   on the adjacent map; verify the temporary-work-site rejection. Verify ordinary
   maps and deconstruction are unaffected.
7. Remove the provider or make the origin unavailable, run the monitor, and verify
   the Pawn/site remain alive with a diagnostic and retry state.
8. Clear the site, run the eviction action, and verify compression plus the owning
   factory remove the real map and deinitialize it; otherwise verify a veto.
9. Run the checklist after each save/load boundary. Same-tile identity conflicts
   and partial-Prepare compensation remain covered by pure tests unless a provider
   test harness is installed.

No UI action is used by the normal scheduler. The adjacent feature flag remains
disabled by default and adjacent regions are not production-ready until this live
checklist passes in RimWorld.

This checkout has no separate automated live-world harness. The listed RimWorld and
Scribe scenarios are exercised through the adjacent diagnostics/dev actions and the
Wildlife integration test environment, with Pawn identity and journal transitions
inspected in the diagnostics dump.
