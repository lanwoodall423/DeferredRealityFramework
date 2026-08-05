param([string]$ModRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$expectedId = 'lan.deferredreality.framework'
$expectedPackage = 'lan.deferredreality.framework'
$directory = [IO.Path]::GetFullPath((Join-Path $ModRoot 'DevTools\BridgeAdapters'))
$manifests = @(Get-ChildItem -LiteralPath $directory -Filter '*.manifest.json' -File)
if ($manifests.Count -ne 1) { throw "Expected one $expectedId manifest, found $($manifests.Count)." }
$manifest = Get-Content -LiteralPath $manifests[0].FullName -Raw | ConvertFrom-Json
$dll = Join-Path $directory $manifest.assemblyFile
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Manifest DLL is missing: $dll" }
if ((Get-ChildItem -LiteralPath $directory -File).Count -ne 2) { throw 'Owner adapter directory contains unexpected files.' }
if ($manifest.adapterId -ne $expectedId) { throw "Unexpected adapterId: $($manifest.adapterId)" }
if (@($manifest.requiredPackageIds) -notcontains $expectedPackage) { throw "Missing required package: $expectedPackage" }
if ($manifest.executionContract -ne 'cooperative-v1') { throw "Unexpected execution contract: $($manifest.executionContract)" }
$info = Get-Item -LiteralPath $dll
$identity = [Reflection.AssemblyName]::GetAssemblyName($dll).FullName
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
if ($manifest.assemblyIdentity -ne $identity -or [long]$manifest.assemblyBytes -ne $info.Length -or $manifest.contentHash -ne $hash) { throw 'Owner manifest does not match its exact DLL.' }
Write-Output ('ownerAdapterVerification=PASS adapter={0} generation={1} bytes={2} sha256={3}' -f $expectedId, $manifest.generation, $info.Length, $hash)
