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
- compatible and incompatible constraint facets such as `departed north` and
  `injured` sharing a subject without a false conflict.
- adjacent return grace/completion/unsafe-state/backoff boundaries, inverse edges,
  recency/tie-stable warm eviction, active-lease blocking, marker/ticket defaults,
  and construction rejection policy.

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
immediate adjacent safety monitor, and an eviction attempt. No UI action is used
by the normal scheduler. The adjacent feature flag remains disabled by default
until these in-game checks pass.

This checkout has no separate automated live-world harness. The listed RimWorld and
Scribe scenarios are exercised through the adjacent diagnostics/dev actions and the
Wildlife integration test environment, with Pawn identity and journal transitions
inspected in the diagnostics dump.
