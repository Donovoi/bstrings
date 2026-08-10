[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackendArchive,
    [Parameter(Mandatory = $true)]
    [string]$CudaRuntimeArchive,
    [Parameter(Mandatory = $true)]
    [string]$LicenseFile,
    [Parameter(Mandatory = $true)]
    [string]$DestinationDirectory,
    [string]$ComponentLockPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-CudaProgress([double]$Percent, [string]$Detail) {
    Write-Host ("Progress: llama.cpp CUDA overlay: {0:F1}% ({1})" -f $Percent, $Detail)
}

Write-CudaProgress 0.0 'starting'

if ([string]::IsNullOrWhiteSpace($ComponentLockPath)) {
    $ComponentLockPath = Join-Path $PSScriptRoot 'offline-components.lock.json'
}

function Resolve-PhysicalFile([string]$Path, [string]$Name) {
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

function Assert-ExactFile(
    [string]$Path,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$Name
) {
    $resolved = Resolve-PhysicalFile $Path $Name
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Length -ne $ExpectedBytes) {
        throw "$Name byte length mismatch: expected $ExpectedBytes, found $($item.Length): $resolved"
    }
    $actual = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256) {
        throw "$Name SHA-256 mismatch: expected $ExpectedSha256, found ${actual}: $resolved"
    }
    return $resolved
}

function Resolve-SafeOutputPath([string]$Root, [string]$RelativePath, [string]$Name) {
    if (
        [string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\/])\.\.([\/]|$)'
    ) {
        throw "$Name must be a safe relative path: '$RelativePath'"
    }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $candidate = [IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath))
    if (-not $candidate.StartsWith(
        $rootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "$Name escapes the controlled destination: $candidate"
    }
    return $candidate
}

function Resolve-Dumpbin {
    $vswhereCandidates = @(
        @(
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
            (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
        ) | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            (Test-Path -LiteralPath $_ -PathType Leaf)
        }
    )
    if ($vswhereCandidates.Count -lt 1) {
        throw 'vswhere.exe is required for the CUDA overlay PE-import audit.'
    }
    $vswhere = Resolve-PhysicalFile $vswhereCandidates[0] 'vswhere'
    $instances = @(& $vswhere `
        -latest `
        -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -format json | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $instances.Count -ne 1) {
        throw 'A complete Visual Studio C++ x64 toolchain was not found for the CUDA overlay audit.'
    }
    $toolsRoot = Join-Path ([string]$instances[0].installationPath) 'VC\Tools\MSVC'
    $candidates = @(
        Get-ChildItem -LiteralPath $toolsRoot -Recurse -Filter 'dumpbin.exe' -File |
            Where-Object {
                $_.FullName.Contains(
                    '\Hostx64\x64\',
                    [StringComparison]::OrdinalIgnoreCase
                )
            } |
            Sort-Object FullName -Descending
    )
    if ($candidates.Count -lt 1) {
        throw 'The Visual Studio x64 dumpbin.exe was not found.'
    }
    return Resolve-PhysicalFile $candidates[0].FullName 'dumpbin'
}

function Get-PeImports([string]$Dumpbin, [string]$Path) {
    $output = & $Dumpbin /nologo /dependents $Path 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dumpbin dependency inspection failed for ${Path}: $($output | Out-String)"
    }
    return @(
        $output |
            ForEach-Object { $_.ToString().Trim() } |
            Where-Object { $_ -match '^[A-Za-z0-9_.-]+\.dll$' } |
            Sort-Object -Unique
    )
}

function Assert-ArchiveInventory([IO.Compression.ZipArchive]$Archive, [object]$Spec) {
    $entries = @($Archive.Entries)
    $measure = $entries | Measure-Object -Property Length -Sum
    if (
        $entries.Count -ne [int]$Spec.entries -or
        [long]$measure.Sum -ne [long]$Spec.expandedBytes
    ) {
        throw "CUDA archive '$($Spec.id)' inventory mismatch: expected $($Spec.entries) entries and $($Spec.expandedBytes) expanded bytes."
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        if (-not $seen.Add([string]$entry.FullName)) {
            throw "CUDA archive '$($Spec.id)' contains a duplicate path: $($entry.FullName)"
        }
    }
}

$lockPath = Resolve-PhysicalFile $ComponentLockPath 'Offline component lock'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.profile -ne 'windows-x64-offline-v2') {
    throw "Unsupported offline component lock schema or profile: $lockPath"
}
$overlay = $lock.llamaCudaOverlay
if (
    $null -eq $overlay -or
    [string]$overlay.sourceTag -ne [string]$lock.components.llamaCpp.sourceTag -or
    [string]$overlay.sourceCommit -ne [string]$lock.components.llamaCpp.sourceCommit -or
    [string]$overlay.platform -cne 'windows-x64' -or
    [string]$overlay.acceptanceHardware.computeCapability -cne '8.9'
) {
    throw 'The llama.cpp CUDA overlay lock is missing or does not match the source-built CPU runtime.'
}
Write-CudaProgress 10.0 'component lock authenticated'
$archives = @($overlay.archives)
if (
    $archives.Count -ne 2 -or
    @($archives | ForEach-Object { [string]$_.id } | Sort-Object -Unique).Count -ne 2
) {
    throw 'The CUDA overlay must lock exactly two uniquely identified official archives.'
}
$archiveById = @{}
foreach ($archive in $archives) {
    if (
        [string]$archive.id -notmatch '^[a-z0-9-]+$' -or
        [IO.Path]::GetFileName([string]$archive.fileName) -cne [string]$archive.fileName -or
        [long]$archive.bytes -lt 1 -or
        [string]$archive.sha256 -notmatch '^[0-9a-f]{64}$' -or
        [int]$archive.entries -lt 1 -or
        [long]$archive.expandedBytes -lt 1
    ) {
        throw 'The CUDA overlay contains an invalid archive record.'
    }
    $archiveById[[string]$archive.id] = $archive
}
$archivePaths = @{
    'llama-cuda-backend' = Assert-ExactFile `
        $BackendArchive `
        ([long]$archiveById['llama-cuda-backend'].bytes) `
        ([string]$archiveById['llama-cuda-backend'].sha256) `
        'Official llama.cpp CUDA backend archive'
    'cuda-runtime' = Assert-ExactFile `
        $CudaRuntimeArchive `
        ([long]$archiveById['cuda-runtime'].bytes) `
        ([string]$archiveById['cuda-runtime'].sha256) `
        'Official llama.cpp CUDA redistributable archive'
}
$licensePath = Assert-ExactFile `
    $LicenseFile `
    ([long]$overlay.license.bytes) `
    ([string]$overlay.license.sha256) `
    'NVIDIA CUDA 12.4 EULA'
Write-CudaProgress 25.0 'official archives and license authenticated'

$runtimeSpecs = @($overlay.runtimeFiles)
if (
    $runtimeSpecs.Count -ne 4 -or
    @($runtimeSpecs | ForEach-Object { [string]$_.path } | Sort-Object -Unique).Count -ne 4
) {
    throw 'The CUDA overlay must lock exactly four uniquely named runtime files.'
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
[IO.Directory]::CreateDirectory($destination) | Out-Null
$destinationItem = Get-Item -LiteralPath $destination -Force
if (($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "CUDA overlay destination is a link or reparse point: $destination"
}
$incomplete = Join-Path $destination '.cuda-overlay.incomplete'
if (Test-Path -LiteralPath $incomplete) {
    throw "CUDA overlay destination already contains an incomplete marker: $incomplete"
}
[IO.File]::WriteAllText($incomplete, "CUDA overlay staging did not complete.`n")

Add-Type -AssemblyName System.IO.Compression.FileSystem
$opened = @{}
try {
    foreach ($archive in $archives) {
        $zip = [IO.Compression.ZipFile]::OpenRead($archivePaths[[string]$archive.id])
        $opened[[string]$archive.id] = $zip
        Assert-ArchiveInventory $zip $archive
    }

    $stagedFiles = @()
    foreach ($spec in $runtimeSpecs) {
        $archiveId = [string]$spec.archive
        if (-not $opened.ContainsKey($archiveId)) {
            throw "CUDA runtime file references an unknown archive: $archiveId"
        }
        if (
            [string]$spec.entry -ne [IO.Path]::GetFileName([string]$spec.entry) -or
            [string]$spec.path -ne [IO.Path]::GetFileName([string]$spec.path) -or
            [long]$spec.bytes -lt 1 -or
            [string]$spec.sha256 -notmatch '^[0-9a-f]{64}$'
        ) {
            throw 'The CUDA overlay contains an invalid runtime file record.'
        }
        $entries = @($opened[$archiveId].Entries | Where-Object {
            [string]$_.FullName -ceq [string]$spec.entry
        })
        if ($entries.Count -ne 1) {
            throw "CUDA archive '$archiveId' does not contain exactly one '$($spec.entry)' entry."
        }
        if ([long]$entries[0].Length -ne [long]$spec.bytes) {
            throw "CUDA archive entry byte length does not match its lock: $($spec.entry)"
        }
        $target = Resolve-SafeOutputPath $destination ([string]$spec.path) 'CUDA runtime file'
        if (Test-Path -LiteralPath $target) {
            throw "CUDA overlay refuses to overwrite an existing runtime file: $target"
        }
        $input = $null
        $output = $null
        try {
            $input = $entries[0].Open()
            $output = [IO.FileStream]::new(
                $target,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None
            )
            $input.CopyTo($output, 1024 * 1024)
            $output.Flush($true)
        }
        finally {
            if ($null -ne $output) { $output.Dispose() }
            if ($null -ne $input) { $input.Dispose() }
        }
        $null = Assert-ExactFile `
            $target `
            ([long]$spec.bytes) `
            ([string]$spec.sha256) `
            "Staged CUDA runtime $($spec.path)"
        $stagedFiles += [pscustomobject]@{ spec = $spec; path = $target }
        Write-CudaProgress `
            (25.0 + (40.0 * $stagedFiles.Count / $runtimeSpecs.Count)) `
            "staged $($stagedFiles.Count)/$($runtimeSpecs.Count) runtime files"
    }

    $dumpbin = Resolve-Dumpbin
    foreach ($staged in $stagedFiles) {
        $actualImports = @(Get-PeImports $dumpbin $staged.path)
        $expectedImports = @($staged.spec.imports | ForEach-Object { [string]$_ })
        $actualNormalized = @($actualImports | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object)
        $expectedNormalized = @($expectedImports | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object)
        if (($actualNormalized -join '|') -cne ($expectedNormalized -join '|')) {
            throw "CUDA runtime PE imports differ from the reviewed lock for $($staged.spec.path)."
        }
    }
    Write-CudaProgress 80.0 'PE import closure verified'

    $stagedLicense = Resolve-SafeOutputPath `
        $destination `
        ([string]$overlay.license.path) `
        'CUDA license destination'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $stagedLicense)) | Out-Null
    if (Test-Path -LiteralPath $stagedLicense) {
        throw "CUDA overlay refuses to overwrite an existing license: $stagedLicense"
    }
    [IO.File]::Copy($licensePath, $stagedLicense, $false)
    $null = Assert-ExactFile `
        $stagedLicense `
        ([long]$overlay.license.bytes) `
        ([string]$overlay.license.sha256) `
        'Staged NVIDIA CUDA 12.4 EULA'

    $provenancePath = Resolve-SafeOutputPath `
        $destination `
        ([string]$overlay.provenancePath) `
        'CUDA overlay provenance destination'
    if (Test-Path -LiteralPath $provenancePath) {
        throw "CUDA overlay refuses to overwrite existing provenance: $provenancePath"
    }
    $provenance = [ordered]@{
        schemaVersion = 1
        component = 'llama.cpp-cuda-overlay'
        version = [string]$overlay.version
        sourceTag = [string]$overlay.sourceTag
        sourceCommit = [string]$overlay.sourceCommit
        platform = [string]$overlay.platform
        distribution = [string]$overlay.distribution
        acceptanceHardware = $overlay.acceptanceHardware
        archives = @($archives | ForEach-Object {
            [ordered]@{
                id = [string]$_.id
                fileName = [string]$_.fileName
                url = [string]$_.url
                bytes = [long]$_.bytes
                sha256 = [string]$_.sha256
                entries = [int]$_.entries
                expandedBytes = [long]$_.expandedBytes
            }
        })
        runtimeFiles = @($runtimeSpecs | ForEach-Object {
            [ordered]@{
                archive = [string]$_.archive
                entry = [string]$_.entry
                path = [string]$_.path
                bytes = [long]$_.bytes
                sha256 = [string]$_.sha256
                role = [string]$_.role
                imports = @($_.imports | ForEach-Object { [string]$_ })
            }
        })
        license = [ordered]@{
            source = [string]$overlay.license.source
            fileName = [string]$overlay.license.fileName
            url = [string]$overlay.license.url
            bytes = [long]$overlay.license.bytes
            sha256 = [string]$overlay.license.sha256
            path = [string]$overlay.license.path
        }
        cpuFallback = [ordered]@{
            sourceBuiltRuntimeRetained = $true
            dynamicBackend = 'ggml-cuda.dll'
            behavior = 'GPU absence or backend load rejection leaves the source-built CPU backends available.'
        }
    }
    $provenance | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $provenancePath -Encoding utf8

    [IO.File]::Delete($incomplete)
    Write-CudaProgress 100.0 'overlay staged and provenance recorded'
    [pscustomobject]@{
        outputDirectory = $destination
        provenance = $provenancePath
        runtimeFiles = @($runtimeSpecs | ForEach-Object { [string]$_.path })
        license = $stagedLicense
    }
}
finally {
    foreach ($zip in $opened.Values) {
        $zip.Dispose()
    }
}
