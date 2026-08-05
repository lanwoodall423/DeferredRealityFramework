$ErrorActionPreference = 'Stop'
$resolver = Join-Path $PSScriptRoot 'Resolve-RimWorldDependencies.ps1'
$dependencyJson = & $resolver -RequireHarmony
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$dependencies = $dependencyJson | ConvertFrom-Json
$env:RIMWORLD_ROOT = $dependencies.rimWorldRoot
$env:DEFERRED_REALITY_HARMONY_PATH = $dependencies.harmonyPath
$buildProperties = @(
    "-p:DeferredRealityRimWorldRoot=$($dependencies.rimWorldRoot)",
    "-p:DeferredRealityHarmonyPath=$($dependencies.harmonyPath)"
)
$project = Join-Path $PSScriptRoot '..\Tests\DeferredReality.PureTests.csproj'
dotnet build $project -c Release @buildProperties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project $project -c Release --no-build
exit $LASTEXITCODE
