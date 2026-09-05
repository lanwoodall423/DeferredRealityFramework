param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$packageDirectory = Join-Path $root '1.6\Assemblies'
$packagePath = Join-Path $packageDirectory 'DeferredRealityFramework.dll'
$releaseBuildPath = Join-Path $root 'Source\obj\Release\DeferredRealityFramework.dll'
$identitySource = Get-Content -LiteralPath (Join-Path $root 'Source\API\FrameworkIdentity.cs') -Raw
$expectedVersion = ([regex]::Match($identitySource, 'public const string Version = "([^"]+)"')).Groups[1].Value
$expectedAssemblyVersion = ([regex]::Match($identitySource, 'public const string AssemblyVersion = "([^"]+)"')).Groups[1].Value
$expectedInformationalVersion = $expectedVersion + '+release-candidate'
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
    $packageRoot = Join-Path $root '1.6'
    $packageDlls = @()
    if (Test-Path -LiteralPath $packageRoot -PathType Container) {
        $packageDlls = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.dll')
    }
    $packageMatches = @($packageDlls | Where-Object {
        [string]::Equals($_.FullName, $packagePath, [StringComparison]::OrdinalIgnoreCase)
    })
    $exists = Test-Path -LiteralPath $packagePath -PathType Leaf
    Add-Check 'framework-dll' ($exists -and $packageMatches.Count -eq 1) $(
        if ($exists -and $packageMatches.Count -eq 1) { $packagePath } else { 'missing or ambiguous framework Release output' }
    )
    Add-Check 'framework-dll-count' ($packageDlls.Count -eq 1 -and $packageMatches.Count -eq 1) (
        "expected one framework DLL under 1.6/Assemblies; found $($packageDlls.Count)"
    )
    if ($exists -and $packageMatches.Count -eq 1) {
        $badReferences = @($forbiddenReferences | Where-Object {
                Test-AssemblyReferenceToken $packagePath $_
            })
        Add-Check 'framework-provider-references' ($badReferences.Count -eq 0) $(if ($badReferences.Count -eq 0) { 'none' } else { $badReferences -join '; ' })
        $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($packagePath)
        Add-Check 'framework-identity' ($assemblyName.Name -eq 'DeferredRealityFramework') $assemblyName.Name
        Add-Check 'framework-assembly-version' ($assemblyName.Version.ToString() -eq $expectedAssemblyVersion) $assemblyName.Version.ToString()
        $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($packagePath)
        Add-Check 'framework-file-version' ($fileVersion.FileVersion -eq $expectedAssemblyVersion) $fileVersion.FileVersion
        Add-Check 'framework-informational-version' ($fileVersion.ProductVersion -eq $expectedInformationalVersion) $fileVersion.ProductVersion
    }
    $providerDlls = @($packageDlls | Where-Object {
        -not [string]::Equals($_.FullName, $packagePath, [StringComparison]::OrdinalIgnoreCase)
    })
    Add-Check 'package-provider-dlls' ($providerDlls.Count -eq 0) $(if ($providerDlls.Count -eq 0) { 'none' } else { ($providerDlls.Name -join '; ') })

    $buildExists = Test-Path -LiteralPath $releaseBuildPath -PathType Leaf
    $matches = $false
    if ($buildExists -and $exists -and $packageMatches.Count -eq 1) {
        $matches = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -eq
            (Get-FileHash -LiteralPath $releaseBuildPath -Algorithm SHA256).Hash
    }
    Add-Check 'release-build-match' ($buildExists -and $matches) $(if ($matches) { $releaseBuildPath } else { 'direct Source/obj/Release build counterpart unavailable or differs' })

    $status = if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
    [pscustomobject]@{ status = $status; checks = $checks.ToArray(); failures = $failures.ToArray() } | ConvertTo-Json -Depth 5
    if ($failures.Count -gt 0) { exit 1 }
    exit 0
}
catch {
    [pscustomobject]@{ status = 'FAIL'; checks = $checks.ToArray(); failures = @($_.Exception.Message) } | ConvertTo-Json -Depth 5
    exit 1
}
