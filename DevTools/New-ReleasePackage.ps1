param(
    [string]$Version = '0.1.0-rc.1',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root 'artifacts\release'
}

$identitySourcePath = Join-Path $root 'Source\API\FrameworkIdentity.cs'
$identitySource = Get-Content -LiteralPath $identitySourcePath -Raw
$expectedVersion = ([regex]::Match($identitySource, 'public const string Version = "([^"]+)"')).Groups[1].Value
$expectedAssemblyVersion = ([regex]::Match($identitySource, 'public const string AssemblyVersion = "([^"]+)"')).Groups[1].Value
$requiredRootFiles = @(
    'LoadFolders.xml'
    'LICENSE'
    'README.md'
    'ARCHITECTURE.md'
    'SAVE_FORMAT.md'
    'PROVIDER_GUIDE.md'
    'COMPATIBILITY.md'
    'RELEASE_NOTES.md'
)
$expectedRuntimeFiles = @(
    '1.6/Assemblies/DeferredRealityFramework.dll'
    '1.6/Assemblies/DeferredRealityFramework.xml'
    '1.6/Languages/English/Keyed/DeferredRealityFramework.xml'
)
$expectedRuntimeDirectories = @(
    '1.6/Assemblies'
    '1.6/Languages'
    '1.6/Languages/English'
    '1.6/Languages/English/Keyed'
)
$expectedPackageFiles = @(
    $requiredRootFiles + @('About/About.xml') + $expectedRuntimeFiles
) | Sort-Object
$expectedPackageDirectories = @(
    'About'
    '1.6'
    $expectedRuntimeDirectories
) | Sort-Object
$packageName = "DeferredRealityFramework-$Version"
$topLevelDirectory = 'DeferredRealityFramework'
$stagingPath = Join-Path $OutputRoot $topLevelDirectory
$zipPath = Join-Path $OutputRoot ($packageName + '.zip')
$checksumPath = Join-Path $OutputRoot 'SHA256SUMS.txt'
$runtimeRoot = Join-Path $root '1.6'
$assemblyPath = Join-Path $runtimeRoot 'Assemblies\DeferredRealityFramework.dll'
$checks = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $script:checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    if (-not $Passed) { $script:failures.Add($Name + ': ' + $Detail) }
}

function Get-RelativePath([string]$Path) {
    return $Path.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
}

function Get-StagingRelativePath([string]$Path) {
    return $Path.Substring($stagingPath.Length).TrimStart('\', '/').Replace('\', '/')
}

function Remove-ReleaseArtifacts {
    foreach ($path in @($stagingPath, $zipPath, $checksumPath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-ExpectedSourceFiles {
    $files = New-Object System.Collections.Generic.List[object]
    foreach ($relative in $expectedPackageFiles) {
        $path = Join-Path $root ($relative.Replace('/', '\'))
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $files.Add((Get-Item -LiteralPath $path))
        }
    }
    return @($files | Sort-Object -Property FullName)
}

function Write-Result([string]$Status) {
    [pscustomobject]@{
        status = $Status
        version = $Version
        checks = $checks.ToArray()
        failures = $failures.ToArray()
        stagingPath = if (Test-Path -LiteralPath $stagingPath) { $stagingPath } else { $null }
        packagePath = if (Test-Path -LiteralPath $zipPath) { $zipPath } else { $null }
        checksumPath = if (Test-Path -LiteralPath $checksumPath) { $checksumPath } else { $null }
        sha256 = if (Test-Path -LiteralPath $zipPath) { (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash } else { $null }
    } | ConvertTo-Json -Depth 6
}
try {
    Add-Check 'version-format' ($Version -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') 'version must be semantic major.minor.patch with optional prerelease'
    Add-Check 'version-identity' ($Version -eq $expectedVersion) ("release version must be $expectedVersion")
    if ($failures.Count -gt 0) {
        Write-Result 'FAIL'
        exit 1
    }
    Remove-ReleaseArtifacts

    # All subsequent checks operate on a version-validated package name, so stale
    # artifacts cannot be mistaken for the result of this invocation.

    $sourceAssemblyVersion = $expectedAssemblyVersion
    $sourceFileVersion = $expectedAssemblyVersion
    $sourceInformationalVersion = $expectedVersion + '+release-candidate'
    $sourceIdentityMatches = (
        -not [string]::IsNullOrWhiteSpace($expectedVersion) -and
        -not [string]::IsNullOrWhiteSpace($expectedAssemblyVersion)
    )
    Add-Check 'source-identity' $sourceIdentityMatches (
        "$sourceAssemblyVersion / $sourceFileVersion / $sourceInformationalVersion"
    )
$semanticVersion = ($Version -split '-', 2)[0]
Add-Check 'version-source-match' (
    $Version -eq $expectedVersion -and $semanticVersion + '.0' -eq $expectedAssemblyVersion
) ("package version $Version must match the authoritative framework identity")

    $assemblyExists = Test-Path -LiteralPath $assemblyPath -PathType Leaf
    Add-Check 'runtime-assembly' $assemblyExists $assemblyPath
    if ($assemblyExists) {
        $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath)
        $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath)
        Add-Check 'assembly-version-match' (
            $assemblyName.Version.ToString() -eq $expectedAssemblyVersion -and
            $fileVersion.FileVersion -eq $expectedAssemblyVersion -and
            $fileVersion.ProductVersion -eq $sourceInformationalVersion
        ) ($fileVersion.ProductVersion + ' / ' + $fileVersion.FileVersion)
    }

    $aboutPath = Join-Path $root 'About\About.xml'
    $aboutExists = Test-Path -LiteralPath $aboutPath -PathType Leaf
    Add-Check 'about-metadata' $aboutExists 'About/About.xml'
    Add-Check 'load-folders' (Test-Path -LiteralPath (Join-Path $root 'LoadFolders.xml') -PathType Leaf) 'LoadFolders.xml'
    Add-Check 'license' (Test-Path -LiteralPath (Join-Path $root 'LICENSE') -PathType Leaf) 'LICENSE'
    Add-Check 'public-readme' (Test-Path -LiteralPath (Join-Path $root 'README.md') -PathType Leaf) 'README.md'
    Add-Check 'provider-guide' (Test-Path -LiteralPath (Join-Path $root 'PROVIDER_GUIDE.md') -PathType Leaf) 'PROVIDER_GUIDE.md'
    Add-Check 'compatibility' (Test-Path -LiteralPath (Join-Path $root 'COMPATIBILITY.md') -PathType Leaf) 'COMPATIBILITY.md'
    Add-Check 'release-notes' (Test-Path -LiteralPath (Join-Path $root 'RELEASE_NOTES.md') -PathType Leaf) 'RELEASE_NOTES.md'
    Add-Check 'supported-runtime' (Test-Path -LiteralPath $runtimeRoot -PathType Container) '1.6'
    $loadFoldersPath = Join-Path $root 'LoadFolders.xml'
    $loadFoldersContentValid = $false
    if (Test-Path -LiteralPath $loadFoldersPath -PathType Leaf) {
        try {
            $loadFolders = [xml](Get-Content -LiteralPath $loadFoldersPath -Raw)
            $loadFoldersContentValid = $null -ne $loadFolders.SelectSingleNode('/loadFolders/v1.6/li[. = "1.6"]')
        }
        catch {
            $loadFoldersContentValid = $false
        }
    }
    Add-Check 'load-folders-content' $loadFoldersContentValid 'valid LoadFolders.xml with a 1.6 load folder'
    $aboutContentValid = $false
    $aboutVersionMatches = $false
    if ($aboutExists) {
        try {
            $about = [xml](Get-Content -LiteralPath $aboutPath -Raw)
            $nameNode = $about.SelectSingleNode('/ModMetaData/name')
            $packageIdNode = $about.SelectSingleNode('/ModMetaData/packageId')
            $aboutVersionNode = $about.SelectSingleNode('/ModMetaData/modVersion')
            $aboutContentValid = (
                $null -ne $nameNode -and -not [string]::IsNullOrWhiteSpace($nameNode.InnerText) -and
                $null -ne $packageIdNode -and $packageIdNode.InnerText -eq 'lan.deferredreality.framework' -and
                $null -ne $about.SelectSingleNode('/ModMetaData/supportedVersions/li[. = "1.6"]') -and
                $null -ne $about.SelectSingleNode('/ModMetaData/modDependencies/li/packageId[. = "brrainz.harmony"]')
            )
            $aboutVersionMatches = $null -ne $aboutVersionNode -and $aboutVersionNode.InnerText -eq $Version
        }
        catch {
            $aboutContentValid = $false
            $aboutVersionMatches = $false
        }
    }
    Add-Check 'about-content' $aboutContentValid 'valid About.xml with non-empty name, package identity, 1.6 support, and brrainz.harmony dependency'
    Add-Check 'about-version-match' $aboutVersionMatches "About.xml modVersion must be $Version"

    $runtimeFilesOnDisk = @()
    $runtimeDirectoriesOnDisk = @()
    if (Test-Path -LiteralPath $runtimeRoot -PathType Container) {
        $runtimeFilesOnDisk = @(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File)
        $runtimeDirectoriesOnDisk = @(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -Directory)
    }
    $runtimeRelativeFiles = @($runtimeFilesOnDisk | ForEach-Object { Get-RelativePath $_.FullName } | Sort-Object)
    $runtimeFileMismatch = (Compare-Object -ReferenceObject ($expectedRuntimeFiles | Sort-Object) -DifferenceObject $runtimeRelativeFiles | Out-String)
    Add-Check 'runtime-file-set' ([string]::IsNullOrWhiteSpace($runtimeFileMismatch)) $(if ([string]::IsNullOrWhiteSpace($runtimeFileMismatch)) { 'expected 1.6 runtime files only' } else { $runtimeFileMismatch.Trim() })
    $runtimeDirectoryNames = @($runtimeDirectoriesOnDisk | ForEach-Object { Get-RelativePath $_.FullName } | Sort-Object)
    $runtimeDirectoryMismatch = (Compare-Object -ReferenceObject ($expectedRuntimeDirectories | Sort-Object) -DifferenceObject $runtimeDirectoryNames | Out-String)
    Add-Check 'runtime-directory-boundary' ([string]::IsNullOrWhiteSpace($runtimeDirectoryMismatch)) $(if ([string]::IsNullOrWhiteSpace($runtimeDirectoryMismatch)) { 'expected 1.6 runtime folders only' } else { $runtimeDirectoryMismatch.Trim() })
    $runtimeUnexpectedTypes = @($runtimeFilesOnDisk | Where-Object { $_.Extension -notin @('.dll', '.xml') })
    Add-Check 'runtime-file-types' ($runtimeUnexpectedTypes.Count -eq 0) $(if ($runtimeUnexpectedTypes.Count -eq 0) { 'DLL/XML runtime files only' } else { ($runtimeUnexpectedTypes | ForEach-Object { Get-RelativePath $_.FullName }) -join '; ' })
    $runtimeDlls = @($runtimeFilesOnDisk | Where-Object { $_.Extension -ieq '.dll' })
    $runtimeAssemblyMatches = @($runtimeDlls | Where-Object { [string]::Equals($_.FullName, $assemblyPath, [StringComparison]::OrdinalIgnoreCase) })
    Add-Check 'runtime-assembly-count' ($runtimeDlls.Count -eq 1 -and $runtimeAssemblyMatches.Count -eq 1) (
        "expected one DeferredRealityFramework.dll under 1.6; found $($runtimeDlls.Count)"
    )
    Add-Check 'runtime-provider-boundary' ($runtimeDlls.Count -eq 1 -and $runtimeAssemblyMatches.Count -eq 1) $(if ($runtimeDlls.Count -eq 1 -and $runtimeAssemblyMatches.Count -eq 1) { 'only DeferredRealityFramework.dll' } else { ($runtimeDlls | ForEach-Object { Get-RelativePath $_.FullName }) -join '; ' })

    $sourceFiles = Get-ExpectedSourceFiles
    $sourceRelativeFiles = @($sourceFiles | ForEach-Object { Get-RelativePath $_.FullName } | Sort-Object)
    $sourceFileMismatch = @(Compare-Object -ReferenceObject $expectedPackageFiles -DifferenceObject $sourceRelativeFiles)
    Add-Check 'package-allowlist' ($sourceFileMismatch.Count -eq 0) $(if ($sourceFileMismatch.Count -eq 0) { 'all mandatory end-user files selected' } else { ($sourceFileMismatch | Out-String).Trim() })
    Add-Check 'package-files' ($sourceFiles.Count -eq ($requiredRootFiles.Count + 1 + $expectedRuntimeFiles.Count)) (
        "$($sourceFiles.Count) required public/runtime files selected"
    )

    if ($failures.Count -gt 0) {
        Write-Result 'FAIL'
        exit 1
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

    foreach ($sourceFile in $sourceFiles) {
        $relative = Get-RelativePath $sourceFile.FullName
        $destination = Join-Path $stagingPath $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination -Force
    }
    $stagingRelativeFiles = @(Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
        ForEach-Object { Get-StagingRelativePath $_.FullName } | Sort-Object)
    $stagingFileMismatch = @(Compare-Object -ReferenceObject $expectedPackageFiles -DifferenceObject $stagingRelativeFiles)
    Add-Check 'staging-entries' ($stagingFileMismatch.Count -eq 0) $(if ($stagingFileMismatch.Count -eq 0) { 'staging contains exact allowlisted file set' } else { ($stagingFileMismatch | Out-String).Trim() })
    $stagingDirectoryNames = @(Get-ChildItem -LiteralPath $stagingPath -Recurse -Directory |
        ForEach-Object { Get-StagingRelativePath $_.FullName } | Sort-Object)
    $stagingDirectoryMismatch = @(Compare-Object -ReferenceObject $expectedPackageDirectories -DifferenceObject $stagingDirectoryNames)
    Add-Check 'staging-directories' ($stagingDirectoryMismatch.Count -eq 0) $(if ($stagingDirectoryMismatch.Count -eq 0) { 'staging contains exact public/runtime directory set' } else { ($stagingDirectoryMismatch | Out-String).Trim() })

    $stagingFiles = @(Get-ChildItem -LiteralPath $stagingPath -Recurse -File | Sort-Object -Property FullName)
    $entryNames = New-Object System.Collections.Generic.List[string]
    foreach ($stagingFile in $stagingFiles) {
        $relative = Get-StagingRelativePath $stagingFile.FullName
        $entryNames.Add(($topLevelDirectory + '/' + $relative))
    }

    $zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = New-Object IO.Compression.ZipArchive($zipStream, ([IO.Compression.ZipArchiveMode]::Create), $false)
    try {
        $fixedTime = [DateTimeOffset]::Parse('2000-01-01T00:00:00Z')
        foreach ($stagingFile in $stagingFiles) {
            $relative = Get-StagingRelativePath $stagingFile.FullName
            $entry = $archive.CreateEntry(($topLevelDirectory + '/' + $relative), [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTime
            $input = [IO.File]::OpenRead($stagingFile.FullName)
            $output = $entry.Open()
            try { $input.CopyTo($output) }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
        $zipStream.Dispose()
    }

    $expectedEntries = @($entryNames | Sort-Object)
    $readStream = [IO.File]::OpenRead($zipPath)
    $readArchive = New-Object IO.Compression.ZipArchive($readStream, ([IO.Compression.ZipArchiveMode]::Read), $false)
    try {
        $actualEntries = @($readArchive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    }
    finally {
        $readArchive.Dispose()
        $readStream.Dispose()
    }
    $entryMismatch = (Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $actualEntries) | Out-String
    Add-Check 'package-entries' ([string]::IsNullOrWhiteSpace($entryMismatch)) $(if ([string]::IsNullOrWhiteSpace($entryMismatch)) { 'exact runtime and public-file set' } else { $entryMismatch.Trim() })
    $forbiddenEntries = @($actualEntries | Where-Object { $_ -match '(^|/)(Source|Tests|TestCatalog|DevTools|BridgeAdapters|BridgeAdapter|\.rimdev|\.rimctx|\.github|artifacts|diagnostics|evidence)(/|$)|(^|/)(AGENTS\.md|\.pdb)$' })
    Add-Check 'package-development-boundary' ($forbiddenEntries.Count -eq 0) $(if ($forbiddenEntries.Count -eq 0) { 'no development-only files' } else { $forbiddenEntries -join '; ' })
    Add-Check 'package-created' (Test-Path -LiteralPath $zipPath -PathType Leaf) $zipPath
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $checksumPath -Value ($zipHash + '  ' + [IO.Path]::GetFileName($zipPath)) -Encoding ascii -NoNewline

    $status = if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
    if ($status -eq 'FAIL') {
        Remove-ReleaseArtifacts
    }
    Write-Result $status
    if ($status -eq 'FAIL') { exit 1 }
    exit 0
}
catch {
    try { Remove-ReleaseArtifacts } catch { }
    $failures.Add($_.Exception.Message)
    Write-Result 'FAIL'
    exit 1
}
