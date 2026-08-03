# Test Plan

## Pure automated checks

`Tests/RealityPureTests.csproj` runs without a RimWorld world and verifies stable
region serialization, deterministic seeds, and deterministic RNG streams.

## Framework checks

- repeat analytical process execution with the same seed and explicit epoch;
- compare concise snapshots before and after save/load;
- verify process priority/provider/ID ordering and bounded catch-up;
- isolate provider exceptions and retain paused process payloads;
- preserve unknown providers and missing Def references;
- migrate schemas idempotently and quarantine duplicate IDs;
- detect incompatible constraints without silently selecting one;
- force materialization failure and verify latent rollback;
- verify compression vetoes and rollback;
- round-trip topology and map aliases;
- capture diagnostics repeatedly without changing revisions.
- load a new game through `LongEventHandler` map initialization and verify that
  deferred map registration and all installed adapter migrations execute on the
  main thread without blocking map readiness.

## Provider and in-game checks

Wildlife: old regional records, roaming identity, exact-once local kill/birth,
cooldown after hunting, expedition population target, trail causality, cardinal
topology seeding, latent population migration, adjacent materialization,
atomic pawn transfer/rollback, safe fallback, and no duplicate regional ticking.

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
catch-up, audit, concise dump, and compression dry-run. No UI action is used by
the normal scheduler.
