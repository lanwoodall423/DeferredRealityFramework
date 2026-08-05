param(
    [string]$RimWorldRoot = $env:RIMWORLD_ROOT,
    [string]$HarmonyPath = $env:DEFERRED_REALITY_HARMONY_PATH,
    [switch]$RequireHarmony
)

$ErrorActionPreference = 'Stop'

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($RimWorldRoot)) {
        $relativeRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '..\..'))
        if (Test-Path -LiteralPath (Join-Path $relativeRoot 'RimWorldWin64_Data\Managed\Assembly-CSharp.dll')) {
            $RimWorldRoot = $relativeRoot
        }
    }
    if ([string]::IsNullOrWhiteSpace($RimWorldRoot) -or
        -not (Test-Path -LiteralPath (Join-Path $RimWorldRoot 'RimWorldWin64_Data\Managed\Assembly-CSharp.dll'))) {
        throw 'RimWorld 1.6 assemblies unavailable. Set RIMWORLD_ROOT or pass -RimWorldRoot.'
    }
    $RimWorldRoot = [IO.Path]::GetFullPath($RimWorldRoot)
    $managedPath = Join-Path $RimWorldRoot 'RimWorldWin64_Data\Managed'
    if (-not (Test-Path -LiteralPath (Join-Path $managedPath 'UnityEngine.CoreModule.dll'))) {
        throw "UnityEngine.CoreModule.dll is missing under $managedPath."
    }
    if ($RequireHarmony -and [string]::IsNullOrWhiteSpace($HarmonyPath)) {
        $steamApps = Split-Path -Parent (Split-Path -Parent $RimWorldRoot)
        $pattern = Join-Path $steamApps 'workshop\content\294100\*\Current\Assemblies\0Harmony.dll'
        $candidate = @(Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue |
            Sort-Object -Property FullName)[0]
        if ($candidate -ne $null) { $HarmonyPath = $candidate.FullName }
    }
    if ($RequireHarmony -and ([string]::IsNullOrWhiteSpace($HarmonyPath) -or
        -not (Test-Path -LiteralPath $HarmonyPath -PathType Leaf))) {
        throw '0Harmony.dll is unavailable. Set DEFERRED_REALITY_HARMONY_PATH or pass -HarmonyPath.'
    }
    [pscustomobject]@{
        rimWorldRoot = $RimWorldRoot
        managedPath = $managedPath
        harmonyPath = if ([string]::IsNullOrWhiteSpace($HarmonyPath)) { $null } else { [IO.Path]::GetFullPath($HarmonyPath) }
    } | ConvertTo-Json -Compress
    exit 0
}
catch {
    [Console]::Error.WriteLine("Deferred Reality setup error: $($_.Exception.Message)")
    exit 2
}
