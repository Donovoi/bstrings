[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseAssetBaseUrl,
    [string]$ComponentLockPath,
    [switch]$SkipAssemblyTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ComponentLockPath)) {
    $ComponentLockPath = Join-Path $PSScriptRoot 'offline-components.lock.json'
}
if (-not (Test-Path -LiteralPath $BundleDirectory -PathType Container)) {
    throw "Complete bundle was not found: $BundleDirectory"
}
if (-not (Test-Path -LiteralPath $ComponentLockPath -PathType Leaf)) {
    throw "Offline component lock was not found: $ComponentLockPath"
}
$bundleRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $BundleDirectory).Path)
$lockPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ComponentLockPath).Path)
$lockItem = Get-Item -LiteralPath $lockPath -Force
if (($lockItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Offline component lock must not be a link or reparse point: $lockPath"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Split-pack output must not already exist: $output"
}
$baseUri = $null
if (
    -not [Uri]::TryCreate($ReleaseAssetBaseUrl.TrimEnd('/'), [UriKind]::Absolute, [ref]$baseUri) -or
    $baseUri.Scheme -ne 'https' -or
    -not [string]::IsNullOrEmpty($baseUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($baseUri.Query) -or
    -not [string]::IsNullOrEmpty($baseUri.Fragment)
) {
    throw 'ReleaseAssetBaseUrl must be an absolute HTTPS directory URL without credentials, a query, or a fragment.'
}
$bundlePrefix = $bundleRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
) + [IO.Path]::DirectorySeparatorChar
if ($output.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Split-pack output must not be created inside the verified complete bundle.'
}

function Assert-NoReparsePoints([string]$Root, [string]$Name) {
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $Root"
    }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Name contains a link or reparse point: $($item.FullName)"
        }
    }
}

function Get-FileIdentity([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    return [ordered]@{
        bytes = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Write-JsonFile([string]$Path, [object]$Value, [int]$Depth = 10) {
    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, $json + "`n", [Text.UTF8Encoding]::new($false))
}

function Get-AssetUrl([string]$FileName) {
    return $script:baseUri.AbsoluteUri.TrimEnd('/') + '/' + [Uri]::EscapeDataString($FileName)
}

function Assert-SafeLeafName([string]$Value, [string]$Name) {
    if (
        [string]::IsNullOrWhiteSpace($Value) -or
        [IO.Path]::IsPathRooted($Value) -or
        [IO.Path]::GetFileName($Value) -ne $Value -or
        $Value.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
    ) {
        throw "$Name is not a safe file name: '$Value'"
    }
}

function Assert-LockedFileSpec([object]$Spec, [string]$Name) {
    if ($null -eq $Spec) {
        throw "$Name is missing from the component lock."
    }
    Assert-SafeLeafName ([string]$Spec.fileName) "$Name fileName"
    if ([long]$Spec.bytes -lt 1 -or [long]$Spec.bytes -eq [long]::MaxValue) {
        throw "$Name must have a bounded positive byte length."
    }
    if ([string]$Spec.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "$Name must have a lowercase SHA-256."
    }
    $uri = $null
    if (
        -not [Uri]::TryCreate([string]$Spec.url, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)
    ) {
        throw "$Name must have an absolute HTTPS URL without credentials or a fragment."
    }
}

Assert-NoReparsePoints $bundleRoot 'Complete bundle'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if (
    [int]$lock.schemaVersion -ne 1 -or
    [string]$lock.profile -ne 'windows-x64-offline-v2' -or
    [string]$lock.defaultTranslationProfile -ne 'quality'
) {
    throw 'Offline component lock does not use the reviewed split-pack profile.'
}
$profileNames = @($lock.translationProfiles.PSObject.Properties.Name)
if ((@($profileNames | Sort-Object) -join '|') -cne 'balanced|compact|quality') {
    throw 'Offline component lock must contain exactly quality, balanced, and compact profiles.'
}
foreach ($profileName in $profileNames) {
    $profile = $lock.translationProfiles.$profileName
    Assert-LockedFileSpec $profile "$profileName translation model"
    if (
        [string]$profile.archiveType -ne 'file' -or
        [string]::IsNullOrWhiteSpace([string]$profile.modelId) -or
        [string]$profile.revision -notmatch '^[0-9a-f]{40}$'
    ) {
        throw "$profileName translation model metadata is incomplete."
    }
    Assert-LockedFileSpec $profile.license "$profileName translation license"
    if ([string]$profile.license.source -ne 'download') {
        throw "$profileName translation license must be a locked download."
    }
}
$configurationPath = Join-Path $bundleRoot 'airgap-config.json'
$manifestPath = Join-Path $bundleRoot 'airgap-manifest.json'
$bstringsPath = Join-Path $bundleRoot 'bstrings.exe'
foreach ($required in @($configurationPath, $manifestPath, $bstringsPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Complete bundle is missing a required file: $required"
    }
}
& $bstringsPath bundle verify --bundle-root $bundleRoot
if ($LASTEXITCODE -ne 0) {
    throw 'Complete input bundle failed native manifest verification.'
}
$templateConfiguration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
$templateProfile = [string]$templateConfiguration.translationProfile
if ($templateProfile -notin $profileNames) {
    throw "Complete input bundle has an unknown translation profile: $templateProfile"
}
$templateModel = $lock.translationProfiles.$templateProfile
if (
    [string]$templateConfiguration.translationModel.id -ne [string]$templateModel.modelId -or
    [string]$templateConfiguration.translationModel.revision -ne [string]$templateModel.revision -or
    [string]$templateConfiguration.translationModel.sha256 -ne [string]$templateModel.sha256
) {
    throw 'Complete input bundle translation identity does not match its selected profile.'
}
$templateModelPath = Join-Path $bundleRoot (
    ([string]$templateConfiguration.translationModel.path) -replace '/', '\'
)
if (-not (Test-Path -LiteralPath $templateModelPath -PathType Leaf)) {
    throw "Complete input bundle is missing its translation model: $templateModelPath"
}
$templateModelIdentity = Get-FileIdentity $templateModelPath
if (
    $templateModelIdentity.bytes -ne [long]$templateModel.bytes -or
    $templateModelIdentity.sha256 -ne [string]$templateModel.sha256
) {
    throw 'Complete input bundle translation model does not match its lock.'
}

$originalManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([int]$originalManifest.schemaVersion -ne 1 -or @($originalManifest.files).Count -lt 1) {
    throw 'Complete input bundle has an invalid air-gap manifest.'
}
$modelTargets = @(
    $profileNames | ForEach-Object { "models/hy-mt2/$($lock.translationProfiles.$_.fileName)" }
)
$baseExclusions = @(
    'airgap-config.json',
    'airgap-manifest.json',
    'licenses/Hy-MT2-Apache-2.0.txt'
) + $modelTargets
$baseRows = @(
    $originalManifest.files | Where-Object { [string]$_.path -notin $baseExclusions }
)
if ($baseRows.Count -ge @($originalManifest.files).Count) {
    throw 'Split-pack base exclusions did not remove the template model and profile files.'
}

[IO.Directory]::CreateDirectory($output) | Out-Null
$incompleteMarker = Join-Path $output '.incomplete'
[IO.File]::WriteAllText(
    $incompleteMarker,
    "bstrings split-pack release generation is incomplete.`n",
    [Text.UTF8Encoding]::new($false)
)
$baseArchiveName = 'bstrings-win-x64-offline-base.zip'
$baseArchivePath = Join-Path $output $baseArchiveName
Add-Type -AssemblyName System.IO.Compression
$archiveStream = $null
$archive = $null
try {
    $archiveStream = [IO.FileStream]::new(
        $baseArchivePath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None
    )
    $archive = [IO.Compression.ZipArchive]::new(
        $archiveStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $false,
        [Text.Encoding]::UTF8
    )
    foreach ($row in @($baseRows | Sort-Object { [string]$_.path })) {
        $relative = [string]$row.path
        $source = Join-Path $bundleRoot ($relative -replace '/', '\')
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Base pack source is missing: $relative"
        }
        $identity = Get-FileIdentity $source
        if ($identity.bytes -ne [long]$row.bytes -or $identity.sha256 -ne [string]$row.sha256) {
            throw "Base pack source changed after complete-bundle verification: $relative"
        }
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::NoCompression)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $input = $null
        $entryOutput = $null
        try {
            $input = [IO.File]::OpenRead($source)
            $entryOutput = $entry.Open()
            $input.CopyTo($entryOutput, 1024 * 1024)
        }
        finally {
            if ($null -ne $entryOutput) { $entryOutput.Dispose() }
            if ($null -ne $input) { $input.Dispose() }
        }
    }
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $archiveStream) { $archiveStream.Dispose() }
}
$baseIdentity = Get-FileIdentity $baseArchivePath
if ($baseIdentity.bytes -lt 1 -or $baseIdentity.bytes -ge 2000000000) {
    throw "Base ZIP must be between 1 and 1,999,999,999 bytes; found $($baseIdentity.bytes)."
}

$licenseSources = @{
    quality = 'licenses/Hy-MT2-7B-Apache-2.0.txt'
    balanced = 'licenses/Hy-MT2-1.8B-Apache-2.0.txt'
    compact = 'licenses/Hy-MT2-1.8B-Apache-2.0.txt'
}
$results = [Collections.Generic.List[object]]::new()
foreach ($profileName in @('quality', 'balanced', 'compact')) {
    $profile = $lock.translationProfiles.$profileName
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    $configuration.translationProfile = $profileName
    $configuration.translationModel.path = "models/hy-mt2/$($profile.fileName)"
    $configuration.translationModel.id = [string]$profile.modelId
    $configuration.translationModel.revision = [string]$profile.revision
    $configuration.translationModel.sha256 = [string]$profile.sha256
    $configurationName = "airgap-config-$profileName.json"
    $configurationOutput = Join-Path $output $configurationName
    Write-JsonFile $configurationOutput $configuration 10
    $configurationIdentity = Get-FileIdentity $configurationOutput

    $licenseSource = Join-Path $bundleRoot ($licenseSources[$profileName] -replace '/', '\')
    if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) {
        throw "Complete bundle lacks the $profileName translation license source: $licenseSource"
    }
    $licenseSourceIdentity = Get-FileIdentity $licenseSource
    if (
        $licenseSourceIdentity.bytes -ne [long]$profile.license.bytes -or
        $licenseSourceIdentity.sha256 -ne [string]$profile.license.sha256
    ) {
        throw "$profileName translation license source does not match its lock."
    }
    $licenseName = "Hy-MT2-Apache-2.0-$profileName.txt"
    $licenseOutput = Join-Path $output $licenseName
    Copy-Item -LiteralPath $licenseSource -Destination $licenseOutput
    $licenseIdentity = Get-FileIdentity $licenseOutput

    $rows = [Collections.Generic.List[object]]::new()
    foreach ($row in $baseRows) {
        $rows.Add([ordered]@{
            path = [string]$row.path
            bytes = [long]$row.bytes
            sha256 = [string]$row.sha256
        })
    }
    $rows.Add([ordered]@{
        path = 'airgap-config.json'
        bytes = $configurationIdentity.bytes
        sha256 = $configurationIdentity.sha256
    })
    $rows.Add([ordered]@{
        path = 'licenses/Hy-MT2-Apache-2.0.txt'
        bytes = $licenseIdentity.bytes
        sha256 = $licenseIdentity.sha256
    })
    $rows.Add([ordered]@{
        path = "models/hy-mt2/$($profile.fileName)"
        bytes = [long]$profile.bytes
        sha256 = [string]$profile.sha256
    })
    $manifestDocument = [ordered]@{
        schemaVersion = 1
        files = @($rows | Sort-Object { [string]$_.path })
    }
    $profileManifestName = "airgap-manifest-$profileName.json"
    $profileManifestOutput = Join-Path $output $profileManifestName
    Write-JsonFile $profileManifestOutput $manifestDocument 6
    $profileManifestIdentity = Get-FileIdentity $profileManifestOutput

    $trustManifestName = "bundle-packs-$profileName.json"
    $trustManifestOutput = Join-Path $output $trustManifestName
    $trustManifest = [ordered]@{
        schemaVersion = 1
        profile = "windows-x64-offline-v2-$profileName"
        bundleIdentity = "bstrings-$profileName-$($profileManifestIdentity.sha256.Substring(0, 24))"
        airgapManifestSha256 = $profileManifestIdentity.sha256
        packs = @(
            [ordered]@{
                id = 'base'
                url = Get-AssetUrl $baseArchiveName
                bytes = $baseIdentity.bytes
                sha256 = $baseIdentity.sha256
            },
            [ordered]@{
                id = 'configuration'
                url = Get-AssetUrl $configurationName
                kind = 'file'
                target = 'airgap-config.json'
                bytes = $configurationIdentity.bytes
                sha256 = $configurationIdentity.sha256
            },
            [ordered]@{
                id = 'translation-license'
                url = Get-AssetUrl $licenseName
                kind = 'file'
                target = 'licenses/Hy-MT2-Apache-2.0.txt'
                bytes = $licenseIdentity.bytes
                sha256 = $licenseIdentity.sha256
            },
            [ordered]@{
                id = 'airgap-manifest'
                url = Get-AssetUrl $profileManifestName
                kind = 'file'
                target = 'airgap-manifest.json'
                bytes = $profileManifestIdentity.bytes
                sha256 = $profileManifestIdentity.sha256
            },
            [ordered]@{
                id = 'translation-model'
                url = [string]$profile.url
                kind = 'file'
                target = "models/hy-mt2/$($profile.fileName)"
                bytes = [long]$profile.bytes
                sha256 = [string]$profile.sha256
            }
        )
    }
    Write-JsonFile $trustManifestOutput $trustManifest 8
    $trustIdentity = Get-FileIdentity $trustManifestOutput
    $results.Add([pscustomobject]@{
        profile = $profileName
        trustManifest = $trustManifestOutput
        trustManifestSha256 = $trustIdentity.sha256
        airgapManifestSha256 = $profileManifestIdentity.sha256
    })
}

$checksums = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $output -File | Sort-Object Name) {
    if ($file.Name -in @('.incomplete', 'SHA256SUMS.txt')) { continue }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums.Add("$hash  $($file.Name)")
}
[IO.File]::WriteAllText(
    (Join-Path $output 'SHA256SUMS.txt'),
    ($checksums -join "`n") + "`n",
    [Text.UTF8Encoding]::new($false)
)

if (-not $SkipAssemblyTest) {
    $testProfile = $templateProfile
    $testTrust = Join-Path $output "bundle-packs-$testProfile.json"
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) ('bstrings-pack-test-' + [Guid]::NewGuid().ToString('N'))
    $cache = Join-Path $testRoot 'cache'
    $assembled = Join-Path $testRoot 'assembled'
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    try {
        Copy-Item -LiteralPath $baseArchivePath -Destination (Join-Path $cache 'base.zip')
        Copy-Item `
            -LiteralPath (Join-Path $output "airgap-config-$testProfile.json") `
            -Destination (Join-Path $cache 'configuration.file')
        Copy-Item `
            -LiteralPath (Join-Path $output "Hy-MT2-Apache-2.0-$testProfile.txt") `
            -Destination (Join-Path $cache 'translation-license.file')
        Copy-Item `
            -LiteralPath (Join-Path $output "airgap-manifest-$testProfile.json") `
            -Destination (Join-Path $cache 'airgap-manifest.file')
        try {
            New-Item `
                -ItemType HardLink `
                -Path (Join-Path $cache 'translation-model.file') `
                -Target $templateModelPath `
                -ErrorAction Stop | Out-Null
        }
        catch {
            Copy-Item -LiteralPath $templateModelPath `
                -Destination (Join-Path $cache 'translation-model.file')
        }
        & $bstringsPath bundle assemble `
            --manifest $testTrust `
            --cache $cache `
            --output $assembled
        if ($LASTEXITCODE -ne 0) {
            throw "Local $testProfile split-pack assembly test failed."
        }
    }
    finally {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        if (
            [IO.Path]::GetFileName($resolvedTestRoot).StartsWith('bstrings-pack-test-', [StringComparison]::Ordinal) -and
            $resolvedTestRoot.StartsWith(
                [IO.Path]::GetFullPath([IO.Path]::GetTempPath()),
                [StringComparison]::OrdinalIgnoreCase
            )
        ) {
            [IO.Directory]::Delete($resolvedTestRoot, $true)
        }
        else {
            throw "Refusing to remove uncontrolled split-pack test directory: $resolvedTestRoot"
        }
    }
}

[IO.File]::Delete($incompleteMarker)
$results | Format-Table -AutoSize | Out-Host
Write-Host "Split-pack release created: $output"
Write-Host "Base ZIP: $($baseIdentity.bytes) bytes; $($baseIdentity.sha256)"
Write-Host 'Quality is the default; each model remains an immutable, hash-gated external file pack.'
