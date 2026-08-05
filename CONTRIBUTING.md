# Contributing and Releases

DRF changes must remain provider-neutral. Provider integrations belong in their
consuming mod repositories and are not part of the default framework build or
package.

## Required checks

- Run `DevTools/Check-RepositoryIntegrity.ps1` on every change; it is the
  legal clean-checkout CI check and does not require RimWorld binaries.
- On a configured development machine, run `DevTools/Build-All.ps1`,
  `DevTools/Audit-Outputs.ps1`, and `DevTools/Run-PureTests.ps1`.
- Build and verify bridge adapters separately with the BridgeAdapter scripts.
- Live RimWorld/Scribe acceptance tests require the configured local game and
  are not represented by a successful CI integrity run.

## Release preparation

Build the core framework in Release, run the pure tests, audit the framework
assembly and package contents, and inspect the generated package before tagging.
Provider adapters must be built and released by their owning repositories.
Use descriptive commits that state the behavioral boundary being changed.
