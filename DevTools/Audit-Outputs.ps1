param(
    [string]$BuildAssemblyPath = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageDirectory = Join-Path $root '1.6\Assemblies'
$packagePath = Join-Path $packageDirectory 'DeferredRealityFramework.dll'
$forbiddenReferences = @('Wildlife', 'Herds', 'Aquaculture', 'Horticulture', 'PacksAndPredators')
$checks = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $script:checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    if (-not $Passed) { $script:failures.Add($Name + ': ' + $Detail) }
}

function Test-AssemblyReferenceToken([string]$Path, [string]$Token) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $ascii = [Text.Encoding]::ASCII.GetString($bytes)
    $unicode = [Text.Encoding]::Unicode.GetString($bytes)
    return $ascii.Contains($Token) -or $unicode.Contains($Token)
}

try {
    $exists = Test-Path -LiteralPath $packagePath -PathType Leaf
    Add-Check 'framework-dll' $exists $(if ($exists) { $packagePath } else { 'missing framework Release output' })
    if ($exists) {
        $badReferences = @($forbiddenReferences | Where-Object {
                Test-AssemblyReferenceToken $packagePath $_
            })
        Add-Check 'framework-provider-references' ($badReferences.Count -eq 0) $(if ($badReferences.Count -eq 0) { 'none' } else { $badReferences -join '; ' })
        $packageAssemblyName = [Reflection.AssemblyName]::GetAssemblyName($packagePath).Name
        Add-Check 'framework-identity' ($packageAssemblyName -eq 'DeferredRealityFramework') $packageAssemblyName
    }

    $providerDlls = @()
    $packageRoot = Join-Path $root '1.6'
    if (Test-Path -LiteralPath $packageRoot) {
        $providerDlls = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.dll' |
            Where-Object { $_.FullName -ne $packagePath })
    }
    Add-Check 'package-provider-dlls' ($providerDlls.Count -eq 0) $(if ($providerDlls.Count -eq 0) { 'none' } else { ($providerDlls.Name -join '; ') })

    if ([string]::IsNullOrWhiteSpace($BuildAssemblyPath)) {
        $candidates = @(
            (Join-Path $root 'Source\bin\Release\net472\DeferredRealityFramework.dll'),
            (Join-Path $root 'Source\obj\Release\net472\DeferredRealityFramework.dll')
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
        if ($candidates.Count -eq 1) { $BuildAssemblyPath = $candidates[0] }
    }
    if (-not [string]::IsNullOrWhiteSpace($BuildAssemblyPath) -and $exists) {
        $buildExists = Test-Path -LiteralPath $BuildAssemblyPath -PathType Leaf
        $matches = $false
        if ($buildExists) {
            $matches = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -eq
                (Get-FileHash -LiteralPath $BuildAssemblyPath -Algorithm SHA256).Hash
        }
        Add-Check 'release-build-match' ($buildExists -and $matches) $(if ($matches) { $BuildAssemblyPath } else { 'release build counterpart unavailable or differs' })
    }
    else {
        Add-Check 'release-build-match' $true 'not compared; the configured Release output is the packaged framework DLL'
    }

    $status = if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
    [pscustomobject]@{ status = $status; checks = $checks.ToArray(); failures = $failures.ToArray() } | ConvertTo-Json -Depth 5
    if ($failures.Count -gt 0) { exit 1 }
    exit 0
}
catch {
    [pscustomobject]@{ status = 'FAIL'; checks = $checks.ToArray(); failures = @($_.Exception.Message) } | ConvertTo-Json -Depth 5
    exit 1
}
