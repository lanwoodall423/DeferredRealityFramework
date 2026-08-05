$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
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
    $required = @('.gitignore', 'CONTRIBUTING.md', 'ARCHITECTURE.md', 'COMPATIBILITY.md',
        'PROVIDER_GUIDE.md', 'TEST_PLAN.md', 'SAVE_FORMAT.md', 'RELEASE_NOTES.md',
        'MANUAL_ACCEPTANCE_REPORT.md', '.github\workflows\integrity.yml',
        'Source\DeferredRealityFramework.csproj', 'Tests\DeferredReality.PureTests.csproj',
        'DevTools\Build-All.ps1', 'DevTools\Audit-Outputs.ps1',
        'DevTools\RimWorldReferences.props', 'DevTools\Resolve-RimWorldDependencies.ps1',
        'DevTools\Check-RepositoryIntegrity.ps1')
    foreach ($path in $required) {
        Add-Check "file:$path" (Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf) 'required file exists'
    }

    $xmlFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.xml' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    foreach ($file in $xmlFiles) {
        try { [xml](Get-Content -LiteralPath $file.FullName -Raw) | Out-Null; Add-Check "xml:$($file.Name)" $true 'well-formed' }
        catch { Add-Check "xml:$($file.Name)" $false $_.Exception.Message }
    }

    $scriptFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'DevTools') -File -Filter '*.ps1')
    foreach ($file in $scriptFiles) {
        $tokens = $null; $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
        Add-Check "powershell:$($file.Name)" ($parseErrors.Count -eq 0) $(if ($parseErrors.Count -eq 0) { 'parseable' } else { $parseErrors[0].Message })
    }

    $coreProject = Get-Content -LiteralPath (Join-Path $root 'Source\DeferredRealityFramework.csproj') -Raw
    Add-Check 'core-project-boundary' ($coreProject -match 'Adapters\\\*\*' -and $coreProject -notmatch 'Aquaculture|Horticulture|Wildlife|Herds') 'provider source is excluded from the core project'
    $buildScript = Get-Content -LiteralPath (Join-Path $root 'DevTools\Build-All.ps1') -Raw
    Add-Check 'default-build-boundary' ($buildScript -notmatch 'Aquaculture|Horticulture|Wildlife|Herds|Source\\Adapters') 'default build has no provider project dependency'
    $auditScript = Get-Content -LiteralPath (Join-Path $root 'DevTools\Audit-Outputs.ps1') -Raw
    $auditDriveText = [string][char]58 + [char]92
    $modsText = [string][char]47 + 'Mods' + [char]47
    Add-Check 'audit-boundary' (-not $auditScript.Contains($modsText) -and -not $auditScript.Contains($auditDriveText)) 'default audit has no provider or machine-specific path'

    $tracked = @(git -C $root ls-files)
    $trackedIntermediates = @($tracked | Where-Object { $_ -match '(^|[\\/])(bin|obj)([\\/]|$)' })
    Add-Check 'tracked-intermediates' ($trackedIntermediates.Count -eq 0) $(if ($trackedIntermediates.Count -eq 0) { 'none' } else { $trackedIntermediates -join '; ' })
    $drivePrefix = [string][char]58
    $absolutePattern = [regex]::Escape($drivePrefix + [char]92) + '|' +
        [regex]::Escape(([string][char]47 + 'Users' + [char]47)) + '|' +
        [regex]::Escape(([string][char]47 + 'home' + [char]47))
    $absolutePaths = @($tracked | Where-Object { $_ -notmatch '(^|[\\/])1\.6([\\/])' } | ForEach-Object {
        $relative = $_
        $full = Join-Path $root $_
        if ((Test-Path -LiteralPath $full -PathType Leaf) -and
            (Select-String -LiteralPath $full -Pattern $absolutePattern -SimpleMatch:$false -Quiet)) { $relative }
    })
    Add-Check 'tracked-absolute-paths' ($absolutePaths.Count -eq 0) $(if ($absolutePaths.Count -eq 0) { 'none' } else { 'absolute path found' })

    $packageDir = Join-Path $root '1.6\Assemblies'
    $packageRoot = Join-Path $root '1.6'
    $packageDlls = if (Test-Path -LiteralPath $packageRoot) {
        @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.dll')
    } else { @() }
    $unexpected = @($packageDlls | Where-Object { $_.FullName -ne (Join-Path $packageDir 'DeferredRealityFramework.dll') })
    Add-Check 'package-provider-boundary' ($unexpected.Count -eq 0) $(if ($unexpected.Count -eq 0) { 'no provider DLL packaged' } else { $unexpected.Name -join '; ' })

    $frameworkDll = Join-Path $packageDir 'DeferredRealityFramework.dll'
    if (Test-Path -LiteralPath $frameworkDll -PathType Leaf) {
        $forbidden = @('Wildlife', 'Herds', 'Aquaculture', 'Horticulture', 'PacksAndPredators')
        $badReferences = @($forbidden | Where-Object {
                Test-AssemblyReferenceToken $frameworkDll $_
            })
        Add-Check 'framework-assembly-boundary' ($badReferences.Count -eq 0) $(if ($badReferences.Count -eq 0) { 'no provider references' } else { $badReferences -join '; ' })
    }
    else {
        Add-Check 'framework-assembly-boundary' $true 'not built; compile requires local RimWorld dependencies'
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
