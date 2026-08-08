[CmdletBinding()]
param(
    [string]$DestinationDirectory = (Join-Path (Get-Location).Path 'bstrings-quality'),
    [string]$InstallerCacheDirectory,
    [string]$ReleaseTag = 'v1.9.11',
    [switch]$KeepCache,
    [ValidateRange(1, 10)]
    [int]$AcquireAttempts = 3,
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

$expectedReleaseTag = 'v1.9.11'
$repository = 'Donovoi/bstrings'
$qualityManifestName = 'bundle-packs-quality.json'
$coreArchiveName = 'bstrings-win-x64.zip'
$checksumName = 'SHA256SUMS.txt'
$installerName = 'Install-BstringsQuality.ps1'
$maximumMetadataBytes = 4MB
$maximumCoreBytes = 2000000000
$productionMinimumFreeBytes = 30GB
$createdDestination = $false
$installationSucceeded = $false
$ownedCache = $false
$cacheRoot = $null
$destination = $null
$destinationParent = $null

function Write-InstallerProgress([double]$Percent, [string]$Activity) {
    if ($Percent -lt 0 -or $Percent -gt 100 -or [string]::IsNullOrWhiteSpace($Activity)) {
        throw 'Installer progress requires a percentage from 0 through 100 and an activity.'
    }
    $formatted = $Percent.ToString('F1', [Globalization.CultureInfo]::InvariantCulture)
    Write-Host "Progress: quality installer: $formatted% ($Activity)"
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
            throw "The quality installation requires at least $RequiredBytes free bytes on '$root'; found $available."
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

function Expand-VerifiedCore([string]$ArchivePath, [string]$CacheDirectory) {
    $runtimeLeaf = '.core-runtime-' + [Guid]::NewGuid().ToString('N')
    $runtimeRoot = Join-Path $CacheDirectory $runtimeLeaf
    if (Test-Path -LiteralPath $runtimeRoot) {
        throw "Core runtime staging path unexpectedly exists: $runtimeRoot"
    }
    [IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
    Assert-PhysicalItem $runtimeRoot 'Core runtime staging directory' $true | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = $null
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
        if ($archive.Entries.Count -lt 1 -or $archive.Entries.Count -gt 10000) {
            throw 'The core archive has an invalid entry count.'
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
                throw "The core archive contains an unsafe path: '$name'"
            }
            $isDirectory = $name.EndsWith('/', [StringComparison]::Ordinal)
            $segments = @($name.TrimEnd('/').Split('/'))
            if (
                $segments.Count -eq 0 -or
                @($segments | Where-Object {
                    [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..'
                }).Count -ne 0
            ) {
                throw "The core archive contains an unsafe path: '$name'"
            }
            $relative = $segments -join [IO.Path]::DirectorySeparatorChar
            $target = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $relative))
            if (-not $target.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The core archive path escapes its staging directory: '$name'"
            }
            if (-not $seen.Add($target)) {
                throw "The core archive contains a duplicate path: '$name'"
            }
            $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
            if (
                $unixType -eq 0xA000 -or
                ($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "The core archive contains a linked entry: '$name'"
            }
            if ($isDirectory) {
                [IO.Directory]::CreateDirectory($target) | Out-Null
                continue
            }
            if ([long]$entry.Length -lt 0) {
                throw "The core archive contains an invalid entry length: '$name'"
            }
            $expandedBytes += [long]$entry.Length
            if ($expandedBytes -gt 4GB) {
                throw 'The core archive expands beyond the installer safety limit.'
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
                throw "The extracted core entry has the wrong length: '$name'"
            }
        }
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
    }
    $executable = Join-Path $runtimeRoot 'bstrings.exe'
    Assert-PhysicalItem $executable 'Extracted core bstrings.exe' $false | Out-Null
    return [pscustomobject]@{
        Root = $runtimeRoot
        Executable = $executable
    }
}

function Get-TrustManifestIdentity([string]$Path) {
    $item = Assert-PhysicalItem $Path 'Quality trust manifest' $false
    if ([long]$item.Length -lt 2 -or [long]$item.Length -gt $script:maximumMetadataBytes) {
        throw 'The quality trust manifest has an invalid byte length.'
    }
    try {
        $manifest = [IO.File]::ReadAllText(
            $item.FullName,
            [Text.UTF8Encoding]::new($false, $true)
        ) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The quality trust manifest is not valid JSON: $($_.Exception.Message)"
    }
    $airgapHash = [string]$manifest.airgapManifestSha256
    if (
        [int]$manifest.schemaVersion -ne 1 -or
        [string]$manifest.profile -cne 'windows-x64-offline-v2-quality' -or
        $airgapHash -cnotmatch '^[0-9a-f]{64}$'
    ) {
        throw 'The trust manifest is not the exact supported quality profile.'
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
        throw 'The installed air-gap manifest does not match the quality trust manifest.'
    }
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
    Assert-PhysicalItem $fullPath $Name $true | Out-Null
    $linked = @(
        Get-ChildItem -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }
    )
    if ($linked.Count -ne 0) {
        throw "Refusing to remove $Name because it contains a link or reparse point: $fullPath"
    }
    [IO.Directory]::Delete($fullPath, $true)
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The quality installer supports Windows only.'
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
    throw "The quality installer requires Windows x64; detected '$operatingSystemArchitecture'."
}
if ($ReleaseTag -cne $expectedReleaseTag) {
    throw "This installer is pinned to $expectedReleaseTag; '$ReleaseTag' is not supported."
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
if ($null -ne $existingDestination) {
    Assert-PhysicalItem $destination 'DestinationDirectory' $true | Out-Null
}
Assert-ExistingPathChain $destination 'DestinationDirectory'

if ([string]::IsNullOrWhiteSpace($InstallerCacheDirectory)) {
    $ownedCache = $true
    $ownedCacheParent = Join-Path $destinationParent '.bstrings-quality-installer-cache'
    $cacheRoot = Join-Path $ownedCacheParent $ReleaseTag
}
else {
    $cacheRoot = Get-NormalizedDirectoryPath `
        $InstallerCacheDirectory `
        'InstallerCacheDirectory'
    $ownedCacheParent = $null
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
Assert-FreeSpace @($destination, $cacheRoot) $MinimumFreeBytes

$destinationParent = Ensure-PhysicalDirectory $destinationParent 'Destination parent directory'
$cacheRoot = Ensure-PhysicalDirectory $cacheRoot 'Installer cache directory'
$releaseAssetCache = Ensure-PhysicalDirectory `
    (Join-Path $cacheRoot 'release-assets') `
    'Release-asset cache directory'
$packCache = Ensure-PhysicalDirectory `
    (Join-Path $cacheRoot 'bundle-packs') `
    'Bundle-pack cache directory'

$script:webHeaders = @{
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = "bstrings-$ReleaseTag-quality-installer"
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
        [bool]$release.prerelease
    ) {
        throw "The release API did not return the exact published $ReleaseTag release."
    }

    $installerAsset = Get-RequiredAsset $release $installerName $maximumMetadataBytes
    $checksumAsset = Get-RequiredAsset $release $checksumName $maximumMetadataBytes
    $coreAsset = Get-RequiredAsset $release $coreArchiveName $maximumCoreBytes
    $qualityManifestAsset = Get-RequiredAsset `
        $release `
        $qualityManifestName `
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
    Assert-AssetChecksum $qualityManifestAsset $checksums
    $selfChecksum = Get-RequiredChecksum $checksums $installerName
    if ((Get-LowerSha256 $selfPath 'Running installer') -cne $selfChecksum) {
        throw 'The running installer does not match SHA256SUMS.txt.'
    }
    Write-InstallerProgress 15 'release checksums authenticated'

    $coreArchivePath = Receive-VerifiedAsset $coreAsset $releaseAssetCache
    if ((Get-LowerSha256 $coreArchivePath 'Core release archive') -cne (
        Get-RequiredChecksum $checksums $coreArchiveName
    )) {
        throw 'The core release archive does not match SHA256SUMS.txt.'
    }
    Write-InstallerProgress 20 'core runtime downloaded and verified'
    $qualityManifestPath = Receive-VerifiedAsset $qualityManifestAsset $releaseAssetCache
    if ((Get-LowerSha256 $qualityManifestPath 'Quality trust manifest') -cne (
        Get-RequiredChecksum $checksums $qualityManifestName
    )) {
        throw 'The quality trust manifest does not match SHA256SUMS.txt.'
    }
    Write-InstallerProgress 25 'quality manifest downloaded and verified'

    $trustIdentity = Get-TrustManifestIdentity $qualityManifestPath
    $coreRuntime = Expand-VerifiedCore $coreArchivePath $cacheRoot
    Write-InstallerProgress 30 'authenticated core runtime ready'

    if ($null -ne $existingDestination) {
        Assert-InstalledManifest $destination $trustIdentity.AirgapManifestSha256
        Invoke-BundleVerify `
            $coreRuntime.Executable `
            $destination `
            'Trusted-core verification of the existing quality bundle'
        $installationSucceeded = $true
        Write-InstallerProgress 95 'existing quality bundle verified'
        Write-Host "The existing bstrings quality kit is complete and verified: $destination"
    }
    else {
        $lastAcquireExit = 0
        for ($attempt = 1; $attempt -le $AcquireAttempts; $attempt++) {
            Write-Host "Acquiring the quality bundle (attempt $attempt of $AcquireAttempts)..."
            $lastAcquireExit = Invoke-NativeProcess $coreRuntime.Executable @(
                'bundle',
                'acquire',
                '--manifest',
                $qualityManifestPath,
                '--cache',
                $packCache,
                '--output',
                $destination
            )
            if ($lastAcquireExit -eq 0) {
                $createdDestination = $true
                break
            }
            if (Test-Path -LiteralPath $destination) {
                throw "Bundle acquisition failed with exit code $lastAcquireExit and a destination now exists. It was preserved because this installer cannot prove ownership after a failed native operation."
            }
            if ($attempt -lt $AcquireAttempts) {
                Write-Warning "Bundle acquisition exited with $lastAcquireExit; retrying with the same resumable cache."
            }
        }
        if ($lastAcquireExit -ne 0) {
            throw "Bundle acquisition failed after $AcquireAttempts attempt(s); last exit code: $lastAcquireExit."
        }
        Write-InstallerProgress 85 'quality bundle acquired and assembled'
        Assert-PhysicalItem $destination 'New quality bundle' $true | Out-Null
        Assert-InstalledManifest $destination $trustIdentity.AirgapManifestSha256
        $installedExecutable = Join-Path $destination 'bstrings.exe'
        Assert-PhysicalItem $installedExecutable 'Installed bstrings.exe' $false | Out-Null
        Invoke-BundleVerify `
            $installedExecutable `
            $destination `
            'Final installed quality-bundle verification'
        $installationSucceeded = $true
        Write-InstallerProgress 95 'installed quality bundle verified'
        Write-Host "bstrings quality kit installed and verified: $destination"
    }

    Remove-ValidatedDirectory `
        $coreRuntime.Root `
        $cacheRoot `
        ([IO.Path]::GetFileName([string]$coreRuntime.Root)) `
        'temporary authenticated core runtime'

    if ($ownedCache -and -not $KeepCache) {
        Remove-ValidatedDirectory `
            $cacheRoot `
            $ownedCacheParent `
            $ReleaseTag `
            'versioned installer cache'
        if (
            (Test-Path -LiteralPath $ownedCacheParent -PathType Container) -and
            @(Get-ChildItem -LiteralPath $ownedCacheParent -Force).Count -eq 0
        ) {
            Remove-ValidatedDirectory `
                $ownedCacheParent `
                $destinationParent `
                '.bstrings-quality-installer-cache' `
                'installer cache parent'
        }
        Write-Host 'Verified installer cache removed.'
    }
    else {
        Write-Host "Verified installer cache retained: $cacheRoot"
    }
    Write-InstallerProgress 100 'complete'
    Write-Host "Run: $destination\bstrings.exe analyze -d <input> --full -o <output>"
}
catch {
    $originalError = $_
    if ($createdDestination -and -not $installationSucceeded -and (Test-Path -LiteralPath $destination)) {
        try {
            Remove-ValidatedDirectory `
                $destination `
                $destinationParent `
                ([IO.Path]::GetFileName($destination)) `
                'invocation-created invalid destination'
            Write-Warning 'Removed the invalid destination created by this failed installation; the resumable cache was retained.'
        }
        catch {
            throw "Installation failed: $($originalError.Exception.Message) Cleanup also failed: $($_.Exception.Message)"
        }
    }
    throw $originalError
}
finally {
    $ProgressPreference = $previousProgressPreference
}
