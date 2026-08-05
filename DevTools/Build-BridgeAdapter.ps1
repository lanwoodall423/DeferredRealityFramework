param(
    [string]$Destination = (Join-Path $PSScriptRoot 'BridgeAdapters'),
    [string]$PublisherPath = (Join-Path $PSScriptRoot '..\..\RimWorldDevBridge\DevTools\Publish-RimWorldBridgeAdapter.ps1'),
    [string]$Generation = 'typed-v3'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'BridgeAdapter\DeferredRealityFramework.BridgeAdapter.csproj'
$build = Join-Path $PSScriptRoot 'BridgeAdapter\Build'
$destination = [IO.Path]::GetFullPath($Destination)
$publisher = [IO.Path]::GetFullPath($PublisherPath)
$assemblyName = "DeferredRealityFramework.BridgeAdapter.$Generation"
New-Item -ItemType Directory -Force -Path $build | Out-Null
dotnet build $project -c Release "-p:AssemblyName=$assemblyName" "-p:OutputPath=$build"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$assembly = Join-Path $build ($assemblyName + '.dll')
if (-not (Test-Path -LiteralPath $publisher -PathType Leaf)) { throw "Bridge adapter publisher not found: $publisher" }
& $publisher -AssemblyPath $assembly -Destination $destination -AdapterId 'lan.deferredreality.framework' -DisplayName 'Deferred Reality Framework' -Version '1' -Generation $Generation -ProviderType 'DeferredReality.BridgeAdapter.DeferredRealityBridgeAdapterProvider' -ExecutionContract 'cooperative-v1' -CommandSpecs @(
  'DEFERRED_REALITY|R|Compact framework status and cached snapshot',
  'DR_REGIONS|R|List stable regions, fidelity, active-map links, and observation levels',
  'DR_PROCESSES|R|List scheduled processes and next due ticks',
  'DR_DUMP|R|Dump a concise deterministic latent snapshot',
  'DR_AUDIT|R|Run a read-only stable-ID and reference audit',
  'DR_COMPRESSION_DRY_RUN|R|Collect conservative compression vetoes for the current map',
  'DR_SIMULATE_DAY|W|Run one bounded day of analytical processes'
 ) -RequiredPackageIds @('lan.deferredreality.framework') -NoMapCommands @('DEFERRED_REALITY','DR_REGIONS','DR_PROCESSES','DR_DUMP','DR_AUDIT') -SimulationCommands @('DR_SIMULATE_DAY') -TemporaryCommands @('DR_SIMULATE_DAY')
exit $LASTEXITCODE
