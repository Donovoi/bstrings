[CmdletBinding()]
param(
    [string]$DestinationDirectory = (Join-Path (Get-Location).Path 'bstrings-kit'),
    [string]$InstallerCacheDirectory,
    [string]$ReleaseTag = 'v2.0.0',
    [switch]$KeepCache,
    [ValidateRange(1, 10)]
    [int]$AcquireAttempts = 3,
    [switch]$RemoveCacheAfterSuccess,
    [Parameter(DontShow = $true)]
    [uri]$ReleaseApiUri,
    [Parameter(DontShow = $true)]
    [switch]$AllowLoopbackHttpForTesting,
    [Parameter(DontShow = $true)]
    [ValidateRange(1, [long]::MaxValue)]
    [long]$MinimumFreeBytes = 30GB
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedReleaseTag = 'v2.0.0'
$repository = 'Donovoi/bstrings'
$trustManifestName = 'bundle-packs.json'
$coreArchiveName = 'bstrings-win-x64.zip'
$checksumName = 'SHA256SUMS.txt'
$installerName = 'Install-Bstrings.ps1'
$compatiblePriorManifestSha256 = @(
    # Immutable v1.9.17 complete-kit manifest. This private compatibility
    # identity permits a verified existing installation to seed one upgrade.
    '8685a859dbf3927e076f974f21290b62036e6be43c0b7238e32e76e4a1ed5142'
)
$maximumMetadataBytes = 4MB
$maximumCoreBytes = 2000000000
$productionMinimumFreeBytes = 30GB
$installationSucceeded = $false
$ownedCache = $false
$cacheRoot = $null
$legacyCacheRoot = $null
$destination = $null
$destinationParent = $null
$stagingDestination = $null
$stagingDestinationCreated = $false
$backupDestination = $null
$backupDestinationCreated = $false
$publishedDestination = $false
$legacyInstallation = $null
$removeLegacyInstallationAfterSuccess = $false

function Write-InstallerProgress([double]$Percent, [string]$Activity) {
    if ($Percent -lt 0 -or $Percent -gt 100 -or [string]::IsNullOrWhiteSpace($Activity)) {
        throw 'Installer progress requires a percentage from 0 through 100 and an activity.'
    }
    $formatted = $Percent.ToString('F1', [Globalization.CultureInfo]::InvariantCulture)
    Write-Host "Progress: bstrings installer: $formatted% ($Activity)"
}

function Get-FullPath([string]$Path, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Name must not be empty."
    }
    try {
        return [IO.Path]::GetFullPath($Path)
    }
    catch {
        throw "$Name is not a valid path: $Path"
    }
}

function Get-NormalizedDirectoryPath([string]$Path, [string]$Name) {
    $fullPath = Get-FullPath $Path $Name
    $root = [IO.Path]::GetPathRoot($fullPath)
    if (
        -not [string]::IsNullOrWhiteSpace($root) -and
        $fullPath.Equals($root, [StringComparison]::OrdinalIgnoreCase)
    ) {
        return $root
    }
    return $fullPath.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
}

function Test-SameOrDescendant([string]$Candidate, [string]$Parent) {
    $candidatePath = Get-FullPath $Candidate 'Candidate path'
    $parentPath = (Get-FullPath $Parent 'Parent path').TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    if ($candidatePath.Equals($parentPath, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    $prefix = $parentPath + [IO.Path]::DirectorySeparatorChar
    return $candidatePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-PhysicalItem([string]$Path, [string]$Name, [bool]$Directory) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (
        [bool]$item.PSIsContainer -ne $Directory -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        $kind = if ($Directory) { 'directory' } else { 'file' }
        throw "$Name must be a physical ${kind}: $Path"
    }
    return $item
}

function Assert-ExistingPathChain([string]$Path, [string]$Name) {
    $current = Get-FullPath $Path $Name
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Name crosses a link or reparse point: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) {
            break
        }
        $current = $parent
    }
}

function Ensure-PhysicalDirectory([string]$Path, [string]$Name) {
    $fullPath = Get-FullPath $Path $Name
    Assert-ExistingPathChain $fullPath $Name
    $existing = Get-Item -LiteralPath $fullPath -Force -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        [IO.Directory]::CreateDirectory($fullPath) | Out-Null
    }
    Assert-PhysicalItem $fullPath $Name $true | Out-Null
    return $fullPath
}

function Get-ExistingDirectoryAncestor([string]$Path, [string]$Name) {
    $current = Get-FullPath $Path $Name
    while ($true) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item) {
            if (
                -not $item.PSIsContainer -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "$Name has a linked or non-directory existing ancestor: $current"
            }
            return $item.FullName
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) {
            throw "Could not find an existing directory ancestor for ${Name}: $Path"
        }
        $current = $parent
    }
}

function Assert-FreeSpace([string[]]$Paths, [long]$RequiredBytes) {
    $checkedRoots = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($path in $Paths) {
        $ancestor = Get-ExistingDirectoryAncestor $path 'Install or cache path'
        $root = [IO.Path]::GetPathRoot($ancestor)
        if ([string]::IsNullOrWhiteSpace($root) -or -not $checkedRoots.Add($root)) {
            continue
        }
        try {
            $drive = [IO.DriveInfo]::new($root)
        }
        catch {
            throw "Could not inspect free space for volume '$root': $($_.Exception.Message)"
        }
        if (-not $drive.IsReady) {
            throw "Install volume is not ready: $root"
        }
        $available = [long]$drive.AvailableFreeSpace
        if ($available -lt $RequiredBytes) {
            throw "The bstrings installation requires at least $RequiredBytes free bytes on '$root'; found $available."
        }
    }
}

function Get-LowerSha256([string]$Path, [string]$Name) {
    $item = Assert-PhysicalItem $Path $Name $false
    if ([long]$item.Length -lt 1) {
        throw "$Name must not be empty: $Path"
    }
    $stream = [IO.File]::OpenRead($item.FullName)
    try {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $hasher.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-RequiredAsset([object]$Release, [string]$FileName, [long]$MaximumBytes) {
    $matches = @($Release.assets | Where-Object { [string]$_.name -ceq $FileName })
    if ($matches.Count -ne 1) {
        throw "The release must contain exactly one '$FileName' asset."
    }
    $asset = $matches[0]
    $bytes = [long]$asset.size
    if ($bytes -lt 1 -or $bytes -gt $MaximumBytes) {
        throw "Release asset '$FileName' has an invalid byte length: $bytes"
    }
    $digest = [string]$asset.digest
    if ($digest -cnotmatch '^sha256:(?<hash>[0-9a-f]{64})$') {
        throw "Release asset '$FileName' is missing a canonical SHA-256 API digest."
    }
    $actualUrl = [string]$asset.browser_download_url
    $expectedUrl = if ($AllowLoopbackHttpForTesting) {
        "http://127.0.0.1:$($ReleaseApiUri.Port)/$repository/releases/download/$ReleaseTag/$FileName"
    }
    else {
        "https://github.com/$repository/releases/download/$ReleaseTag/$FileName"
    }
    if ($actualUrl -cne $expectedUrl) {
        throw "Release asset '$FileName' does not use its exact canonical tagged URL."
    }
    return [pscustomobject]@{
        Name = $FileName
        Bytes = $bytes
        Sha256 = $Matches.hash
        Url = $actualUrl
    }
}

function Remove-ValidatedPartialFile([string]$Path, [string]$ExpectedParent) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $fullPath = Get-FullPath $Path 'Partial download'
    $parent = [IO.Path]::GetDirectoryName($fullPath)
    $leaf = [IO.Path]::GetFileName($fullPath)
    if (
        -not $parent.Equals(
            (Get-FullPath $ExpectedParent 'Partial-download parent'),
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        $leaf -cnotmatch '^\.[A-Za-z0-9._-]+\.download-[0-9a-f]{32}\.partial$'
    ) {
        throw "Refusing to remove an uncontrolled partial download: $fullPath"
    }
    Assert-PhysicalItem $fullPath 'Partial download' $false | Out-Null
    [IO.File]::Delete($fullPath)
}

function Receive-VerifiedAsset([object]$Asset, [string]$Directory) {
    $root = Ensure-PhysicalDirectory $Directory 'Release-asset cache directory'
    $destinationPath = Join-Path $root ([string]$Asset.Name)
    $existing = Get-Item -LiteralPath $destinationPath -Force -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Assert-PhysicalItem $destinationPath "Cached $($Asset.Name)" $false | Out-Null
        if (
            [long]$existing.Length -eq [long]$Asset.Bytes -and
            (Get-LowerSha256 $destinationPath "Cached $($Asset.Name)") -ceq [string]$Asset.Sha256
        ) {
            return $destinationPath
        }
    }

    $partialName = ".$($Asset.Name).download-$([Guid]::NewGuid().ToString('N')).partial"
    $partialPath = Join-Path $root $partialName
    try {
        Invoke-WebRequest `
            -UseBasicParsing `
            -Uri ([string]$Asset.Url) `
            -Headers $script:webHeaders `
            -OutFile $partialPath
        $partial = Assert-PhysicalItem $partialPath "Downloaded $($Asset.Name)" $false
        if ([long]$partial.Length -ne [long]$Asset.Bytes) {
            throw "Release API size mismatch for '$($Asset.Name)'."
        }
        $hash = Get-LowerSha256 $partialPath "Downloaded $($Asset.Name)"
        if ($hash -cne [string]$Asset.Sha256) {
            throw "Release API digest mismatch for '$($Asset.Name)'."
        }
        [IO.File]::Copy($partialPath, $destinationPath, $true)
        $publishedHash = Get-LowerSha256 $destinationPath "Cached $($Asset.Name)"
        if ($publishedHash -cne [string]$Asset.Sha256) {
            throw "Cached release asset changed while publishing '$($Asset.Name)'."
        }
        return $destinationPath
    }
    finally {
        Remove-ValidatedPartialFile $partialPath $root
    }
}

function Read-ChecksumMap([string]$Path) {
    $item = Assert-PhysicalItem $Path 'SHA256SUMS.txt' $false
    if ([long]$item.Length -lt 1 -or [long]$item.Length -gt $script:maximumMetadataBytes) {
        throw 'SHA256SUMS.txt has an invalid byte length.'
    }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $text = [IO.File]::ReadAllText($item.FullName, $encoding)
    if (-not $text.EndsWith("`n", [StringComparison]::Ordinal) -or $text.Contains("`r")) {
        throw 'SHA256SUMS.txt must contain canonical LF-terminated rows.'
    }
    $rows = @($text.Substring(0, $text.Length - 1) -split "`n")
    if ($rows.Count -lt 3 -or $rows.Count -gt 128) {
        throw 'SHA256SUMS.txt has an unexpected row count.'
    }
    $result = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($row in $rows) {
        if ($row -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^/\\]+)$') {
            throw "Invalid SHA256SUMS.txt row: $row"
        }
        $fileName = [string]$Matches.name
        if ($result.ContainsKey($fileName)) {
            throw "Duplicate SHA256SUMS.txt row: $($Matches.name)"
        }
        $result.Add($fileName, [string]$Matches.hash)
    }
    return $result
}

function Get-RequiredChecksum(
    [Collections.Generic.Dictionary[string, string]]$Checksums,
    [string]$FileName
) {
    $hash = $null
    if (-not $Checksums.TryGetValue($FileName, [ref]$hash)) {
        throw "SHA256SUMS.txt does not cover '$FileName'."
    }
    return [string]$hash
}

function Assert-AssetChecksum(
    [object]$Asset,
    [Collections.Generic.Dictionary[string, string]]$Checksums
) {
    $checksum = Get-RequiredChecksum $Checksums ([string]$Asset.Name)
    if ($checksum -cne [string]$Asset.Sha256) {
        throw "The release API digest and SHA256SUMS.txt differ for '$($Asset.Name)'."
    }
}

function Expand-VerifiedCore(
    [string]$ArchivePath,
    [string]$CacheDirectory,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256
) {
    if ($ExpectedBytes -lt 1 -or $ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Authenticated base runtime archive identity is invalid.'
    }
    $runtimeLeaf = '.core-runtime-' + [Guid]::NewGuid().ToString('N')
    $runtimeRoot = Join-Path $CacheDirectory $runtimeLeaf
    if (Test-Path -LiteralPath $runtimeRoot) {
        throw "Base runtime staging path unexpectedly exists: $runtimeRoot"
    }
    [IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
    Assert-PhysicalItem $runtimeRoot 'Base runtime staging directory' $true | Out-Null

    # Windows PowerShell 5.1 does not reliably load ZipArchive when only the
    # FileSystem companion assembly is requested.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = $null
    $archiveStream = $null
    try {
        $archiveItem = Assert-PhysicalItem $ArchivePath 'Base runtime archive' $false
        $archiveStream = [IO.FileStream]::new(
            $archiveItem.FullName,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read,
            1MB,
            [IO.FileOptions]::SequentialScan
        )
        if ([long]$archiveStream.Length -ne $ExpectedBytes) {
            throw 'The leased base runtime archive has an unexpected byte length.'
        }
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $leasedHash = ([BitConverter]::ToString(
                $hasher.ComputeHash($archiveStream)
            )).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $hasher.Dispose()
        }
        if ($leasedHash -cne $ExpectedSha256) {
            throw 'The leased base runtime archive failed exact SHA-256 authentication.'
        }
        $archiveStream.Position = 0
        $archive = [IO.Compression.ZipArchive]::new(
            $archiveStream,
            [IO.Compression.ZipArchiveMode]::Read,
            $true
        )
        if ($archive.Entries.Count -lt 1 -or $archive.Entries.Count -gt 10000) {
            throw 'The base runtime archive has an invalid entry count.'
        }
        $seen = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )
        $runtimePrefix = $runtimeRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        ) + [IO.Path]::DirectorySeparatorChar
        [long]$expandedBytes = 0
        foreach ($entry in $archive.Entries) {
            $name = [string]$entry.FullName
            if (
                [string]::IsNullOrWhiteSpace($name) -or
                $name.Length -gt 4096 -or
                $name.Contains('\') -or
                $name.Contains(':') -or
                $name.StartsWith('/', [StringComparison]::Ordinal)
            ) {
                throw "The base runtime archive contains an unsafe path: '$name'"
            }
            $isDirectory = $name.EndsWith('/', [StringComparison]::Ordinal)
            $segments = @($name.TrimEnd('/').Split('/'))
            if (
                $segments.Count -eq 0 -or
                @($segments | Where-Object {
                    [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..'
                }).Count -ne 0
            ) {
                throw "The base runtime archive contains an unsafe path: '$name'"
            }
            $relative = $segments -join [IO.Path]::DirectorySeparatorChar
            $target = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $relative))
            if (-not $target.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The base runtime archive path escapes its staging directory: '$name'"
            }
            if (-not $seen.Add($target)) {
                throw "The base runtime archive contains a duplicate path: '$name'"
            }
            $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
            if (
                $unixType -eq 0xA000 -or
                ($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "The base runtime archive contains a linked entry: '$name'"
            }
            if ($isDirectory) {
                [IO.Directory]::CreateDirectory($target) | Out-Null
                continue
            }
            if ([long]$entry.Length -lt 0) {
                throw "The base runtime archive contains an invalid entry length: '$name'"
            }
            $expandedBytes += [long]$entry.Length
            if ($expandedBytes -gt 4GB) {
                throw 'The base runtime archive expands beyond the installer safety limit.'
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            $input = $null
            $output = $null
            try {
                $input = $entry.Open()
                $output = [IO.FileStream]::new(
                    $target,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None
                )
                $input.CopyTo($output, 1MB)
            }
            finally {
                if ($null -ne $output) { $output.Dispose() }
                if ($null -ne $input) { $input.Dispose() }
            }
            if ([long](Get-Item -LiteralPath $target -Force).Length -ne [long]$entry.Length) {
                throw "The extracted base runtime entry has the wrong length: '$name'"
            }
        }
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
        if ($null -ne $archiveStream) { $archiveStream.Dispose() }
    }
    $executable = Join-Path $runtimeRoot 'bstrings.exe'
    Assert-PhysicalItem $executable 'Extracted base runtime bstrings.exe' $false | Out-Null
    return [pscustomobject]@{
        Root = $runtimeRoot
        Executable = $executable
    }
}

function Get-TrustManifestIdentity([string]$Path) {
    $item = Assert-PhysicalItem $Path 'Bundle trust manifest' $false
    if ([long]$item.Length -lt 2 -or [long]$item.Length -gt $script:maximumMetadataBytes) {
        throw 'The bundle trust manifest has an invalid byte length.'
    }
    try {
        $manifest = [IO.File]::ReadAllText(
            $item.FullName,
            [Text.UTF8Encoding]::new($false, $true)
        ) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The bundle trust manifest is not valid JSON: $($_.Exception.Message)"
    }
    $airgapHash = [string]$manifest.airgapManifestSha256
    if (
        [int]$manifest.schemaVersion -ne 1 -or
        [string]$manifest.profile -cne 'windows-x64-offline-v3' -or
        $airgapHash -cnotmatch '^[0-9a-f]{64}$'
    ) {
        throw 'The trust manifest is not the exact supported bstrings kit.'
    }
    return [pscustomobject]@{
        AirgapManifestSha256 = $airgapHash
        BundleIdentity = [string]$manifest.bundleIdentity
    }
}

function Assert-InstalledManifest([string]$BundleRoot, [string]$ExpectedHash) {
    $manifestPath = Join-Path $BundleRoot 'airgap-manifest.json'
    $actualHash = Get-LowerSha256 $manifestPath 'Installed air-gap manifest'
    if ($actualHash -cne $ExpectedHash) {
        throw 'The installed air-gap manifest does not match the bundle trust manifest.'
    }
}

function Import-VerifiedLegacyReleaseAsset(
    [object]$Asset,
    [string]$LegacyDirectory,
    [string]$DestinationDirectory
) {
    if (
        [string]::IsNullOrWhiteSpace($LegacyDirectory) -or
        -not (Test-Path -LiteralPath $LegacyDirectory -PathType Container)
    ) {
        return
    }
    $sourcePath = Join-Path $LegacyDirectory ([string]$Asset.Name)
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        return
    }
    try {
        Assert-ExistingPathChain $sourcePath 'Legacy release-asset cache entry'
        $source = Assert-PhysicalItem $sourcePath 'Legacy release-asset cache entry' $false
        if (
            [long]$source.Length -ne [long]$Asset.Bytes -or
            (Get-LowerSha256 $sourcePath 'Legacy release-asset cache entry') -cne
                [string]$Asset.Sha256
        ) {
            Write-Warning "Ignored an invalid legacy cached release asset: $($Asset.Name)"
            return
        }
        $destinationRoot = Ensure-PhysicalDirectory `
            $DestinationDirectory `
            'Release-asset cache directory'
        $destinationPath = Join-Path $destinationRoot ([string]$Asset.Name)
        if (Test-Path -LiteralPath $destinationPath) {
            return
        }
        $partialPath = Join-Path $destinationRoot (
            ".$($Asset.Name).download-$([Guid]::NewGuid().ToString('N')).partial"
        )
        try {
            [IO.File]::Copy($sourcePath, $partialPath, $false)
            $partial = Assert-PhysicalItem $partialPath 'Imported release-asset cache entry' $false
            if (
                [long]$partial.Length -ne [long]$Asset.Bytes -or
                (Get-LowerSha256 $partialPath 'Imported release-asset cache entry') -cne
                    [string]$Asset.Sha256
            ) {
                throw "Legacy release-asset import changed while copying '$($Asset.Name)'."
            }
            [IO.File]::Move($partialPath, $destinationPath)
        }
        finally {
            Remove-ValidatedPartialFile $partialPath $destinationRoot
        }
    }
    catch {
        Write-Warning "Could not reuse legacy release asset '$($Asset.Name)': $($_.Exception.Message)"
    }
}

function Import-VerifiedLegacyPackCache(
    [string]$ManifestPath,
    [string]$LegacyPackCache,
    [string]$DestinationPackCache
) {
    if (
        [string]::IsNullOrWhiteSpace($LegacyPackCache) -or
        -not (Test-Path -LiteralPath $LegacyPackCache -PathType Container)
    ) {
        return
    }
    try {
        Assert-ExistingPathChain $LegacyPackCache 'Legacy bundle-pack cache'
        Assert-PhysicalItem $LegacyPackCache 'Legacy bundle-pack cache' $true | Out-Null
        $manifest = [IO.File]::ReadAllText(
            (Assert-PhysicalItem $ManifestPath 'Bundle trust manifest' $false).FullName,
            [Text.UTF8Encoding]::new($false, $true)
        ) | ConvertFrom-Json -ErrorAction Stop
        $packs = @($manifest.packs)
        if ($packs.Count -lt 1 -or $packs.Count -gt 16) {
            throw 'The bundle trust manifest has an invalid pack count.'
        }
        foreach ($pack in $packs) {
            $bytes = [long]$pack.bytes
            $sha256 = [string]$pack.sha256
            $kind = if (
                -not ($pack.PSObject.Properties.Name -ccontains 'kind') -or
                [string]$pack.kind -ceq 'zip'
            ) {
                'zip'
            }
            elseif ([string]$pack.kind -ceq 'file') {
                'file'
            }
            else {
                throw "The bundle trust manifest has an unsupported pack kind: $($pack.kind)"
            }
            if ($bytes -lt 1 -or $sha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw 'The bundle trust manifest has an invalid pack identity.'
            }
            $relativeObjectPath = Join-Path `
                (Join-Path `
                    (Join-Path `
                        (Join-Path `
                            (Join-Path 'objects' 'v1') `
                            $kind
                        ) `
                        $bytes.ToString([Globalization.CultureInfo]::InvariantCulture)
                    ) `
                    $sha256.Substring(0, 2)
                ) `
                ($sha256 + '.object')
            $sourcePath = Join-Path $LegacyPackCache $relativeObjectPath
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                continue
            }
            try {
                Assert-ExistingPathChain $sourcePath 'Legacy bundle-pack cache object'
                $source = Assert-PhysicalItem `
                    $sourcePath `
                    'Legacy bundle-pack cache object' `
                    $false
                if (
                    [long]$source.Length -ne $bytes -or
                    (Get-LowerSha256 $sourcePath 'Legacy bundle-pack cache object') -cne $sha256
                ) {
                    Write-Warning "Ignored an invalid legacy bundle-pack object: $sha256"
                    continue
                }
                $destinationPath = Join-Path $DestinationPackCache $relativeObjectPath
                $destinationParent = Ensure-PhysicalDirectory `
                    ([IO.Path]::GetDirectoryName($destinationPath)) `
                    'Bundle-pack cache object parent'
                if (Test-Path -LiteralPath $destinationPath) {
                    continue
                }
                $partialPath = Join-Path $destinationParent (
                    ".$sha256.import-$([Guid]::NewGuid().ToString('N')).partial"
                )
                try {
                    [IO.File]::Copy($sourcePath, $partialPath, $false)
                    $partial = Assert-PhysicalItem `
                        $partialPath `
                        'Imported bundle-pack cache object' `
                        $false
                    if (
                        [long]$partial.Length -ne $bytes -or
                        (Get-LowerSha256 $partialPath 'Imported bundle-pack cache object') -cne
                            $sha256
                    ) {
                        throw "Legacy bundle-pack object changed while copying: $sha256"
                    }
                    [IO.File]::Move($partialPath, $destinationPath)
                }
                finally {
                    if (Test-Path -LiteralPath $partialPath) {
                        Assert-PhysicalItem `
                            $partialPath `
                            'Imported bundle-pack cache partial' `
                            $false | Out-Null
                        [IO.File]::Delete($partialPath)
                    }
                }
            }
            catch {
                Write-Warning "Could not reuse legacy bundle-pack object '$sha256': $($_.Exception.Message)"
            }
        }
    }
    catch {
        Write-Warning "Legacy bundle-pack cache reuse was skipped: $($_.Exception.Message)"
    }
}

function Assert-ExistingBundleShape([string]$BundleRoot) {
    Assert-PhysicalDirectoryTree $BundleRoot 'Existing destination'
    Assert-PhysicalItem `
        (Join-Path $BundleRoot 'bstrings.exe') `
        'Existing destination bstrings.exe' `
        $false | Out-Null
    Assert-PhysicalItem `
        (Join-Path $BundleRoot 'airgap-manifest.json') `
        'Existing destination air-gap manifest' `
        $false | Out-Null
}

function Assert-ExistingBundleIsReplaceable(
    [string]$BundleRoot,
    [string]$VerifierExecutable,
    [string]$CurrentManifestSha256
) {
    Assert-ExistingBundleShape $BundleRoot
    $manifestPath = Join-Path $BundleRoot 'airgap-manifest.json'
    $manifestSha256 = Get-LowerSha256 $manifestPath 'Existing destination air-gap manifest'
    $allowedManifestSha256 = @($CurrentManifestSha256) + @($script:compatiblePriorManifestSha256)
    if ($allowedManifestSha256 -cnotcontains $manifestSha256) {
        throw (
            'The existing destination is not a supported prior bstrings kit. ' +
            'Choose an empty destination or move the unrelated directory first.'
        )
    }
    Invoke-BundleVerify `
        $VerifierExecutable `
        $BundleRoot `
        'Existing destination ownership verification'
}

function ConvertTo-WindowsCommandLineArgument([string]$Value) {
    if ($null -eq $Value) {
        throw 'Native command arguments must not be null.'
    }
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }
    $builder = [Text.StringBuilder]::new()
    $null = $builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            $null = $builder.Append(('\' * (($backslashes * 2) + 1)))
            $null = $builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            $null = $builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        $null = $builder.Append($character)
    }
    if ($backslashes -gt 0) {
        $null = $builder.Append(('\' * ($backslashes * 2)))
    }
    $null = $builder.Append('"')
    return $builder.ToString()
}

function Invoke-NativeProcess([string]$Executable, [string[]]$Arguments) {
    Assert-PhysicalItem $Executable 'Native bstrings executable' $false | Out-Null
    $argumentLine = @(
        $Arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument ([string]$_) }
    ) -join ' '
    $process = Start-Process `
        -FilePath $Executable `
        -ArgumentList $argumentLine `
        -NoNewWindow `
        -Wait `
        -PassThru
    try {
        return [int]$process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-BundleVerify([string]$Executable, [string]$BundleRoot, [string]$Name) {
    $exitCode = Invoke-NativeProcess $Executable @(
        'bundle',
        'verify',
        '--bundle-root',
        $BundleRoot
    )
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode."
    }
}

function Assert-PhysicalDirectoryTree([string]$Path, [string]$Name) {
    Assert-PhysicalItem $Path $Name $true | Out-Null
    $linked = @(
        Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }
    )
    if ($linked.Count -ne 0) {
        throw "$Name contains a link or reparse point: $Path"
    }
}

function Remove-ValidatedDirectory(
    [string]$Path,
    [string]$ExpectedParent,
    [string]$ExpectedLeaf,
    [string]$Name
) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $fullPath = Get-FullPath $Path $Name
    $parent = [IO.Path]::GetDirectoryName($fullPath)
    $leaf = [IO.Path]::GetFileName($fullPath)
    if (
        -not $parent.Equals(
            (Get-FullPath $ExpectedParent "$Name parent"),
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        $leaf -cne $ExpectedLeaf -or
        [string]::IsNullOrWhiteSpace($leaf)
    ) {
        throw "Refusing to remove an uncontrolled $Name path: $fullPath"
    }
    Assert-PhysicalDirectoryTree $fullPath $Name
    [IO.Directory]::Delete($fullPath, $true)
}

function Remove-OwnedInstallerCache([string]$Path, [string]$ExpectedParent) {
    $fullPath = Get-FullPath $Path 'Shared installer cache'
    Assert-PhysicalDirectoryTree $fullPath 'Shared installer cache'
    $entries = @(Get-ChildItem -LiteralPath $fullPath -Force -ErrorAction Stop)
    $requiredNames = @('bundle-packs', 'release-assets')
    $actualNames = @($entries.Name)
    $unexpected = @($entries | Where-Object {
        -not $_.PSIsContainer -or
        (
            $_.Name -cnotin $requiredNames -and
            $_.Name -cnotmatch '^\.core-runtime-[0-9a-f]{32}$' -and
            $_.Name -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$'
        )
    })
    if (
        @($requiredNames | Where-Object { $actualNames -cnotcontains $_ }).Count -ne 0 -or
        $unexpected.Count -ne 0
    ) {
        throw 'Refusing to remove the shared installer cache because it contains unowned top-level entries.'
    }
    Remove-ValidatedDirectory `
        $fullPath `
        $ExpectedParent `
        '.bstrings-installer-cache' `
        'shared installer cache'
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The bstrings installer supports Windows only.'
}
$operatingSystemArchitecture = if (
    -not [string]::IsNullOrWhiteSpace($env:PROCESSOR_ARCHITEW6432)
) {
    $env:PROCESSOR_ARCHITEW6432
}
else {
    $env:PROCESSOR_ARCHITECTURE
}
if ($operatingSystemArchitecture -cne 'AMD64') {
    throw "The bstrings installer requires Windows x64; detected '$operatingSystemArchitecture'."
}
if ($ReleaseTag -cne $expectedReleaseTag) {
    throw "This installer is pinned to $expectedReleaseTag; '$ReleaseTag' is not supported."
}
if ($KeepCache -and $RemoveCacheAfterSuccess) {
    throw 'KeepCache and RemoveCacheAfterSuccess cannot be used together.'
}
if (
    $RemoveCacheAfterSuccess -and
    -not [string]::IsNullOrWhiteSpace($InstallerCacheDirectory)
) {
    throw 'RemoveCacheAfterSuccess cannot remove a caller-owned InstallerCacheDirectory.'
}

$expectedApiUri = [uri]"https://api.github.com/repos/$repository/releases/tags/$ReleaseTag"
if ($null -eq $ReleaseApiUri) {
    $ReleaseApiUri = $expectedApiUri
}
if ($AllowLoopbackHttpForTesting) {
    if (
        $ReleaseApiUri.Scheme -cne 'http' -or
        $ReleaseApiUri.Host -cne '127.0.0.1' -or
        $ReleaseApiUri.IsDefaultPort -or
        $ReleaseApiUri.AbsolutePath -cne "/repos/$repository/releases/tags/$ReleaseTag" -or
        -not [string]::IsNullOrEmpty($ReleaseApiUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($ReleaseApiUri.Query) -or
        -not [string]::IsNullOrEmpty($ReleaseApiUri.Fragment)
    ) {
        throw 'The test release API URI must be an exact loopback HTTP tag endpoint.'
    }
}
elseif ($ReleaseApiUri.AbsoluteUri -cne $expectedApiUri.AbsoluteUri) {
    throw "ReleaseApiUri must be the exact pinned GitHub tag endpoint: $expectedApiUri"
}
if (-not $AllowLoopbackHttpForTesting -and $MinimumFreeBytes -ne $productionMinimumFreeBytes) {
    throw "MinimumFreeBytes is fixed at $productionMinimumFreeBytes outside the isolated test path."
}

$destination = Get-NormalizedDirectoryPath $DestinationDirectory 'DestinationDirectory'
$destinationRoot = [IO.Path]::GetPathRoot($destination)
if (
    [string]::IsNullOrWhiteSpace($destinationRoot) -or
    $destination.Equals($destinationRoot, [StringComparison]::OrdinalIgnoreCase)
) {
    throw 'DestinationDirectory must not be a filesystem root.'
}
$destinationParent = [IO.Path]::GetDirectoryName($destination)
if ([string]::IsNullOrWhiteSpace($destinationParent)) {
    throw 'DestinationDirectory must have a parent directory.'
}
$existingDestination = Get-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
$usingDefaultDestination = -not $PSBoundParameters.ContainsKey('DestinationDirectory')
$legacyCandidate = $null
$legacyCandidateExists = $false
if ($usingDefaultDestination) {
    $legacyCandidate = Join-Path $destinationParent 'bstrings-quality'
    $legacyCandidateExists = Test-Path -LiteralPath $legacyCandidate
    if ($null -ne $existingDestination -and $legacyCandidateExists) {
        throw (
            'Both bstrings-kit and the previous bstrings-quality installation exist. ' +
            'Verify and remove or move one installation before you continue.'
        )
    }
}
if ($null -ne $existingDestination) {
    Assert-ExistingBundleShape $destination
}
Assert-ExistingPathChain $destination 'DestinationDirectory'

if (
    $null -eq $existingDestination -and
    $usingDefaultDestination -and
    $legacyCandidateExists
) {
    $legacyInstallation = Get-NormalizedDirectoryPath `
        $legacyCandidate `
        'Previous bstrings installation'
    if (
        (Test-SameOrDescendant $legacyInstallation $destination) -or
        (Test-SameOrDescendant $destination $legacyInstallation)
    ) {
        throw 'The previous and current installation paths must not overlap.'
    }
    Assert-ExistingBundleShape $legacyInstallation
    $removeLegacyInstallationAfterSuccess = $true
}

if ([string]::IsNullOrWhiteSpace($InstallerCacheDirectory)) {
    $ownedCache = $true
    $cacheRoot = Join-Path $destinationParent '.bstrings-installer-cache'
    $legacyCacheRoot = Join-Path $destinationParent '.bstrings-quality-installer-cache'
}
else {
    $cacheRoot = Get-NormalizedDirectoryPath `
        $InstallerCacheDirectory `
        'InstallerCacheDirectory'
}
$cacheRoot = Get-NormalizedDirectoryPath $cacheRoot 'InstallerCacheDirectory'
$cachePathRoot = [IO.Path]::GetPathRoot($cacheRoot)
if (
    [string]::IsNullOrWhiteSpace($cachePathRoot) -or
    $cacheRoot.Equals($cachePathRoot, [StringComparison]::OrdinalIgnoreCase)
) {
    throw 'InstallerCacheDirectory must not be a filesystem root.'
}
if (
    (Test-SameOrDescendant $cacheRoot $destination) -or
    (Test-SameOrDescendant $destination $cacheRoot)
) {
    throw 'DestinationDirectory and InstallerCacheDirectory must not overlap.'
}
Assert-ExistingPathChain $cacheRoot 'InstallerCacheDirectory'
if (
    $null -ne $legacyCacheRoot -and
    (Test-Path -LiteralPath $legacyCacheRoot)
) {
    $legacyCacheRoot = Get-NormalizedDirectoryPath `
        $legacyCacheRoot `
        'Legacy installer cache directory'
    if (
        (Test-SameOrDescendant $legacyCacheRoot $destination) -or
        (Test-SameOrDescendant $destination $legacyCacheRoot) -or
        (Test-SameOrDescendant $legacyCacheRoot $cacheRoot) -or
        (Test-SameOrDescendant $cacheRoot $legacyCacheRoot)
    ) {
        throw 'The legacy cache must not overlap the destination or current installer cache.'
    }
    Assert-ExistingPathChain $legacyCacheRoot 'Legacy installer cache directory'
    Assert-PhysicalItem $legacyCacheRoot 'Legacy installer cache directory' $true | Out-Null
}
Assert-FreeSpace @($destination, $cacheRoot) $MinimumFreeBytes

$destinationParent = Ensure-PhysicalDirectory $destinationParent 'Destination parent directory'
$cacheRoot = Ensure-PhysicalDirectory $cacheRoot 'Installer cache directory'
$releaseAssetRoot = Ensure-PhysicalDirectory `
    (Join-Path $cacheRoot 'release-assets') `
    'Release-asset cache root'
$releaseAssetCache = Ensure-PhysicalDirectory `
    (Join-Path $releaseAssetRoot $ReleaseTag) `
    'Release-asset cache directory'
$packCache = Ensure-PhysicalDirectory `
    (Join-Path $cacheRoot 'bundle-packs') `
    'Bundle-pack cache directory'

$script:webHeaders = @{
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = "bstrings-$ReleaseTag-installer"
}
$previousProgressPreference = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'

try {
    Write-InstallerProgress 0 'starting'
    if (-not $AllowLoopbackHttpForTesting) {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    $release = Invoke-RestMethod `
        -UseBasicParsing `
        -Uri $ReleaseApiUri `
        -Headers $script:webHeaders
    Write-InstallerProgress 5 'release metadata authenticated'
    if (
        [string]$release.tag_name -cne $ReleaseTag -or
        [bool]$release.draft -or
        [bool]$release.prerelease -or
        -not ($release.PSObject.Properties.Name -ccontains 'immutable') -or
        -not [bool]$release.immutable
    ) {
        throw "The release API did not return the exact immutable published $ReleaseTag release."
    }

    $installerAsset = Get-RequiredAsset $release $installerName $maximumMetadataBytes
    $checksumAsset = Get-RequiredAsset $release $checksumName $maximumMetadataBytes
    $coreAsset = Get-RequiredAsset $release $coreArchiveName $maximumCoreBytes
    $trustManifestAsset = Get-RequiredAsset `
        $release `
        $trustManifestName `
        $maximumMetadataBytes

    $selfPath = Get-FullPath $PSCommandPath 'Running installer path'
    $selfItem = Assert-PhysicalItem $selfPath 'Running installer' $false
    if ($selfItem.Name -cne $installerName) {
        throw "The installer must retain its exact release filename: $installerName"
    }
    if (
        [long]$selfItem.Length -ne [long]$installerAsset.Bytes -or
        (Get-LowerSha256 $selfPath 'Running installer') -cne [string]$installerAsset.Sha256
    ) {
        throw 'The running installer does not match its GitHub release API digest.'
    }
    Write-InstallerProgress 10 'installer authenticated'

    $checksumPath = Receive-VerifiedAsset $checksumAsset $releaseAssetCache
    $checksums = Read-ChecksumMap $checksumPath
    Assert-AssetChecksum $installerAsset $checksums
    Assert-AssetChecksum $coreAsset $checksums
    Assert-AssetChecksum $trustManifestAsset $checksums
    $selfChecksum = Get-RequiredChecksum $checksums $installerName
    if ((Get-LowerSha256 $selfPath 'Running installer') -cne $selfChecksum) {
        throw 'The running installer does not match SHA256SUMS.txt.'
    }
    Write-InstallerProgress 15 'release checksums authenticated'

    if ($null -ne $legacyCacheRoot) {
        $legacyReleaseAssetCache = Join-Path `
            (Join-Path $legacyCacheRoot 'release-assets') `
            $ReleaseTag
        Import-VerifiedLegacyReleaseAsset `
            $coreAsset `
            $legacyReleaseAssetCache `
            $releaseAssetCache
    }
    $coreArchivePath = Receive-VerifiedAsset $coreAsset $releaseAssetCache
    if ((Get-LowerSha256 $coreArchivePath 'Base runtime archive') -cne (
        Get-RequiredChecksum $checksums $coreArchiveName
    )) {
        throw 'The base runtime archive does not match SHA256SUMS.txt.'
    }
    Write-InstallerProgress 20 'base runtime downloaded and verified'
    $trustManifestPath = Receive-VerifiedAsset $trustManifestAsset $releaseAssetCache
    if ((Get-LowerSha256 $trustManifestPath 'Bundle trust manifest') -cne (
        Get-RequiredChecksum $checksums $trustManifestName
    )) {
        throw 'The bundle trust manifest does not match SHA256SUMS.txt.'
    }
    Write-InstallerProgress 25 'bundle manifest downloaded and verified'

    $trustIdentity = Get-TrustManifestIdentity $trustManifestPath
    $coreRuntime = Expand-VerifiedCore `
        $coreArchivePath `
        $cacheRoot `
        ([long]$coreAsset.Bytes) `
        ([string]$coreAsset.Sha256)
    Write-InstallerProgress 30 'authenticated base runtime ready'

    if ($null -ne $existingDestination) {
        Assert-ExistingBundleIsReplaceable `
            $destination `
            $coreRuntime.Executable `
            $trustIdentity.AirgapManifestSha256
        Write-InstallerProgress 32 'existing bstrings kit ownership verified'
    }
    if ($null -ne $legacyInstallation) {
        Assert-ExistingBundleIsReplaceable `
            $legacyInstallation `
            $coreRuntime.Executable `
            $trustIdentity.AirgapManifestSha256
        Write-InstallerProgress 33 'previous bstrings installation verified for migration'
    }

    if ($null -ne $legacyCacheRoot) {
        Import-VerifiedLegacyPackCache `
            $trustManifestPath `
            (Join-Path $legacyCacheRoot 'bundle-packs') `
            $packCache
    }

    $destinationLeaf = [IO.Path]::GetFileName($destination)
    $stagingLeaf = ".${destinationLeaf}.install-$([Guid]::NewGuid().ToString('N')).staging"
    $backupLeaf = ".${destinationLeaf}.backup-$([Guid]::NewGuid().ToString('N')).tmp"
    $stagingDestination = Join-Path $destinationParent $stagingLeaf
    $backupDestination = Join-Path $destinationParent $backupLeaf
    if (
        (Test-Path -LiteralPath $stagingDestination) -or
        (Test-Path -LiteralPath $backupDestination)
    ) {
        throw 'A unique installer staging or backup path unexpectedly exists.'
    }
    Write-InstallerProgress 35 'replacement staging prepared'

    $lastAcquireExit = 0
    for ($attempt = 1; $attempt -le $AcquireAttempts; $attempt++) {
        Write-Host "Acquiring the bstrings kit (attempt $attempt of $AcquireAttempts)..."
        $acquireArguments = @(
            'bundle',
            'acquire',
            '--manifest',
            $trustManifestPath,
            '--cache',
            $packCache,
            '--output',
            $stagingDestination
        )
        if ($null -ne $existingDestination) {
            $acquireArguments += @('--seed-bundle', $destination)
        }
        elseif ($null -ne $legacyInstallation) {
            $acquireArguments += @('--seed-bundle', $legacyInstallation)
        }
        $lastAcquireExit = Invoke-NativeProcess `
            $coreRuntime.Executable `
            $acquireArguments
        if ($lastAcquireExit -eq 0) {
            $stagingDestinationCreated = $true
            break
        }
        if (Test-Path -LiteralPath $stagingDestination) {
            Remove-ValidatedDirectory `
                $stagingDestination `
                $destinationParent `
                $stagingLeaf `
                'invocation-owned failed staging destination'
        }
        if ($attempt -lt $AcquireAttempts) {
            Write-Warning "Bundle acquisition exited with $lastAcquireExit; retrying with the same resumable cache."
        }
    }
    if ($lastAcquireExit -ne 0) {
        throw "Bundle acquisition failed after $AcquireAttempts attempt(s); last exit code: $lastAcquireExit."
    }
    Write-InstallerProgress 85 'bstrings kit acquired and assembled'
    Assert-PhysicalDirectoryTree $stagingDestination 'Staged replacement bstrings kit'
    Assert-InstalledManifest $stagingDestination $trustIdentity.AirgapManifestSha256
    Invoke-BundleVerify `
        $coreRuntime.Executable `
        $stagingDestination `
        'Authenticated base runtime verification of the staged replacement bstrings kit'
    Write-InstallerProgress 90 'replacement bstrings kit verified'

    if ($null -ne $existingDestination) {
        Assert-ExistingBundleIsReplaceable `
            $destination `
            $coreRuntime.Executable `
            $trustIdentity.AirgapManifestSha256
        Assert-PhysicalDirectoryTree $destination 'DestinationDirectory before replacement'
        [IO.Directory]::Move($destination, $backupDestination)
        $backupDestinationCreated = $true
    }
    try {
        [IO.Directory]::Move($stagingDestination, $destination)
        $stagingDestinationCreated = $false
        $publishedDestination = $true
    }
    catch {
        if ($backupDestinationCreated -and -not (Test-Path -LiteralPath $destination)) {
            [IO.Directory]::Move($backupDestination, $destination)
            $backupDestinationCreated = $false
        }
        throw
    }

    Assert-InstalledManifest $destination $trustIdentity.AirgapManifestSha256
    $installedExecutable = Join-Path $destination 'bstrings.exe'
    Assert-PhysicalItem $installedExecutable 'Installed bstrings.exe' $false | Out-Null
    Invoke-BundleVerify `
        $installedExecutable `
        $destination `
        'Final installed bstrings-kit verification'
    $installationSucceeded = $true
    Write-InstallerProgress 95 'installed replacement verified'

    if ($removeLegacyInstallationAfterSuccess -and $null -ne $legacyInstallation) {
        try {
            Assert-ExistingBundleIsReplaceable `
                $legacyInstallation `
                $installedExecutable `
                $trustIdentity.AirgapManifestSha256
            Remove-ValidatedDirectory `
                $legacyInstallation `
                $destinationParent `
                'bstrings-quality' `
                'verified previous bstrings installation'
            Write-Host 'Removed the verified previous installation after migration.'
            $removeLegacyInstallationAfterSuccess = $false
        }
        catch {
            Write-Warning (
                'The new bstrings kit is verified, but the previous installation ' +
                "could not be removed: $($_.Exception.Message)"
            )
        }
    }

    if ($backupDestinationCreated) {
        try {
            Remove-ValidatedDirectory `
                $backupDestination `
                $destinationParent `
                $backupLeaf `
                'replaced bstrings-kit backup'
            $backupDestinationCreated = $false
        }
        catch {
            Write-Warning (
                "The replacement is verified, but the previous bundle backup could not be removed: " +
                "$backupDestination ($($_.Exception.Message))"
            )
        }
    }
    Write-Host "bstrings kit installed or refreshed and verified: $destination"

    Remove-ValidatedDirectory `
        $coreRuntime.Root `
        $cacheRoot `
        ([IO.Path]::GetFileName([string]$coreRuntime.Root)) `
        'temporary authenticated base runtime'

    if ($ownedCache -and $RemoveCacheAfterSuccess) {
        Remove-OwnedInstallerCache $cacheRoot $destinationParent
        Write-Host 'Verified installer cache removed.'
    }
    else {
        Write-Host "Verified shared installer cache retained: $cacheRoot"
    }
    Write-InstallerProgress 100 'complete'
    Write-Host "Run: $destination\bstrings.exe analyze -d <input> --full -o <output>"
}
catch {
    $originalError = $_
    if (-not $installationSucceeded) {
        $cleanupError = $null
        try {
            if ($publishedDestination -and (Test-Path -LiteralPath $destination)) {
                Remove-ValidatedDirectory `
                    $destination `
                    $destinationParent `
                    ([IO.Path]::GetFileName($destination)) `
                    'failed replacement destination'
                $publishedDestination = $false
            }
            if ($backupDestinationCreated -and -not (Test-Path -LiteralPath $destination)) {
                [IO.Directory]::Move($backupDestination, $destination)
                $backupDestinationCreated = $false
                Write-Warning 'Restored the previous bstrings kit after the replacement failed.'
            }
            elseif ($backupDestinationCreated) {
                throw "The previous bstrings kit could not be restored because the destination path reappeared. The previous bytes remain at: $backupDestination"
            }
            if (
                $stagingDestinationCreated -and
                $null -ne $stagingDestination -and
                (Test-Path -LiteralPath $stagingDestination)
            ) {
                Remove-ValidatedDirectory `
                    $stagingDestination `
                    $destinationParent `
                    ([IO.Path]::GetFileName($stagingDestination)) `
                    'failed replacement staging destination'
                $stagingDestinationCreated = $false
            }
        }
        catch {
            $cleanupError = $_
        }
        if ($null -ne $cleanupError) {
            throw "Installation failed: $($originalError.Exception.Message) Rollback also failed: $($cleanupError.Exception.Message)"
        }
        if ($null -eq $existingDestination -and -not (Test-Path -LiteralPath $destination)) {
            Write-Warning 'Removed the invalid replacement created by this failed installation; the resumable cache was retained.'
        }
    }
    throw $originalError
}
finally {
    $ProgressPreference = $previousProgressPreference
}
