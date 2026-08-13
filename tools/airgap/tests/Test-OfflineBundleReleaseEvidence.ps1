[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Utf8([string]$Path, [string]$Value) {
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Write-Json([string]$Path, [object]$Value) {
    Write-Utf8 $Path (($Value | ConvertTo-Json -Depth 30) + "`n")
}

function Get-Identity([string]$Path, [string]$FileName) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    return [pscustomobject]@{
        fileName = $FileName
        bytes = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Assert-ValidationFails(
    [hashtable]$Arguments,
    [string]$ExpectedMessage
) {
    $failed = $false
    try {
        & $script:validator @Arguments | Out-Null
    }
    catch {
        $failed = $true
        if ($_.Exception.Message -notmatch $ExpectedMessage) {
            throw "Validation failed for an unexpected reason: $($_.Exception.Message)"
        }
    }
    if (-not $failed) {
        throw "Expected release evidence validation to fail: $ExpectedMessage"
    }
}

function Sync-CheckedEvidence(
    [string]$Root,
    [object]$Evidence,
    [string]$EvidencePath,
    [string[]]$PackAssetNames
) {
    $releaseAssets = [Collections.Generic.List[object]]::new()
    foreach ($fileName in $PackAssetNames) {
        $releaseAssets.Add((Get-Identity (Join-Path $Root $fileName) $fileName))
    }
    $Evidence.releaseAssets = @($releaseAssets)
        $bundleRow = $Evidence.bundle
        $trustIdentity = Get-Identity `
            (Join-Path $Root 'bundle-packs.json') `
            'bundle-packs.json'
        $manifestIdentity = Get-Identity `
            (Join-Path $Root 'airgap-manifest.json') `
            'airgap-manifest.json'
        $configurationIdentity = Get-Identity `
            (Join-Path $Root 'airgap-config.json') `
            'airgap-config.json'
        $licenseIdentity = Get-Identity `
            (Join-Path $Root 'Hy-MT2-Apache-2.0.txt') `
            'Hy-MT2-Apache-2.0.txt'
        $bundleRow.bundlePackTrustManifestBytes = $trustIdentity.bytes
        $bundleRow.bundlePackTrustManifestSha256 = $trustIdentity.sha256
        $bundleRow.airgapManifestBytes = $manifestIdentity.bytes
        $bundleRow.airgapManifestSha256 = $manifestIdentity.sha256
        $bundleRow.configurationBytes = $configurationIdentity.bytes
        $bundleRow.configurationSha256 = $configurationIdentity.sha256
        $bundleRow.translationLicenseBytes = $licenseIdentity.bytes
        $bundleRow.translationLicenseSha256 = $licenseIdentity.sha256
    $baseReleaseIdentity = Get-Identity `
        (Join-Path $Root 'bstrings-win-x64.zip') `
        'bstrings-win-x64.zip'
    $checksummedAssets = @($baseReleaseIdentity) + @($releaseAssets)
    $checksumRows = @(
        $checksummedAssets |
            Sort-Object fileName |
            ForEach-Object { "$($_.sha256)  $($_.fileName)" }
    )
    $checksumPath = Join-Path $Root 'SHA256SUMS.txt'
    Write-Utf8 $checksumPath (($checksumRows -join "`n") + "`n")
    $checksumIdentity = Get-Identity $checksumPath 'SHA256SUMS.txt'
    $Evidence.checksumFileBytes = $checksumIdentity.bytes
    $Evidence.checksumFileSha256 = $checksumIdentity.sha256
    Write-Json $EvidencePath $Evidence
}

$validator = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\Test-OfflineBundleReleaseEvidence.ps1')
)
$validatorItem = Get-Item -LiteralPath $validator -Force -ErrorAction Stop
if (
    $validatorItem.PSIsContainer -or
    ($validatorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
) {
    throw "Release evidence validator is not a physical file: $validator"
}

$testRoot = Join-Path (
    [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
) ('bstrings-release-evidence-test-' + [Guid]::NewGuid().ToString('N'))
$safeTestLeaf = [IO.Path]::GetFileName($testRoot)
if ($safeTestLeaf -notmatch '^bstrings-release-evidence-test-[0-9a-f]{32}$') {
    throw "Unexpected synthetic test path: $testRoot"
}
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$assetRoot = Join-Path $testRoot 'release-assets'
[IO.Directory]::CreateDirectory($assetRoot) | Out-Null

try {
    $repository = 'example/bstrings'
    $serverUrl = 'https://github.example.invalid'
    $tag = 'v9.9.9-test'
    $commit = '0123456789abcdef0123456789abcdef01234567'
    $runId = '123456789'
    $componentLockPath = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\offline-components.lock.json')
    )
    $componentLock = Get-Content -LiteralPath $componentLockPath -Raw | ConvertFrom-Json
        $lockedModel = $componentLock.components.translationModel
        $model = [pscustomobject]@{
            id = [string]$lockedModel.modelId
            revision = [string]$lockedModel.revision
            bytes = [long]$lockedModel.bytes
            sha256 = [string]$lockedModel.sha256
            fileName = [string]$lockedModel.fileName
            url = [string]$lockedModel.url
        }

    Write-Utf8 (Join-Path $assetRoot 'bstrings-win-x64.zip') 'synthetic base runtime archive'
    Write-Utf8 (Join-Path $assetRoot 'bstrings-win-x64-offline-base.zip') 'synthetic base archive'
    Write-Utf8 (Join-Path $assetRoot 'bstrings-win-x64-offline-cuda.zip') 'synthetic CUDA archive'
        $configurationName = 'airgap-config.json'
        $licenseName = 'Hy-MT2-Apache-2.0.txt'
        $manifestName = 'airgap-manifest.json'
        $trustName = 'bundle-packs.json'
        $modelPath = "models/hy-mt2/$($model.fileName)"

        $configuration = [ordered]@{
            schemaVersion = 2
            bundleProfile = 'windows-x64-offline-v3'
            translationModel = [ordered]@{
                path = $modelPath
                id = $model.id
                revision = $model.revision
                sha256 = $model.sha256
            }
        }
        Write-Json (Join-Path $assetRoot $configurationName) $configuration
        Write-Utf8 (Join-Path $assetRoot $licenseName) "synthetic translation license`n"
        $configurationIdentity = Get-Identity `
            (Join-Path $assetRoot $configurationName) `
            $configurationName
        $licenseIdentity = Get-Identity (Join-Path $assetRoot $licenseName) $licenseName

        $manifest = [ordered]@{
            schemaVersion = 1
            files = @(
                [ordered]@{
                    path = 'airgap-config.json'
                    bytes = $configurationIdentity.bytes
                    sha256 = $configurationIdentity.sha256
                }
                [ordered]@{
                    path = 'licenses/Hy-MT2-Apache-2.0.txt'
                    bytes = $licenseIdentity.bytes
                    sha256 = $licenseIdentity.sha256
                }
                [ordered]@{
                    path = $modelPath
                    bytes = $model.bytes
                    sha256 = $model.sha256
                }
            )
        }
        Write-Json (Join-Path $assetRoot $manifestName) $manifest
        $manifestIdentity = Get-Identity (Join-Path $assetRoot $manifestName) $manifestName
        $baseIdentity = Get-Identity `
            (Join-Path $assetRoot 'bstrings-win-x64-offline-base.zip') `
            'bstrings-win-x64-offline-base.zip'
        $cudaIdentity = Get-Identity `
            (Join-Path $assetRoot 'bstrings-win-x64-offline-cuda.zip') `
            'bstrings-win-x64-offline-cuda.zip'
        $bundleIdentity = "synthetic-kit-$($manifestIdentity.sha256.Substring(0, 24))"
        $assetBaseUrl = "$serverUrl/$repository/releases/download/$tag"
        $trust = [ordered]@{
            schemaVersion = 1
            profile = 'windows-x64-offline-v3'
            bundleIdentity = $bundleIdentity
            airgapManifestSha256 = $manifestIdentity.sha256
            packs = @(
                [ordered]@{
                    id = 'base'
                    url = "$assetBaseUrl/bstrings-win-x64-offline-base.zip"
                    bytes = $baseIdentity.bytes
                    sha256 = $baseIdentity.sha256
                }
                [ordered]@{
                    id = 'cuda-runtime'
                    url = "$assetBaseUrl/bstrings-win-x64-offline-cuda.zip"
                    bytes = $cudaIdentity.bytes
                    sha256 = $cudaIdentity.sha256
                }
                [ordered]@{
                    id = 'configuration'
                    url = "$assetBaseUrl/$configurationName"
                    kind = 'file'
                    target = 'airgap-config.json'
                    bytes = $configurationIdentity.bytes
                    sha256 = $configurationIdentity.sha256
                }
                [ordered]@{
                    id = 'translation-license'
                    url = "$assetBaseUrl/$licenseName"
                    kind = 'file'
                    target = 'licenses/Hy-MT2-Apache-2.0.txt'
                    bytes = $licenseIdentity.bytes
                    sha256 = $licenseIdentity.sha256
                }
                [ordered]@{
                    id = 'airgap-manifest'
                    url = "$assetBaseUrl/$manifestName"
                    kind = 'file'
                    target = 'airgap-manifest.json'
                    bytes = $manifestIdentity.bytes
                    sha256 = $manifestIdentity.sha256
                }
                [ordered]@{
                    id = 'translation-model'
                    url = $model.url
                    kind = 'file'
                    target = $modelPath
                    bytes = $model.bytes
                    sha256 = $model.sha256
                }
            )
        }
        Write-Json (Join-Path $assetRoot $trustName) $trust
        $trustIdentity = Get-Identity (Join-Path $assetRoot $trustName) $trustName
        $bundleEvidence = [pscustomobject]@{
            status = 'passed'
            translationSmoke = 'passed'
            acceptedAtUtc = '2026-08-05T00:00:00.0000000+00:00'
            bundleIdentity = $bundleIdentity
            bundlePackTrustManifestBytes = $trustIdentity.bytes
            bundlePackTrustManifestSha256 = $trustIdentity.sha256
            airgapManifestBytes = $manifestIdentity.bytes
            airgapManifestSha256 = $manifestIdentity.sha256
            configurationBytes = $configurationIdentity.bytes
            configurationSha256 = $configurationIdentity.sha256
            translationLicenseBytes = $licenseIdentity.bytes
            translationLicenseSha256 = $licenseIdentity.sha256
            translationModelId = $model.id
            translationModelRevision = $model.revision
            translationModelUrl = $model.url
            translationModelBytes = $model.bytes
            translationModelSha256 = $model.sha256
        }

    $packAssetNames = [Collections.Generic.List[string]]::new()
    $packAssetNames.Add('bstrings-win-x64-offline-base.zip')
    $packAssetNames.Add('bstrings-win-x64-offline-cuda.zip')
    Write-Utf8 (Join-Path $assetRoot 'Install-Bstrings.ps1') "# synthetic installer`n"
    $packAssetNames.Add('Install-Bstrings.ps1')
    $packAssetNames.Add('airgap-config.json')
    $packAssetNames.Add('Hy-MT2-Apache-2.0.txt')
    $packAssetNames.Add('airgap-manifest.json')
    $packAssetNames.Add('bundle-packs.json')
    $releaseAssets = [Collections.Generic.List[object]]::new()
    foreach ($fileName in $packAssetNames) {
        $releaseAssets.Add((Get-Identity (Join-Path $assetRoot $fileName) $fileName))
    }
    $baseReleaseIdentity = Get-Identity `
        (Join-Path $assetRoot 'bstrings-win-x64.zip') `
        'bstrings-win-x64.zip'
    $checksummedAssets = @($baseReleaseIdentity) + @($releaseAssets)
    $checksumRows = @(
        $checksummedAssets |
            Sort-Object fileName |
            ForEach-Object { "$($_.sha256)  $($_.fileName)" }
    )
    $checksumPath = Join-Path $assetRoot 'SHA256SUMS.txt'
    Write-Utf8 $checksumPath (($checksumRows -join "`n") + "`n")
    $checksumIdentity = Get-Identity $checksumPath 'SHA256SUMS.txt'
    $evidence = [ordered]@{
        schemaVersion = 3
        status = 'passed'
        classification = 'synthetic-release-bundle-acceptance'
        generatedAtUtc = '2026-08-05T00:00:00.0000000+00:00'
        sourceRepository = $repository
        sourceTag = $tag
        sourceCommit = $commit
        sourceBuildRunId = $runId
        acceptanceRunAttempt = '7'
        minimumFreeBytes = [long]1
        freeBytesAtStart = [long]1
        baseArchiveBytes = $baseReleaseIdentity.bytes
        baseArchiveSha256 = $baseReleaseIdentity.sha256
        checksumFileBytes = $checksumIdentity.bytes
        checksumFileSha256 = $checksumIdentity.sha256
        releaseAssets = @($releaseAssets)
        bundle = $bundleEvidence
    }
    $evidencePath = Join-Path $testRoot 'offline-bundle-acceptance.json'
    Write-Json $evidencePath $evidence
    $validatorArguments = @{
        EvidencePath = $evidencePath
        AssetDirectory = $assetRoot
        ExpectedRepository = $repository
        ExpectedServerUrl = $serverUrl
        ExpectedTag = $tag
        ExpectedCommit = $commit
        ExpectedBuildRunId = $runId
        ComponentLockPath = $componentLockPath
    }

    & $validator @validatorArguments | Out-Null

    $wrongRunArguments = $validatorArguments.Clone()
    $wrongRunArguments.ExpectedBuildRunId = '123456788'
    Assert-ValidationFails $wrongRunArguments 'not bound to this exact repository, tag, commit, and build run'

    $evidence.acceptanceRunAttempt = '0'
    Write-Json $evidencePath $evidence
    Assert-ValidationFails $validatorArguments 'positive acceptance run attempt'
    $evidence.acceptanceRunAttempt = '7'
    Write-Json $evidencePath $evidence

    $extraPath = Join-Path $assetRoot 'unexpected.bin'
    Write-Utf8 $extraPath 'unexpected'
    Assert-ValidationFails $validatorArguments 'linked or non-file|exact expected file set'
    [IO.File]::Delete($extraPath)

    $originalChecksumText = [IO.File]::ReadAllText($checksumPath)
    Write-Utf8 $checksumPath ($originalChecksumText + $checksumRows[0] + "`n")
    $mutatedChecksumIdentity = Get-Identity $checksumPath 'SHA256SUMS.txt'
    $evidence.checksumFileBytes = $mutatedChecksumIdentity.bytes
    $evidence.checksumFileSha256 = $mutatedChecksumIdentity.sha256
    Write-Json $evidencePath $evidence
    Assert-ValidationFails $validatorArguments 'exactly one row|Duplicate SHA256SUMS.txt row'
    Write-Utf8 $checksumPath $originalChecksumText
    $evidence.checksumFileBytes = $checksumIdentity.bytes
    $evidence.checksumFileSha256 = $checksumIdentity.sha256
    Write-Json $evidencePath $evidence

    $publicEvidencePath = Join-Path $assetRoot 'offline-bundle-acceptance.json'
    Copy-Item -LiteralPath $evidencePath -Destination $publicEvidencePath
    Assert-ValidationFails $validatorArguments 'exact expected file set'
    [IO.File]::Delete($publicEvidencePath)

    $withoutBaseChecksumRows = @(
        $checksumRows | Where-Object { $_ -notmatch '  bstrings-win-x64\.zip$' }
    )
    Write-Utf8 $checksumPath (($withoutBaseChecksumRows -join "`n") + "`n")
    $mutatedChecksumIdentity = Get-Identity $checksumPath 'SHA256SUMS.txt'
    $evidence.checksumFileBytes = $mutatedChecksumIdentity.bytes
    $evidence.checksumFileSha256 = $mutatedChecksumIdentity.sha256
    Write-Json $evidencePath $evidence
    Assert-ValidationFails $validatorArguments 'exactly one row|exact checksummed public release-asset set'
    Write-Utf8 $checksumPath $originalChecksumText
    $evidence.checksumFileBytes = $checksumIdentity.bytes
    $evidence.checksumFileSha256 = $checksumIdentity.sha256
    Write-Json $evidencePath $evidence

    $originalFirstAsset = $evidence.releaseAssets[0]
    $originalLastAsset = $evidence.releaseAssets[-1]
    $evidence.releaseAssets[-1] = $originalFirstAsset
    Write-Json $evidencePath $evidence
    Assert-ValidationFails $validatorArguments 'Duplicate release evidence asset row'
    $evidence.releaseAssets[-1] = $originalLastAsset
    Write-Json $evidencePath $evidence

    $originalModelId = $evidence.bundle.translationModelId
    $evidence.bundle.translationModelId = 'example/tampered-model'
    Write-Json $evidencePath $evidence
    Assert-ValidationFails $validatorArguments 'translation model evidence differs from the exact checked component lock'
    $evidence.bundle.translationModelId = $originalModelId
    Write-Json $evidencePath $evidence

    $bundleTrustPath = Join-Path $assetRoot 'bundle-packs.json'
    $bundleTrust = Get-Content -LiteralPath $bundleTrustPath -Raw | ConvertFrom-Json
    $bundleBasePacks = @($bundleTrust.packs | Where-Object { [string]$_.id -ceq 'base' })
    $bundleModelPacks = @($bundleTrust.packs | Where-Object { [string]$_.id -ceq 'translation-model' })
    if ($bundleBasePacks.Count -ne 1 -or $bundleModelPacks.Count -ne 1) {
        throw 'Synthetic bundle trust manifest lost its exact base or model pack.'
    }
    $bundleBasePack = $bundleBasePacks[0]
    $bundleModelPack = $bundleModelPacks[0]
    $canonicalBaseUrl = [string]$bundleBasePack.url
    $releaseUrlMutations = @(
        "https://wrong-host.example.invalid/$repository/releases/download/$tag/bstrings-win-x64-offline-base.zip"
        "$serverUrl/other/bstrings/releases/download/$tag/bstrings-win-x64-offline-base.zip"
        "$serverUrl/$repository/releases/download/v9.9.8-test/bstrings-win-x64-offline-base.zip"
        "$serverUrl/$repository/releases/download/$tag/nested/bstrings-win-x64-offline-base.zip"
        "${canonicalBaseUrl}?download=true"
        "https://GITHUB.example.invalid/$repository/releases/download/$tag/bstrings-win-x64-offline-base.zip"
        "$serverUrl/example/%62strings/releases/download/$tag/bstrings-win-x64-offline-base.zip"
    )
    foreach ($mutatedUrl in $releaseUrlMutations) {
        $bundleBasePack.url = $mutatedUrl
        Write-Json $bundleTrustPath $bundleTrust
        Sync-CheckedEvidence $assetRoot $evidence $evidencePath @($packAssetNames)
        Assert-ValidationFails `
            $validatorArguments `
            'exact expected server, repository, tag, path, and casing|without credentials, a query, or a fragment'
    }
    $bundleBasePack.url = $canonicalBaseUrl
    Write-Json $bundleTrustPath $bundleTrust
    Sync-CheckedEvidence $assetRoot $evidence $evidencePath @($packAssetNames)

    $canonicalModelUrl = [string]$bundleModelPack.url
    $mutatedModelUrl = $canonicalModelUrl.Replace('huggingface.co', 'models.example.invalid')
    $bundleModelPack.url = $mutatedModelUrl
    $evidence.bundle.translationModelUrl = $mutatedModelUrl
    Write-Json $bundleTrustPath $bundleTrust
    Sync-CheckedEvidence $assetRoot $evidence $evidencePath @($packAssetNames)
    Assert-ValidationFails $validatorArguments 'translation model evidence differs from the exact checked component lock'
    $bundleModelPack.url = $canonicalModelUrl
    $evidence.bundle.translationModelUrl = $canonicalModelUrl
    Write-Json $bundleTrustPath $bundleTrust
    Sync-CheckedEvidence $assetRoot $evidence $evidencePath @($packAssetNames)

    $basePath = Join-Path $assetRoot 'bstrings-win-x64-offline-base.zip'
    $originalBase = [IO.File]::ReadAllBytes($basePath)
    [IO.File]::WriteAllBytes($basePath, [byte[]](1, 2, 3, 4))
    Assert-ValidationFails $validatorArguments 'does not match its checked release evidence'
    [IO.File]::WriteAllBytes($basePath, $originalBase)

    $linkPath = Join-Path $assetRoot 'linked-extra'
    try {
        New-Item -ItemType SymbolicLink -Path $linkPath -Target $checksumPath -ErrorAction Stop | Out-Null
        Assert-ValidationFails $validatorArguments 'linked or non-file entry'
    }
    catch {
        if (Test-Path -LiteralPath $linkPath) {
            throw
        }
        Write-Host 'Symbolic-link rejection probe skipped because links are unavailable.'
    }
    finally {
        if (Test-Path -LiteralPath $linkPath) {
            [IO.File]::Delete($linkPath)
        }
    }

    & $validator @validatorArguments | Out-Null
    Write-Host 'Offline bundle release evidence synthetic tests passed.'
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    if (
        $resolvedTestRoot.StartsWith(
            $tempRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase
        ) -and
        [IO.Path]::GetFileName($resolvedTestRoot) -match '^bstrings-release-evidence-test-[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $resolvedTestRoot -PathType Container)
    ) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
