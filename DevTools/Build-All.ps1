param(
    [string]$RimWorldRoot = $env:RIMWORLD_ROOT,
    [string]$HarmonyPath = $env:DEFERRED_REALITY_HARMONY_PATH
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$resolver = Join-Path $PSScriptRoot 'Resolve-RimWorldDependencies.ps1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet SDK was not found on PATH.'
    exit 2
}

$dependencyJson = & $resolver -RimWorldRoot $RimWorldRoot -HarmonyPath $HarmonyPath -RequireHarmony
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$dependencies = $dependencyJson | ConvertFrom-Json
$buildProperties = @(
    "-p:DeferredRealityRimWorldRoot=$($dependencies.rimWorldRoot)",
    "-p:DeferredRealityHarmonyPath=$($dependencies.harmonyPath)"
)
$projects = @(
    (Join-Path $root 'Source\DeferredRealityFramework.csproj'),
    (Join-Path $root 'Tests\DeferredReality.PureTests.csproj')
)

function Invoke-Dotnet([string[]]$Arguments, [string]$Description) {
    Write-Host ("==> " + $Description)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Error ("$Description failed with exit code $LASTEXITCODE")
        exit $LASTEXITCODE
    }
}

foreach ($project in $projects) {
    Invoke-Dotnet (@('build', $project, '-c', 'Release') + $buildProperties) ("Build " + $project)
}
Invoke-Dotnet @('run', '--project', (Join-Path $root 'Tests\DeferredReality.PureTests.csproj'),
    '-c', 'Release', '--no-build') 'Run pure tests'
exit 0
