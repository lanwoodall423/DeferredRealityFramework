# Deferred Reality Framework Stable Acceptance Report

Status: `PASS — supported-scope live acceptance complete`

## Release lineage

- Stable release commit: the commit tagged `v0.1.0`; final hash is recorded by release evidence.
- Qualified stable DRF DLL SHA-256:
  `1255106C9141E3B54FE54D0001345B44E31CE3665567E2E71D4E99C1FCB2661C`.
- Qualified RC.2 source commit: `c12e688008f0bfb8f4c76d4edf15140f922ac1c9`.
- Qualified RC.2 DRF DLL SHA-256:
  `CFBE5D2438C58BE8112DFFBA1EFE3044FD8119D9CBF579BDA581266CC32280B3`.
- Provider API version: `1`.
- Stable artifact loaded in live RimWorld generation: `28`.
- Stable exact-artifact quicktest:
  `run-09eacb2d7ccb4f67aa3d610b23f1eb65`, `success: true`.
- Stable quicktest evidence: `DevBridge2/Runtime/readiness.json`.
- Stable generation-28 error query: `success: true`, zero error records.
- Frontier acceptance fixture commit:
  `27a9565f415fc6e36bb5d246d1326e7c1bb104c5`.
- Final Frontier live generation: `26`.
- Framework smoke workflow:
  `rw-ea517ee70c344d1980cf1e92d774f1ff`.
- Framework runtime-gap workflow:
  `rw-fea3413adafc443184adc8f8a49a362d`.

The stable release changes no framework implementation behavior. The RC.2 live
matrix remains valid behavioral evidence because the stable candidate changes
the runtime implementation only through release identity metadata.

## Supported-scope matrix

| Supported case | Result | Evidence |
|---|---|---|
| Framework/Frontier smoke | PASS | `artifacts/release/frontier-smoke.json`; workflow `rw-ea517ee70c344d1980cf1e92d774f1ff` |
| Runtime-gap coverage | PASS | `artifacts/release/frontier-runtime-gaps-pass.json`; workflow `rw-fea3413adafc443184adc8f8a49a362d` |
| Stateful operations | PASS | `artifacts/release/live-fixture-setup.json`, `live-progression-advance.json`, `live-fixture-cleanup.json` |
| Outbound save/reload | PASS | `artifacts/release/live-save-outbound.json`, `live-load-outbound.json`, `live-snapshot-outbound-after-load.json` |
| Arrived save/reload | PASS | `artifacts/release/live-save-arrived.json`, `live-load-arrived.json`, `live-snapshot-arrived-after-load.json` |
| Durable `ReturnRequested -> Returning` | PASS | `artifacts/release/live-journey-return-after-reload.json` |
| Save/reload while Returning | PASS | `artifacts/release/live-save-returned.json`, `live-load-returned.json`, `live-snapshot-returned-after-load.json` |
| Provider-controlled Pending/Ready return gate | PASS | `artifacts/release/live-journey-return-after-reload.json`; Frontier fixture commit `27a9565f415fc6e36bb5d246d1326e7c1bb104c5` |
| Exactly-once completion and replay safety | PASS | `artifacts/release/live-snapshot-returned-after-load.json`; `artifacts/release/live-fixture-cleanup.json` |
| Provider disappearance/removal and restoration | PASS | `artifacts/release/live-clean-status-final.json`; `artifacts/release/frontier-runtime-gaps-pass.json` |
| Semantic state preservation through provider absence | PASS | `artifacts/release/live-snapshot-returned-after-load.json` |
| Forced transfer failure after Pawn ownership mutation | PASS | `artifacts/release/frontier-runtime-gaps-pass.json`; Frontier fixture commit `27a9565f415fc6e36bb5d246d1326e7c1bb104c5` |
| Rollback with zero committed effects | PASS | `artifacts/release/frontier-runtime-gaps-pass.json` |
| Clean retry with exactly one commit/effect | PASS | `artifacts/release/frontier-runtime-gaps-pass.json` |
| Exact Pawn identity and inventory/equipment/apparel/hediff/relations/needs preservation | PASS | `artifacts/release/live-snapshot-returned-after-load.json`; Frontier fixture commit `27a9565f415fc6e36bb5d246d1326e7c1bb104c5` |
| No duplicate Pawn identity | PASS | `artifacts/release/live-snapshot-returned-after-load.json` |
| Origin unavailability and save/reload | PASS | `artifacts/release/live-clean-status-final.json`; `artifacts/release/frontier-runtime-gaps-pass.json` |
| No alternate-origin substitution | PASS | `artifacts/release/live-snapshot-returned-after-load.json` |
| Exact-origin restoration and successful retry | PASS | `artifacts/release/live-snapshot-returned-after-load.json` |
| Temporary-site provider ownership | PASS | `artifacts/release/frontier-runtime-gaps-pass.json`; Frontier fixture commit `27a9565f415fc6e36bb5d246d1326e7c1bb104c5` |
| Temporary-site construction/blueprint/frame/floor/reinstall vetoes | PASS | `artifacts/release/frontier-runtime-gaps-pass.json` |
| Ordinary-map noninterference | PASS | `artifacts/release/frontier-runtime-gaps-pass.json` |
| Vetoed eviction and veto persistence across save/reload | PASS | `artifacts/release/frontier-runtime-gaps-pass.json` |
| Safe eviction, map removal/deinitialization, and stale identity cleanup | PASS | `artifacts/release/frontier-runtime-gaps-pass.json`; `artifacts/release/live-clean-status-final.json` |
| Same-tile identity conflict fail-closed behavior | PASS | `../Frontier/artifacts/frontier-stable-identity-conflict-evidence.json`; fixture commit `27a9565f415fc6e36bb5d246d1326e7c1bb104c5` |
| Partial materialization `Prepare` failure and rollback after `Preparation` | PASS | `../Frontier/artifacts/frontier-stable-materialization-prepare-evidence.json`; fixture commit `27a9565f415fc6e36bb5d246d1326e7c1bb104c5` |
| Clean materialization retry without duplicate map/state | PASS | `../Frontier/artifacts/frontier-stable-materialization-prepare-evidence.json`; fixture commit `27a9565f415fc6e36bb5d246d1326e7c1bb104c5` |

## Identity-conflict evidence

The live fixture created two distinct Frontier maps (`4` and `5`) on tile
`59687`, with both maps claiming the same provider-region identity. Deferred
Reality rejected the competing registration with:

`A live map identity is already claimed by another map.`

The original projection remained authoritative and unrelated map IDs were
unchanged.

## Partial-Prepare evidence

The live fixture observed provider partial work before rollback and recorded a
non-empty provider state marker. The failure occurred after the
`Preparation` stage began; rollback ran once for `Preparation` and cleared the
provider state. The failure left the map census unchanged. A clean retry
materialized exactly one map and projection, with no duplicate map and clean
fixture cleanup.

## Compatibility and limitations

- Provider API remains `1`.
- `IRealityExcursionReturnGate` is optional and additive; providers that do not
  implement it retain default `Ready` behavior.
- Save/schema identifiers and provider-neutral record semantics remain unchanged.
- DRF preserves the current record shape and does not promise unlimited
  historical or pre-release save migration.
- Adjacent temporary excursion sites remain experimental, opt-in, and disabled
  by default; their provider gameplay and rollback remain provider-owned.
- This report claims only the supported cases listed above.

## Cleanup

The final live runtime was `READY` with no active test lease after fixture
cleanup. Frontier's provider evidence is retained in its repository; no
Frontier fixture material is part of the DRF package.
