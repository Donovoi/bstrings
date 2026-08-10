[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishedBstringsDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$WorkingDirectory,
    [string]$ComponentLockPath,
    [string]$OcrComponentLockPath,
    [ValidateSet('quality')]
    [string]$TranslationProfile = 'quality',
    [string]$VisualCppRuntimeDirectory,
    [switch]$DryRun,
    [switch]$ValidateOnly,
    [switch]$KeepStaging
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($DryRun -and $ValidateOnly) {
    throw '-DryRun and -ValidateOnly are mutually exclusive.'
}
if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
    $WorkingDirectory = Join-Path ([IO.Path]::GetTempPath()) 'bstrings-offline-components-v1'
}
if ([string]::IsNullOrWhiteSpace($ComponentLockPath)) {
    $ComponentLockPath = Join-Path $PSScriptRoot 'offline-components.lock.json'
}
if ([string]::IsNullOrWhiteSpace($OcrComponentLockPath)) {
    $OcrComponentLockPath = Join-Path $PSScriptRoot 'ocr-components.lock.json'
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Resolve-ExistingFile([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name was not found: $Path"
    }
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $item = Get-Item -LiteralPath $resolved -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $resolved"
    }
    return $resolved
}

function Resolve-ExistingDirectory([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Name was not found: $Path"
    }
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $item = Get-Item -LiteralPath $resolved -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $resolved"
    }
    return $resolved
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
    Assert-SafeLeafName ([string]$Spec.fileName) "$Name fileName"
    if ([long]$Spec.bytes -lt 1) {
        throw "$Name must have a positive locked byte length."
    }
    if ([long]$Spec.bytes -eq [long]::MaxValue) {
        throw "$Name locked byte length is too large to enforce a bounded download."
    }
    $hash = [string]$Spec.sha256
    if ($hash -notmatch '^[0-9a-f]{64}$') {
        throw "$Name must have a lowercase SHA-256 in the component lock."
    }
    $uri = $null
    if (-not [Uri]::TryCreate([string]$Spec.url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
        throw "$Name must have an absolute HTTPS URL in the component lock."
    }
}

function Assert-ExactFile(
    [string]$Path,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$Name
) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $Path"
    }
    if ($item.Length -ne $ExpectedBytes) {
        throw "$Name byte length mismatch: expected $ExpectedBytes, found $($item.Length): $Path"
    }
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw "$Name SHA-256 mismatch: expected $ExpectedSha256, found ${actualHash}: $Path"
    }
}

function Assert-PathInside([string]$Root, [string]$Path, [string]$Name) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $fullRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escapes its controlled root: $fullPath"
    }
}

function Get-VerifiedDownload(
    [object]$Spec,
    [string]$Destination,
    [string]$Name,
    [bool]$AllowNetwork
) {
    Assert-PathInside $script:downloadsDirectory $Destination "$Name destination"
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        try {
            Assert-ExactFile `
                $Destination `
                ([long]$Spec.bytes) `
                ([string]$Spec.sha256) `
                $Name
            Write-Host "Using verified cached ${Name}: $Destination"
            return
        }
        catch {
            if (-not $AllowNetwork) {
                throw
            }
            [IO.File]::Delete([IO.Path]::GetFullPath($Destination))
        }
    }
    if (-not $AllowNetwork) {
        throw "$Name is not present in the validation cache: $Destination"
    }

    $partialPath = $Destination + '.partial.' + [Guid]::NewGuid().ToString('N')
    Assert-PathInside $script:downloadsDirectory $partialPath "$Name partial download"
    $response = $null
    $input = $null
    $output = $null
    try {
        Write-Host "Downloading $Name from $($Spec.url)"
        $response = $script:httpClient.GetAsync(
            [string]$Spec.url,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead
        ).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode() | Out-Null
        if ($response.RequestMessage.RequestUri.Scheme -ne 'https') {
            throw "$Name redirected to a non-HTTPS download URL: $($response.RequestMessage.RequestUri)"
        }
        $expectedBytes = [long]$Spec.bytes
        $contentLength = $response.Content.Headers.ContentLength
        if ($null -ne $contentLength -and [long]$contentLength -ne $expectedBytes) {
            throw "$Name HTTP Content-Length mismatch: expected $expectedBytes, received $contentLength."
        }
        $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = [IO.FileStream]::new(
            $partialPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            1024 * 1024,
            [IO.FileOptions]::SequentialScan
        )
        $buffer = [byte[]]::new(1024 * 1024)
        $downloadedBytes = [long]0
        while ($true) {
            $remainingWithSentinel = ($expectedBytes - $downloadedBytes) + 1
            $readSize = [int][Math]::Min([long]$buffer.Length, $remainingWithSentinel)
            $read = $input.Read($buffer, 0, $readSize)
            if ($read -eq 0) {
                break
            }
            $output.Write($buffer, 0, $read)
            $downloadedBytes += $read
            if ($downloadedBytes -gt $expectedBytes) {
                throw "$Name exceeded its locked byte length of $expectedBytes bytes."
            }
        }
        $output.Flush($true)
        $output.Dispose()
        $output = $null
        $input.Dispose()
        $input = $null
        $response.Dispose()
        $response = $null
        Assert-ExactFile `
            $partialPath `
            ([long]$Spec.bytes) `
            ([string]$Spec.sha256) `
            $Name
        [IO.File]::Move($partialPath, $Destination)
        Assert-ExactFile `
            $Destination `
            ([long]$Spec.bytes) `
            ([string]$Spec.sha256) `
            $Name
    }
    finally {
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $input) { $input.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        if (Test-Path -LiteralPath $partialPath -PathType Leaf) {
            [IO.File]::Delete([IO.Path]::GetFullPath($partialPath))
        }
    }
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

function Expand-VerifiedZip([string]$Archive, [string]$Destination, [string]$Name) {
    if (Test-Path -LiteralPath $Destination) {
        throw "$Name extraction destination already exists: $Destination"
    }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $destinationPrefix = $destinationRoot + [IO.Path]::DirectorySeparatorChar
    $seenPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    $totalExpandedBytes = [long]0
    $maximumExpandedBytes = 4GB
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $entryName = [string]$entry.FullName
            if ([string]::IsNullOrWhiteSpace($entryName) -or $entryName.IndexOf([char]0) -ge 0) {
                throw "$Name contains an empty or invalid archive entry."
            }
            $normalized = $entryName.Replace('\', '/')
            if (
                $normalized.StartsWith('/') -or
                $normalized.StartsWith('//') -or
                $normalized -match '^[A-Za-z]:' -or
                $normalized.Contains(':')
            ) {
                throw "$Name contains an absolute or alternate-stream archive path: $entryName"
            }
            $parts = @($normalized.Split('/') | Where-Object { $_.Length -gt 0 })
            if ($parts.Count -eq 0 -or @($parts | Where-Object { $_ -in @('.', '..') }).Count -gt 0) {
                throw "$Name contains a non-canonical archive path: $entryName"
            }

            $external = [BitConverter]::ToUInt32(
                [BitConverter]::GetBytes([int]$entry.ExternalAttributes),
                0
            )
            $unixType = ($external -shr 16) -band 0xF000
            if (
                ($external -band [uint32][IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $unixType -eq 0xA000 -or
                $unixType -notin @(0, 0x4000, 0x8000)
            ) {
                throw "$Name contains a linked or unsupported archive entry: $entryName"
            }

            $relativePath = [IO.Path]::Combine([string[]]$parts)
            $targetPath = [IO.Path]::GetFullPath((Join-Path $destinationRoot $relativePath))
            if (-not $targetPath.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "$Name archive entry escapes its extraction directory: $entryName"
            }
            if (-not $seenPaths.Add($targetPath)) {
                throw "$Name contains a duplicate archive path: $entryName"
            }

            $isDirectory = $normalized.EndsWith('/') -or [string]::IsNullOrEmpty($entry.Name)
            if ($isDirectory) {
                if ($entry.Length -ne 0) {
                    throw "$Name contains a directory entry with data: $entryName"
                }
                [IO.Directory]::CreateDirectory($targetPath) | Out-Null
                continue
            }

            if ([long]$entry.Length -gt ($maximumExpandedBytes - $totalExpandedBytes)) {
                throw "$Name expands beyond the 4 GiB safety ceiling."
            }
            $totalExpandedBytes += [long]$entry.Length
            [IO.Directory]::CreateDirectory((Split-Path -Parent $targetPath)) | Out-Null
            $entryStream = $null
            $targetStream = $null
            try {
                $entryStream = $entry.Open()
                $targetStream = [IO.FileStream]::new(
                    $targetPath,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None
                )
                $entryStream.CopyTo($targetStream, 1024 * 1024)
            }
            finally {
                if ($null -ne $targetStream) { $targetStream.Dispose() }
                if ($null -ne $entryStream) { $entryStream.Dispose() }
            }
            if ((Get-Item -LiteralPath $targetPath).Length -ne $entry.Length) {
                throw "$Name extracted byte length mismatch for: $entryName"
            }
        }
    }
    finally {
        $zip.Dispose()
    }
    Assert-NoReparsePoints $Destination "$Name extraction"
}

function Remove-ControlledStaging([string]$WorkingRoot, [string]$StagingRoot) {
    $resolvedWorking = [IO.Path]::GetFullPath($WorkingRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $resolvedStaging = [IO.Path]::GetFullPath($StagingRoot)
    $leaf = [IO.Path]::GetFileName($resolvedStaging)
    if (
        -not $resolvedStaging.StartsWith(
            $resolvedWorking + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        -not $leaf.StartsWith('staging-', [StringComparison]::Ordinal) -or
        $leaf.Length -ne ('staging-'.Length + 32)
    ) {
        throw "Refusing to remove an uncontrolled staging directory: $resolvedStaging"
    }
    if ([IO.Directory]::Exists($resolvedStaging)) {
        [IO.Directory]::Delete($resolvedStaging, $true)
    }
}

$lockPath = Resolve-ExistingFile $ComponentLockPath 'Offline component lock'
$ocrLockPath = Resolve-ExistingFile $OcrComponentLockPath 'OCR component lock'
$publishedDirectory = Resolve-ExistingDirectory `
    $PublishedBstringsDirectory `
    'Published bstrings directory'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.profile -ne 'windows-x64-offline-v2') {
    throw "Unsupported offline component lock schema or profile: $lockPath"
}
if ([string]$lock.defaultTranslationProfile -ne 'quality') {
    throw 'The offline component lock must select quality as its default translation profile.'
}
$translationProfileNames = @($lock.translationProfiles.PSObject.Properties.Name)
if ((@($translationProfileNames | Sort-Object) -join '|') -cne 'quality') {
    throw 'The offline component lock must define exactly the quality translation profile.'
}
$translationProfileSpecs = @{}
foreach ($profileName in $translationProfileNames) {
    $profileSpec = $lock.translationProfiles.$profileName
    Assert-LockedFileSpec $profileSpec "$profileName translation profile"
    Assert-LockedFileSpec $profileSpec.license "$profileName translation profile license"
    $translationProfileSpecs[$profileName] = $profileSpec
}
$selectedTranslationModel = $lock.translationProfiles.$TranslationProfile
if ($null -eq $selectedTranslationModel) {
    throw "Translation profile is not present in the component lock: $TranslationProfile"
}
$lock.components.translationModel = $selectedTranslationModel
$componentOrder = @('python', 'magika', 'floss', 'llamaCpp', 'translationModel')
foreach ($componentName in $componentOrder) {
    $component = $lock.components.$componentName
    if ($null -eq $component) {
        throw "Offline component lock is missing '$componentName'."
    }
    Assert-LockedFileSpec $component "$componentName component"
    $allowedArchiveTypes = if ($componentName -eq 'llamaCpp') {
        @('source-zip')
    }
    else {
        @('zip', 'file')
    }
    if ($component.archiveType -notin $allowedArchiveTypes) {
        throw "Offline component '$componentName' has an unsupported archive type."
    }
    if ($component.license.source -eq 'download') {
        Assert-LockedFileSpec $component.license "$componentName license"
    }
    elseif ($component.license.source -ne 'archive') {
        throw "Offline component '$componentName' has an unsupported license source."
    }
}
$llamaComponent = $lock.components.llamaCpp
if (
    [string]$llamaComponent.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
    [string]$llamaComponent.sourceTag -ne [string]$llamaComponent.version -or
    [string]$llamaComponent.sourceRoot -notmatch '^llama\.cpp-[0-9a-f]{40}$'
) {
    throw 'The locked llama.cpp source tag, commit, or archive root is invalid.'
}
foreach ($notice in @($llamaComponent.license) + @($llamaComponent.notices)) {
    if (
        [string]::IsNullOrWhiteSpace([string]$notice.sourcePath) -or
        [string]$notice.path -notmatch '^notices/llama\.cpp/[A-Za-z0-9._-]+$' -or
        [long]$notice.bytes -lt 1 -or
        [string]$notice.sha256 -notmatch '^[0-9a-f]{64}$'
    ) {
        throw 'The locked llama.cpp source notice inventory is invalid.'
    }
}
$cudaOverlay = $lock.llamaCudaOverlay
if (
    $null -eq $cudaOverlay -or
    [string]$cudaOverlay.sourceTag -ne [string]$llamaComponent.sourceTag -or
    [string]$cudaOverlay.sourceCommit -ne [string]$llamaComponent.sourceCommit -or
    [string]$cudaOverlay.platform -cne 'windows-x64' -or
    [string]$cudaOverlay.acceptanceHardware.computeCapability -cne '8.9' -or
    [string]$cudaOverlay.provenancePath -cne 'llama-cuda-overlay-provenance.json'
) {
    throw 'The locked llama.cpp CUDA overlay is missing or does not match the source-built CPU runtime.'
}
$cudaArchiveIds = @($cudaOverlay.archives | ForEach-Object { [string]$_.id })
if (
    (@($cudaArchiveIds | Sort-Object) -join '|') -cne 'cuda-runtime|llama-cuda-backend' -or
    @($cudaArchiveIds | Sort-Object -Unique).Count -ne 2
) {
    throw 'The llama.cpp CUDA overlay must pin exactly the backend and CUDA runtime archives.'
}
foreach ($archive in @($cudaOverlay.archives)) {
    Assert-LockedFileSpec $archive "CUDA archive $($archive.id)"
    if ([int]$archive.entries -lt 1 -or [long]$archive.expandedBytes -lt 1) {
        throw "CUDA archive '$($archive.id)' has an invalid inventory bound."
    }
}
$cudaRuntimeNames = @($cudaOverlay.runtimeFiles | ForEach-Object { [string]$_.path })
if (
    (@($cudaRuntimeNames | Sort-Object) -join '|') -cne
        'cublas64_12.dll|cublasLt64_12.dll|cudart64_12.dll|ggml-cuda.dll' -or
    @($cudaRuntimeNames | Sort-Object -Unique).Count -ne 4
) {
    throw 'The llama.cpp CUDA overlay must lock exactly the reviewed four-file runtime closure.'
}
foreach ($runtimeFile in @($cudaOverlay.runtimeFiles)) {
    Assert-SafeLeafName ([string]$runtimeFile.entry) 'CUDA runtime archive entry'
    Assert-SafeLeafName ([string]$runtimeFile.path) 'CUDA runtime path'
    if (
        [string]$runtimeFile.archive -notin $cudaArchiveIds -or
        [long]$runtimeFile.bytes -lt 1 -or
        [string]$runtimeFile.sha256 -notmatch '^[0-9a-f]{64}$' -or
        @($runtimeFile.imports).Count -lt 1
    ) {
        throw "The locked CUDA runtime record is invalid: $($runtimeFile.path)"
    }
}
Assert-LockedFileSpec $cudaOverlay.license 'NVIDIA CUDA 12.4 EULA'
if (
    [string]$cudaOverlay.license.source -cne 'download' -or
    [string]$cudaOverlay.license.path -cne
        'notices/nvidia-cuda/NVIDIA-CUDA-12.4-EULA.pdf'
) {
    throw 'The locked NVIDIA CUDA license destination is invalid.'
}
$magikaRedistribution = $lock.components.magika.redistribution
if (
    $null -eq $magikaRedistribution -or
    [string]$magikaRedistribution.inventoryPath -notmatch '^licenses/magika-cli-1\.1\.0-redistribution\.json$' -or
    [long]$magikaRedistribution.inventoryBytes -lt 1 -or
    [string]$magikaRedistribution.inventorySha256 -notmatch '^[0-9a-f]{64}$' -or
    [string]$magikaRedistribution.stageScript -cne 'tools/licenses/Stage-MagikaRedistribution.ps1' -or
    [string]$magikaRedistribution.verifyScript -cne 'tools/licenses/Verify-MagikaRedistribution.ps1' -or
    [string]$magikaRedistribution.runtimeExecutable -cne 'tools/magika/magika.exe' -or
    [string]$magikaRedistribution.runtimeDependency -cne 'tools/magika/DirectML.dll' -or
    [long]$magikaRedistribution.stagedFiles -lt 1 -or
    [long]$magikaRedistribution.stagedBytes -lt 1
) {
    throw 'The locked Magika redistribution overlay description is invalid.'
}
$magikaInventoryPath = Resolve-ExistingFile `
    (Join-Path $repoRoot ([string]$magikaRedistribution.inventoryPath)) `
    'Magika redistribution inventory'
Assert-PathInside $repoRoot $magikaInventoryPath 'Magika redistribution inventory'
Assert-ExactFile `
    $magikaInventoryPath `
    ([long]$magikaRedistribution.inventoryBytes) `
    ([string]$magikaRedistribution.inventorySha256) `
    'Magika redistribution inventory'
$magikaStageScript = Resolve-ExistingFile `
    (Join-Path $repoRoot ([string]$magikaRedistribution.stageScript)) `
    'Magika redistribution stager'
$magikaVerifyScript = Resolve-ExistingFile `
    (Join-Path $repoRoot ([string]$magikaRedistribution.verifyScript)) `
    'Magika redistribution verifier'
Assert-PathInside $repoRoot $magikaStageScript 'Magika redistribution stager'
Assert-PathInside $repoRoot $magikaVerifyScript 'Magika redistribution verifier'
$flossRedistribution = $lock.components.floss.redistribution
if (
    $null -eq $flossRedistribution -or
    [string]$flossRedistribution.inventoryPath -notmatch '^licenses/floss-v3\.1\.1-win-x64\.json$' -or
    [long]$flossRedistribution.inventoryBytes -lt 1 -or
    [string]$flossRedistribution.inventorySha256 -notmatch '^[0-9a-f]{64}$' -or
    [string]$flossRedistribution.stageScript -cne 'tools/licenses/Stage-FlossThirdPartyNotices.ps1' -or
    [string]$flossRedistribution.verifyScript -cne 'tools/licenses/Verify-FlossThirdPartyNotices.ps1' -or
    [long]$flossRedistribution.stagedFiles -lt 1 -or
    [long]$flossRedistribution.stagedBytes -lt 1
) {
    throw 'The locked FLOSS redistribution overlay description is invalid.'
}
$flossInventoryPath = Resolve-ExistingFile `
    (Join-Path $repoRoot ([string]$flossRedistribution.inventoryPath)) `
    'FLOSS redistribution inventory'
Assert-PathInside $repoRoot $flossInventoryPath 'FLOSS redistribution inventory'
Assert-ExactFile `
    $flossInventoryPath `
    ([long]$flossRedistribution.inventoryBytes) `
    ([string]$flossRedistribution.inventorySha256) `
    'FLOSS redistribution inventory'
$flossStageScript = Resolve-ExistingFile `
    (Join-Path $repoRoot ([string]$flossRedistribution.stageScript)) `
    'FLOSS redistribution stager'
$flossVerifyScript = Resolve-ExistingFile `
    (Join-Path $repoRoot ([string]$flossRedistribution.verifyScript)) `
    'FLOSS redistribution verifier'
Assert-PathInside $repoRoot $flossStageScript 'FLOSS redistribution stager'
Assert-PathInside $repoRoot $flossVerifyScript 'FLOSS redistribution verifier'

function New-VerifiedMagikaRedistribution(
    [string]$DestinationDirectory,
    [switch]$CacheOnly
) {
    $stageParameters = @{
        DestinationDirectory = $DestinationDirectory
        InventoryPath = $script:magikaInventoryPath
        DownloadCacheDirectory = $script:downloadsDirectory
    }
    if ($CacheOnly) {
        $stageParameters.CacheOnly = $true
    }
    $stageResult = @(& $script:magikaStageScript @stageParameters)
    if ($stageResult.Count -ne 1) {
        throw 'Magika redistribution stager did not return one verified overlay result.'
    }
    $verifyResult = @(
        & $script:magikaVerifyScript `
            -BundleDirectory $DestinationDirectory `
            -InventoryPath $script:magikaInventoryPath
    )
    $overlayMeasure = Get-ChildItem `
        -LiteralPath $DestinationDirectory `
        -Recurse `
        -File | Measure-Object -Property Length -Sum
    if (
        $verifyResult.Count -ne 1 -or
        [long]$verifyResult[0].files -ne [long]$script:magikaRedistribution.stagedFiles -or
        [long]$verifyResult[0].totalOwnedBytes -ne [long]$script:magikaRedistribution.stagedBytes -or
        [long]$overlayMeasure.Count -ne [long]$script:magikaRedistribution.stagedFiles -or
        [long]$overlayMeasure.Sum -ne [long]$script:magikaRedistribution.stagedBytes
    ) {
        throw 'Magika redistribution overlay did not match its locked size and dependency closure.'
    }
    return $verifyResult[0]
}

function New-VerifiedFlossRedistribution(
    [string]$FlossExecutable,
    [string]$DestinationDirectory
) {
    $stageResult = @(
        & $script:flossStageScript `
            -FlossExecutable $FlossExecutable `
            -DestinationDirectory $DestinationDirectory `
            -InventoryPath $script:flossInventoryPath
    )
    if (
        $stageResult.Count -ne 1 -or
        [long]$stageResult[0].FileCount -ne [long]$script:flossRedistribution.stagedFiles -or
        [long]$stageResult[0].TotalBytes -ne [long]$script:flossRedistribution.stagedBytes -or
        [string]$stageResult[0].InventorySha256 -cne [string]$script:flossRedistribution.inventorySha256
    ) {
        throw 'FLOSS redistribution overlay did not match its locked file count, size, or inventory.'
    }
    $null = & $script:flossVerifyScript `
        -FlossExecutable $FlossExecutable `
        -InventoryPath $script:flossInventoryPath `
        -StagedDirectory $DestinationDirectory `
        -SkipVersionProbe
    return $stageResult[0]
}

$runtimeStager = Resolve-ExistingFile `
    (Join-Path $PSScriptRoot 'Stage-VisualCppRuntime.ps1') `
    'Visual C++ runtime staging helper'
$llamaBuilder = Resolve-ExistingFile `
    (Join-Path $PSScriptRoot 'Build-LlamaCpuRuntime.ps1') `
    'Pinned llama.cpp CPU runtime builder'
$cudaOverlayStager = Resolve-ExistingFile `
    (Join-Path $PSScriptRoot 'Stage-LlamaCudaOverlay.ps1') `
    'Pinned official llama.cpp CUDA overlay stager'
$ocrBuilder = Resolve-ExistingFile `
    (Join-Path $PSScriptRoot 'Build-OcrComponents.ps1') `
    'Pinned offline OCR component builder'
$ocrStagingHelper = Resolve-ExistingFile `
    (Join-Path $PSScriptRoot 'stage_ocr_components.py') `
    'OCR runtime-inventory helper'
$runtimeInspectionParameters = @{
    ComponentLockPath = $lockPath
    InspectOnly = $true
}
if (-not [string]::IsNullOrWhiteSpace($VisualCppRuntimeDirectory)) {
    $runtimeInspectionParameters.VisualCppRuntimeDirectory = $VisualCppRuntimeDirectory
}
$runtimeInspection = @(& $runtimeStager @runtimeInspectionParameters)
if ($runtimeInspection.Count -ne 1) {
    throw 'Visual C++ runtime staging helper did not return one inspection result.'
}
$resolvedVisualCppRuntime = [string]$runtimeInspection[0].sourceDirectory
$runtimeInventory = @($runtimeInspection[0].files)
if ($runtimeInventory.Count -ne 4) {
    throw 'Visual C++ runtime staging helper did not inventory all four required runtime DLL names and selected bytes.'
}
if (-not (Test-Path -LiteralPath (Join-Path $publishedDirectory 'bstrings.exe') -PathType Leaf)) {
    throw "Published bstrings directory does not contain bstrings.exe: $publishedDirectory"
}
Assert-NoReparsePoints $publishedDirectory 'Published bstrings directory'

$workingRoot = [IO.Path]::GetFullPath($WorkingDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
$script:downloadsDirectory = Join-Path $workingRoot 'downloads'
$ocrDownloadsDirectory = Join-Path $workingRoot 'ocr-downloads'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $DryRun -and -not $ValidateOnly -and (Test-Path -LiteralPath $outputRoot)) {
    throw "OutputDirectory must not already exist: $outputRoot"
}

if ($DryRun) {
    Write-Host "Offline bundle dry run: $($lock.profile)"
    Write-Host "Component lock: $lockPath"
    Write-Host "Published bstrings: $publishedDirectory"
    Write-Host "Visual C++ app-local runtime: $resolvedVisualCppRuntime"
    foreach ($runtimeFile in $runtimeInventory) {
        Write-Host "- $($runtimeFile.name) $($runtimeFile.fileVersion): $($runtimeFile.bytes) bytes; $($runtimeFile.sha256)"
    }
    Write-Host "Download cache: $script:downloadsDirectory"
    Write-Host "Output bundle: $outputRoot"
    foreach ($componentName in $componentOrder) {
        $component = $lock.components.$componentName
        Write-Host "- $componentName $($component.version): $($component.bytes) bytes; $($component.sha256); $($component.url)"
        if ($component.license.source -eq 'download') {
            Write-Host "  license: $($component.license.bytes) bytes; $($component.license.sha256); $($component.license.url)"
        }
        if ($componentName -eq 'llamaCpp') {
            Write-Host "  source build: $($component.sourceTag) at $($component.sourceCommit); target $($component.build.target)"
            Write-Host "  flags: $(@($component.build.flags) -join '; ')"
        }
        if ($componentName -eq 'magika') {
            Write-Host "  redistribution overlay: $($magikaRedistribution.stagedBytes) bytes; inventory $($magikaRedistribution.inventorySha256)"
        }
        if ($componentName -eq 'floss') {
            Write-Host "  redistribution overlay: $($flossRedistribution.stagedBytes) bytes; inventory $($flossRedistribution.inventorySha256)"
        }
    }
    foreach ($archive in @($cudaOverlay.archives)) {
        Write-Host "- CUDA archive $($archive.id): $($archive.bytes) bytes; $($archive.sha256); $($archive.url)"
    }
    Write-Host "  NVIDIA CUDA EULA: $($cudaOverlay.license.bytes) bytes; $($cudaOverlay.license.sha256); $($cudaOverlay.license.url)"
    & $ocrBuilder `
        -DestinationDirectory ($outputRoot + '-ocr-dry-run') `
        -DownloadCacheDirectory $ocrDownloadsDirectory `
        -LockPath $ocrLockPath `
        -DryRun
    Write-Host 'Dry run complete; no files were downloaded, extracted, or created.'
    return
}

[IO.Directory]::CreateDirectory($workingRoot) | Out-Null
Assert-NoReparsePoints $workingRoot 'Offline bundle working directory'
[IO.Directory]::CreateDirectory($script:downloadsDirectory) | Out-Null
Assert-NoReparsePoints $script:downloadsDirectory 'Offline bundle download cache'

$script:httpClient = $null
if (-not $ValidateOnly) {
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $script:httpClient = [Net.Http.HttpClient]::new($handler, $true)
    $script:httpClient.Timeout = [TimeSpan]::FromHours(4)
    $script:httpClient.DefaultRequestHeaders.UserAgent.ParseAdd('bstrings-offline-bundle-builder/1.0')
}

$downloadPaths = @{}
$licensePaths = @{}
$cudaArchivePaths = @{}
$translationProfileLicensePaths = @{}
try {
    foreach ($componentName in $componentOrder) {
        $component = $lock.components.$componentName
        $downloadPath = Join-Path $script:downloadsDirectory ([string]$component.fileName)
        Get-VerifiedDownload `
            $component `
            $downloadPath `
            "$componentName component" `
            (-not $ValidateOnly)
        $downloadPaths[$componentName] = $downloadPath
        if ($component.license.source -eq 'download') {
            $licensePath = Join-Path `
                $script:downloadsDirectory `
                ([string]$component.license.fileName)
            Get-VerifiedDownload `
                $component.license `
                $licensePath `
                "$componentName license" `
                (-not $ValidateOnly)
            $licensePaths[$componentName] = $licensePath
        }
    }
    foreach ($archive in @($cudaOverlay.archives)) {
        $archivePath = Join-Path $script:downloadsDirectory ([string]$archive.fileName)
        Get-VerifiedDownload `
            $archive `
            $archivePath `
            "CUDA archive $($archive.id)" `
            (-not $ValidateOnly)
        $cudaArchivePaths[[string]$archive.id] = $archivePath
    }
    $cudaLicensePath = Join-Path `
        $script:downloadsDirectory `
        ([string]$cudaOverlay.license.fileName)
    Get-VerifiedDownload `
        $cudaOverlay.license `
        $cudaLicensePath `
        'NVIDIA CUDA 12.4 EULA' `
        (-not $ValidateOnly)
    foreach ($profileName in $translationProfileNames) {
        $license = $translationProfileSpecs[$profileName].license
        $licensePath = Join-Path $script:downloadsDirectory ([string]$license.fileName)
        Get-VerifiedDownload `
            $license `
            $licensePath `
            "$profileName translation profile license" `
            (-not $ValidateOnly)
        $translationProfileLicensePaths[$profileName] = $licensePath
    }
}
finally {
    if ($null -ne $script:httpClient) {
        $script:httpClient.Dispose()
        $script:httpClient = $null
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
if ($ValidateOnly) {
    $validationRoot = Join-Path $workingRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($validationRoot) | Out-Null
    try {
        $validationFloss = Join-Path $validationRoot 'floss'
        $validationMagikaOverlay = Join-Path $validationRoot 'magika-redistribution'
        $validationFlossOverlay = Join-Path $validationRoot 'floss-redistribution'
        $validationCudaOverlay = Join-Path $validationRoot 'llama-cuda-overlay'
        Expand-VerifiedZip `
            $downloadPaths.floss `
            $validationFloss `
            'floss archive for cache-only redistribution validation'
        $validationFlossExecutable = Join-Path `
            $validationFloss `
            ([string]$lock.components.floss.executable)
        $null = New-VerifiedMagikaRedistribution `
            -DestinationDirectory $validationMagikaOverlay `
            -CacheOnly
        $null = New-VerifiedFlossRedistribution `
            -FlossExecutable $validationFlossExecutable `
            -DestinationDirectory $validationFlossOverlay
        $cudaValidation = @(
            & $cudaOverlayStager `
                -BackendArchive $cudaArchivePaths['llama-cuda-backend'] `
                -CudaRuntimeArchive $cudaArchivePaths['cuda-runtime'] `
                -LicenseFile $cudaLicensePath `
                -DestinationDirectory $validationCudaOverlay `
                -ComponentLockPath $lockPath
        )
        if ($cudaValidation.Count -ne 1) {
            throw 'Pinned CUDA overlay cache validation did not return one verified result.'
        }
        & $ocrBuilder `
            -DestinationDirectory (Join-Path $validationRoot 'ocr-components-unused') `
            -DownloadCacheDirectory $ocrDownloadsDirectory `
            -LockPath $ocrLockPath `
            -ValidateOnly
        Assert-NoReparsePoints $validationRoot 'Cache-only redistribution validation directory'
    }
    finally {
        Remove-ControlledStaging $workingRoot $validationRoot
    }
    Write-Host 'Every cached offline component, license, CUDA archive, Magika dependency artifact, and FLOSS redistribution closure matches its locked bytes.'
    return
}

$stagingRoot = Join-Path $workingRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
$completed = $false
try {
    $staged = @{
        bstrings = Join-Path $stagingRoot 'bstrings'
        python = Join-Path $stagingRoot 'python'
        magika = Join-Path $stagingRoot 'magika'
        magikaRedistribution = Join-Path $stagingRoot 'magika-redistribution'
        floss = Join-Path $stagingRoot 'floss'
        flossRedistribution = Join-Path $stagingRoot 'floss-redistribution'
        llamaCpp = Join-Path $stagingRoot 'llama'
        llamaSourceArchive = Join-Path $stagingRoot 'llama-source'
        llamaBuild = Join-Path $stagingRoot 'llama-build'
        translationModel = Join-Path $stagingRoot 'model'
        ocr = Join-Path $stagingRoot 'ocr-components'
    }
    [IO.Directory]::CreateDirectory($staged.bstrings) | Out-Null
    foreach ($publishedItem in Get-ChildItem -LiteralPath $publishedDirectory -Force) {
        Copy-Item `
            -LiteralPath $publishedItem.FullName `
            -Destination $staged.bstrings `
            -Recurse `
            -Force
    }
    foreach ($componentName in @('python', 'magika', 'floss')) {
        Expand-VerifiedZip `
            $downloadPaths[$componentName] `
            $staged[$componentName] `
            "$componentName archive"
    }
    Expand-VerifiedZip `
        $downloadPaths.llamaCpp `
        $staged.llamaSourceArchive `
        'llamaCpp source archive'
    $llamaSourceRoot = Join-Path `
        $staged.llamaSourceArchive `
        ([string]$llamaComponent.sourceRoot)
    if (-not (Test-Path -LiteralPath $llamaSourceRoot -PathType Container)) {
        throw "Pinned llama.cpp archive did not contain its locked source root: $llamaSourceRoot"
    }
    $llamaSourceSiblings = @(
        Get-ChildItem -LiteralPath $staged.llamaSourceArchive -Force
    )
    if ($llamaSourceSiblings.Count -ne 1 -or -not $llamaSourceSiblings[0].PSIsContainer) {
        throw 'Pinned llama.cpp archive must contain exactly one top-level source directory.'
    }
    $llamaBuildResult = @(
        & $llamaBuilder `
            -SourceDirectory $llamaSourceRoot `
            -BuildDirectory $staged.llamaBuild `
            -OutputDirectory $staged.llamaCpp `
            -ComponentLockPath $lockPath `
            -VisualCppRuntimeDirectory $resolvedVisualCppRuntime
    )
    if ($llamaBuildResult.Count -ne 1) {
        throw 'Pinned llama.cpp CPU runtime builder did not return one verified result.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $staged.llamaCpp 'llama-build-provenance.json') -PathType Leaf)) {
        throw 'Pinned llama.cpp CPU runtime builder did not emit build provenance.'
    }
    $forbiddenLlamaFiles = @(
        Get-ChildItem -LiteralPath $staged.llamaCpp -Recurse -File -Force | Where-Object {
            $_.Name -like 'libomp140*.dll' -or
            $_.FullName -match '(?i)(^|[\\/])debug_nonredist([\\/]|$)'
        }
    )
    if ($forbiddenLlamaFiles.Count -ne 0) {
        throw "Locked llama.cpp archive contains a non-redistributable Visual Studio OpenMP/debug artifact: $($forbiddenLlamaFiles.FullName -join ', ')"
    }
    $cudaStageResult = @(
        & $cudaOverlayStager `
            -BackendArchive $cudaArchivePaths['llama-cuda-backend'] `
            -CudaRuntimeArchive $cudaArchivePaths['cuda-runtime'] `
            -LicenseFile $cudaLicensePath `
            -DestinationDirectory $staged.llamaCpp `
            -ComponentLockPath $lockPath
    )
    if ($cudaStageResult.Count -ne 1) {
        throw 'Pinned official llama.cpp CUDA overlay stager did not return one verified result.'
    }

    [IO.Directory]::CreateDirectory($staged.translationModel) | Out-Null
    $modelDestination = Join-Path `
        $staged.translationModel `
        ([string]$lock.components.translationModel.fileName)
    try {
        New-Item `
            -ItemType HardLink `
            -Path $modelDestination `
            -Target $downloadPaths.translationModel `
            -ErrorAction Stop | Out-Null
    }
    catch {
        Copy-Item -LiteralPath $downloadPaths.translationModel -Destination $modelDestination
    }
    Assert-ExactFile `
        $modelDestination `
        ([long]$lock.components.translationModel.bytes) `
        ([string]$lock.components.translationModel.sha256) `
        'Staged translation model'

    foreach ($componentName in @('magika', 'floss', 'translationModel')) {
        $license = $lock.components.$componentName.license
        Copy-Item `
            -LiteralPath $licensePaths[$componentName] `
            -Destination (Join-Path $staged[$componentName] ([string]$license.fileName))
    }
    $copiedProfileLicenses = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($profileName in $translationProfileNames) {
        $licenseName = [string]$translationProfileSpecs[$profileName].license.fileName
        if ($copiedProfileLicenses.Add($licenseName)) {
            Copy-Item `
                -LiteralPath $translationProfileLicensePaths[$profileName] `
                -Destination (Join-Path $staged.translationModel $licenseName) `
                -Force
        }
    }

    $null = New-VerifiedMagikaRedistribution `
        -DestinationDirectory $staged.magikaRedistribution
    $archiveMagika = Join-Path $staged.magika ([string]$lock.components.magika.executable)
    $overlayMagika = Join-Path `
        $staged.magikaRedistribution `
        ([string]$magikaRedistribution.runtimeExecutable)
    if (
        (Get-FileHash -LiteralPath $archiveMagika -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $overlayMagika -Algorithm SHA256).Hash
    ) {
        throw 'The lock-pinned Magika archive and redistribution overlay contain different executables.'
    }
    $flossExecutable = Join-Path $staged.floss ([string]$lock.components.floss.executable)
    $null = New-VerifiedFlossRedistribution `
        -FlossExecutable $flossExecutable `
        -DestinationDirectory $staged.flossRedistribution

    & $ocrBuilder `
        -DestinationDirectory $staged.ocr `
        -DownloadCacheDirectory $ocrDownloadsDirectory `
        -LockPath $ocrLockPath

    $runtimeStageResult = @(
        & $runtimeStager `
            -ComponentLockPath $lockPath `
            -VisualCppRuntimeDirectory $resolvedVisualCppRuntime `
            -DestinationDirectory @(
                $staged.bstrings,
                $staged.python,
                $staged.magika,
                $staged.floss,
                $staged.llamaCpp,
                (Join-Path $staged.ocr 'runtime\ocr-cpu'),
                (Join-Path $staged.ocr 'runtime\ocr-directml')
            )
    )
    if (
        $runtimeStageResult.Count -ne 1 -or
        @($runtimeStageResult[0].destinations).Count -ne 7
    ) {
        throw 'Visual C++ runtime staging helper did not verify all seven app-local destinations.'
    }
    & python -I -B $ocrStagingHelper refresh-inventory `
        --output $staged.ocr `
        --visual-cpp-runtime $resolvedVisualCppRuntime
    if ($LASTEXITCODE -ne 0) {
        throw "OCR runtime-inventory refresh failed with exit code $LASTEXITCODE."
    }
    Assert-NoReparsePoints $stagingRoot 'Offline component staging directory'

    $builder = Join-Path $PSScriptRoot 'Build-AirgapBundle.ps1'
    & $builder `
        -OutputDirectory $outputRoot `
        -PublishedBstringsDirectory $staged.bstrings `
        -PythonDirectory $staged.python `
        -MagikaDirectory $staged.magika `
        -MagikaRedistributionDirectory $staged.magikaRedistribution `
        -FlossDirectory $staged.floss `
        -FlossRedistributionDirectory $staged.flossRedistribution `
        -LlamaDirectory $staged.llamaCpp `
        -TranslationModelDirectory $staged.translationModel `
        -VisualCppRuntimeDirectory $resolvedVisualCppRuntime `
        -OcrComponentsDirectory $staged.ocr `
        -TranslationModelRevision ([string]$lock.components.translationModel.revision) `
        -TranslationModelId ([string]$lock.components.translationModel.modelId) `
        -TranslationModel ([string]$lock.components.translationModel.fileName) `
        -TranslationProfile $TranslationProfile `
        -ComponentLockPath $lockPath
    $completed = $true
}
finally {
    if ($KeepStaging) {
        Write-Host "Component staging retained: $stagingRoot"
    }
    else {
        Remove-ControlledStaging $workingRoot $stagingRoot
    }
}

if (-not $completed) {
    throw 'Complete offline bundle construction did not complete.'
}
Write-Host "Complete offline $TranslationProfile bundle is ready: $outputRoot"
