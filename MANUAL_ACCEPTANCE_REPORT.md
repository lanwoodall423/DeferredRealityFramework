# Manual Acceptance Report

Status: `NOT ACCEPTED — mandatory live matrix incomplete; stable promotion forbidden`

Fresh evidence was obtained through the canonical DevBridge/RimLiaison workflow
against the RC assembly hash
`e19fd10d99b0948a53a00783ae985f1aa09c9bf8eaede3d91dd7d5d21650f82c`.
The following rows remain unexecuted or only partial; they are not promoted.

| Check | Evidence | Result | Remaining action |
|---|---|---|---|
| Save/reload while excursion is outbound | `artifacts/release/live-save-outbound.json`, `live-load-outbound.json`, `live-snapshot-outbound-after-load.json` | PASS | None for this stage |
| Save/reload during return | `live-journey-return-after-reload.json` only | UNEXECUTED | Provider bridge has no save point while stage is `returning` |
| Provider heartbeat, completion, abandonment after reload | No provider callback fixture | UNEXECUTED | Add/use a provider-owned reload callback scenario |
| Forced `Pawn.ExitMap` failure/no-op with zero aggregate drift | No failure-injection surface | UNEXECUTED | Exercise provider-owned ExitMap failure and aggregate assertions |
| Successful departure and exactly-one aggregate transfer | Stateful report: `frontier-stateful-tests.json` | PARTIAL | Add aggregate-before/after and exactly-one transfer assertions |
| Missing origin and provider removal/re-registration | No live scenario surface | UNEXECUTED | Run provider removal and origin-loss recovery in-game |
| Exact Pawn identity and inventory/equipment/apparel/health/relations/needs | Snapshot exposes only `partyCount` | UNEXECUTED | Compare exact Pawn and all listed state before/after |
| Duplicate monitor ticks and duplicate operation replay | Stateful report + runtime-gap suite | PARTIAL PASS | Monitor-tick duplicate path remains unobserved |
| Construction, blueprint, frame, floor, and reinstall blocking | No live temporary-site construction scenario | UNEXECUTED | Exercise every listed construction path on a marked map |
| Ordinary-map behavior remains unaffected | No paired live ordinary-map run | UNEXECUTED | Repeat construction checks on an ordinary map |
| Safe eviction and vetoed eviction | No live eviction scenario | UNEXECUTED | Verify deinitialization and veto retention in-game |
| Same-tile identity conflicts and partial-Prepare rollback | Frontier smoke map identity PASS; rollback capability PASS | PARTIAL | Run same-tile conflict and provider partial-Prepare cases live |

The provider state was restored to its clean fixture after the run.

## Stable Runtime Qualification

Status: `NOT ACCEPTED — live matrix incomplete`

RC runtime evidence is current and positive for the exercised subset:

- outbound save/reload preserved a planned journey and party;
- arrived save/reload preserved the materialized destination identity;
- return completed after reload and returned save/reload loaded as `compatible`;
- no `ERROR` records were returned for generation 1;
- no stable version artifact was promoted.

Stable qualification requires fresh evidence for every mandatory supported case.
No stable source/version/tag/package changes were made.

## Current Live Evidence

- Frontier smoke: workflow `rw-ea517ee70c344d1980cf1e92d774f1ff`, PASS.
- Frontier runtime gaps: workflow `rw-fea3413adafc443184adc8f8a49a362d`, PASS 3/3.
- Frontier stateful report: `DevBridge2/Runtime/frontier-stateful-tests.json`, PASS 15/15 operations.
- Runtime generation: `1`, launch ID `98d3aec241074681908fed456d5262ef`.
- DevBridge status after cleanup: `READY`, no active test lease.

The remaining rows are not classified as DRF product failures: the canonical
provider fixture does not expose the required live scenarios. They still block
stable acceptance because the acceptance contract requires evidence, not a
waiver.

Adjacent regions remain disabled by default and experimental; this boundary
does not waive any mandatory supported core case.


## RC2 Qualification

Status: `NOT ACCEPTED — Frontier smoke and lifecycle-specific live evidence remain incomplete`

RC2 framework evidence:

- Release build, pure regression executable, repository integrity, and output
  audit: PASS.
- Canonical DRF affected/runtime workflow
  `rw-9f89b4b1479c41dea97f2160ab4a6b12`: PASS.
- RC2 assembly SHA-256:
  `cfbe5d2438c58be8112dffba1efe3044fd8119d9cbf579bda581266cc32280b3`.
- RC2 package SHA-256:
  `0678ea4c49831fcfcdcbe08977fa4711bc4c0ee283bca8b76f57eb4cfc116719`.

Frontier evidence:

- Release source build: PASS.
- Canonical `runtime-gaps` workflow
  `rw-8f9c1722a6e8480ea6e994380a8601d8`: PASS, 3/3.
- Canonical `smoke` workflows `rw-df469206201d4b85be4ffc8b8697d57c` and
  `rw-7cfa75b862a2435f85d0a60d73870392`: BLOCKED by
  `DEVBRIDGE_INTERNAL_TRANSACTION_FAILED` before test execution.
- The new provider return-gate control is present in the separated fixture
  surface, but no catalog scenario currently drives a real DRF ticket through
  `Returning` with `Pending` and `Ready`; those lifecycle rows remain
  unexecuted.

RC.1 artifacts, hashes, and tag remain unchanged. RC2 is not recommended for
stable promotion until the blocked Frontier smoke and lifecycle-specific live
matrix are rerun through canonical RimTest/RimLiaison.
