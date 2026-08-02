$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'BridgeAdapter\DeferredRealityFramework.BridgeAdapter.csproj'
dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$assembly = Join-Path $PSScriptRoot 'BridgeAdapter\bin\Release\DeferredRealityFramework.BridgeAdapter.dll'
$publish = Join-Path $PSScriptRoot '..\..\RimWorldDevBridge\DevTools\Publish-RimWorldBridgeAdapter.ps1'
& $publish -AssemblyPath $assembly -AdapterId 'lan.deferredreality.framework' -DisplayName 'Deferred Reality Framework' -Version '1' -Generation 'typed-v3' -ProviderType 'DeferredReality.BridgeAdapter.DeferredRealityBridgeAdapterProvider' -CommandSpecs @(
  'DEFERRED_REALITY|R|Compact framework status and cached snapshot',
  'DR_REGIONS|R|List stable regions, fidelity, active-map links, and observation levels',
  'DR_PROCESSES|R|List scheduled processes and next due ticks',
  'DR_DUMP|R|Dump a concise deterministic latent snapshot',
  'DR_AUDIT|R|Run a read-only stable-ID and reference audit',
  'DR_COMPRESSION_DRY_RUN|R|Collect conservative compression vetoes for the current map',
  'DR_SIMULATE_DAY|W|Run one bounded day of analytical processes'
 ) -RequiredPackageIds @('lan.deferredreality.framework') -NoMapCommands @('DEFERRED_REALITY','DR_REGIONS','DR_PROCESSES','DR_DUMP','DR_AUDIT') -SimulationCommands @('DR_SIMULATE_DAY') -TemporaryCommands @('DR_SIMULATE_DAY')
exit $LASTEXITCODE
