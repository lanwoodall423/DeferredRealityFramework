# Deferred Reality Framework Release Candidate

Recommended tag: `deferred-reality-framework-v0.1.0-rc.1`

This candidate keeps the framework provider-neutral. The default Release
workflow builds only `DeferredRealityFramework.dll` and the pure-test project.
Provider adapters are built and released by their consuming repositories.

## Included

- Deterministic regional simulation, scheduler gating, retention, and replay-safe
  exactly-once cursor APIs.
- Transaction-scoped adjacent map creation, exact-Pawn excursion recovery, and
  provider-owned compression/factory contracts.
- Framework-only package and assembly-boundary audits.
- Legal clean-checkout integrity checks that do not require proprietary RimWorld
  assemblies.

## Release Checklist

Run `DevTools/Check-RepositoryIntegrity.ps1`, `DevTools/Build-All.ps1`,
`DevTools/Run-PureTests.ps1`, and `DevTools/Audit-Outputs.ps1`. Build optional
provider adapters from their own repositories. Run the manual checklist in
`MANUAL_ACCEPTANCE_REPORT.md` inside RimWorld before enabling adjacent regions.

Adjacent regions remain experimental, disabled by default, and are not
production-ready until the live checklist passes.
