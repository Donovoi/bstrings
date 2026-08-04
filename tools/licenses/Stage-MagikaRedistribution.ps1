[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DestinationDirectory,
    [string]$InventoryPath,
    [string]$DownloadCacheDirectory,
    [switch]$CacheOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath = Join-Path $repoRoot 'licenses\magika-cli-1.1.0-redistribution.json'
}
if ([string]::IsNullOrWhiteSpace($DownloadCacheDirectory)) {
    $DownloadCacheDirectory = Join-Path ([IO.Path]::GetTempPath()) 'bstrings-magika-redistribution-cache'
}

function Assert-NoReparseTraversal([string]$Path, [string]$Name) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($root)) {
        throw "$Name has no filesystem root: $fullPath"
    }
    $cursor = $root
    foreach ($segment in $fullPath.Substring($root.Length).Split(
        [IO.Path]::DirectorySeparatorChar,
        [StringSplitOptions]::RemoveEmptyEntries
    )) {
        $cursor = Join-Path $cursor $segment
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Name contains a link or reparse point: $cursor"
            }
        }
    }
}

function Resolve-ExistingFile([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name was not found: $Path"
    }
    Assert-NoReparseTraversal $Path $Name
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    if (((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $resolved"
    }
    return $resolved
}

function Assert-ExactFile(
    [string]$Path,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$Name
) {
    $resolved = Resolve-ExistingFile $Path $Name
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Length -ne $ExpectedBytes) {
        throw "$Name byte length mismatch: expected $ExpectedBytes, found $($item.Length): $resolved"
    }
    $hash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $ExpectedSha256) {
        throw "$Name SHA-256 mismatch: expected $ExpectedSha256, found ${hash}: $resolved"
    }
    return $resolved
}

function Resolve-ContainedPath([string]$Root, [string]$RelativePath, [string]$Name) {
    if (
        [string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath)
    ) {
        throw "$Name must be a non-empty relative path: '$RelativePath'"
    }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $candidate = [IO.Path]::GetFullPath(
        (Join-Path $rootFull ($RelativePath -replace '/', [IO.Path]::DirectorySeparatorChar))
    )
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escapes its root: $RelativePath"
    }
    return $candidate
}

function Get-VerifiedDownload($Spec, [string]$CacheRoot, [bool]$AllowDownload) {
    $uri = [Uri]([string]$Spec.url)
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -cne [Uri]::UriSchemeHttps) {
        throw "Magika redistribution downloads must use absolute HTTPS URLs: $($Spec.url)"
    }
    $cacheName = [IO.Path]::GetFileName(([string]$Spec.destination -replace '/', '\'))
    if ([string]::IsNullOrWhiteSpace($cacheName)) {
        throw "Download artifact has no cache file name: $($Spec.id)"
    }
    $cachePath = Resolve-ContainedPath $CacheRoot $cacheName "Download cache path for $($Spec.id)"
    if (Test-Path -LiteralPath $cachePath -PathType Leaf) {
        return Assert-ExactFile `
            $cachePath `
            ([long]$Spec.bytes) `
            ([string]$Spec.sha256) `
            "Cached artifact $($Spec.id)"
    }
    if (Test-Path -LiteralPath $cachePath) {
        throw "Download cache target is not a regular file: $cachePath"
    }
    if (-not $AllowDownload) {
        throw "Cache-only validation is missing locked artifact $($Spec.id): $cachePath"
    }

    $partialPath = $cachePath + '.partial.' + [Guid]::NewGuid().ToString('N')
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(20)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('bstrings-release-audit/1.0')
    $response = $null
    $inputStream = $null
    $outputStream = $null
    $hasher = $null
    try {
        $response = $client.GetAsync(
            $uri,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead
        ).GetAwaiter().GetResult()
        $null = $response.EnsureSuccessStatusCode()
        $finalUri = $response.RequestMessage.RequestUri
        if (
            $null -eq $finalUri -or
            -not $finalUri.IsAbsoluteUri -or
            $finalUri.Scheme -cne [Uri]::UriSchemeHttps
        ) {
            throw "Final download URI is not absolute HTTPS for $($Spec.id): $finalUri"
        }
        if (
            $null -ne $response.Content.Headers.ContentLength -and
            $response.Content.Headers.ContentLength -ne [long]$Spec.bytes
        ) {
            throw "Download Content-Length mismatch for $($Spec.id): expected $($Spec.bytes), found $($response.Content.Headers.ContentLength)."
        }
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $outputStream = [IO.FileStream]::new(
            $partialPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            1MB,
            [IO.FileOptions]::SequentialScan
        )
        $hasher = [Security.Cryptography.SHA256]::Create()
        $buffer = [byte[]]::new(1MB)
        $total = 0L
        while ($true) {
            $remaining = ([long]$Spec.bytes + 1L) - $total
            if ($remaining -le 0) {
                throw "Download exceeds its locked $($Spec.bytes)-byte length: $($Spec.id)"
            }
            $read = $inputStream.Read($buffer, 0, [Math]::Min($buffer.Length, [int]$remaining))
            if ($read -eq 0) {
                break
            }
            $outputStream.Write($buffer, 0, $read)
            $null = $hasher.TransformBlock($buffer, 0, $read, $buffer, 0)
            $total += $read
        }
        $null = $hasher.TransformFinalBlock([byte[]]::new(0), 0, 0)
        $outputStream.Flush($true)
        $outputStream.Dispose()
        $outputStream = $null
        if ($total -ne [long]$Spec.bytes) {
            throw "Download byte length mismatch for $($Spec.id): expected $($Spec.bytes), found $total."
        }
        $hash = [Convert]::ToHexString($hasher.Hash).ToLowerInvariant()
        if ($hash -cne [string]$Spec.sha256) {
            throw "Download SHA-256 mismatch for $($Spec.id): expected $($Spec.sha256), found $hash."
        }
        [IO.File]::Move($partialPath, $cachePath, $false)
    }
    finally {
        if ($null -ne $hasher) { $hasher.Dispose() }
        if ($null -ne $outputStream) { $outputStream.Dispose() }
        if ($null -ne $inputStream) { $inputStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose()
        $handler.Dispose()
        if ([IO.File]::Exists($partialPath)) {
            [IO.File]::Delete($partialPath)
        }
    }
    return Assert-ExactFile `
        $cachePath `
        ([long]$Spec.bytes) `
        ([string]$Spec.sha256) `
        "Downloaded artifact $($Spec.id)"
}

function Copy-ExactFile($Spec, [string]$Source, [string]$Root, [string]$Name) {
    $verifiedSource = Assert-ExactFile `
        $Source `
        ([long]$Spec.bytes) `
        ([string]$Spec.sha256) `
        $Name
    $target = Resolve-ContainedPath $Root ([string]$Spec.destination) "Destination for $Name"
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Assert-NoReparseTraversal ([IO.Path]::GetDirectoryName($target)) "Destination for $Name"
    if ([IO.File]::Exists($target) -or [IO.Directory]::Exists($target)) {
        throw "Destination already exists for ${Name}: $target"
    }
    [IO.File]::Copy($verifiedSource, $target, $false)
    return Assert-ExactFile `
        $target `
        ([long]$Spec.bytes) `
        ([string]$Spec.sha256) `
        "Staged $Name"
}

function Expand-ExactZipEntry(
    [string]$ArchivePath,
    [string]$EntryName,
    [string]$TargetPath,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$Name
) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries | Where-Object FullName -CEQ $EntryName)
        if ($entries.Count -ne 1) {
            throw "$Name container must contain exactly one '$EntryName' entry; found $($entries.Count)."
        }
        if ($entries[0].Length -ne $ExpectedBytes) {
            throw "$Name entry byte length mismatch: expected $ExpectedBytes, found $($entries[0].Length)."
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($TargetPath)) | Out-Null
        if ([IO.File]::Exists($TargetPath) -or [IO.Directory]::Exists($TargetPath)) {
            throw "$Name destination already exists: $TargetPath"
        }
        $inputStream = $entries[0].Open()
        $outputStream = $null
        try {
            $outputStream = [IO.FileStream]::new(
                $TargetPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None
            )
            $inputStream.CopyTo($outputStream)
        }
        finally {
            if ($null -ne $outputStream) { $outputStream.Dispose() }
            $inputStream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
    return Assert-ExactFile $TargetPath $ExpectedBytes $ExpectedSha256 $Name
}

function Expand-ExactTarEntry(
    [string]$ArchivePath,
    [string]$EntryName,
    [string]$TargetPath,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$StagingRoot,
    [string]$Name
) {
    $tar = Get-Command tar.exe -CommandType Application -ErrorAction Stop
    $temporaryRoot = Resolve-ContainedPath `
        $StagingRoot `
        ('.extract-' + [Guid]::NewGuid().ToString('N')) `
        "$Name temporary extraction root"
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    try {
        $output = @(& $tar.Source -xf $ArchivePath -C $temporaryRoot -- $EntryName 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "$Name extraction failed: $($output -join [Environment]::NewLine)"
        }
        $extracted = Resolve-ContainedPath $temporaryRoot $EntryName "$Name extracted entry"
        $verified = Assert-ExactFile $extracted $ExpectedBytes $ExpectedSha256 "$Name extracted entry"
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($TargetPath)) | Out-Null
        if ([IO.File]::Exists($TargetPath) -or [IO.Directory]::Exists($TargetPath)) {
            throw "$Name destination already exists: $TargetPath"
        }
        [IO.File]::Copy($verified, $TargetPath, $false)
    }
    finally {
        $rootFull = [IO.Path]::GetFullPath($StagingRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $tempFull = [IO.Path]::GetFullPath($temporaryRoot)
        if (
            [IO.Directory]::Exists($tempFull) -and
            $tempFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        ) {
            [IO.Directory]::Delete($tempFull, $true)
        }
    }
    return Assert-ExactFile $TargetPath $ExpectedBytes $ExpectedSha256 $Name
}

$inventoryFile = Resolve-ExistingFile $InventoryPath 'Magika redistribution inventory'
$inventory = Get-Content -LiteralPath $inventoryFile -Raw | ConvertFrom-Json
if (
    $inventory.schemaVersion -ne 1 -or
    [string]$inventory.component -cne 'magika-cli' -or
    [string]$inventory.version -cne '1.1.0' -or
    [string]$inventory.target -cne 'x86_64-pc-windows-msvc'
) {
    throw 'Unsupported Magika redistribution inventory.'
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw "Staging destination is not a directory: $destination"
    }
    if (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1) {
        throw "Staging destination must be new or empty: $destination"
    }
}
else {
    [IO.Directory]::CreateDirectory($destination) | Out-Null
}
Assert-NoReparseTraversal $destination 'Magika redistribution destination'
if (((Get-Item -LiteralPath $destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Staging destination is a link or reparse point: $destination"
}

$cacheRoot = [IO.Path]::GetFullPath($DownloadCacheDirectory)
[IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
Assert-NoReparseTraversal $cacheRoot 'Magika redistribution download cache'
if (((Get-Item -LiteralPath $cacheRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Download cache is a link or reparse point: $cacheRoot"
}

$stagedById = @{}
$seenDownloadDestinations = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($spec in @($inventory.downloadArtifacts)) {
    if (
        $spec.PSObject.Properties.Name -notcontains 'ship' -or
        $spec.ship -isnot [bool]
    ) {
        throw "Download artifact $($spec.id) must have an explicit Boolean ship property."
    }
    if ($stagedById.ContainsKey([string]$spec.id)) {
        throw "Duplicate download artifact id: $($spec.id)"
    }
    if (-not $seenDownloadDestinations.Add([string]$spec.destination)) {
        throw "Duplicate download artifact destination: $($spec.destination)"
    }
    $source = Get-VerifiedDownload $spec $cacheRoot (-not $CacheOnly)
    $stagedById[[string]$spec.id] = $source
    if ([bool]$spec.ship) {
        $null = Copy-ExactFile `
            $spec `
            $source `
            $destination `
            "download artifact $($spec.id)"
    }
}

$runtimeInventoryPath = Join-Path $repoRoot (
    'licenses\' + [string]$inventory.runtimeInventory.path
)
$runtimeRows = @(Import-Csv -LiteralPath $runtimeInventoryPath -Delimiter "`t")
$registryRows = @($runtimeRows | Where-Object { $_.checksum_sha256 -match '^[0-9a-f]{64}$' })
if ($runtimeRows.Count -ne [int]$inventory.runtimeInventory.packageCount) {
    throw "Runtime package count drifted: expected $($inventory.runtimeInventory.packageCount), found $($runtimeRows.Count)."
}
if ($registryRows.Count -ne [int]$inventory.runtimeInventory.registryPackageCount) {
    throw "Registry package count drifted: expected $($inventory.runtimeInventory.registryPackageCount), found $($registryRows.Count)."
}

foreach ($spec in @($inventory.repositoryArtifacts)) {
    $source = Resolve-ContainedPath $repoRoot ([string]$spec.source) "Repository artifact $($spec.source)"
    $null = Copy-ExactFile $spec $source $destination "repository artifact $($spec.source)"
}

$manifestTarget = Resolve-ContainedPath `
    $destination `
    'licenses/magika-cli-1.1.0/magika-cli-1.1.0-redistribution.json' `
    'Redistribution inventory destination'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifestTarget)) | Out-Null
[IO.File]::Copy($inventoryFile, $manifestTarget, $false)

foreach ($spec in @($inventory.derivedArtifacts)) {
    $container = $stagedById[[string]$spec.containerId]
    if ([string]::IsNullOrWhiteSpace($container)) {
        throw "Derived artifact references an unknown container: $($spec.containerId)"
    }
    $target = Resolve-ContainedPath $destination ([string]$spec.destination) "Derived artifact $($spec.id)"
    if ([string]$spec.containerId -ceq 'rustc-archive') {
        $null = Expand-ExactTarEntry `
            $container `
            ([string]$spec.entry) `
            $target `
            ([long]$spec.bytes) `
            ([string]$spec.sha256) `
            $destination `
            "derived artifact $($spec.id)"
    }
    else {
        $null = Expand-ExactZipEntry `
            $container `
            ([string]$spec.entry) `
            $target `
            ([long]$spec.bytes) `
            ([string]$spec.sha256) `
            "derived artifact $($spec.id)"
    }
}

Write-Host ((
        'Staged Magika CLI {0}: {1} reviewed runtime packages, {2} verified cache inputs, ' +
        '{3} directly shipped artifacts, and {4} derived runtime/notices.'
    ) -f $inventory.version,
    $runtimeRows.Count,
    @($inventory.downloadArtifacts).Count,
    @($inventory.downloadArtifacts | Where-Object { [bool]$_.ship }).Count,
    @($inventory.derivedArtifacts).Count)

[pscustomobject]@{
    directory = $destination
    packages = $runtimeRows.Count
    cacheInputs = @($inventory.downloadArtifacts).Count
    shippedDownloads = @($inventory.downloadArtifacts | Where-Object { [bool]$_.ship }).Count
    derived = @($inventory.derivedArtifacts).Count
}
