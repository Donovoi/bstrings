[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,
    [Parameter(Mandatory = $true)]
    [string]$AssetDirectory,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedRepository,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedServerUrl,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedTag,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedCommit,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedBuildRunId,
    [Parameter(Mandatory = $true)]
    [string]$ComponentLockPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Get-FileIdentity([string]$Path, [string]$Name) {
    $resolved = Resolve-PhysicalFile $Path $Name
    $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
    return [pscustomobject]@{
        path = $resolved
        bytes = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Assert-NoDuplicateJsonProperties(
    [Text.Json.JsonElement]$Element,
    [string]$Name
) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $propertyNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal
        )
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $propertyNames.Add($property.Name)) {
                throw "$Name contains a duplicate JSON property: $($property.Name)"
            }
            Assert-NoDuplicateJsonProperties $property.Value $Name
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($child in $Element.EnumerateArray()) {
            Assert-NoDuplicateJsonProperties $child $Name
        }
    }
}

function Get-JsonDocument(
    [string]$Path,
    [string]$Name,
    [long]$MaximumBytes = 67108864
) {
    $resolved = Resolve-PhysicalFile $Path $Name
    $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
    if ([long]$item.Length -lt 2 -or [long]$item.Length -gt $MaximumBytes) {
        throw "$Name has an invalid byte length: $($item.Length)"
    }
    $text = [IO.File]::ReadAllText($resolved, [Text.UTF8Encoding]::new($false, $true))
    $jsonDocument = $null
    try {
        $jsonOptions = [Text.Json.JsonDocumentOptions]::new()
        $jsonOptions.AllowTrailingCommas = $false
        $jsonOptions.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
        $jsonOptions.MaxDepth = 100
        $jsonDocument = [Text.Json.JsonDocument]::Parse($text, $jsonOptions)
        Assert-NoDuplicateJsonProperties $jsonDocument.RootElement $Name
        return $text | ConvertFrom-Json -Depth 100 -ErrorAction Stop
    }
    catch {
        throw "$Name is not valid bounded JSON: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $jsonDocument) {
            $jsonDocument.Dispose()
        }
    }
}

function Get-PositiveInt64([object]$Value, [string]$Name) {
    if (-not ($Value -is [int] -or $Value -is [long])) {
        throw "$Name must be a JSON integer."
    }
    $number = [long]$Value
    if ($number -lt 1 -or $number -eq [long]::MaxValue) {
        throw "$Name must be a bounded positive integer."
    }
    return $number
}

function Get-NonNegativeInt64([object]$Value, [string]$Name) {
    if (-not ($Value -is [int] -or $Value -is [long])) {
        throw "$Name must be a JSON integer."
    }
    $number = [long]$Value
    if ($number -lt 0 -or $number -eq [long]::MaxValue) {
        throw "$Name must be a bounded non-negative integer."
    }
    return $number
}

function Get-LowerSha256([object]$Value, [string]$Name) {
    $hash = [string]$Value
    if ($hash -notmatch '^[0-9a-f]{64}$') {
        throw "$Name must be a lowercase SHA-256."
    }
    return $hash
}

function Assert-SafeLeafName([string]$Value, [string]$Name) {
    if (
        [string]::IsNullOrWhiteSpace($Value) -or
        [IO.Path]::IsPathRooted($Value) -or
        [IO.Path]::GetFileName($Value) -cne $Value -or
        $Value.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
    ) {
        throw "$Name is not a safe file name: '$Value'"
    }
}

function Assert-SafeManifestPath([string]$Value, [string]$Name) {
    if (
        [string]::IsNullOrWhiteSpace($Value) -or
        $Value.Contains('\') -or
        $Value.StartsWith('/', [StringComparison]::Ordinal) -or
        $Value.EndsWith('/', [StringComparison]::Ordinal) -or
        $Value.Contains('//')
    ) {
        throw "$Name is not a canonical relative path: '$Value'"
    }
    foreach ($segment in $Value.Split('/')) {
        if ($segment -in @('.', '..') -or [string]::IsNullOrWhiteSpace($segment)) {
            throw "$Name contains an unsafe path segment: '$Value'"
        }
    }
}

function Assert-Identity(
    [object]$Actual,
    [object]$ExpectedBytes,
    [object]$ExpectedSha256,
    [string]$Name
) {
    $bytes = Get-PositiveInt64 $ExpectedBytes "$Name bytes"
    $hash = Get-LowerSha256 $ExpectedSha256 "$Name SHA-256"
    if ([long]$Actual.bytes -ne $bytes -or [string]$Actual.sha256 -cne $hash) {
        throw "$Name does not match its checked release evidence."
    }
}

function Get-RequiredMapValue(
    [Collections.Generic.Dictionary[string, object]]$Map,
    [string]$Key,
    [string]$Name
) {
    $value = $null
    if (-not $Map.TryGetValue($Key, [ref]$value)) {
        throw "$Name is missing: $Key"
    }
    return $value
}

function Get-RequiredChecksum(
    [Collections.Generic.Dictionary[string, string]]$Map,
    [string]$Key
) {
    $value = $null
    if (-not $Map.TryGetValue($Key, [ref]$value)) {
        throw "SHA256SUMS.txt is missing release asset: $Key"
    }
    return $value
}

function Assert-ExactReleaseAssetUrl(
    [object]$Value,
    [string]$ExpectedUrl,
    [string]$Name
) {
    $url = [string]$Value
    $uri = $null
    if (
        -not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)
    ) {
        throw "$Name must be an exact HTTPS release URL without credentials, a query, or a fragment."
    }
    if ($url -cne $ExpectedUrl) {
        throw "$Name is not bound to the exact expected server, repository, tag, path, and casing."
    }
}

function Assert-ExactModelUrl(
    [object]$Value,
    [string]$ExpectedUrl,
    [string]$Name
) {
    $url = [string]$Value
    $uri = $null
    if (
        -not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)
    ) {
        throw "$Name must be the exact checked HTTPS model URL."
    }
    if ($url -cne $ExpectedUrl) {
        throw "$Name differs from the exact checked component-lock URL."
    }
}

if ($ExpectedRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'ExpectedRepository must be an owner/repository name.'
}
if ($ExpectedTag -notmatch '^v[A-Za-z0-9][A-Za-z0-9._+-]*$') {
    throw 'ExpectedTag must be a canonical version release tag.'
}
if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'ExpectedCommit must be a full Git commit SHA.'
}
if ($ExpectedBuildRunId -notmatch '^[1-9][0-9]*$') {
    throw 'ExpectedBuildRunId must be a positive GitHub Actions run ID.'
}
$serverUri = $null
if (
    -not [Uri]::TryCreate($ExpectedServerUrl, [UriKind]::Absolute, [ref]$serverUri) -or
    $serverUri.Scheme -cne 'https' -or
    -not [string]::IsNullOrEmpty($serverUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($serverUri.Query) -or
    -not [string]::IsNullOrEmpty($serverUri.Fragment) -or
    $serverUri.AbsolutePath -cne '/'
) {
    throw 'ExpectedServerUrl must be an HTTPS server origin without credentials, a path, query, or fragment.'
}
$releaseAssetBaseUrl = $ExpectedServerUrl.TrimEnd('/') + '/' +
    $ExpectedRepository + '/releases/download/' + $ExpectedTag
$resolvedComponentLock = Resolve-PhysicalFile $ComponentLockPath 'Offline component lock'
$componentLock = Get-JsonDocument $resolvedComponentLock 'Offline component lock' 4194304
if (
    (Get-PositiveInt64 $componentLock.schemaVersion 'Offline component lock schemaVersion') -ne 1 -or
    [string]$componentLock.profile -cne 'windows-x64-offline-v2' -or
    [string]$componentLock.defaultTranslationProfile -cne 'quality'
) {
    throw 'Offline component lock does not use the exact reviewed release profile.'
}
$lockedProfileNames = @($componentLock.translationProfiles.PSObject.Properties.Name)
if ((@($lockedProfileNames | Sort-Object) -join '|') -cne 'balanced|compact|quality') {
    throw 'Offline component lock does not contain the exact translation profile set.'
}

$assetRoot = Resolve-PhysicalDirectory $AssetDirectory 'Release asset directory'
$resolvedEvidence = Resolve-PhysicalFile $EvidencePath 'Offline profile acceptance evidence'
if ([IO.Path]::GetFileName($resolvedEvidence) -cne 'offline-profile-acceptance.json') {
    throw 'Offline profile acceptance evidence must use its exact internal gate-evidence file name.'
}

$profiles = @('quality', 'balanced', 'compact')
$packAssetNames = [Collections.Generic.List[string]]::new()
$packAssetNames.Add('bstrings-win-x64-offline-base.zip')
foreach ($profile in $profiles) {
    $packAssetNames.Add("airgap-config-$profile.json")
    $packAssetNames.Add("Hy-MT2-Apache-2.0-$profile.txt")
    $packAssetNames.Add("airgap-manifest-$profile.json")
    $packAssetNames.Add("bundle-packs-$profile.json")
}
$expectedFileNames = @(
    'bstrings-win-x64.zip'
    @($packAssetNames)
    'SHA256SUMS.txt'
)
$entries = @(Get-ChildItem -LiteralPath $assetRoot -Force -ErrorAction Stop)
foreach ($entry in $entries) {
    if (
        $entry.PSIsContainer -or
        ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "Release asset directory contains a linked or non-file entry: $($entry.FullName)"
    }
}
if (
    (@($entries.Name | Sort-Object) -join '|') -cne
    (@($expectedFileNames | Sort-Object) -join '|')
) {
    throw 'Release asset directory does not contain the exact expected file set.'
}

$actualByName = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($entry in $entries) {
    if ($actualByName.ContainsKey($entry.Name)) {
        throw "Duplicate release asset file name: $($entry.Name)"
    }
    $actualByName.Add(
        $entry.Name,
        (Get-FileIdentity $entry.FullName "Release asset $($entry.Name)")
    )
}

$record = Get-JsonDocument $resolvedEvidence 'Offline profile acceptance evidence' 4194304
if (
    (Get-PositiveInt64 $record.schemaVersion 'Acceptance evidence schemaVersion') -ne 2 -or
    [string]$record.status -cne 'passed' -or
    [string]$record.classification -cne 'synthetic-release-profile-acceptance' -or
    [string]$record.sourceRepository -cne $ExpectedRepository -or
    [string]$record.sourceTag -cne $ExpectedTag -or
    [string]$record.sourceCommit -cne $ExpectedCommit.ToLowerInvariant() -or
    [string]$record.sourceBuildRunId -cne $ExpectedBuildRunId
) {
    throw 'Profile-acceptance evidence is not bound to this exact repository, tag, commit, and build run.'
}
if ([string]$record.acceptanceRunAttempt -notmatch '^[1-9][0-9]*$') {
    throw 'Profile-acceptance evidence must record a positive acceptance run attempt.'
}
$advertisedProfiles = @($record.advertisedProfiles)
if (($advertisedProfiles -join '|') -cne ($profiles -join '|')) {
    throw 'Profile-acceptance evidence does not advertise the exact ordered profile set.'
}

$coreActual = Get-RequiredMapValue $actualByName 'bstrings-win-x64.zip' 'Release assets'
Assert-Identity `
    $coreActual `
    $record.coreArchiveBytes `
    $record.coreArchiveSha256 `
    'Core release archive'
$checksumActual = Get-RequiredMapValue $actualByName 'SHA256SUMS.txt' 'Release assets'
Assert-Identity `
    $checksumActual `
    $record.checksumFileBytes `
    $record.checksumFileSha256 `
    'SHA256SUMS.txt'

$releaseAssetRows = @($record.releaseAssets)
if ($releaseAssetRows.Count -ne $packAssetNames.Count) {
    throw 'Profile-acceptance evidence does not contain the exact release-pack asset count.'
}
$evidenceAssetByName = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($row in $releaseAssetRows) {
    $fileName = [string]$row.fileName
    Assert-SafeLeafName $fileName 'Release evidence fileName'
    if (-not $evidenceAssetByName.TryAdd($fileName, $row)) {
        throw "Duplicate release evidence asset row: $fileName"
    }
}
if (
    (@($evidenceAssetByName.Keys | Sort-Object) -join '|') -cne
    (@($packAssetNames | Sort-Object) -join '|')
) {
    throw 'Profile-acceptance evidence does not contain the exact release-pack asset set.'
}
foreach ($fileName in $packAssetNames) {
    $expected = Get-RequiredMapValue $evidenceAssetByName $fileName 'Release evidence assets'
    if ([string]$expected.fileName -cne $fileName) {
        throw "Release evidence asset casing differs from its required name: $fileName"
    }
    $actual = Get-RequiredMapValue $actualByName $fileName 'Release assets'
    Assert-Identity $actual $expected.bytes $expected.sha256 "Release asset $fileName"
}

$checksumPath = [string]$checksumActual.path
if ([long]$checksumActual.bytes -gt 1048576 -or [long]$checksumActual.bytes -lt 1) {
    throw 'SHA256SUMS.txt must have a bounded non-empty byte length.'
}
$checksumText = [IO.File]::ReadAllText(
    $checksumPath,
    [Text.UTF8Encoding]::new($false, $true)
)
if (-not $checksumText.EndsWith("`n", [StringComparison]::Ordinal) -or $checksumText.Contains("`r")) {
    throw 'SHA256SUMS.txt must use canonical LF-terminated rows.'
}
$checksumRows = @($checksumText.Substring(0, $checksumText.Length - 1) -split "`n")
$checksummedAssetNames = @('bstrings-win-x64.zip') + @($packAssetNames)
if ($checksumRows.Count -ne $checksummedAssetNames.Count) {
    throw 'SHA256SUMS.txt does not contain exactly one row per checksummed public release asset.'
}
$checksumByName = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($row in $checksumRows) {
    if ($row -notmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^/\\]+)$') {
        throw "Invalid SHA256SUMS.txt row: $row"
    }
    if (-not $checksumByName.TryAdd([string]$Matches.name, [string]$Matches.hash)) {
        throw "Duplicate SHA256SUMS.txt row: $($Matches.name)"
    }
}
if (
    (@($checksumByName.Keys | Sort-Object) -join '|') -cne
    (@($checksummedAssetNames | Sort-Object) -join '|')
) {
    throw 'SHA256SUMS.txt does not name the exact checksummed public release-asset set.'
}
$coreChecksumHash = Get-RequiredChecksum $checksumByName 'bstrings-win-x64.zip'
if (
    $coreChecksumHash -cne [string]$coreActual.sha256 -or
    $coreChecksumHash -cne (Get-LowerSha256 $record.coreArchiveSha256 'Core archive SHA-256')
) {
    throw 'SHA256SUMS.txt differs from the core asset and acceptance-evidence hash.'
}
foreach ($fileName in $packAssetNames) {
    $checksumHash = Get-RequiredChecksum $checksumByName $fileName
    $actual = Get-RequiredMapValue $actualByName $fileName 'Release assets'
    $expected = Get-RequiredMapValue $evidenceAssetByName $fileName 'Release evidence assets'
    if (
        $checksumHash -cne [string]$actual.sha256 -or
        $checksumHash -cne [string]$expected.sha256
    ) {
        throw "SHA256SUMS.txt differs from the asset and evidence hash: $fileName"
    }
}

$profileRows = @($record.profiles)
if ($profileRows.Count -ne $profiles.Count) {
    throw 'Profile-acceptance evidence must contain exactly three profile results.'
}
$profileByName = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($row in $profileRows) {
    $profileName = [string]$row.profile
    if (-not $profileByName.TryAdd($profileName, $row)) {
        throw "Duplicate profile-acceptance evidence row: $profileName"
    }
}
if ((@($profileRows.profile) -join '|') -cne ($profiles -join '|')) {
    throw 'Profile-acceptance evidence does not contain the exact ordered profile rows.'
}

$baseName = 'bstrings-win-x64-offline-base.zip'
$baseEvidence = Get-RequiredMapValue $evidenceAssetByName $baseName 'Release evidence assets'
foreach ($profile in $profiles) {
    $profileEvidence = Get-RequiredMapValue $profileByName $profile 'Profile evidence'
    if (
        [string]$profileEvidence.profile -cne $profile -or
        [string]$profileEvidence.status -cne 'passed' -or
        [string]$profileEvidence.translationSmoke -cne 'passed' -or
        [string]::IsNullOrWhiteSpace([string]$profileEvidence.bundleIdentity)
    ) {
        throw "Profile-acceptance evidence is incomplete for $profile."
    }

    $trustName = "bundle-packs-$profile.json"
    $manifestName = "airgap-manifest-$profile.json"
    $configurationName = "airgap-config-$profile.json"
    $licenseName = "Hy-MT2-Apache-2.0-$profile.txt"
    $trustEvidence = Get-RequiredMapValue $evidenceAssetByName $trustName 'Release evidence assets'
    $manifestEvidence = Get-RequiredMapValue $evidenceAssetByName $manifestName 'Release evidence assets'
    $configurationEvidence = Get-RequiredMapValue $evidenceAssetByName $configurationName 'Release evidence assets'
    $licenseEvidence = Get-RequiredMapValue $evidenceAssetByName $licenseName 'Release evidence assets'

    Assert-Identity `
        (Get-RequiredMapValue $actualByName $trustName 'Release assets') `
        $profileEvidence.bundlePackTrustManifestBytes `
        $profileEvidence.bundlePackTrustManifestSha256 `
        "$profile trust manifest"
    Assert-Identity `
        (Get-RequiredMapValue $actualByName $manifestName 'Release assets') `
        $profileEvidence.airgapManifestBytes `
        $profileEvidence.airgapManifestSha256 `
        "$profile final manifest"
    Assert-Identity `
        (Get-RequiredMapValue $actualByName $configurationName 'Release assets') `
        $profileEvidence.configurationBytes `
        $profileEvidence.configurationSha256 `
        "$profile configuration"
    Assert-Identity `
        (Get-RequiredMapValue $actualByName $licenseName 'Release assets') `
        $profileEvidence.translationLicenseBytes `
        $profileEvidence.translationLicenseSha256 `
        "$profile translation license"
    if (
        [string]$profileEvidence.bundlePackTrustManifestSha256 -cne [string]$trustEvidence.sha256 -or
        [string]$profileEvidence.airgapManifestSha256 -cne [string]$manifestEvidence.sha256 -or
        [string]$profileEvidence.configurationSha256 -cne [string]$configurationEvidence.sha256 -or
        [string]$profileEvidence.translationLicenseSha256 -cne [string]$licenseEvidence.sha256
    ) {
        throw "$profile profile evidence differs from the release asset inventory."
    }

    $modelId = [string]$profileEvidence.translationModelId
    $modelRevision = [string]$profileEvidence.translationModelRevision
    $modelUrl = [string]$profileEvidence.translationModelUrl
    $modelBytes = Get-PositiveInt64 `
        $profileEvidence.translationModelBytes `
        "$profile translation model bytes"
    $modelHash = Get-LowerSha256 `
        $profileEvidence.translationModelSha256 `
        "$profile translation model SHA-256"
    if ([string]::IsNullOrWhiteSpace($modelId) -or $modelRevision -notmatch '^[0-9a-f]{40}$') {
        throw "$profile translation model ID or revision is invalid."
    }
    $lockedModel = $componentLock.translationProfiles.$profile
    $lockedModelFileName = [string]$lockedModel.fileName
    Assert-SafeLeafName $lockedModelFileName "$profile locked translation model file name"
    if (
        [string]$lockedModel.archiveType -cne 'file' -or
        [string]$lockedModel.modelId -cne $modelId -or
        [string]$lockedModel.revision -cne $modelRevision -or
        (Get-PositiveInt64 $lockedModel.bytes "$profile locked translation model bytes") -ne $modelBytes -or
        (Get-LowerSha256 $lockedModel.sha256 "$profile locked translation model SHA-256") -cne $modelHash -or
        [string]$lockedModel.url -cne $modelUrl
    ) {
        throw "$profile translation model evidence differs from the exact checked component lock."
    }
    if ($modelId -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "$profile locked translation model ID is not a canonical Hugging Face repository ID."
    }
    $canonicalModelUrl = 'https://huggingface.co/' + $modelId + '/resolve/' +
        $modelRevision + '/' + [Uri]::EscapeDataString($lockedModelFileName) + '?download=true'
    Assert-ExactModelUrl $modelUrl $canonicalModelUrl "$profile evidence model URL"

    $configuration = Get-JsonDocument `
        (Join-Path $assetRoot $configurationName) `
        "$profile air-gap configuration"
    if (
        (Get-PositiveInt64 $configuration.schemaVersion "$profile configuration schemaVersion") -ne 1 -or
        [string]$configuration.translationProfile -cne $profile -or
        [string]$configuration.translationModel.id -cne $modelId -or
        [string]$configuration.translationModel.revision -cne $modelRevision -or
        [string]$configuration.translationModel.sha256 -cne $modelHash
    ) {
        throw "$profile configuration translation identity differs from checked evidence."
    }
    $modelPath = [string]$configuration.translationModel.path
    Assert-SafeManifestPath $modelPath "$profile translation model path"
    if (-not $modelPath.StartsWith('models/hy-mt2/', [StringComparison]::Ordinal)) {
        throw "$profile translation model path is outside models/hy-mt2."
    }
    $modelLeaf = $modelPath.Split('/')[-1]
    Assert-SafeLeafName $modelLeaf "$profile translation model file name"
    if ($modelLeaf -cne $lockedModelFileName) {
        throw "$profile configuration model file name differs from the exact checked component lock."
    }

    $trust = Get-JsonDocument (Join-Path $assetRoot $trustName) "$profile trust manifest"
    if (
        (Get-PositiveInt64 $trust.schemaVersion "$profile trust schemaVersion") -ne 1 -or
        [string]$trust.profile -cne "windows-x64-offline-v2-$profile" -or
        [string]$trust.bundleIdentity -cne [string]$profileEvidence.bundleIdentity -or
        [string]$trust.airgapManifestSha256 -cne [string]$manifestEvidence.sha256
    ) {
        throw "$profile trust manifest identity differs from checked evidence."
    }
    $trustPacks = @($trust.packs)
    if ($trustPacks.Count -ne 5) {
        throw "$profile trust manifest must contain exactly five packs."
    }
    $trustById = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal
    )
    foreach ($pack in $trustPacks) {
        $packId = [string]$pack.id
        if (-not $trustById.TryAdd($packId, $pack)) {
            throw "$profile trust manifest contains duplicate pack ID: $packId"
        }
    }
    if ((@($trustById.Keys | Sort-Object) -join '|') -cne 'airgap-manifest|base|configuration|translation-license|translation-model') {
        throw "$profile trust manifest does not contain the exact pack ID set."
    }

    $assetPackChecks = @(
        [pscustomobject]@{
            id = 'base'; fileName = $baseName; target = $null; evidence = $baseEvidence
        }
        [pscustomobject]@{
            id = 'configuration'; fileName = $configurationName; target = 'airgap-config.json'; evidence = $configurationEvidence
        }
        [pscustomobject]@{
            id = 'translation-license'; fileName = $licenseName; target = 'licenses/Hy-MT2-Apache-2.0.txt'; evidence = $licenseEvidence
        }
        [pscustomobject]@{
            id = 'airgap-manifest'; fileName = $manifestName; target = 'airgap-manifest.json'; evidence = $manifestEvidence
        }
    )
    foreach ($check in $assetPackChecks) {
        $pack = Get-RequiredMapValue $trustById $check.id "$profile trust packs"
        $packBytes = Get-PositiveInt64 $pack.bytes "$profile $($check.id) pack bytes"
        $packHash = Get-LowerSha256 $pack.sha256 "$profile $($check.id) pack SHA-256"
        if (
            $packBytes -ne [long]$check.evidence.bytes -or
            $packHash -cne [string]$check.evidence.sha256
        ) {
            throw "$profile $($check.id) trust pack differs from checked release evidence."
        }
        $expectedReleaseUrl = $releaseAssetBaseUrl + '/' +
            [Uri]::EscapeDataString([string]$check.fileName)
        Assert-ExactReleaseAssetUrl `
            $pack.url `
            $expectedReleaseUrl `
            "$profile $($check.id) pack URL"
        if ($null -ne $check.target) {
            if ([string]$pack.kind -cne 'file' -or [string]$pack.target -cne [string]$check.target) {
                throw "$profile $($check.id) trust pack has an unexpected kind or target."
            }
        }
    }

    $modelPack = Get-RequiredMapValue $trustById 'translation-model' "$profile trust packs"
    if (
        [string]$modelPack.kind -cne 'file' -or
        [string]$modelPack.target -cne $modelPath -or
        (Get-PositiveInt64 $modelPack.bytes "$profile trusted model bytes") -ne $modelBytes -or
        (Get-LowerSha256 $modelPack.sha256 "$profile trusted model SHA-256") -cne $modelHash
    ) {
        throw "$profile trusted translation model differs from configuration and acceptance evidence."
    }
    if ([string]$modelPack.url -cne $modelUrl) {
        throw "$profile trust model URL differs from checked acceptance evidence."
    }
    Assert-ExactModelUrl $modelPack.url $canonicalModelUrl "$profile translation model URL"

    $manifest = Get-JsonDocument (Join-Path $assetRoot $manifestName) "$profile final manifest"
    if ((Get-PositiveInt64 $manifest.schemaVersion "$profile final manifest schemaVersion") -ne 1) {
        throw "$profile final manifest has an unexpected schema."
    }
    $manifestRows = @($manifest.files)
    if ($manifestRows.Count -lt 3 -or $manifestRows.Count -gt 250000) {
        throw "$profile final manifest has an invalid file count."
    }
    $manifestByPath = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($manifestRow in $manifestRows) {
        $relativePath = [string]$manifestRow.path
        Assert-SafeManifestPath $relativePath "$profile final manifest path"
        Get-NonNegativeInt64 $manifestRow.bytes "$profile final manifest bytes for $relativePath" | Out-Null
        Get-LowerSha256 $manifestRow.sha256 "$profile final manifest SHA-256 for $relativePath" | Out-Null
        if (-not $manifestByPath.TryAdd($relativePath, $manifestRow)) {
            throw "$profile final manifest contains a duplicate path: $relativePath"
        }
    }
    $requiredManifestRows = @(
        [pscustomobject]@{
            path = 'airgap-config.json'; bytes = $configurationEvidence.bytes; sha256 = $configurationEvidence.sha256
        }
        [pscustomobject]@{
            path = 'licenses/Hy-MT2-Apache-2.0.txt'; bytes = $licenseEvidence.bytes; sha256 = $licenseEvidence.sha256
        }
        [pscustomobject]@{
            path = $modelPath; bytes = $modelBytes; sha256 = $modelHash
        }
    )
    foreach ($required in $requiredManifestRows) {
        $manifestRow = Get-RequiredMapValue $manifestByPath $required.path "$profile final manifest"
        if (
            [string]$manifestRow.path -cne [string]$required.path -or
            [long]$manifestRow.bytes -ne [long]$required.bytes -or
            [string]$manifestRow.sha256 -cne [string]$required.sha256
        ) {
            throw "$profile final manifest identity differs for $($required.path)."
        }
    }
}

Write-Host 'Offline profile release evidence and exact release assets passed validation.'
