$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expected = @(
    (Join-Path $root '1.6\Assemblies\DeferredRealityFramework.dll'),
    'C:\Games\Steam\steamapps\common\RimWorld\Mods\AquacultureFishing\1.6\Assemblies\DeferredReality.Aquaculture.dll',
    'C:\Games\Steam\steamapps\common\RimWorld\Mods\Horticulture - Novel Seeds\1.6\Assemblies\DeferredReality.Horticulture.dll'
)
$missing = @($expected | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Error "Missing output: $_" }
    exit 1
}
Write-Output 'Deferred Reality outputs present.'
exit 0
