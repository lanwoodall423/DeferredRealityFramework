# Manual Acceptance Report

Status: `RELEASE CANDIDATE - LIVE EXECUTION REQUIRED`

This report intentionally records no live RimWorld result unless a game log or
direct observation is attached. The current repository validation had no live
RimWorld execution.

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

## Automated Evidence

- `DevTools/Check-RepositoryIntegrity.ps1`: structured PASS.
- `DevTools/Build-All.ps1`: framework and pure tests built; pure tests passed.
- `DevTools/Audit-Outputs.ps1`: structured PASS.
- Provider adapter builds and provider-specific runtime tests remain the
  responsibility of consuming repositories.

Adjacent regions must remain disabled until every manual row has attached live
evidence.
