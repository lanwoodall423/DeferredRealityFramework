$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\Tests\DeferredReality.PureTests.csproj'
dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project $project -c Release --no-build
exit $LASTEXITCODE
