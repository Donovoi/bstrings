[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DestinationDirectory,
    [string]$AssetsPath,
    [string]$InventoryPath,
    [string]$DownloadCacheDirectory,
    [string]$DeviceIoLicensePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($AssetsPath)) {
    $AssetsPath = Join-Path $repoRoot 'bstrings\obj\project.assets.json'
}
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath = Join-Path $repoRoot 'licenses\bstrings-managed-win-x64.json'
}
if ([string]::IsNullOrWhiteSpace($DownloadCacheDirectory)) {
    $DownloadCacheDirectory = Join-Path ([IO.Path]::GetTempPath()) 'bstrings-release-notices'
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
        if (
            [IO.File]::Exists($cursor) -or
            [IO.Directory]::Exists($cursor)
        ) {
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
    if ($hash -ne $ExpectedSha256) {
        throw "$Name SHA-256 mismatch: expected $ExpectedSha256, found ${hash}: $resolved"
    }
    return $resolved
}

function Resolve-PackageRoot($Assets, [string]$PackagePath, [string]$Name) {
    foreach ($packageFolder in @($Assets.packageFolders.Keys)) {
        $candidate = Join-Path $packageFolder ($PackagePath -replace '/', '\')
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            Assert-NoReparseTraversal $candidate $Name
            return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $candidate).Path)
        }
    }
    throw "$Name was not restored in any project.assets.json package folder: $PackagePath"
}

function Get-VerifiedDownload($Spec, [string]$CacheDirectory) {
    if (-not [string]::IsNullOrWhiteSpace($DeviceIoLicensePath)) {
        return Assert-ExactFile `
            $DeviceIoLicensePath `
            ([long]$Spec.bytes) `
            ([string]$Spec.sha256) `
            'Pinned DeviceIOControlLib license'
    }
    $uri = [Uri]([string]$Spec.source)
    if ($uri.Scheme -ne [Uri]::UriSchemeHttps -or -not $uri.IsAbsoluteUri) {
        throw "Release notice download must use an absolute HTTPS URL: $($Spec.source)"
    }
    [IO.Directory]::CreateDirectory($CacheDirectory) | Out-Null
    Assert-NoReparseTraversal $CacheDirectory 'Release notice download cache'
    $cachePath = Join-Path $CacheDirectory ([IO.Path]::GetFileName([string]$Spec.destination))
    if (Test-Path -LiteralPath $cachePath -PathType Leaf) {
        try {
            return Assert-ExactFile `
                $cachePath `
                ([long]$Spec.bytes) `
                ([string]$Spec.sha256) `
                'Cached DeviceIOControlLib license'
        }
        catch {
            [IO.File]::Delete($cachePath)
        }
    }
    elseif (Test-Path -LiteralPath $cachePath) {
        throw "Release notice cache target is not a regular file: $cachePath"
    }

    $temporaryPath = $cachePath + '.partial.' + [Guid]::NewGuid().ToString('N')
    $client = [Net.Http.HttpClient]::new()
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
    $response = $null
    $inputStream = $null
    $memory = $null
    try {
        $client.Timeout = [TimeSpan]::FromMinutes(2)
        $response = $client.SendAsync(
            $request,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead
        ).GetAwaiter().GetResult()
        $null = $response.EnsureSuccessStatusCode()
        if (
            $null -eq $response.Content.Headers.ContentLength -or
            $response.Content.Headers.ContentLength -ne [long]$Spec.bytes
        ) {
            throw "Downloaded DeviceIOControlLib license Content-Length mismatch: expected $($Spec.bytes), found $($response.Content.Headers.ContentLength)."
        }
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $memory = [IO.MemoryStream]::new([int]$Spec.bytes)
        $buffer = [byte[]]::new([Math]::Min(4096, [int]$Spec.bytes + 1))
        $total = 0L
        while ($true) {
            $remaining = ([long]$Spec.bytes + 1L) - $total
            if ($remaining -le 0) {
                throw "Downloaded DeviceIOControlLib license exceeds its locked $($Spec.bytes)-byte length."
            }
            $read = $inputStream.Read(
                $buffer,
                0,
                [Math]::Min($buffer.Length, [int]$remaining)
            )
            if ($read -eq 0) {
                break
            }
            $memory.Write($buffer, 0, $read)
            $total += $read
        }
        if ($total -ne [long]$Spec.bytes) {
            throw "Downloaded DeviceIOControlLib license byte length mismatch: expected $($Spec.bytes), found $total."
        }
        $bytes = $memory.ToArray()
        [IO.File]::WriteAllBytes($temporaryPath, $bytes)
        $null = Assert-ExactFile `
            $temporaryPath `
            ([long]$Spec.bytes) `
            ([string]$Spec.sha256) `
            'Downloaded DeviceIOControlLib license'
        [IO.File]::Move($temporaryPath, $cachePath, $true)
    }
    finally {
        if ($null -ne $memory) {
            $memory.Dispose()
        }
        if ($null -ne $inputStream) {
            $inputStream.Dispose()
        }
        if ($null -ne $response) {
            $response.Dispose()
        }
        $request.Dispose()
        $client.Dispose()
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
    return Assert-ExactFile `
        $cachePath `
        ([long]$Spec.bytes) `
        ([string]$Spec.sha256) `
        'Cached DeviceIOControlLib license'
}

$assetsFile = Resolve-ExistingFile $AssetsPath 'project.assets.json'
$inventoryFile = Resolve-ExistingFile $InventoryPath 'Managed release-notice inventory'
$assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json -AsHashtable
$inventory = Get-Content -LiteralPath $inventoryFile -Raw | ConvertFrom-Json
if ($inventory.schemaVersion -ne 1) {
    throw "Unsupported managed release-notice inventory schema: $($inventory.schemaVersion)"
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw "Release notice staging destination is not a directory: $destination"
    }
    if (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1) {
        throw "Release notice staging destination must be new or empty: $destination"
    }
}
else {
    [IO.Directory]::CreateDirectory($destination) | Out-Null
}
Assert-NoReparseTraversal $destination 'Release notice staging destination'
if (((Get-Item -LiteralPath $destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Release notice staging destination is a link or reparse point: $destination"
}

$packageById = @{}
foreach ($package in @($inventory.packages)) {
    $packageById[[string]$package.id] = $package
}
$runtimeRoot = Resolve-PackageRoot `
    $assets `
    ([string]$inventory.runtimePack.packagePath) `
    'Pinned .NET runtime pack'

$rustOutput = @(& rustc --version --verbose 2>&1)
if ($LASTEXITCODE -ne 0 -or -not ($rustOutput -contains 'release: 1.95.0')) {
    throw "rustc 1.95.0 is required to stage its standard-library attribution: $($rustOutput -join [Environment]::NewLine)"
}
$rustSysroot = (& rustc --print sysroot 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($rustSysroot)) {
    throw 'rustc --print sysroot did not return the pinned Rust 1.95.0 sysroot.'
}

foreach ($spec in @($inventory.releaseNoticeSources)) {
    $source = switch ([string]$spec.sourceType) {
        'download' {
            Get-VerifiedDownload $spec $DownloadCacheDirectory
            break
        }
        'nuget-package' {
            $package = $packageById[[string]$spec.packageId]
            if ($null -eq $package) {
                throw "Release notice references an unknown package: $($spec.packageId)"
            }
            $packageRoot = Resolve-PackageRoot `
                $assets `
                ([string]$package.packagePath) `
                "NuGet package $($package.id)"
            Join-Path $packageRoot ([string]$spec.source)
            break
        }
        'runtime-pack' {
            Join-Path $runtimeRoot ([string]$spec.source)
            break
        }
        'rust-sysroot' {
            if ([string]$spec.rustVersion -ne '1.95.0') {
                throw "Unsupported Rust notice version in inventory: $($spec.rustVersion)"
            }
            Join-Path $rustSysroot (([string]$spec.source) -replace '/', '\')
            break
        }
        default {
            throw "Unsupported release notice source type: $($spec.sourceType)"
        }
    }
    $verifiedSource = Assert-ExactFile `
        $source `
        ([long]$spec.bytes) `
        ([string]$spec.sha256) `
        "Release notice source $($spec.destination)"
    $target = [IO.Path]::GetFullPath(
        (Join-Path $destination (([string]$spec.destination) -replace '/', '\'))
    )
    $prefix = $destination.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release notice destination escapes its staging root: $($spec.destination)"
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Assert-NoReparseTraversal ([IO.Path]::GetDirectoryName($target)) 'Release notice staging destination'
    [IO.File]::Copy($verifiedSource, $target, $false)
    $null = Assert-ExactFile `
        $target `
        ([long]$spec.bytes) `
        ([string]$spec.sha256) `
        "Staged release notice $($spec.destination)"
}

Write-Host "Staged and byte-verified $(@($inventory.releaseNoticeSources).Count) release notices: $destination"
[pscustomobject]@{
    directory = $destination
    files = @($inventory.releaseNoticeSources | ForEach-Object { [string]$_.destination })
}
