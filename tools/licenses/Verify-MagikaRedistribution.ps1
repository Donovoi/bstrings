[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BundleDirectory,
    [string]$InventoryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath = Join-Path $repoRoot 'licenses\magika-cli-1.1.0-redistribution.json'
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
    $nativeRelative = $RelativePath.Replace(
        [IO.Path]::AltDirectorySeparatorChar,
        [IO.Path]::DirectorySeparatorChar
    )
    $candidate = [IO.Path]::GetFullPath((Join-Path $rootFull $nativeRelative))
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escapes its root: $RelativePath"
    }
    return $candidate
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

function Read-UInt16([byte[]]$Bytes, [int]$Offset, [string]$Name) {
    if ($Offset -lt 0 -or $Offset + 2 -gt $Bytes.Length) {
        throw "$Name has a truncated PE structure at offset $Offset."
    }
    return [BitConverter]::ToUInt16($Bytes, $Offset)
}

function Read-UInt32([byte[]]$Bytes, [int]$Offset, [string]$Name) {
    if ($Offset -lt 0 -or $Offset + 4 -gt $Bytes.Length) {
        throw "$Name has a truncated PE structure at offset $Offset."
    }
    return [BitConverter]::ToUInt32($Bytes, $Offset)
}

function Get-PeImports([string]$Path, [string]$Name) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "$Name is not a valid MZ executable."
    }
    $peOffset = [int](Read-UInt32 $bytes 0x3c $Name)
    if (
        $peOffset -lt 0 -or
        $peOffset + 24 -gt $bytes.Length -or
        $bytes[$peOffset] -ne 0x50 -or
        $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or
        $bytes[$peOffset + 3] -ne 0
    ) {
        throw "$Name has an invalid PE signature."
    }
    $sectionCount = [int](Read-UInt16 $bytes ($peOffset + 6) $Name)
    $optionalBytes = [int](Read-UInt16 $bytes ($peOffset + 20) $Name)
    $optionalOffset = $peOffset + 24
    $optionalMagic = Read-UInt16 $bytes $optionalOffset $Name
    if ($optionalMagic -eq 0x20b) {
        $dataDirectoryOffset = $optionalOffset + 112
    }
    elseif ($optionalMagic -eq 0x10b) {
        $dataDirectoryOffset = $optionalOffset + 96
    }
    else {
        throw "$Name has unsupported PE optional-header magic 0x$($optionalMagic.ToString('x'))."
    }
    if ($dataDirectoryOffset + 16 -gt $optionalOffset + $optionalBytes) {
        throw "$Name has no complete import directory."
    }
    $importRva = [uint32](Read-UInt32 $bytes ($dataDirectoryOffset + 8) $Name)
    if ($importRva -eq 0) {
        return @()
    }
    $sectionOffset = $optionalOffset + $optionalBytes
    $sections = @()
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $offset = $sectionOffset + (40 * $index)
        if ($offset + 40 -gt $bytes.Length) {
            throw "$Name has a truncated section table."
        }
        $sections += [pscustomobject]@{
            virtualSize = [uint32](Read-UInt32 $bytes ($offset + 8) $Name)
            virtualAddress = [uint32](Read-UInt32 $bytes ($offset + 12) $Name)
            rawSize = [uint32](Read-UInt32 $bytes ($offset + 16) $Name)
            rawOffset = [uint32](Read-UInt32 $bytes ($offset + 20) $Name)
        }
    }
    function Convert-RvaToOffset([uint32]$Rva) {
        foreach ($section in $sections) {
            $extent = [Math]::Max([uint64]$section.virtualSize, [uint64]$section.rawSize)
            if (
                [uint64]$Rva -ge [uint64]$section.virtualAddress -and
                [uint64]$Rva -lt ([uint64]$section.virtualAddress + $extent)
            ) {
                $mapped = [uint64]$section.rawOffset + ([uint64]$Rva - [uint64]$section.virtualAddress)
                if ($mapped -ge [uint64]$bytes.Length) {
                    throw "$Name maps an RVA outside the file."
                }
                return [int]$mapped
            }
        }
        if ($Rva -lt $bytes.Length) {
            return [int]$Rva
        }
        throw "$Name contains an unmapped RVA 0x$($Rva.ToString('x'))."
    }
    $descriptorOffset = Convert-RvaToOffset $importRva
    $imports = @()
    for ($descriptor = 0; $descriptor -lt 4096; $descriptor++) {
        $offset = $descriptorOffset + (20 * $descriptor)
        $firstThunk = Read-UInt32 $bytes $offset $Name
        $timeDate = Read-UInt32 $bytes ($offset + 4) $Name
        $forwarder = Read-UInt32 $bytes ($offset + 8) $Name
        $nameRva = [uint32](Read-UInt32 $bytes ($offset + 12) $Name)
        $thunk = Read-UInt32 $bytes ($offset + 16) $Name
        if (($firstThunk -bor $timeDate -bor $forwarder -bor $nameRva -bor $thunk) -eq 0) {
            return @($imports)
        }
        if ($nameRva -eq 0) {
            throw "$Name has an import descriptor without a name RVA."
        }
        $nameOffset = Convert-RvaToOffset $nameRva
        $end = $nameOffset
        while ($end -lt $bytes.Length -and $bytes[$end] -ne 0 -and ($end - $nameOffset) -lt 512) {
            $end++
        }
        if ($end -ge $bytes.Length -or $bytes[$end] -ne 0) {
            throw "$Name has an unterminated import name."
        }
        $imports += [Text.Encoding]::ASCII.GetString($bytes, $nameOffset, $end - $nameOffset)
    }
    throw "$Name has more than 4096 import descriptors."
}

function Assert-ZipSafety([string]$Path, [string]$ExpectedRoot, [string]$Name) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries) {
            $parts = $entry.FullName -split '/'
            if (
                $entry.FullName.Contains('\') -or
                $entry.FullName.StartsWith('/') -or
                $parts.Count -lt 2 -or
                $parts[0] -cne $ExpectedRoot -or
                $parts -contains '..' -or
                $parts -contains '.'
            ) {
                throw "$Name contains an unsafe or unexpected entry: $($entry.FullName)"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$inventoryFile = Resolve-ExistingFile $InventoryPath 'Magika redistribution inventory'
$inventory = Get-Content -LiteralPath $inventoryFile -Raw | ConvertFrom-Json
if (
    $inventory.schemaVersion -ne 1 -or
    [string]$inventory.component -cne 'magika-cli' -or
    [string]$inventory.version -cne '1.1.0' -or
    [string]$inventory.target -cne 'x86_64-pc-windows-msvc' -or
    [string]$inventory.sourceCommit -cne '5e2f437fb7b7452368c8c1fa9354858f5487a5c4' -or
    [string]$inventory.compiler.version -cne '1.94.1' -or
    [string]$inventory.compiler.commit -cne 'e408947bfd200af42db322daf0fadfe7e26d3bd1' -or
    [string]$inventory.nativeComponents.onnxRuntime.version -cne '1.24.2' -or
    [string]$inventory.nativeComponents.onnxRuntime.linkMode -cne 'static' -or
    [string]$inventory.nativeComponents.directML.version -cne '1.15.4' -or
    [string]$inventory.nativeComponents.directML.deployment -cne 'app-local' -or
    [long]$inventory.runtimeInventory.bytes -ne 10067 -or
    [string]$inventory.runtimeInventory.sha256 -cne '9ff2978dc999d15f077bd3f911f8e0ea62c913fe2e0fdf455b26f80a92bff0dd' -or
    [long]$inventory.runtimeNotices.bytes -ne 775825 -or
    [string]$inventory.runtimeNotices.sha256 -cne '0be21297725d9b56428220b512718808f7d25eaf1aec945aefa44276d84375a0'
) {
    throw 'Unsupported or drifted Magika redistribution inventory.'
}
if (
    @($inventory.downloadArtifacts).Count -ne 14 -or
    @($inventory.derivedArtifacts).Count -ne 11 -or
    @($inventory.repositoryArtifacts).Count -ne 3 -or
    [int]$inventory.runtimeInventory.packageCount -ne 70 -or
    [int]$inventory.runtimeInventory.registryPackageCount -ne 68 -or
    [int]$inventory.runtimeNotices.packageCount -ne 70 -or
    [int]$inventory.runtimeNotices.registryArchiveCount -ne 68
) {
    throw 'Magika redistribution inventory counts drifted.'
}

$bundleRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $BundleDirectory).Path)
if (-not (Test-Path -LiteralPath $bundleRoot -PathType Container)) {
    throw "Bundle directory was not found: $BundleDirectory"
}
Assert-NoReparseTraversal $bundleRoot 'Bundle root'

$expectedComponentFiles = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
$seenIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenDestinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

$lockedDownloads = @{
    'magika-release' = @($false, 'd4de347c53e2d25f9663780c93fdf26d156284c64d6a5ec4bcdb829f9994f6f3', 'upstream/magika-cli-x86_64-pc-windows-msvc.zip')
    'magika-license' = @($true, '58d1e17ffe5109a7ae296caafcadfdbe6a7d176f0bc4ab01e12a689b0499d8bd', 'licenses/magika-cli-1.1.0/Magika-LICENSE.txt')
    'magika-source' = @($false, '54983bcea9c11499aee7162ed11989167918b37b69adce67a4891f2fd3e1d7a0', 'sources/magika-5e2f437fb7b7452368c8c1fa9354858f5487a5c4.zip')
    'onnxruntime-source' = @($false, '4bd806b559bf9c91fefbf59d74d21dc9299c5779beab8861623675d57430e044', 'sources/onnxruntime-058787ceead760166e3c50a0a4cba8a833a6f53f.zip')
    'onnxruntime-license' = @($true, '2f07c72751aed99790b8a4869cf2311df85a860b22ded05fa22803587a48922c', 'licenses/magika-cli-1.1.0/ONNXRuntime-1.24.2-LICENSE.txt')
    'onnxruntime-notices' = @($true, '0e07b95f3a8d6230037707c5c4a2b554d12c4cb67369669ac255635528ffcee2', 'licenses/magika-cli-1.1.0/ONNXRuntime-1.24.2-ThirdPartyNotices.txt')
    'eigen-source' = @($true, '6a60d76351f97132669daeeb721d6bf14b008101883ad2d687a3201c5c461eb0', 'sources/magika-cli-1.1.0/eigen-1d8b82b0740839c0de7f1242a3585e3390ff5f33.zip')
    'colored-license' = @($true, 'fab3dd6bdab226f1c08630b1dd917e11fcb4ec5e1e020e2c16f83a0a13863e85', 'licenses/magika-cli-1.1.0/colored-3.0.0-MPL-2.0.txt')
    'colored-source' = @($true, 'fde0e0ec90c9dfb3b4b1a0891a7dcd0e2bffde2f7efed5fe7c9bb00e5bfb915e', 'sources/magika-cli-1.1.0/colored-3.0.0.crate')
    'ort-static-archive' = @($false, 'b685bfc8d336e0ba95c066a7a982c03aa6dedd528a492eb99ca4ccb7f3af9e7a', 'upstream/ort-rs-1.24.2-x86_64-pc-windows-msvc.tar.lzma2')
    'directml-nuget' = @($false, '4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9', 'upstream/microsoft.ai.directml.1.15.4.nupkg')
    'rust-channel-manifest' = @($true, 'cc2f04dfc883549d683c8cc2a9393f523a3dfbd931f5d5eaef00303cca64a60d', 'licenses/magika-cli-1.1.0/provenance/channel-rust-1.94.1.toml')
    'rust-source' = @($false, 'cb3756156fe6d2d6cedad327c94ad3721b612c4cd20dfb226d7543250788b66c', 'sources/rust-src-1.94.1.tar.xz')
    'rustc-archive' = @($false, '73b4e8e6dce2440e1922b8a56c88fb2e49cc32c67a38afcbf88967b9abd70d43', 'upstream/rustc-1.94.1-x86_64-pc-windows-msvc.tar.xz')
}

foreach ($spec in @($inventory.downloadArtifacts)) {
    $id = [string]$spec.id
    $destination = [string]$spec.destination
    if (-not $seenIds.Add($id)) {
        throw "Duplicate download artifact id: $id"
    }
    if (-not $seenDestinations.Add($destination)) {
        throw "Duplicate artifact destination: $destination"
    }
    if ($spec.PSObject.Properties.Name -notcontains 'ship' -or $spec.ship -isnot [bool]) {
        throw "Download artifact $id lacks an explicit Boolean ship property."
    }
    $uri = [Uri]([string]$spec.url)
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -cne [Uri]::UriSchemeHttps) {
        throw "Download artifact $id is not locked to an absolute HTTPS URL."
    }
    if (-not $lockedDownloads.ContainsKey($id)) {
        throw "Unexpected download artifact id: $id"
    }
    if (
        [bool]$spec.ship -ne [bool]$lockedDownloads[$id][0] -or
        [string]$spec.sha256 -cne [string]$lockedDownloads[$id][1] -or
        $destination -cne [string]$lockedDownloads[$id][2]
    ) {
        throw "High-risk download lock drifted: $id"
    }
    $target = Resolve-ContainedPath $bundleRoot $destination "download artifact $id"
    if ([bool]$spec.ship) {
        $null = Assert-ExactFile $target ([long]$spec.bytes) ([string]$spec.sha256) "download artifact $id"
        if ($destination -like 'licenses/magika-cli-1.1.0/*' -or $destination -like 'sources/magika-cli-1.1.0/*') {
            $null = $expectedComponentFiles.Add($destination.Replace('\', '/'))
        }
    }
    elseif (Test-Path -LiteralPath $target) {
        throw "Cache-only artifact leaked into the final bundle: $destination"
    }
}
if ($seenIds.Count -ne 14) {
    throw "Download artifact ids drifted: expected 14, found $($seenIds.Count)."
}

foreach ($spec in @($inventory.derivedArtifacts)) {
    $destination = [string]$spec.destination
    if (-not $seenDestinations.Add($destination)) {
        throw "Duplicate artifact destination: $destination"
    }
    $target = Resolve-ContainedPath $bundleRoot $destination "derived artifact $($spec.id)"
    $null = Assert-ExactFile $target ([long]$spec.bytes) ([string]$spec.sha256) "derived artifact $($spec.id)"
    if ($destination -like 'licenses/magika-cli-1.1.0/*' -or $destination -like 'sources/magika-cli-1.1.0/*') {
        $null = $expectedComponentFiles.Add($destination.Replace('\', '/'))
    }
}

$requiredDerived = @{
    'magika-executable' = @('tools/magika/magika.exe', '3631dab2f57ec42b6646ce141397671132707cd7e7633f4511fe47171efe69eb')
    'directml-runtime' = @('tools/magika/DirectML.dll', '9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1')
    'directml-license' = @('licenses/magika-cli-1.1.0/DirectML-1.15.4-LICENSE.txt', 'a05138e3a085ff60a44881eedfa58dccb03ecc1d7b1f6ae888418e8c2fec4b8d')
    'directml-code-license' = @('licenses/magika-cli-1.1.0/DirectML-1.15.4-LICENSE-CODE.txt', '903df5512f7d02609fed0c780a9b704f5a3eeb6e4d84ebe42a29845c81899a3c')
    'directml-notices' = @('licenses/magika-cli-1.1.0/DirectML-1.15.4-ThirdPartyNotices.txt', '2c95795c13ff48a58b6ed916f37901c23d964b5d9d601af422f17ad2172e7950')
    'eigen-mpl-license' = @('licenses/magika-cli-1.1.0/Eigen-1d8b82b-COPYING.MPL2', '66a3107d5ad6a058aab753eaac2047ccb2ed0e39465dd0fe5844da3e300d5172')
    'rust-library-notice' = @('licenses/magika-cli-1.1.0/Rust-1.94.1-COPYRIGHT-library.html', 'af70aaabed1b73e872f14f9130db37e09f3f4d73a5f7c598b9173697a5d2729f')
    'magika-cargo-manifest' = @('licenses/magika-cli-1.1.0/provenance/Magika-Cargo.toml', '049290817fd4255efa01ff80cadcd724cd517728bcb845fe0747a6da04b43102')
    'magika-cargo-lock' = @('licenses/magika-cli-1.1.0/provenance/Magika-Cargo.lock', '6aed136fe7d903f13a2cd8e64ee28632246e8788044631f3aabadf238e13962e')
    'onnxruntime-version' = @('licenses/magika-cli-1.1.0/provenance/ONNXRuntime-VERSION_NUMBER', '32060e9461395f8bd2b74d95efac19a1d021f3fff70c4d6f10510330d8e6355a')
    'onnxruntime-dependencies' = @('licenses/magika-cli-1.1.0/provenance/ONNXRuntime-cmake-deps.txt', 'a913db05c4e9965f9725aec7a5b9b10b071d13496e567283e81737ebe91ff027')
}
foreach ($requiredId in $requiredDerived.Keys) {
    $matching = @($inventory.derivedArtifacts | Where-Object id -CEQ $requiredId)
    if (
        $matching.Count -ne 1 -or
        [string]$matching[0].destination -cne [string]$requiredDerived[$requiredId][0] -or
        [string]$matching[0].sha256 -cne [string]$requiredDerived[$requiredId][1]
    ) {
        throw "High-risk derived artifact drifted: $requiredId"
    }
}

foreach ($spec in @($inventory.repositoryArtifacts)) {
    $destination = [string]$spec.destination
    if (-not $seenDestinations.Add($destination)) {
        throw "Duplicate artifact destination: $destination"
    }
    $target = Resolve-ContainedPath $bundleRoot $destination "repository artifact $($spec.source)"
    $null = Assert-ExactFile $target ([long]$spec.bytes) ([string]$spec.sha256) "repository artifact $($spec.source)"
    $null = $expectedComponentFiles.Add($destination.Replace('\', '/'))
}

$manifestRelative = 'licenses/magika-cli-1.1.0/magika-cli-1.1.0-redistribution.json'
$manifestTarget = Resolve-ContainedPath $bundleRoot $manifestRelative 'staged redistribution inventory'
$inventoryItem = Get-Item -LiteralPath $inventoryFile
$inventoryHash = (Get-FileHash -LiteralPath $inventoryFile -Algorithm SHA256).Hash.ToLowerInvariant()
$null = Assert-ExactFile $manifestTarget $inventoryItem.Length $inventoryHash 'staged redistribution inventory'
$null = $expectedComponentFiles.Add($manifestRelative)

foreach ($scopeRelative in @('licenses/magika-cli-1.1.0', 'sources/magika-cli-1.1.0')) {
    $scope = Resolve-ContainedPath $bundleRoot $scopeRelative "component scope $scopeRelative"
    if (-not (Test-Path -LiteralPath $scope -PathType Container)) {
        throw "Required component directory is missing: $scopeRelative"
    }
    foreach ($item in Get-ChildItem -LiteralPath $scope -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Component scope contains a link or reparse point: $($item.FullName)"
        }
        if (-not $item.PSIsContainer) {
            $relative = $item.FullName.Substring($bundleRoot.TrimEnd('\').Length + 1).Replace('\', '/')
            if (-not $expectedComponentFiles.Contains($relative)) {
                throw "Unexpected file in Magika component scope: $relative"
            }
        }
    }
}

$runtimeInventory = Resolve-ContainedPath `
    $bundleRoot `
    'licenses/magika-cli-1.1.0/magika-cli-1.1.0-win-x64.tsv' `
    'runtime inventory'
$runtimeInventory = Assert-ExactFile `
    $runtimeInventory `
    ([long]$inventory.runtimeInventory.bytes) `
    ([string]$inventory.runtimeInventory.sha256) `
    'runtime inventory'
$header = Get-Content -LiteralPath $runtimeInventory -TotalCount 1
$expectedHeader = "package`tversion`tchecksum_sha256`tarchive_bytes`tdeclared_license`trepository"
if ($header -cne $expectedHeader) {
    throw 'Magika runtime inventory header drifted.'
}
$rows = @(Import-Csv -LiteralPath $runtimeInventory -Delimiter "`t")
$registryRows = @($rows | Where-Object { $_.checksum_sha256 -match '^[0-9a-f]{64}$' })
$workspaceRows = @($rows | Where-Object { $_.checksum_sha256 -like 'source-commit:*' })
if ($rows.Count -ne 70 -or $registryRows.Count -ne 68 -or $workspaceRows.Count -ne 2) {
    throw 'Magika runtime inventory must contain exactly 70 packages, 68 registry archives, and 2 workspace packages.'
}
$rowGroups = @($rows | Group-Object { "$($_.package)`0$($_.version)" } | Where-Object Count -ne 1)
if ($rowGroups.Count -ne 0) {
    throw 'Magika runtime inventory has duplicate package/version rows.'
}
foreach ($row in $registryRows) {
    if (
        [long]$row.archive_bytes -le 0 -or
        [string]::IsNullOrWhiteSpace($row.declared_license) -or
        -not ([Uri]$row.repository).IsAbsoluteUri
    ) {
        throw "Incomplete registry inventory row: $($row.package) $($row.version)"
    }
}

$highRiskRows = @(
    @('colored', '3.0.0', 'fde0e0ec90c9dfb3b4b1a0891a7dcd0e2bffde2f7efed5fe7c9bb00e5bfb915e', 'MPL-2.0'),
    @('ort', '2.0.0-rc.12', 'd7de3af33d24a745ffb8fab904b13478438d1cd52868e6f17735ef6e1f8bf133', 'MIT OR Apache-2.0'),
    @('ort-sys', '2.0.0-rc.12', 'd7b497d21a8b6fbb4b5a544f8fadb77e801a09ae0add9e411d31c6f89e3c1e90', 'MIT OR Apache-2.0'),
    @('unicode-ident', '1.0.19', 'f63a545481291138910575129486daeaf8ac54aee4387fe7906919f7830c7d9d', '(MIT OR Apache-2.0) AND Unicode-3.0'),
    @('magika-cli', '1.1.0', 'source-commit:5e2f437fb7b7452368c8c1fa9354858f5487a5c4', 'Apache-2.0')
)
foreach ($expected in $highRiskRows) {
    $matching = @($rows | Where-Object { $_.package -ceq $expected[0] -and $_.version -ceq $expected[1] })
    if (
        $matching.Count -ne 1 -or
        $matching[0].checksum_sha256 -cne $expected[2] -or
        $matching[0].declared_license -cne $expected[3]
    ) {
        throw "High-risk runtime inventory row drifted: $($expected[0]) $($expected[1])"
    }
}

$noticePath = Resolve-ContainedPath `
    $bundleRoot `
    'licenses/magika-cli-1.1.0/THIRD-PARTY-NOTICES.txt' `
    'runtime package notices'
$noticePath = Assert-ExactFile `
    $noticePath `
    ([long]$inventory.runtimeNotices.bytes) `
    ([string]$inventory.runtimeNotices.sha256) `
    'runtime package notices'
$noticeText = Get-Content -LiteralPath $noticePath -Raw
$noticeBlocks = @($noticeText -split ('(?m)^' + [regex]::Escape('=' * 80) + "`n")) | Where-Object { $_ -match '(?m)^PACKAGE: ' }
if ($noticeBlocks.Count -ne 70) {
    throw "Runtime package notices must contain exactly 70 package sections; found $($noticeBlocks.Count)."
}
foreach ($row in $rows) {
    $prefix = "PACKAGE: $($row.package) $($row.version)`n"
    $matches = @($noticeBlocks | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
    if ($matches.Count -ne 1) {
        throw "Runtime package notices do not contain exactly one section for $($row.package) $($row.version)."
    }
    foreach ($line in @(
            "DECLARED LICENSE: $($row.declared_license)",
            "REPOSITORY: $($row.repository)",
            "SOURCE CHECKSUM: $($row.checksum_sha256)",
            'LICENSE FILE: '
        )) {
        if (-not $matches[0].Contains($line, [StringComparison]::Ordinal)) {
            throw "Runtime package notice is incomplete for $($row.package) $($row.version): $line"
        }
    }
}
foreach ($requiredNotice in @(
        'Copyright (c) 2014 Alex Crichton',
        'Copyright (c) Microsoft Corporation.',
        'Copyright (c) Tokio Contributors',
        'Copyright © 1991-2023 Unicode, Inc.'
    )) {
    if (-not $noticeText.Contains($requiredNotice, [StringComparison]::Ordinal)) {
        throw "Runtime package notices omit a reviewed copyright statement: $requiredNotice"
    }
}

$cargoLockPath = Resolve-ContainedPath $bundleRoot 'licenses/magika-cli-1.1.0/provenance/Magika-Cargo.lock' 'Magika Cargo lock'
$cargoLockText = Get-Content -LiteralPath $cargoLockPath -Raw
$lockBlocks = [regex]::Split($cargoLockText, '(?m)^\[\[package\]\]\s*\r?\n')
$lockRecords = @()
foreach ($block in $lockBlocks) {
    $nameMatch = [regex]::Match($block, '(?m)^name = "([^"]+)"\s*$')
    $versionMatch = [regex]::Match($block, '(?m)^version = "([^"]+)"\s*$')
    if ($nameMatch.Success -and $versionMatch.Success) {
        $checksumMatch = [regex]::Match($block, '(?m)^checksum = "([0-9a-f]{64})"\s*$')
        $lockRecords += [pscustomobject]@{
            package = $nameMatch.Groups[1].Value
            version = $versionMatch.Groups[1].Value
            checksum = if ($checksumMatch.Success) { $checksumMatch.Groups[1].Value } else { '' }
        }
    }
}
foreach ($row in $rows) {
    $matching = @($lockRecords | Where-Object { $_.package -ceq $row.package -and $_.version -ceq $row.version })
    if ($matching.Count -ne 1) {
        throw "Magika Cargo lock does not contain exactly one record for $($row.package) $($row.version)."
    }
    if ($row.checksum_sha256 -match '^[0-9a-f]{64}$' -and $matching[0].checksum -cne $row.checksum_sha256) {
        throw "Magika Cargo lock checksum differs for $($row.package) $($row.version)."
    }
}

$cargoManifestPath = Resolve-ContainedPath $bundleRoot 'licenses/magika-cli-1.1.0/provenance/Magika-Cargo.toml' 'Magika Cargo manifest'
$cargoManifestText = Get-Content -LiteralPath $cargoManifestPath -Raw
foreach ($dependencyPin in @('colored = "3.0.0"', 'ort = "=2.0.0-rc.12"', 'magika = { version = "=1.1.0", path = "../lib", features = ["serde"] }')) {
    if (-not $cargoManifestText.Contains($dependencyPin, [StringComparison]::Ordinal)) {
        throw "Magika Cargo manifest no longer contains reviewed dependency pin: $dependencyPin"
    }
}

$ortVersionPath = Resolve-ContainedPath $bundleRoot 'licenses/magika-cli-1.1.0/provenance/ONNXRuntime-VERSION_NUMBER' 'ONNX Runtime version'
if ((Get-Content -LiteralPath $ortVersionPath -Raw).Trim() -cne '1.24.2') {
    throw 'ONNX Runtime provenance version is not 1.24.2.'
}
$ortDepsPath = Resolve-ContainedPath $bundleRoot 'licenses/magika-cli-1.1.0/provenance/ONNXRuntime-cmake-deps.txt' 'ONNX Runtime dependencies'
$ortDepsText = Get-Content -LiteralPath $ortDepsPath -Raw
$eigenDependency = 'eigen;https://github.com/eigen-mirror/eigen/archive/1d8b82b0740839c0de7f1242a3585e3390ff5f33/eigen-1d8b82b0740839c0de7f1242a3585e3390ff5f33.zip;05b19b49e6fbb91246be711d801160528c135e34'
if (-not $ortDepsText.Contains($eigenDependency, [StringComparison]::Ordinal)) {
    throw 'ONNX Runtime provenance does not contain the reviewed Eigen pin.'
}

$rustManifestPath = Resolve-ContainedPath $bundleRoot 'licenses/magika-cli-1.1.0/provenance/channel-rust-1.94.1.toml' 'Rust channel manifest'
$rustManifestText = Get-Content -LiteralPath $rustManifestPath -Raw
foreach ($rustLock in @(
        'git_commit_hash = "e408947bfd200af42db322daf0fadfe7e26d3bd1"',
        'xz_hash = "cb3756156fe6d2d6cedad327c94ad3721b612c4cd20dfb226d7543250788b66c"',
        'xz_hash = "73b4e8e6dce2440e1922b8a56c88fb2e49cc32c67a38afcbf88967b9abd70d43"'
    )) {
    if (-not $rustManifestText.Contains($rustLock, [StringComparison]::Ordinal)) {
        throw "Rust channel manifest no longer contains reviewed lock: $rustLock"
    }
}

$eigenPath = Resolve-ContainedPath $bundleRoot 'sources/magika-cli-1.1.0/eigen-1d8b82b0740839c0de7f1242a3585e3390ff5f33.zip' 'Eigen source'
$eigenSha1 = (Get-FileHash -LiteralPath $eigenPath -Algorithm SHA1).Hash.ToLowerInvariant()
if ($eigenSha1 -cne '05b19b49e6fbb91246be711d801160528c135e34') {
    throw "Eigen source SHA-1 differs from the ONNX Runtime dependency lock: $eigenSha1"
}
Assert-ZipSafety $eigenPath 'eigen-1d8b82b0740839c0de7f1242a3585e3390ff5f33' 'Eigen source'

$magikaPath = Resolve-ContainedPath $bundleRoot 'tools/magika/magika.exe' 'Magika executable'
$directMlPath = Resolve-ContainedPath $bundleRoot 'tools/magika/DirectML.dll' 'DirectML runtime'
$magikaImports = @(Get-PeImports $magikaPath 'Magika executable')
$directMlImports = @(Get-PeImports $directMlPath 'DirectML runtime')
if ($magikaImports -notcontains 'DirectML.dll') {
    throw "Magika executable does not import its app-local DirectML.dll: $($magikaImports -join ', ')"
}
if ($magikaImports -contains 'onnxruntime.dll') {
    throw 'Magika executable unexpectedly imports onnxruntime.dll; reviewed build links ONNX Runtime statically.'
}
foreach ($requiredImport in @('d3d12.dll', 'dxgi.dll')) {
    if ($magikaImports -notcontains $requiredImport) {
        throw "Magika executable does not import $requiredImport."
    }
}
if ($directMlImports -notcontains 'd3d12.dll') {
    throw 'DirectML runtime does not import d3d12.dll.'
}
$magikaAscii = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($magikaPath))
foreach ($marker in @('/rustc/e408947bfd200af42db322daf0fadfe7e26d3bd1/library', '1.24.2')) {
    if (-not $magikaAscii.Contains($marker, [StringComparison]::Ordinal)) {
        throw "Magika executable does not contain reviewed provenance marker: $marker"
    }
}

$verifiedFiles = `
    @($inventory.downloadArtifacts | Where-Object { [bool]$_.ship }).Count + `
    @($inventory.derivedArtifacts).Count + `
    @($inventory.repositoryArtifacts).Count + `
    1
$componentBytes = 0L
foreach ($relative in $expectedComponentFiles) {
    $componentBytes += (Get-Item -LiteralPath (Resolve-ContainedPath $bundleRoot $relative 'component byte count')).Length
}
$runtimeBytes = (Get-Item -LiteralPath $magikaPath).Length + (Get-Item -LiteralPath $directMlPath).Length
Write-Host "Verified Magika CLI 1.1.0 offline overlay: $verifiedFiles locked files, $($componentBytes + $runtimeBytes) bytes."
[pscustomobject]@{
    directory = $bundleRoot
    files = $verifiedFiles
    componentBytes = $componentBytes
    runtimeBytes = $runtimeBytes
    totalOwnedBytes = $componentBytes + $runtimeBytes
    packages = $rows.Count
}
