param(
    [string]$Version = '0.1.0-rc.1'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$stages = New-Object System.Collections.Generic.List[object]

function Add-Stage(
    [string]$Name,
    [bool]$Passed,
    [string]$Detail,
    [string]$Status = 'PASS',
    [string]$Classification = 'NONE',
    [object]$Evidence = @()
) {
    $script:stages.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        status = $Status
        classification = $Classification
        detail = $Detail
        evidence = $Evidence
    })
}

function ConvertTo-OutputLines([object[]]$Output) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($item in @($Output)) {
        if ($null -eq $item) { continue }
        if ($item -is [System.Management.Automation.ErrorRecord]) {
            $lines.Add($item.ToString())
        }
        else {
            $lines.Add([string]$item)
        }
    }
    return @($lines.ToArray())
}

function Get-ConciseDiagnostics([string[]]$Lines) {
    $nonEmpty = @($Lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($nonEmpty.Count -le 6) { return ($nonEmpty -join ' | ') }
    return (($nonEmpty | Select-Object -Last 6) -join ' | ')
}

function ConvertTo-StructuredResult([string[]]$Lines) {
    $text = ($Lines -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }

    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add($text)
    $start = $text.IndexOf('{')
    $end = $text.LastIndexOf('}')
    if ($start -ge 0 -and $end -gt $start) {
        $candidates.Add($text.Substring($start, $end - $start + 1))
    }

    foreach ($candidate in $candidates) {
        try {
            $parsed = ConvertFrom-Json -InputObject $candidate -ErrorAction Stop
            if ($null -ne $parsed -and $null -ne $parsed.status) { return $parsed }
        }
        catch {
            # A child may have emitted a concise diagnostic before its JSON result.
        }
    }
    return $null
}

function Get-StructuredEvidence($Result, [string[]]$Lines, [int]$ExitCode) {
    $evidence = [ordered]@{}
    foreach ($property in @('code', 'nextAction', 'evaluationStatus', 'failures')) {
        if ($null -ne $Result -and $null -ne $Result.PSObject.Properties[$property]) {
            $value = $Result.$property
            if ($null -ne $value -and ((@($value)).Count -gt 0)) {
                $evidence[$property] = $value
            }
        }
    }
    $diagnostics = Get-ConciseDiagnostics $Lines
    if (-not [string]::IsNullOrWhiteSpace($diagnostics)) {
        $evidence['output'] = $diagnostics
    }
    $evidence['status'] = if ($null -ne $Result) { [string]$Result.status } else { $null }
    $evidence['exitCode'] = $ExitCode
    return [pscustomobject]$evidence
}

function Get-FailureClassification([string]$Name, [string]$Status, [int]$ExitCode, [string]$Detail) {
    if ($Status -eq 'BLOCKED') { return 'INFRASTRUCTURE_BLOCKED' }
    if ($Name -eq 'RimTest doctor/readiness') { return 'INFRASTRUCTURE' }
    if ($Detail -match '(?i)not found|missing|unavailable|toolchain|dependency|readiness|infrastructure') {
        return 'INFRASTRUCTURE'
    }
    return 'MOD_OR_TEST_FAILURE'
}

function Invoke-Stage(
    [string]$Name,
    [string]$Path,
    [string[]]$Arguments = @(),
    [switch]$JsonOutput
) {
    $captured = @()
    $exitCode = 1
    try {
        $captured = @(& $Path @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    catch {
        $detail = $_.Exception.Message
        $classification = Get-FailureClassification $Name 'FAIL' $exitCode $detail
        Add-Stage $Name $false $detail 'FAIL' $classification ([pscustomobject]@{ output = $detail })
        throw
    }
    $lines = ConvertTo-OutputLines $captured

    if ($JsonOutput) {
        $result = ConvertTo-StructuredResult $lines
        if ($null -eq $result) {
            $detail = if ($lines.Count -gt 0) { Get-ConciseDiagnostics $lines } else { 'no structured result was emitted' }
            $classification = Get-FailureClassification $Name 'FAIL' $exitCode $detail
            Add-Stage $Name $false ("structured result unavailable (exit code " + $exitCode + "): " + $detail) 'FAIL' $classification ([pscustomobject]@{ output = $detail })
            throw "$Name failed: structured result unavailable."
        }

        $status = ([string]$result.status).ToUpperInvariant()
        $evidence = Get-StructuredEvidence $result $lines $exitCode
        if ($status -eq 'BLOCKED') {
            $detail = if ($null -ne $evidence.PSObject.Properties['output']) { [string]$evidence.output } else { 'child reported BLOCKED' }
            Add-Stage $Name $false $detail 'BLOCKED' 'INFRASTRUCTURE_BLOCKED' $evidence
            throw "$Name is blocked."
        }
        if ($status -notin @('PASS', 'READY', 'OK', 'SUCCESS') -or $exitCode -ne 0) {
            $detail = if ($null -ne $evidence.PSObject.Properties['output']) { [string]$evidence.output } else { 'child reported ' + $status }
            $classification = Get-FailureClassification $Name $status $exitCode $detail
            Add-Stage $Name $false $detail 'FAIL' $classification $evidence
            throw "$Name failed with status $status and exit code $exitCode."
        }

        Add-Stage $Name $true ('completed (' + $status + ')') 'PASS' 'NONE' $evidence
        return
    }

    if ($exitCode -ne 0) {
        $detail = if ($lines.Count -gt 0) { Get-ConciseDiagnostics $lines } else { 'no diagnostic output' }
        $classification = Get-FailureClassification $Name 'FAIL' $exitCode $detail
        Add-Stage $Name $false ("exit code " + $exitCode + ': ' + $detail) 'FAIL' $classification ([pscustomobject]@{ output = $detail })
        throw "$Name failed with exit code $exitCode."
    }
    $evidence = [ordered]@{ exitCode = $exitCode }
    $diagnostics = Get-ConciseDiagnostics $lines
    if (-not [string]::IsNullOrWhiteSpace($diagnostics)) {
        $evidence['output'] = $diagnostics
    }
    Add-Stage $Name $true 'completed' 'PASS' 'NONE' ([pscustomobject]$evidence)
}

function Resolve-RimTest {
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:RIMLIAISON_COMMAND)) { $candidates.Add($env:RIMLIAISON_COMMAND) }
    if (-not [string]::IsNullOrWhiteSpace($env:RIMTEST_COMMAND)) { $candidates.Add($env:RIMTEST_COMMAND) }

    $rimDevRoot = $env:RIMDEV_ROOT
    if ([string]::IsNullOrWhiteSpace($rimDevRoot)) {
        $rimDevRoot = Split-Path -Parent (Split-Path -Parent $root)
    }
    $productionManifestPath = Join-Path $rimDevRoot '.rimdev\production-toolchain.json'
    if (Test-Path -LiteralPath $productionManifestPath -PathType Leaf) {
        try {
            $productionManifest = Get-Content -LiteralPath $productionManifestPath -Raw | ConvertFrom-Json
            if (-not [string]::IsNullOrWhiteSpace($productionManifest.rimLiaisonExecutablePath)) {
                $candidates.Add($productionManifest.rimLiaisonExecutablePath)
            }
        }
        catch {
            # Doctor remains responsible for reporting malformed toolchain state.
        }
    }

    foreach ($commandName in @('rimliaison', 'rimtest')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue
        if ($null -ne $command) { $candidates.Add($command.Source) }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:RIMLIAISON_ROOT)) {
        $candidates.Add((Join-Path $env:RIMLIAISON_ROOT 'rimliaison.exe'))
        $candidates.Add((Join-Path $env:RIMLIAISON_ROOT 'rimliaison.cmd'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:RIMTEST_ROOT)) {
        $candidates.Add((Join-Path $env:RIMTEST_ROOT 'rimtest.cmd'))
    }
    $candidates.Add((Join-Path (Split-Path -Parent $root) 'RimTest\rimtest.cmd'))
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'RimLiaison/RimTest launcher was not found on PATH or in the local RimDev checkout.'
}

$failure = $null
try {
    try {
        $rimTest = Resolve-RimTest
    }
    catch {
        $failure = $_.Exception.Message
        Add-Stage 'RimTest doctor/readiness' $false $failure 'BLOCKED' 'INFRASTRUCTURE_BLOCKED' ([pscustomobject]@{ output = $failure })
        throw
    }

    Invoke-Stage 'RimTest doctor/readiness' $rimTest @('doctor', '--json') -JsonOutput
    Invoke-Stage 'repository integrity' (Join-Path $PSScriptRoot 'Check-RepositoryIntegrity.ps1') -JsonOutput
    Invoke-Stage 'release build and pure tests' (Join-Path $PSScriptRoot 'Build-All.ps1')
    Invoke-Stage 'output audit' (Join-Path $PSScriptRoot 'Audit-Outputs.ps1') -JsonOutput
    Invoke-Stage 'canonical RimTest affected/runtime validation' $rimTest @('affected', '--run', '--json') -JsonOutput
    Invoke-Stage 'release package validation' (Join-Path $PSScriptRoot 'New-ReleasePackage.ps1') -Arguments @('-Version', $Version) -JsonOutput

    [pscustomobject]@{
        status = 'PASS'
        version = $Version
        classification = 'NONE'
        stages = $stages.ToArray()
        packageCommand = 'DevTools/New-ReleasePackage.ps1'
        runtimeCommand = 'rimtest affected --run --json'
    } | ConvertTo-Json -Depth 8
    exit 0
}
catch {
    if ([string]::IsNullOrWhiteSpace($failure)) { $failure = $_.Exception.Message }
    $failedStage = @($stages | Where-Object { -not $_.passed } | Select-Object -Last 1)
    $classification = if ($failedStage.Count -gt 0) { $failedStage[0].classification } else { 'INFRASTRUCTURE' }
    [pscustomobject]@{
        status = 'FAIL'
        version = $Version
        classification = $classification
        stages = $stages.ToArray()
        failedStage = if ($failedStage.Count -gt 0) { $failedStage[0] } else { $null }
        failures = @($failure)
    } | ConvertTo-Json -Depth 8
    exit 1
}

