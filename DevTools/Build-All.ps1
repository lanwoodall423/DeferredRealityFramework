$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = @(
    (Join-Path $root 'Source\DeferredRealityFramework.csproj'),
    (Join-Path $root 'Source\Adapters\Wildlife\DeferredReality.Wildlife.csproj'),
    (Join-Path $root 'Source\Adapters\Aquaculture\DeferredReality.Aquaculture.csproj'),
    (Join-Path $root 'Source\Adapters\Horticulture\DeferredReality.Horticulture.csproj'),
    (Join-Path $root 'Tests\DeferredReality.PureTests.csproj')
)
foreach ($project in $projects) {
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
dotnet run --project (Join-Path $root 'Tests\DeferredReality.PureTests.csproj') -c Release --no-build
exit $LASTEXITCODE
