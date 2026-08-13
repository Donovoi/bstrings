[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseAssetBaseUrl,
    [Parameter(Mandatory = $true)]
    [string]$BaseReleaseArchive,
    [Parameter(Mandatory = $true)]
    [string]$InstallerScript,
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
$installerScriptPath = [IO.Path]::GetFullPath($InstallerScript)
$installerScriptItem = Get-Item -LiteralPath $installerScriptPath -Force -ErrorAction Stop
if (
    $installerScriptItem.PSIsContainer -or
    ($installerScriptItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    [long]$installerScriptItem.Length -lt 1
) {
    throw "Installer must be a non-empty physical file: $installerScriptPath"
}
if ($installerScriptItem.Name -cne 'Install-Bstrings.ps1') {
    throw "Installer must use the exact public asset name Install-Bstrings.ps1: $installerScriptPath"
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
    [int]$lock.schemaVersion -ne 2 -or
    [string]$lock.profile -ne 'windows-x64-offline-v3'
) {
    throw 'Offline component lock does not use the one-kit contract.'
}
$model = $lock.components.translationModel
Assert-LockedFileSpec $model 'translation model'
if (
    [string]$model.archiveType -ne 'file' -or
    [string]::IsNullOrWhiteSpace([string]$model.modelId) -or
    [string]$model.revision -notmatch '^[0-9a-f]{40}$'
) {
    throw 'Translation model metadata is incomplete.'
}
Assert-LockedFileSpec $model.license 'translation model license'
if ([string]$model.license.source -ne 'download') {
    throw 'Translation model license must be a locked download.'
}
$cudaLock = $lock.llamaCudaOverlay
if (
    $null -eq $cudaLock -or
    [string]$cudaLock.sourceTag -ne [string]$lock.components.llamaCpp.sourceTag -or
    [string]$cudaLock.sourceCommit -ne [string]$lock.components.llamaCpp.sourceCommit -or
    [string]$cudaLock.platform -cne 'windows-x64' -or
    @($cudaLock.runtimeFiles).Count -ne 4 -or
    [string]$cudaLock.provenancePath -cne 'llama-cuda-overlay-provenance.json'
) {
    throw 'Offline component lock does not contain the reviewed same-commit CUDA overlay.'
}
Assert-LockedFileSpec $cudaLock.license 'NVIDIA CUDA 12.4 EULA'
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
$unexpectedProfileProperty = $templateConfiguration.PSObject.Properties['translationProfile']
if (
    [string]$templateConfiguration.bundleProfile -cne 'windows-x64-offline-v3' -or
    $null -ne $unexpectedProfileProperty
) {
    throw 'Complete input bundle does not use the one-kit configuration contract.'
}
$templateModel = $model
if (
    [string]$templateConfiguration.translationModel.id -ne [string]$templateModel.modelId -or
    [string]$templateConfiguration.translationModel.revision -ne [string]$templateModel.revision -or
    [string]$templateConfiguration.translationModel.sha256 -ne [string]$templateModel.sha256
) {
    throw 'Complete input bundle translation identity does not match its component lock.'
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
$modelTargets = @("models/hy-mt2/$($model.fileName)")
$cudaTargets = @(
    $cudaLock.runtimeFiles | ForEach-Object { "runtime/llama/$($_.path)" }
) + @(
    "runtime/llama/$($cudaLock.provenancePath)",
    "runtime/llama/$($cudaLock.license.path)",
    'licenses/NVIDIA-CUDA-12.4-EULA.pdf'
)
$cudaRows = @(
    foreach ($target in $cudaTargets) {
        $matches = @($originalManifest.files | Where-Object {
            [string]$_.path -ceq $target
        })
        if ($matches.Count -ne 1) {
            throw "Complete bundle manifest must contain exactly one CUDA pack row: $target"
        }
        $matches[0]
    }
)
foreach ($runtimeFile in @($cudaLock.runtimeFiles)) {
    $target = "runtime/llama/$($runtimeFile.path)"
    $row = @($cudaRows | Where-Object { [string]$_.path -ceq $target })[0]
    if (
        [long]$row.bytes -ne [long]$runtimeFile.bytes -or
        [string]$row.sha256 -ne [string]$runtimeFile.sha256
    ) {
        throw "Complete bundle CUDA runtime differs from its lock: $target"
    }
}
foreach ($target in @(
    "runtime/llama/$($cudaLock.license.path)",
    'licenses/NVIDIA-CUDA-12.4-EULA.pdf'
)) {
    $row = @($cudaRows | Where-Object { [string]$_.path -ceq $target })[0]
    if (
        [long]$row.bytes -ne [long]$cudaLock.license.bytes -or
        [string]$row.sha256 -ne [string]$cudaLock.license.sha256
    ) {
        throw "Complete bundle CUDA license differs from its lock: $target"
    }
}
$baseExclusions = @(
    'airgap-config.json',
    'airgap-manifest.json',
    'licenses/Hy-MT2-Apache-2.0.txt'
) + $modelTargets + $cudaTargets
$baseRows = @(
    $originalManifest.files | Where-Object { [string]$_.path -notin $baseExclusions }
)
if ($baseRows.Count -ge @($originalManifest.files).Count) {
    throw 'Split-pack base exclusions did not remove the template model and kit-specific files.'
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

$cudaArchiveName = 'bstrings-win-x64-offline-cuda.zip'
$cudaArchivePath = Join-Path $output $cudaArchiveName
$archiveStream = $null
$archive = $null
try {
    $archiveStream = [IO.FileStream]::new(
        $cudaArchivePath,
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
    foreach ($row in @($cudaRows | Sort-Object { [string]$_.path })) {
        $relative = [string]$row.path
        $source = Join-Path $bundleRoot ($relative -replace '/', '\')
        $identity = Get-FileIdentity $source
        if ($identity.bytes -ne [long]$row.bytes -or $identity.sha256 -ne [string]$row.sha256) {
            throw "CUDA pack source changed after complete-bundle verification: $relative"
        }
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
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
$cudaIdentity = Get-FileIdentity $cudaArchivePath
if ($cudaIdentity.bytes -lt 1 -or $cudaIdentity.bytes -ge 2000000000) {
    throw "CUDA ZIP must be between 1 and 1,999,999,999 bytes; found $($cudaIdentity.bytes)."
}

$results = [Collections.Generic.List[object]]::new()
$configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
$configuration.translationModel.path = "models/hy-mt2/$($model.fileName)"
$configuration.translationModel.id = [string]$model.modelId
$configuration.translationModel.revision = [string]$model.revision
$configuration.translationModel.sha256 = [string]$model.sha256
$configurationName = 'airgap-config.json'
    $configurationOutput = Join-Path $output $configurationName
    Write-JsonFile $configurationOutput $configuration 10
    $configurationIdentity = Get-FileIdentity $configurationOutput

$licenseSource = Join-Path $bundleRoot 'licenses\Hy-MT2-7B-Apache-2.0.txt'
    if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) {
        throw "Complete bundle lacks the translation license source: $licenseSource"
    }
    $licenseSourceIdentity = Get-FileIdentity $licenseSource
    if (
        $licenseSourceIdentity.bytes -ne [long]$model.license.bytes -or
        $licenseSourceIdentity.sha256 -ne [string]$model.license.sha256
    ) {
        throw 'Translation license source does not match its lock.'
    }
    $licenseName = 'Hy-MT2-Apache-2.0.txt'
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
    foreach ($row in $cudaRows) {
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
        path = "models/hy-mt2/$($model.fileName)"
        bytes = [long]$model.bytes
        sha256 = [string]$model.sha256
    })
    $manifestDocument = [ordered]@{
        schemaVersion = 1
        files = @($rows | Sort-Object { [string]$_.path })
    }
    $kitManifestName = 'airgap-manifest.json'
    $kitManifestOutput = Join-Path $output $kitManifestName
    Write-JsonFile $kitManifestOutput $manifestDocument 6
    $kitManifestIdentity = Get-FileIdentity $kitManifestOutput

    $trustManifestName = 'bundle-packs.json'
    $trustManifestOutput = Join-Path $output $trustManifestName
    $trustManifest = [ordered]@{
        schemaVersion = 1
        profile = 'windows-x64-offline-v3'
        bundleIdentity = "bstrings-kit-$($kitManifestIdentity.sha256.Substring(0, 24))"
        airgapManifestSha256 = $kitManifestIdentity.sha256
        packs = @(
            [ordered]@{
                id = 'base'
                url = Get-AssetUrl $baseArchiveName
                bytes = $baseIdentity.bytes
                sha256 = $baseIdentity.sha256
            },
            [ordered]@{
                id = 'cuda-runtime'
                url = Get-AssetUrl $cudaArchiveName
                bytes = $cudaIdentity.bytes
                sha256 = $cudaIdentity.sha256
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
                url = Get-AssetUrl $kitManifestName
                kind = 'file'
                target = 'airgap-manifest.json'
                bytes = $kitManifestIdentity.bytes
                sha256 = $kitManifestIdentity.sha256
            },
            [ordered]@{
                id = 'translation-model'
                url = [string]$model.url
                kind = 'file'
                target = "models/hy-mt2/$($model.fileName)"
                bytes = [long]$model.bytes
                sha256 = [string]$model.sha256
            }
        )
    }
    Write-JsonFile $trustManifestOutput $trustManifest 8
    $trustIdentity = Get-FileIdentity $trustManifestOutput
    $results.Add([pscustomobject]@{
        trustManifest = $trustManifestOutput
        trustManifestSha256 = $trustIdentity.sha256
        airgapManifestSha256 = $kitManifestIdentity.sha256
    })

[IO.File]::Copy(
    $installerScriptItem.FullName,
    (Join-Path $output 'Install-Bstrings.ps1'),
    $false
)

$checksumInputByName = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($file in Get-ChildItem -LiteralPath $output -File | Sort-Object Name) {
    if ($file.Name -in @('.incomplete', 'SHA256SUMS.txt')) { continue }
    if (-not $checksumInputByName.TryAdd($file.Name, $file.FullName)) {
        throw "Duplicate release-asset checksum name: $($file.Name)"
    }
}
$baseReleaseArchivePath = [IO.Path]::GetFullPath($BaseReleaseArchive)
$baseReleaseArchiveItem = Get-Item -LiteralPath $baseReleaseArchivePath -Force -ErrorAction Stop
if (
    $baseReleaseArchiveItem.PSIsContainer -or
    ($baseReleaseArchiveItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    [long]$baseReleaseArchiveItem.Length -lt 1
) {
    throw "Base release archive must be a non-empty physical file: $baseReleaseArchivePath"
}
if ($baseReleaseArchiveItem.Name -cne 'bstrings-win-x64.zip') {
    throw "Runtime archive must use the exact internal asset name bstrings-win-x64.zip: $baseReleaseArchivePath"
}
if (-not $checksumInputByName.TryAdd($baseReleaseArchiveItem.Name, $baseReleaseArchiveItem.FullName)) {
    throw "Duplicate release-asset checksum name: $($baseReleaseArchiveItem.Name)"
}

$checksums = [Collections.Generic.List[string]]::new()
foreach ($fileName in @($checksumInputByName.Keys | Sort-Object)) {
    $hash = (
        Get-FileHash -LiteralPath $checksumInputByName[$fileName] -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $checksums.Add("$hash  $fileName")
}
[IO.File]::WriteAllText(
    (Join-Path $output 'SHA256SUMS.txt'),
    ($checksums -join "`n") + "`n",
    [Text.UTF8Encoding]::new($false)
)

if (-not $SkipAssemblyTest) {
    $testTrust = Join-Path $output 'bundle-packs.json'
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) ('bstrings-pack-test-' + [Guid]::NewGuid().ToString('N'))
    $cache = Join-Path $testRoot 'cache'
    $assembled = Join-Path $testRoot 'assembled'
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    try {
        Copy-Item -LiteralPath $baseArchivePath -Destination (Join-Path $cache 'base.zip')
        Copy-Item -LiteralPath $cudaArchivePath -Destination (Join-Path $cache 'cuda-runtime.zip')
        Copy-Item `
            -LiteralPath (Join-Path $output 'airgap-config.json') `
            -Destination (Join-Path $cache 'configuration.file')
        Copy-Item `
            -LiteralPath (Join-Path $output 'Hy-MT2-Apache-2.0.txt') `
            -Destination (Join-Path $cache 'translation-license.file')
        Copy-Item `
            -LiteralPath (Join-Path $output 'airgap-manifest.json') `
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
            throw 'Local one-kit split-pack assembly test failed.'
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
Write-Host "CUDA ZIP: $($cudaIdentity.bytes) bytes; $($cudaIdentity.sha256)"
Write-Host 'The one translation model remains an immutable, hash-gated external file pack.'
