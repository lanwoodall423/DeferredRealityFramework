# Test Plan

## Pure automated checks

`Tests/DeferredReality.PureTests.csproj` runs without a RimWorld world and verifies
stable region serialization, deterministic seeds/RNG, and pure scheduling,
transition, retention, identity, constraint, and adjacent-policy seams.

Focused checks cover:

- earliest-due scheduler gating and deterministic due ordering;
- first-due, one-interval, one-tick-before-two-intervals, exactly-two-intervals,
  and maximum-step catch-up boundaries;
- manual, provider-unavailable, provider-failure, provider-requested, resume,
  cancellation, and provider re-registration pause transitions;
- interrupted transfer journals, terminal-history retention, observation eviction,
  cancelled-process retention, and bounded diagnostics;
- first-slot duplicate repair with schema/update-tick preference and save defaults;
- transactional fake-provider failures at every materialization stage, including a
  provider whose partial `Prepare` throws, reverse idempotent rollback, and
  original/rollback error preservation;
- transactional anchors, final-only legacy notifications, scoped compression, and
  committed-compression compensation;
- standard-map collisions, explicit provider map claims, conflicting claims,
  persisted aliases, and single map-readiness handling;
- core `Surface(tile)` identity with a separate provider adjacent owner, creation
  intent binding, pre-existing-map rejection, stale/conflicting intent rejection,
  load-time reconciliation, and generated-map classification;
- explicit constraint domains/facets and compatible legacy fallback semantics;
- adjacent safe-idle/unsafe-state, return grace/completion/backoff, inverse edges,
  recency/tie-stable eviction, active-lease blocking, construction policy, and
  terminal transition states;
- provider task IDs, bounded fresh evidence, explicit completion/abandonment,
  stale/future evidence rejection, lease-expiry idle fallback, and dirty/coarse
  maintenance;
- exactly-once operation domains, sequence cursor replay rejection, gap and
  out-of-order behavior, save/load cursor preservation, and durable legacy domains;
- creation-intent stale/ambiguous/ID-reuse recovery and terminal excursion queries;
- explicit main-thread establishment and worker-thread mutation rejection.

## Framework checks

- repeat analytical process execution with the same seed and explicit epoch;
- compare concise snapshots before and after save/load;
- verify no-due ticks do not enumerate/sort the full process collection;
- isolate missing and throwing providers without losing payloads;
- preserve unknown provider records and missing Def references;
- migrate schemas idempotently and quarantine duplicate IDs;
- force materialization/factory/constraint/anchor/validation failure and verify
  latent state, intent, provider, anchor, and created-map rollback;
- verify compression vetoes, owner isolation, and rollback after failed map removal;
- round-trip topology, aliases, adjacent markers, tickets, diagnostics, cursors,
  and creation intents;
- verify maintenance is dirty/coarse rather than a full scan after each mutation;
- verify active versus terminal ticket indexing and recoverable cancelled tickets;
- verify provider task cleanup occurs only after terminal recovery is complete.

## Provider integration checks

Each consuming provider tests its own legacy migration, gameplay ownership,
population semantics, map factory, identity claims, transfer host, task evidence,
and provider-specific rollback in its own repository. The framework tests only
provider-neutral contracts and synthetic providers. Provider assemblies are not
built or packaged by this project.

Adjacent in-game cases must cover outbound transfer, idle return, explicit task
completion, save/load during outbound and return, missing origin, unsafe Pawn state,
duplicate monitor ticks, provider removal/re-registration, construction designator
and blueprint/frame defense-in-depth, same-tile identity conflicts, partial
`Prepare` compensation, and warm eviction vetoes/successful factory removal.
Verify the exact Pawn instance retains inventory/equipment/apparel/health/relations/
needs and that no alternate map is selected.

## Provider-neutral dev actions

Actions are exposed under `Deferred Reality`: inspector, one-day/quadrum/year
catch-up, audit, concise dump, compression dry-run, adjacent diagnostics, an
immediate adjacent safety monitor, eviction attempt, `Heartbeat selected adjacent
excursion`, `Request return for selected adjacent excursion`, and `Run provider-
neutral adjacent release checks`. These use only public lease, diagnostic, and
eviction APIs; they do not fake provider map creation, save/load, or deinitialization.

## Manual RimWorld/Scribe checklist

1. Create or load a home map and enable the experimental adjacent setting.
2. Use the consuming provider's public path to create a cardinal adjacent site;
   verify represented region, explicit provider owner, temporary lifecycle, intent
   cleanup, construction rejection, and ordinary-map behavior.
3. Transfer one Pawn through the provider path; inspect exact Pawn load ID, origin
   map, provider task ID, and durable outbound ticket.
4. Save and reload while resident; call provider heartbeat/evidence, then explicit
   completion or abandonment.
5. Save and reload during return; run the monitor twice and verify one completed
   return journal, the same Pawn instance on the origin map, and no duplicate ticket.
6. Attempt buildings, walls, furniture, floors, reinstall, blueprints, and frames
   on the marked map; verify rejection while deconstruction and ordinary maps work.
7. Remove the provider or make the origin unavailable; verify the Pawn/site remain
   alive with a diagnostic and retry state.
8. Clear the site and attempt eviction; verify compression plus the owning factory
   removes the real map and deinitializes it, or a clear veto is retained.
9. Create same-tile identities and verify ambiguous claims never overwrite a
   projection. Force partial `Prepare` and verify reverse compensation.

This checkout has no automated live-world harness. The above runtime cases were
not executed by the pure test process; they require a consuming provider's test
environment and actual RimWorld/Scribe save/load. Adjacent regions remain disabled
by default and are not production-ready until this checklist passes in-game.
