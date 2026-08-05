[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CoreArchive,
    [Parameter(Mandatory = $true)]
    [string]$PackDirectory,
    [Parameter(Mandatory = $true)]
    [string]$WorkingDirectory,
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory,
    [Parameter(Mandatory = $true)]
    [string]$SourceBuildRunId,
    [Parameter(Mandatory = $true)]
    [string]$SourceRunAttempt,
    [Parameter(Mandatory = $true)]
    [string]$SourceTag,
    [Parameter(Mandatory = $true)]
    [string]$SourceCommit,
    [Parameter(Mandatory = $true)]
    [string]$SourceRepository,
    [ValidateRange(1, [long]::MaxValue)]
    [long]$MinimumFreeBytes = 30000000000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-PhysicalFile([string]$Path, [string]$Name) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if (
        $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "$Name must be a physical regular file: $fullPath"
    }
    return $item.FullName
}

function Resolve-PhysicalDirectory([string]$Path, [string]$Name) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if (
        -not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "$Name must be a physical directory: $fullPath"
    }
    return $item.FullName.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
}

function Assert-NewDirectory([string]$Path, [string]$Name) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($null -ne (Get-Item -LiteralPath $fullPath -Force -ErrorAction SilentlyContinue)) {
        throw "$Name already exists: $fullPath"
    }
    return $fullPath
}

function Get-RequiredPack([string]$Root, [string]$FileName) {
    return Resolve-PhysicalFile (Join-Path $Root $FileName) "Release pack $FileName"
}

function Get-FileIdentityRow([string]$Path, [string]$FileName) {
    $resolved = Resolve-PhysicalFile $Path "Release asset $FileName"
    $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
    if ([long]$item.Length -lt 1) {
        throw "Release asset must not be empty: $FileName"
    }
    return [pscustomobject]@{
        fileName = $FileName
        bytes = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-CheckedPackInventory(
    [string]$Root,
    [string[]]$ExpectedAssetNames,
    [object[]]$AdditionalChecksummedAssets = @()
) {
    $checksumName = 'SHA256SUMS.txt'
    $expectedDirectoryNames = @($ExpectedAssetNames) + $checksumName
    $entries = @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop)
    foreach ($entry in $entries) {
        if (
            $entry.PSIsContainer -or
            ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "Split-pack artifact directory contains a linked or non-file entry: $($entry.FullName)"
        }
    }
    if (
        (@($entries.Name | Sort-Object) -join '|') -cne
        (@($expectedDirectoryNames | Sort-Object) -join '|')
    ) {
        throw 'Split-pack artifact directory does not contain the exact release-owned file set.'
    }

    $checksummedAssetByName = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($asset in @($AdditionalChecksummedAssets)) {
        $fileName = [string]$asset.fileName
        if (
            [string]::IsNullOrWhiteSpace($fileName) -or
            [IO.Path]::GetFileName($fileName) -cne $fileName -or
            [long]$asset.bytes -lt 1 -or
            [string]$asset.sha256 -notmatch '^[0-9a-f]{64}$' -or
            -not $checksummedAssetByName.TryAdd($fileName, $asset)
        ) {
            throw "Invalid or duplicate additional checksummed release asset: $fileName"
        }
    }
    foreach ($expectedName in $ExpectedAssetNames) {
        if ($checksummedAssetByName.ContainsKey($expectedName)) {
            throw "Checksummed release-asset name collides with a split-pack asset: $expectedName"
        }
    }
    $expectedChecksumNames = @($ExpectedAssetNames) + @($checksummedAssetByName.Keys)

    $checksumPath = Get-RequiredPack $Root $checksumName
    $checksumItem = Get-Item -LiteralPath $checksumPath -Force -ErrorAction Stop
    if ([long]$checksumItem.Length -lt 1 -or [long]$checksumItem.Length -gt 1048576) {
        throw 'SHA256SUMS.txt must have a bounded non-empty byte length.'
    }
    $checksumText = [IO.File]::ReadAllText($checksumPath, [Text.UTF8Encoding]::new($false, $true))
    if (-not $checksumText.EndsWith("`n", [StringComparison]::Ordinal) -or $checksumText.Contains("`r")) {
        throw 'SHA256SUMS.txt must use canonical LF-terminated rows.'
    }
    $checksumRows = @($checksumText.Substring(0, $checksumText.Length - 1) -split "`n")
    if ($checksumRows.Count -ne $expectedChecksumNames.Count) {
        throw 'SHA256SUMS.txt does not contain exactly one row per checksummed public release asset.'
    }
    $checksumByName = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($row in $checksumRows) {
        if ($row -notmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^/\\]+)$') {
            throw "Invalid split-pack checksum row: $row"
        }
        if (-not $checksumByName.TryAdd([string]$Matches.name, [string]$Matches.hash)) {
            throw "Duplicate split-pack checksum row: $($Matches.name)"
        }
    }
    if (
        (@($checksumByName.Keys | Sort-Object) -join '|') -cne
        (@($expectedChecksumNames | Sort-Object) -join '|')
    ) {
        throw 'SHA256SUMS.txt does not name the exact checksummed public release-asset set.'
    }

    $inventory = [Collections.Generic.List[object]]::new()
    foreach ($expectedName in $ExpectedAssetNames) {
        $expectedHash = $null
        if (-not $checksumByName.TryGetValue($expectedName, [ref]$expectedHash)) {
            throw "SHA256SUMS.txt is missing release asset: $expectedName"
        }
        $identity = Get-FileIdentityRow (Join-Path $Root $expectedName) $expectedName
        if ($identity.sha256 -cne $expectedHash) {
            throw "SHA256SUMS.txt does not match release asset: $expectedName"
        }
        $inventory.Add($identity)
    }
    foreach ($additionalName in $checksummedAssetByName.Keys) {
        $expectedHash = $null
        if (-not $checksumByName.TryGetValue($additionalName, [ref]$expectedHash)) {
            throw "SHA256SUMS.txt is missing release asset: $additionalName"
        }
        $identity = $checksummedAssetByName[$additionalName]
        if ([string]$identity.sha256 -cne $expectedHash) {
            throw "SHA256SUMS.txt does not match release asset: $additionalName"
        }
    }
    return @($inventory)
}

function Remove-CompletedProfileDirectory(
    [string]$Root,
    [string]$Target,
    [string]$Profile
) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $targetPath = [IO.Path]::GetFullPath($Target).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $requiredPrefix = $rootPath + [IO.Path]::DirectorySeparatorChar
    if (
        -not $targetPath.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($targetPath) -cne "profile-$Profile"
    ) {
        throw "Refusing to remove an uncontrolled profile path: $targetPath"
    }
    $targetItem = Get-Item -LiteralPath $targetPath -Force -ErrorAction Stop
    if (
        -not $targetItem.PSIsContainer -or
        ($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "Refusing to remove a linked or non-directory profile path: $targetPath"
    }
    $linkedEntries = @(
        Get-ChildItem -LiteralPath $targetPath -Recurse -Force |
            Where-Object {
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            }
    )
    if ($linkedEntries.Count -ne 0) {
        throw "Refusing to remove a profile tree containing links: $($linkedEntries.FullName -join ', ')"
    }
    Remove-Item -LiteralPath $targetPath -Recurse -Force
    if ($null -ne (Get-Item -LiteralPath $targetPath -Force -ErrorAction SilentlyContinue)) {
        throw "Completed profile work directory was not removed: $targetPath"
    }
}

if ($SourceBuildRunId -notmatch '^[1-9][0-9]*$') {
    throw 'SourceBuildRunId must be a positive GitHub Actions run ID.'
}
if ($SourceRunAttempt -notmatch '^[1-9][0-9]*$') {
    throw 'SourceRunAttempt must be a positive GitHub Actions run attempt.'
}
if ([string]::IsNullOrWhiteSpace($SourceTag)) {
    throw 'SourceTag must not be empty.'
}
if ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'SourceCommit must be a full Git commit SHA.'
}
if ($SourceRepository -notmatch '^[^/\s]+/[^/\s]+$') {
    throw 'SourceRepository must be an owner/repository name.'
}

$resolvedCoreArchive = Resolve-PhysicalFile $CoreArchive 'Core release archive'
if ([IO.Path]::GetFileName($resolvedCoreArchive) -cne 'bstrings-win-x64.zip') {
    throw "Unexpected core release archive name: $resolvedCoreArchive"
}
$packRoot = Resolve-PhysicalDirectory $PackDirectory 'Split-pack artifact directory'
$workRoot = Assert-NewDirectory $WorkingDirectory 'Profile acceptance work directory'
$evidenceRoot = Assert-NewDirectory $EvidenceDirectory 'Profile acceptance evidence directory'
if ($workRoot -ceq $evidenceRoot) {
    throw 'WorkingDirectory and EvidenceDirectory must be different paths.'
}

$driveRoot = [IO.Path]::GetPathRoot($workRoot)
$drive = [IO.DriveInfo]::new($driveRoot)
if (-not $drive.IsReady) {
    throw "Profile acceptance drive is not ready: $driveRoot"
}
$freeBytesAtStart = [long]$drive.AvailableFreeSpace
if ($freeBytesAtStart -lt $MinimumFreeBytes) {
    throw "Profile acceptance requires at least $MinimumFreeBytes free bytes; found $freeBytesAtStart on $driveRoot."
}

$profiles = @('quality', 'balanced', 'compact')
$releaseAssetNames = [Collections.Generic.List[string]]::new()
$releaseAssetNames.Add('bstrings-win-x64-offline-base.zip')
foreach ($profile in $profiles) {
    $releaseAssetNames.Add("airgap-config-$profile.json")
    $releaseAssetNames.Add("Hy-MT2-Apache-2.0-$profile.txt")
    $releaseAssetNames.Add("airgap-manifest-$profile.json")
    $releaseAssetNames.Add("bundle-packs-$profile.json")
}
$coreIdentity = Get-FileIdentityRow $resolvedCoreArchive 'bstrings-win-x64.zip'
$releaseAssets = @(
    Get-CheckedPackInventory $packRoot @($releaseAssetNames) @($coreIdentity)
)
$releaseAssetByName = @{}
foreach ($releaseAsset in $releaseAssets) {
    $releaseAssetByName[[string]$releaseAsset.fileName] = $releaseAsset
}
$checksumIdentity = Get-FileIdentityRow `
    (Join-Path $packRoot 'SHA256SUMS.txt') `
    'SHA256SUMS.txt'

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$coreRoot = Join-Path $workRoot 'core'
[IO.Compression.ZipFile]::ExtractToDirectory($resolvedCoreArchive, $coreRoot)
$coreExecutable = Resolve-PhysicalFile (Join-Path $coreRoot 'bstrings.exe') 'Core bstrings executable'
$childPowerShell = Resolve-PhysicalFile (Get-Process -Id $PID).Path 'Current PowerShell executable'
$results = [Collections.Generic.List[object]]::new()

foreach ($profile in $profiles) {
    $profileRoot = Join-Path $workRoot "profile-$profile"
    $cacheRoot = Join-Path $profileRoot 'pack-cache'
    $bundleRoot = Join-Path $profileRoot 'bundle'
    [IO.Directory]::CreateDirectory($cacheRoot) | Out-Null

    $trustManifest = Get-RequiredPack $packRoot "bundle-packs-$profile.json"
    $cacheInputs = [ordered]@{
        'bstrings-win-x64-offline-base.zip' = 'base.zip'
        "airgap-config-$profile.json" = 'configuration.file'
        "Hy-MT2-Apache-2.0-$profile.txt" = 'translation-license.file'
        "airgap-manifest-$profile.json" = 'airgap-manifest.file'
    }
    foreach ($sourceName in $cacheInputs.Keys) {
        $sourcePath = Get-RequiredPack $packRoot $sourceName
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $cacheRoot $cacheInputs[$sourceName])
    }

    & $coreExecutable bundle acquire `
        --manifest $trustManifest `
        --cache $cacheRoot `
        --output $bundleRoot
    if ($LASTEXITCODE -ne 0) {
        throw "$profile bundle acquisition and assembly failed with exit code $LASTEXITCODE."
    }

    $bundleExecutable = Resolve-PhysicalFile `
        (Join-Path $bundleRoot 'bstrings.exe') `
        "$profile assembled bstrings executable"
    & $bundleExecutable bundle verify --bundle-root $bundleRoot
    if ($LASTEXITCODE -ne 0) {
        throw "$profile assembled bundle verification failed with exit code $LASTEXITCODE."
    }

    $verifier = Resolve-PhysicalFile `
        (Join-Path $bundleRoot 'Verify-AirgapBundle.ps1') `
        "$profile runtime verifier"
    $smokeExitCode = 0
    Push-Location $bundleRoot
    try {
        & $childPowerShell `
            -NoLogo `
            -NoProfile `
            -NonInteractive `
            -File $verifier `
            -TranslationSmoke
        $smokeExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($smokeExitCode -ne 0) {
        throw "$profile translation smoke failed with exit code $smokeExitCode."
    }

    $trust = Get-Content -LiteralPath $trustManifest -Raw | ConvertFrom-Json
    $configurationPath = Join-Path $bundleRoot 'airgap-config.json'
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    if (
        [int]$trust.schemaVersion -ne 1 -or
        [string]$trust.profile -cne "windows-x64-offline-v2-$profile" -or
        [int]$configuration.schemaVersion -ne 1 -or
        [string]$configuration.translationProfile -cne $profile
    ) {
        throw "$profile acceptance resolved an unexpected trust manifest or bundle configuration."
    }
    $modelPacks = @($trust.packs | Where-Object { [string]$_.id -ceq 'translation-model' })
    if ($modelPacks.Count -ne 1) {
        throw "$profile trust manifest must contain exactly one translation-model pack."
    }
    $modelPack = $modelPacks[0]
    $configuredModelHash = ([string]$configuration.translationModel.sha256).ToLowerInvariant()
    $trustedModelHash = ([string]$modelPack.sha256).ToLowerInvariant()
    if (
        $configuredModelHash -notmatch '^[0-9a-f]{64}$' -or
        $configuredModelHash -cne $trustedModelHash -or
        [long]$modelPack.bytes -lt 1
    ) {
        throw "$profile translation model identity differs between trust and configuration."
    }
    $modelPath = Resolve-PhysicalFile `
        (Join-Path $bundleRoot ([string]$configuration.translationModel.path -replace '/', '\')) `
        "$profile translation model"
    $modelIdentity = Get-Item -LiteralPath $modelPath
    if (
        [long]$modelIdentity.Length -ne [long]$modelPack.bytes -or
        (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne
            $trustedModelHash
    ) {
        throw "$profile translation model bytes differ after acceptance."
    }

    $airgapManifestPath = Resolve-PhysicalFile `
        (Join-Path $bundleRoot 'airgap-manifest.json') `
        "$profile air-gap manifest"
    $results.Add([pscustomobject]@{
        profile = $profile
        status = 'passed'
        translationSmoke = 'passed'
        acceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        bundleIdentity = [string]$trust.bundleIdentity
        bundlePackTrustManifestBytes = [long]$releaseAssetByName["bundle-packs-$profile.json"].bytes
        bundlePackTrustManifestSha256 = (Get-FileHash -LiteralPath $trustManifest -Algorithm SHA256).Hash.ToLowerInvariant()
        airgapManifestBytes = [long]$releaseAssetByName["airgap-manifest-$profile.json"].bytes
        airgapManifestSha256 = (Get-FileHash -LiteralPath $airgapManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        configurationBytes = [long]$releaseAssetByName["airgap-config-$profile.json"].bytes
        configurationSha256 = [string]$releaseAssetByName["airgap-config-$profile.json"].sha256
        translationLicenseBytes = [long]$releaseAssetByName["Hy-MT2-Apache-2.0-$profile.txt"].bytes
        translationLicenseSha256 = [string]$releaseAssetByName["Hy-MT2-Apache-2.0-$profile.txt"].sha256
        translationModelId = [string]$configuration.translationModel.id
        translationModelRevision = [string]$configuration.translationModel.revision
        translationModelUrl = [string]$modelPack.url
        translationModelBytes = [long]$modelPack.bytes
        translationModelSha256 = $trustedModelHash
    })

    Remove-CompletedProfileDirectory $workRoot $profileRoot $profile
}

if ($results.Count -ne $profiles.Count) {
    throw "Profile acceptance completed $($results.Count) of $($profiles.Count) profiles."
}
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$evidencePath = Join-Path $evidenceRoot 'offline-profile-acceptance.json'
$record = [ordered]@{
    schemaVersion = 2
    status = 'passed'
    classification = 'synthetic-release-profile-acceptance'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sourceRepository = $SourceRepository
    sourceTag = $SourceTag
    sourceCommit = $SourceCommit.ToLowerInvariant()
    sourceBuildRunId = $SourceBuildRunId
    acceptanceRunAttempt = $SourceRunAttempt
    minimumFreeBytes = $MinimumFreeBytes
    freeBytesAtStart = $freeBytesAtStart
    coreArchiveBytes = [long]$coreIdentity.bytes
    coreArchiveSha256 = [string]$coreIdentity.sha256
    checksumFileBytes = [long]$checksumIdentity.bytes
    checksumFileSha256 = [string]$checksumIdentity.sha256
    advertisedProfiles = $profiles
    releaseAssets = $releaseAssets
    profiles = @($results)
}
[IO.File]::WriteAllText(
    $evidencePath,
    ($record | ConvertTo-Json -Depth 8) + "`n",
    [Text.UTF8Encoding]::new($false)
)
Write-Host "All advertised offline profiles passed acceptance: $evidencePath"
