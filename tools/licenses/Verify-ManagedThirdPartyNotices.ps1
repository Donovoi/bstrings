[CmdletBinding()]
param(
    [string]$AssetsPath,
    [string]$InventoryPath,
    [string]$NoticeSourceDirectory,
    [string]$PublishedDirectory,
    [switch]$CompleteBundle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPath = Join-Path $repoRoot 'bstrings\bstrings.csproj'
$noticePath = Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md'
if ([string]::IsNullOrWhiteSpace($AssetsPath)) {
    $AssetsPath = Join-Path $repoRoot 'bstrings\obj\project.assets.json'
}
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath = Join-Path $repoRoot 'licenses\bstrings-managed-win-x64.json'
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

function Resolve-ExistingDirectory([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
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

function Assert-ExactSet(
    [string[]]$Expected,
    [string[]]$Actual,
    [string]$Name
) {
    $differences = @(Compare-Object `
        ($Expected | Sort-Object -CaseSensitive) `
        ($Actual | Sort-Object -CaseSensitive) `
        -CaseSensitive)
    if ($differences.Count -ne 0) {
        $details = $differences | ForEach-Object {
            $side = if ($_.SideIndicator -eq '<=') { 'inventory only' } else { 'restore only' }
            "[$side] $($_.InputObject)"
        }
        throw "$Name drifted:`n$($details -join "`n")"
    }
}

function Resolve-PackageRoot($Assets, [string]$PackagePath, [string]$Name) {
    foreach ($packageFolder in @($Assets.packageFolders.Keys)) {
        $candidate = Join-Path $packageFolder ($PackagePath -replace '/', '\')
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            return Resolve-ExistingDirectory $candidate $Name
        }
    }
    throw "$Name was not restored in any project.assets.json package folder: $PackagePath"
}

function Get-NuspecMetadata([string]$PackageRoot, [string]$Name) {
    $nuspecs = @(Get-ChildItem -LiteralPath $PackageRoot -File -Filter '*.nuspec')
    if ($nuspecs.Count -ne 1) {
        throw "$Name must contain exactly one nuspec; found $($nuspecs.Count): $PackageRoot"
    }
    [xml]$document = Get-Content -LiteralPath $nuspecs[0].FullName -Raw
    $metadata = $document.package.metadata
    $licenseNode = @($metadata.ChildNodes | Where-Object LocalName -CEQ 'license') |
        Select-Object -First 1
    $repositoryNode = @($metadata.ChildNodes | Where-Object LocalName -CEQ 'repository') |
        Select-Object -First 1
    $licenseType = if ($null -ne $licenseNode) {
        [string]$licenseNode.GetAttribute('type')
    }
    else {
        'url'
    }
    $license = if ($null -ne $licenseNode) {
        [string]$licenseNode.InnerText
    }
    else {
        $licenseUrlNode = @($metadata.ChildNodes | Where-Object LocalName -CEQ 'licenseUrl') |
            Select-Object -First 1
        if ($null -eq $licenseUrlNode) {
            throw "$Name nuspec has neither license nor licenseUrl metadata."
        }
        [string]$licenseUrlNode.InnerText
    }
    function ChildText([string]$ElementName) {
        $node = @($metadata.ChildNodes | Where-Object LocalName -CEQ $ElementName) |
            Select-Object -First 1
        if ($null -eq $node) {
            return ''
        }
        return [string]$node.InnerText
    }
    return [ordered]@{
        id = ChildText 'id'
        version = ChildText 'version'
        licenseType = $licenseType
        license = $license
        authors = ChildText 'authors'
        copyright = ChildText 'copyright'
        repositoryType = if ($null -eq $repositoryNode) { '' } else { [string]$repositoryNode.GetAttribute('type') }
        repositoryUrl = if ($null -eq $repositoryNode) { '' } else { [string]$repositoryNode.GetAttribute('url') }
        repositoryCommit = if ($null -eq $repositoryNode) { '' } else { [string]$repositoryNode.GetAttribute('commit') }
        projectUrl = ChildText 'projectUrl'
    }
}

function Assert-PackageMetadata($Expected, $Actual, [string]$Name) {
    foreach ($property in @(
        'id',
        'version',
        'licenseType',
        'license',
        'authors',
        'copyright',
        'repositoryType',
        'repositoryUrl',
        'repositoryCommit',
        'projectUrl'
    )) {
        $expectedValue = [string]$Expected.$property
        $actualValue = [string]$Actual[$property]
        if ($actualValue -cne $expectedValue) {
            throw "$Name nuspec $property drifted: expected '$expectedValue', found '$actualValue'."
        }
    }
}

function Assert-PackagePayload($Expected, $Assets, [string]$Name) {
    $packageRoot = Resolve-PackageRoot $Assets ([string]$Expected.packagePath) $Name
    $nupkgs = @(Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.nupkg' | Where-Object {
        $_.Name -notlike '*.symbols.nupkg'
    })
    if ($nupkgs.Count -ne 1) {
        throw "$Name must contain exactly one non-symbol nupkg; found $($nupkgs.Count)."
    }
    $nupkgHash = (Get-FileHash -LiteralPath $nupkgs[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($nupkgHash -ne [string]$Expected.nupkgSha256) {
        throw "$Name nupkg SHA-256 drifted: expected $($Expected.nupkgSha256), found $nupkgHash."
    }
    $metadata = Get-NuspecMetadata $packageRoot $Name
    Assert-PackageMetadata $Expected $metadata $Name
    return $packageRoot
}

$assetsFile = Resolve-ExistingFile $AssetsPath 'project.assets.json'
$inventoryFile = Resolve-ExistingFile $InventoryPath 'Managed Windows x64 inventory'
$projectFile = Resolve-ExistingFile $projectPath 'bstrings project'
$thirdPartyNoticeFile = Resolve-ExistingFile $noticePath 'bstrings third-party notice'
$assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json -AsHashtable
$inventory = Get-Content -LiteralPath $inventoryFile -Raw | ConvertFrom-Json

if (
    $inventory.schemaVersion -ne 1 -or
    $inventory.targetFramework -cne 'net10.0' -or
    $inventory.runtimeIdentifier -cne 'win-x64'
) {
    throw 'Managed dependency inventory must use schema 1 and the reviewed net10.0/win-x64 profile.'
}
$packages = @($inventory.packages)
if ($packages.Count -ne 9) {
    throw "Managed dependency inventory must contain exactly nine runtime packages; found $($packages.Count)."
}
$packageKeys = @($packages | ForEach-Object { "$($_.id)/$($_.version)" })
if (@($packageKeys | Sort-Object -Unique).Count -ne $packageKeys.Count) {
    throw 'Managed dependency inventory contains duplicate package identities.'
}

$targetName = "$($inventory.targetFramework)/$($inventory.runtimeIdentifier)"
if (-not $assets.targets.ContainsKey($targetName)) {
    throw "project.assets.json does not contain the reviewed target '$targetName'. Restore with --runtime win-x64."
}
$actualRuntimeKeys = @($assets.targets[$targetName].Keys | Where-Object {
    $entry = $assets.targets[$targetName][$_]
    @($entry.Keys | Where-Object {
        $_ -in @('compile', 'runtime', 'native', 'runtimeTargets', 'contentFiles')
    }).Count -ne 0
})
Assert-ExactSet $packageKeys $actualRuntimeKeys 'Managed win-x64 runtime package closure'

foreach ($package in $packages) {
    $key = "$($package.id)/$($package.version)"
    $library = $assets.libraries[$key]
    if ([string]$library.path -cne [string]$package.packagePath) {
        throw "$key package path drifted: expected '$($package.packagePath)', found '$($library.path)'."
    }
    if ([string]$library.sha512 -cne [string]$package.projectAssetsSha512) {
        throw "$key project.assets.json SHA-512 drifted."
    }
    $null = Assert-PackagePayload $package $assets "Managed package $key"
    if (@($package.noticeFiles).Count -eq 0) {
        throw "Managed package $key does not name a shipped license or notice file."
    }
}

$runtimePack = $inventory.runtimePack
if (
    [string]$runtimePack.id -cne 'Microsoft.NETCore.App.Runtime.win-x64' -or
    [string]$runtimePack.version -cne '10.0.10'
) {
    throw 'The reviewed runtime pack must remain Microsoft.NETCore.App.Runtime.win-x64 10.0.10.'
}
$framework = $assets.project.frameworks[[string]$inventory.targetFramework]
$runtimeDownloads = @($framework.downloadDependencies | Where-Object {
    [string]$_.name -ceq [string]$runtimePack.id
})
if (
    $runtimeDownloads.Count -ne 1 -or
    [string]$runtimeDownloads[0].version -cne '[10.0.10, 10.0.10]'
) {
    throw 'project.assets.json does not byte-pin the reviewed .NET win-x64 runtime pack at 10.0.10.'
}
$runtimeRoot = Assert-PackagePayload $runtimePack $assets 'Pinned .NET win-x64 runtime pack'
$runtimeSha512Path = Join-Path $runtimeRoot (
    ([string]$runtimePack.id).ToLowerInvariant() + '.' + [string]$runtimePack.version + '.nupkg.sha512'
)
$runtimeSha512 = (Get-Content -LiteralPath (Resolve-ExistingFile $runtimeSha512Path 'Runtime pack SHA-512 record') -Raw).Trim()
if ($runtimeSha512 -cne [string]$runtimePack.projectAssetsSha512) {
    throw 'The restored .NET win-x64 runtime pack SHA-512 record drifted.'
}

[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$runtimeVersionNodes = @(
    $project.Project.ChildNodes |
        Where-Object LocalName -CEQ 'PropertyGroup' |
        ForEach-Object { $_.ChildNodes } |
        Where-Object LocalName -CEQ 'RuntimeFrameworkVersion'
)
if ($runtimeVersionNodes.Count -ne 1) {
    throw "bstrings.csproj must declare RuntimeFrameworkVersion exactly once; found $($runtimeVersionNodes.Count)."
}
$runtimeVersion = [string]$runtimeVersionNodes[0].InnerText
if ($runtimeVersion -cne [string]$runtimePack.version) {
    throw "bstrings.csproj RuntimeFrameworkVersion must be exactly $($runtimePack.version); found '$runtimeVersion'."
}

foreach ($spec in @($inventory.repositoryNoticeFiles)) {
    $path = Join-Path $repoRoot (([string]$spec.path) -replace '/', '\')
    $null = Assert-ExactFile `
        $path `
        ([long]$spec.bytes) `
        ([string]$spec.sha256) `
        "Reviewed repository notice $($spec.path)"
}

$projectText = Get-Content -LiteralPath $projectFile -Raw
$noticeText = Get-Content -LiteralPath $thirdPartyNoticeFile -Raw
foreach ($requiredReference in @(
    'CORE_RELEASE_README.md',
    'LICENSE.md',
    'bstrings-managed-win-x64.json',
    'ReleaseNoticeSourceDirectory',
    'DeviceIOControlLib-0.1.6-LICENSE.txt',
    'ILGPU-1.5.3-LICENSE.txt',
    'ILGPU-1.5.3-LICENSE-3RD-PARTY.txt',
    'dotnet-runtime-win-x64-10.0.10-LICENSE.TXT',
    'dotnet-runtime-win-x64-10.0.10-THIRD-PARTY-NOTICES.TXT',
    'Rust-1.95.0-COPYRIGHT-library.html'
)) {
    if (-not $projectText.Contains($requiredReference, [StringComparison]::Ordinal)) {
        throw "bstrings.csproj does not package required release notice '$requiredReference'."
    }
}
foreach ($requiredReference in @(
    'bstrings-managed-win-x64.json',
    'DeviceIOControlLib-0.1.6-LICENSE.txt',
    'ILGPU-1.5.3-LICENSE.txt',
    'ILGPU-1.5.3-LICENSE-3RD-PARTY.txt',
    'dotnet-runtime-win-x64-10.0.10-LICENSE.TXT',
    'dotnet-runtime-win-x64-10.0.10-THIRD-PARTY-NOTICES.TXT',
    'Rust-1.95.0-COPYRIGHT-library.html'
)) {
    if (-not $noticeText.Contains($requiredReference, [StringComparison]::Ordinal)) {
        throw "THIRD_PARTY_NOTICES.md does not reference '$requiredReference'."
    }
}
foreach ($package in $packages) {
    if (-not $noticeText.Contains([string]$package.id, [StringComparison]::Ordinal)) {
        throw "THIRD_PARTY_NOTICES.md does not name managed package '$($package.id)'."
    }
}
foreach ($name in @([string]$runtimePack.id, 'Rust standard library 1.95.0')) {
    if (-not $noticeText.Contains($name, [StringComparison]::Ordinal)) {
        throw "THIRD_PARTY_NOTICES.md does not name '$name'."
    }
}

function Assert-ReleaseNoticeDirectory([string]$Root, [string]$Name) {
    $resolvedRoot = Resolve-ExistingDirectory $Root $Name
    foreach ($spec in @($inventory.releaseNoticeSources)) {
        $path = Join-Path $resolvedRoot (([string]$spec.destination) -replace '/', '\')
        $null = Assert-ExactFile `
            $path `
            ([long]$spec.bytes) `
            ([string]$spec.sha256) `
            "$Name file $($spec.destination)"
    }
}

if (-not [string]::IsNullOrWhiteSpace($NoticeSourceDirectory)) {
    Assert-ReleaseNoticeDirectory $NoticeSourceDirectory 'Staged release-notice source'
}
if (-not [string]::IsNullOrWhiteSpace($PublishedDirectory)) {
    $published = Resolve-ExistingDirectory $PublishedDirectory 'Published bstrings directory'
    if ($CompleteBundle) {
        foreach ($completeMarker in @('airgap-config.json', 'airgap-manifest.json')) {
            $null = Resolve-ExistingFile `
                (Join-Path $published $completeMarker) `
                "Complete-bundle marker $completeMarker"
        }
    }
    Assert-ReleaseNoticeDirectory $published 'Published bstrings directory'
    foreach ($relativePath in @(
        'THIRD_PARTY_NOTICES.md',
        'licenses/bstrings-managed-win-x64.json',
        'licenses/bstrings-core-win-x64.tsv',
        'licenses/Apache-2.0.txt',
        'licenses/MIT.txt',
        'licenses/Unicode-3.0.txt'
    )) {
        $source = Resolve-ExistingFile `
            (Join-Path $repoRoot ($relativePath -replace '/', '\')) `
            "Repository release notice $relativePath"
        $sourceItem = Get-Item -LiteralPath $source -Force
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        $null = Assert-ExactFile `
            (Join-Path $published ($relativePath -replace '/', '\')) `
            $sourceItem.Length `
            $sourceHash `
            "Published release notice $relativePath"
    }
    $publishedReadmeSource = if ($CompleteBundle) { 'README.md' } else { 'CORE_RELEASE_README.md' }
    foreach ($repositoryFile in @(
        [pscustomobject]@{ source = 'LICENSE.md'; destination = 'LICENSE.md' },
        [pscustomobject]@{ source = $publishedReadmeSource; destination = 'README.md' }
    )) {
        $source = Resolve-ExistingFile `
            (Join-Path $repoRoot ([string]$repositoryFile.source)) `
            "Repository package file $($repositoryFile.source)"
        $sourceItem = Get-Item -LiteralPath $source -Force
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        $null = Assert-ExactFile `
            (Join-Path $published ([string]$repositoryFile.destination)) `
            $sourceItem.Length `
            $sourceHash `
            "Published package file $($repositoryFile.destination)"
    }
}

Write-Host ((
        "Managed release notices verified: {0} exact NuGet packages, runtime pack {1}, " +
        "and {2} byte-pinned external notices."
    ) -f $packages.Count, $runtimePack.version, @($inventory.releaseNoticeSources).Count)
