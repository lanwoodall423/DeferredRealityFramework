# Manual Acceptance Report

Status: `NOT ACCEPTED — LIVE EXECUTION REQUIRED`

This report records no manual acceptance pass. It records no live RimWorld
result unless a game log or direct observation is attached. The current
repository validation had no live RimWorld execution.

| Check | Repository | Mode | Evidence | Result | Remaining action |
|---|---|---|---|---|---|
| Save/reload while excursion is outbound | DRF + provider | Manual | No live log | UNEXECUTED | Run with a provider adapter |
| Save/reload during return | DRF + provider | Manual | No live log | UNEXECUTED | Run during inverse transfer |
| Provider heartbeat, completion, abandonment after reload | Provider | Manual | No live log | UNEXECUTED | Exercise provider callbacks |
| Forced `Pawn.ExitMap` failure/no-op with zero aggregate drift | Provider | Manual | No live log | UNEXECUTED | Force exception and verify populations/anchors |
| Successful departure and exactly-one aggregate transfer | Provider | Manual | No live log | UNEXECUTED | Compare before/after population totals |
| Missing origin and provider removal/re-registration | DRF + provider | Manual | No live log | UNEXECUTED | Remove provider and restore it in-game |
| Exact Pawn identity and inventory/equipment/apparel/health/relations/needs | DRF + provider | Manual | No live log | UNEXECUTED | Compare the same Pawn object before/after |
| Duplicate monitor ticks and duplicate operation replay | DRF + provider | Manual | No live log | UNEXECUTED | Repeat monitor and operation requests |
| Construction, blueprint, frame, floor, and reinstall blocking | DRF | Manual | No live log | UNEXECUTED | Test every listed construction path |
| Ordinary-map behavior remains unaffected | DRF | Manual | No live log | UNEXECUTED | Repeat construction tests on a normal map |
| Safe eviction and vetoed eviction | DRF + provider | Manual | No live log | UNEXECUTED | Verify real deinitialization and veto retention |
| Same-tile identity conflicts and partial-Prepare rollback | DRF + provider | Debug/manual | Pure seams only | UNEXECUTED | Run live map and provider failure cases |

## Stable Runtime Qualification

Status: `INFRASTRUCTURE BLOCKED` for the current release-candidate gate.

The current gate did not create a runtime workflow. Its owner-managed readiness
check returned:

- `rimliaison doctor --json`: `status=blocked`
- `code=PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING`
- `nextAction=Repair or reinstall the unified promoted production package`
- `evaluationStatus=NOT_EVALUATED`

The checked-in build artifact is not release-candidate evidence:

- Version: assembly `0.1.0.0`, file `0.1.0.0`, informational `0.1.0`
- Required replacement: rebuild from the release-candidate source identity

No current startup, save/reload, or gameplay evidence is claimed. No product
failure is inferred from the owner-tool infrastructure block.

## Automated Evidence

- `DevTools/Check-RepositoryIntegrity.ps1`: structured PASS.
- `DevTools/Build-All.ps1`: BLOCKED; RimWorld 1.6 assemblies are unavailable.
- `DevTools/Run-PureTests.ps1`: BLOCKED by the same missing dependencies.
- `DevTools/Audit-Outputs.ps1`: FAIL; the checked-in assembly still reports
  informational version `0.1.0` and must be rebuilt.
- Provider adapter builds and provider-specific runtime tests remain the
  responsibility of consuming repositories.

## Latest Runtime Attempt

- `Wildlife\DevTools\Run-WildlifeTests.ps1 -TimeoutSeconds 60` returned
  `summary=SERVER_TIMEOUT`; no `READY`/`DONE` status or test report was produced.
  The spawned `RimWorldWin64` process was stopped after the timeout.
- `RimWorldDevBridge\DevTools\devbridge.ps1 discover` returned
  `{"available":false,"reason":"bridge_not_active"}`.
- `Player.log` reached RimWorld 1.6.4871 assembly loading, Prepatcher completion,
  and mod loading. No Deferred Reality undefined-target exception appeared in
  the captured log, but the test server never became ready, so this is not a
  startup or gameplay pass.

Adjacent regions must remain disabled until every manual row has attached live
evidence.
