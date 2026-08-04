[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FlossExecutable,
    [string]$InventoryPath,
    [string]$StagedDirectory,
    [switch]$SkipVersionProbe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$licenseRoot = Join-Path $repoRoot 'licenses'
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath = Join-Path $licenseRoot 'floss-v3.1.1-win-x64.json'
}

$expectedInventory = [ordered]@{
    bytes = 26448L
    sha256 = 'da77275afdd87180fd473b28b9718594c9f96753b08a4b2bb6672dba4bd9a4dc'
}
$expectedAssets = @(
    [pscustomobject]@{ path = 'floss-v3.1.1/FLOSS-Apache-2.0.txt'; bytes = 11349L; sha256 = '787008a875d16021fa995a81553a98e481b938ece1f2e8a1910daf8dffaf1300' }
    [pscustomobject]@{ path = 'floss-v3.1.1/FLOSS-Embedded-Native-Runtime.tsv'; bytes = 19707L; sha256 = '9ededd2e04f393537746868ddec8183d5bd52d27a42e359eadc343bc38a2a322' }
    [pscustomobject]@{ path = 'floss-v3.1.1/FLOSS-Python-Third-Party-Notices.txt'; bytes = 98981L; sha256 = 'e49bf1db1a5b5f0a517ecd236e03f7462d89e7d4f7807b894e94bd72902f3bbe' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Microsoft-Embedded-Runtime-Notice.md'; bytes = 3965L; sha256 = '4064837f642eb5a2ca4f5243ad7ec13712a230a68146790685cb489bb160c35b' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Microsoft-Visual-Studio-Community-2017-License.docx'; bytes = 40556L; sha256 = 'c01c815bc7142b2c0e30cb2a53a981436408b7b21ca8a84b02dd54b1d43ffbd4' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Microsoft-Visual-Studio-Community-2019-License.docx'; bytes = 43685L; sha256 = '3dc17ec1490b436a97816aa4b06cc08eac9e37933a2813dbfa1886eaf23bde08' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Microsoft-Visual-Studio-Community-2022-License.docx'; bytes = 62859L; sha256 = '41a207b10c8ab91d0d2f10a854715f73dca54509581692d2fe179aa3ffcb8540' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Pydantic-Core-2.23.3-Rust-Inventory.tsv'; bytes = 15822L; sha256 = 'c26c213d2d852b42641e45f80f45d98ea93e69b2d0d928b4db3f782fc82ab065' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Pydantic-Core-2.23.3-Rust-Third-Party-Notices.txt'; bytes = 837881L; sha256 = '8a8a01de6c8aa289f58834d6544e0cfde1464957f71db4be0e79d97c25266834' }
    [pscustomobject]@{ path = 'floss-v3.1.1/PyInstaller-6.8.0-bootloader-waflib-LICENSE.txt'; bytes = 1338L; sha256 = 'f1a08e9a6ef535799105fca437e0fb38dc64acb0f224b115ab38fdc458cede53' }
    [pscustomobject]@{ path = 'floss-v3.1.1/PyInstaller-6.8.0-bootloader-zlib-LICENSE.txt'; bytes = 1002L; sha256 = '0f854f426019c475697e17f1b0fa638270f6e700bc756d9bfe50d17268bd3281' }
    [pscustomobject]@{ path = 'floss-v3.1.1/PyInstaller-6.8.0-COPYING.txt'; bytes = 32138L; sha256 = 'dcf75fdb959db1e3b41c0f8505069d2ece781b5ec6b3d0a4d30975cfc6580245' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Python-3.8.10-LICENSE.txt'; bytes = 32628L; sha256 = 'f830ec5b33c5ce41bf667d7fb4e395c5ee6fe20a108baebc99be565f0ef0907d' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Python-Flirt-0.8.10-Rust-Inventory.tsv'; bytes = 12076L; sha256 = '17423af8e2c39d9d45cbb9a5737afdf469cfd86a86b0093d59c21b8249f0fce9' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Python-Flirt-0.8.10-Rust-Third-Party-Notices.txt'; bytes = 614635L; sha256 = '9de5284afa080b7797c6e01b79f6c342113d81a071381f5a98faca9d886f1046' }
    [pscustomobject]@{ path = 'floss-v3.1.1/Rust-stdlib-COPYRIGHT.txt'; bytes = 21109L; sha256 = 'bd0581fef622b3d8b25836cf70feb7e1a7a6171ea9e440b46dfda74c46a0ed0d' }
    [pscustomobject]@{ path = 'floss-v3.1.1/sources/tqdm-4.66.4.tar.gz'; bytes = 169392L; sha256 = 'e4d936c9de8727928f3be6079590e97d9abfe8d39a590be678eb5919ffc186bb' }
)

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
    if ($hash -cne $ExpectedSha256) {
        throw "$Name SHA-256 mismatch: expected $ExpectedSha256, found ${hash}: $resolved"
    }
    return $resolved
}

function Assert-ExactSet([string[]]$Expected, [string[]]$Actual, [string]$Name) {
    $differences = @(Compare-Object `
        ($Expected | Sort-Object -CaseSensitive) `
        ($Actual | Sort-Object -CaseSensitive) `
        -CaseSensitive)
    if ($differences.Count -ne 0) {
        $details = $differences | ForEach-Object {
            $meaning = if ($_.SideIndicator -eq '<=') { 'expected only' } else { 'actual only' }
            "[$meaning] $($_.InputObject)"
        }
        throw "$Name drifted:`n$($details -join "`n")"
    }
}

function Get-ContainedPath([string]$Root, [string]$RelativePath, [string]$Name) {
    if (
        [string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\', [StringComparison]::Ordinal) -or
        @($RelativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0
    ) {
        throw "$Name is not a normalized relative path: $RelativePath"
    }
    $rootPath = [IO.Path]::GetFullPath($Root)
    $path = [IO.Path]::GetFullPath((Join-Path $rootPath ($RelativePath -replace '/', '\')))
    $prefix = $rootPath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escapes its root: $RelativePath"
    }
    return $path
}

function Assert-Version([string]$Executable) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.ArgumentList.Add('--version')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $start.Environment.Clear()
    $windowsRoot = if ([string]::IsNullOrWhiteSpace($env:SystemRoot)) { 'C:\Windows' } else { $env:SystemRoot }
    $temporaryRoot = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)
    $start.Environment['SystemRoot'] = $windowsRoot
    $start.Environment['WINDIR'] = $windowsRoot
    $start.Environment['PATH'] = (Join-Path $windowsRoot 'System32')
    $start.Environment['TEMP'] = $temporaryRoot
    $start.Environment['TMP'] = $temporaryRoot
    $start.Environment['COMSPEC'] = Join-Path $windowsRoot 'System32\cmd.exe'
    # Python's getpass.getuser() requires one of these variables on Windows.
    # Use a fixed value so the smoke probe does not inherit user-specific data.
    $start.Environment['USERNAME'] = 'bstrings-verifier'
    $start.Environment['NO_COLOR'] = '1'

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw 'FLOSS version process did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            try { $process.Kill($true) } catch { }
            throw 'FLOSS --version exceeded the 30-second timeout.'
        }
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult().Trim()
        $stderr = $stderrTask.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0) {
            throw "FLOSS --version exited $($process.ExitCode): $stderr"
        }
        if ($stdout -cne 'floss.exe v3.1.1-0-g3cd3ee6' -or -not [string]::IsNullOrWhiteSpace($stderr)) {
            throw "FLOSS version output drifted. stdout='$stdout' stderr='$stderr'"
        }
    }
    finally {
        $process.Dispose()
    }
}

$inventoryFile = Assert-ExactFile `
    $InventoryPath `
    $expectedInventory.bytes `
    $expectedInventory.sha256 `
    'FLOSS inventory'
$flossFile = Assert-ExactFile `
    $FlossExecutable `
    31866819L `
    '3a208ab834b4791e81592d66e91a7f07b3458504484d87c44ed32bc7384df7ef' `
    'FLOSS executable'

$inventory = Get-Content -LiteralPath $inventoryFile -Raw | ConvertFrom-Json
if (
    $inventory.schemaVersion -ne 1 -or
    [string]$inventory.profile -cne 'floss-v3.1.1-windows-x64-offline' -or
    [string]$inventory.product.name -cne 'FLARE FLOSS' -or
    [string]$inventory.product.version -cne '3.1.1' -or
    [string]$inventory.product.commandVersion -cne 'floss.exe v3.1.1-0-g3cd3ee6' -or
    [string]$inventory.product.upstreamCommit -cne '3cd3ee6a74992d98e91463c2658d9af446504821' -or
    [long]$inventory.executable.bytes -ne 31866819L -or
    [string]$inventory.executable.sha256 -cne '3a208ab834b4791e81592d66e91a7f07b3458504484d87c44ed32bc7384df7ef' -or
    [string]$inventory.executable.authenticodeStatus -cne 'NotSigned' -or
    [long]$inventory.upstreamReleaseArchive.bytes -ne 31580022L -or
    [string]$inventory.upstreamReleaseArchive.sha256 -cne '6c71089b8c629c69424b042769f1565f71adc6cd24b2f8d3713c96fa7fdac2fb'
) {
    throw 'FLOSS inventory identity or upstream executable pin drifted.'
}
if (
    [string]$inventory.embeddedPython.distribution -cne 'CPython 3.8.10 embeddable package for Windows x64' -or
    [long]$inventory.embeddedPython.sourceBytes -ne 8211403L -or
    [string]$inventory.embeddedPython.sourceSha256 -cne 'abbe314e9b41603dde0a823b76f5bbbe17b3de3e5ac4ef06b759da5466711271' -or
    [string]$inventory.pyInstaller.version -cne '6.8.0' -or
    [string]$inventory.pyInstaller.sourceSha256 -cne '3f4b6520f4423fe19bcc2fd63ab7238851ae2bdcbc98f25bc5d2f97cc62012e9'
) {
    throw 'FLOSS embedded Python or PyInstaller provenance drifted.'
}

$expectedRuntimePackages = @(
    'annotated-types/0.7.0', 'binary2strings/0.1.13', 'colorama/0.4.6', 'cxxfilt/0.3.0',
    'funcy/2.0', 'halo/0.0.31', 'intervaltree/3.1.0', 'log-symbols/0.0.14',
    'markdown-it-py/3.0.0', 'mdurl/0.1.2', 'msgpack/1.0.8', 'networkx/3.1',
    'pefile/2023.2.7', 'pyasn1-modules/0.3.0', 'pycparser/2.22', 'pydantic-core/2.23.3',
    'pydantic/2.9.1', 'pygments/2.18.0', 'python-flirt/0.8.10', 'rich/13.7.1',
    'six/1.16.0', 'sortedcontainers/2.4.0', 'spinners/0.0.24', 'tabulate/0.9.0',
    'termcolor/2.4.0', 'tqdm/4.66.4', 'typing_extensions/4.12.2', 'viv-utils/0.7.11',
    'vivisect/1.2.1'
)
$runtimePackages = @($inventory.pythonRuntimePackages)
$runtimeKeys = @($runtimePackages | ForEach-Object { "$($_.name)/$($_.version)" })
Assert-ExactSet $expectedRuntimePackages $runtimeKeys 'FLOSS embedded Python runtime package set'
if (@($runtimeKeys | Sort-Object -Unique).Count -ne $runtimeKeys.Count) {
    throw 'FLOSS runtime package inventory contains duplicate identities.'
}
foreach ($package in $runtimePackages) {
    if (
        -not [bool]$package.includedInExecutable -or
        [string]$package.noticeAsset -cne 'floss-v3.1.1/FLOSS-Python-Third-Party-Notices.txt' -or
        [string]::IsNullOrWhiteSpace([string]$package.artifactSha256)
    ) {
        throw "Runtime package '$($package.name)/$($package.version)' lacks exact inclusion or notice coverage."
    }
}
$buildOnlyKeys = @($inventory.buildOnlyPackages | ForEach-Object { "$($_.name)/$($_.version)" })
Assert-ExactSet @('click/8.1.7', 'dncil/1.0.2', 'pyasn1/0.5.1', 'PyYAML/6.0.1') $buildOnlyKeys 'FLOSS build-only package set'
foreach ($package in @($inventory.buildOnlyPackages)) {
    if ([bool]$package.includedInExecutable) {
        throw "Build-only package '$($package.name)' is incorrectly marked as executable content."
    }
}

$tqdm = @($runtimePackages | Where-Object { [string]$_.name -ceq 'tqdm' })
$sourceObligation = @($inventory.sourceObligations)
if (
    $tqdm.Count -ne 1 -or
    [string]$tqdm[0].licenseExpression -cne 'MPL-2.0 AND MIT' -or
    $sourceObligation.Count -ne 1 -or
    [string]$sourceObligation[0].component -cne 'tqdm' -or
    [string]$sourceObligation[0].version -cne '4.66.4' -or
    [string]$sourceObligation[0].asset -cne 'floss-v3.1.1/sources/tqdm-4.66.4.tar.gz' -or
    [long]$sourceObligation[0].bytes -ne 169392L -or
    [string]$sourceObligation[0].sha256 -cne 'e4d936c9de8727928f3be6079590e97d9abfe8d39a590be678eb5919ffc186bb'
) {
    throw 'The tqdm MPL-2.0 source obligation is incomplete or drifted.'
}

$inventoryAssets = @($inventory.assets)
Assert-ExactSet @($expectedAssets.path) @($inventoryAssets.path) 'FLOSS notice asset inventory'
if (@($inventoryAssets.path | Sort-Object -Unique).Count -ne $inventoryAssets.Count) {
    throw 'FLOSS notice asset inventory contains duplicate paths.'
}
$inventoryAssetsByPath = @{}
foreach ($asset in $inventoryAssets) {
    $inventoryAssetsByPath[[string]$asset.path] = $asset
}
foreach ($expected in $expectedAssets) {
    $actual = $inventoryAssetsByPath[[string]$expected.path]
    if ([long]$actual.bytes -ne $expected.bytes -or [string]$actual.sha256 -cne $expected.sha256) {
        throw "FLOSS inventory metadata drifted for '$($expected.path)'."
    }
    $source = Get-ContainedPath $licenseRoot ([string]$expected.path) 'FLOSS repository asset'
    $null = Assert-ExactFile $source $expected.bytes $expected.sha256 "FLOSS repository asset $($expected.path)"
}

$assetDirectory = Resolve-ExistingDirectory (Join-Path $licenseRoot 'floss-v3.1.1') 'FLOSS repository asset directory'
$actualRepositoryAssets = @(
    Get-ChildItem -LiteralPath $assetDirectory -Recurse -File | ForEach-Object {
        [IO.Path]::GetRelativePath($licenseRoot, $_.FullName).Replace('\', '/')
    }
)
Assert-ExactSet @($expectedAssets.path) $actualRepositoryAssets 'FLOSS repository asset directory'

$nativePath = Join-Path $assetDirectory 'FLOSS-Embedded-Native-Runtime.tsv'
$nativeRows = @(Import-Csv -LiteralPath $nativePath -Delimiter "`t")
$expectedNativeColumns = @(
    'component', 'source_inside_executable', 'bytes', 'sha256', 'file_version',
    'authenticode_status', 'signer_subject', 'signer_thumbprint'
)
if ($nativeRows.Count -ne 67 -or (@($nativeRows[0].PSObject.Properties.Name) -join "`t") -cne ($expectedNativeColumns -join "`t")) {
    throw 'FLOSS embedded native inventory must contain 67 rows with the reviewed schema.'
}
$expectedNativeNames = @(
    '_asyncio.pyd', '_bz2.pyd', '_ctypes.pyd', '_decimal.pyd', '_elementtree.pyd', '_hashlib.pyd',
    '_lzma.pyd', '_multiprocessing.pyd', '_overlapped.pyd', '_queue.pyd', '_socket.pyd', '_ssl.pyd',
    'api-ms-win-core-console-l1-1-0.dll', 'api-ms-win-core-datetime-l1-1-0.dll',
    'api-ms-win-core-debug-l1-1-0.dll', 'api-ms-win-core-errorhandling-l1-1-0.dll',
    'api-ms-win-core-file-l1-1-0.dll', 'api-ms-win-core-file-l1-2-0.dll',
    'api-ms-win-core-file-l2-1-0.dll', 'api-ms-win-core-handle-l1-1-0.dll',
    'api-ms-win-core-heap-l1-1-0.dll', 'api-ms-win-core-interlocked-l1-1-0.dll',
    'api-ms-win-core-libraryloader-l1-1-0.dll', 'api-ms-win-core-localization-l1-2-0.dll',
    'api-ms-win-core-memory-l1-1-0.dll', 'api-ms-win-core-namedpipe-l1-1-0.dll',
    'api-ms-win-core-processenvironment-l1-1-0.dll', 'api-ms-win-core-processthreads-l1-1-0.dll',
    'api-ms-win-core-processthreads-l1-1-1.dll', 'api-ms-win-core-profile-l1-1-0.dll',
    'api-ms-win-core-rtlsupport-l1-1-0.dll', 'api-ms-win-core-string-l1-1-0.dll',
    'api-ms-win-core-synch-l1-1-0.dll', 'api-ms-win-core-synch-l1-2-0.dll',
    'api-ms-win-core-sysinfo-l1-1-0.dll', 'api-ms-win-core-timezone-l1-1-0.dll',
    'api-ms-win-core-util-l1-1-0.dll', 'api-ms-win-crt-conio-l1-1-0.dll',
    'api-ms-win-crt-convert-l1-1-0.dll', 'api-ms-win-crt-environment-l1-1-0.dll',
    'api-ms-win-crt-filesystem-l1-1-0.dll', 'api-ms-win-crt-heap-l1-1-0.dll',
    'api-ms-win-crt-locale-l1-1-0.dll', 'api-ms-win-crt-math-l1-1-0.dll',
    'api-ms-win-crt-process-l1-1-0.dll', 'api-ms-win-crt-runtime-l1-1-0.dll',
    'api-ms-win-crt-stdio-l1-1-0.dll', 'api-ms-win-crt-string-l1-1-0.dll',
    'api-ms-win-crt-time-l1-1-0.dll', 'api-ms-win-crt-utility-l1-1-0.dll', 'base_library.zip',
    'binary2strings.cp38-win_amd64.pyd', 'flirt\flirt.cp38-win_amd64.pyd', 'libcrypto-1_1.dll',
    'libffi-7.dll', 'libssl-1_1.dll', 'msgpack\_cmsgpack.cp38-win_amd64.pyd',
    'MSVCP140_CODECVT_IDS.dll', 'MSVCP140.dll', 'pydantic_core\_pydantic_core.cp38-win_amd64.pyd',
    'pyexpat.pyd', 'python38.dll', 'select.pyd', 'ucrtbase.dll', 'unicodedata.pyd',
    'VCRUNTIME140_1.dll', 'VCRUNTIME140.dll'
)
$nativeNames = @($nativeRows.source_inside_executable)
Assert-ExactSet $expectedNativeNames $nativeNames 'FLOSS embedded native runtime set'
if (@($nativeNames | Sort-Object -Unique).Count -ne 67) {
    throw 'FLOSS embedded native runtime inventory contains duplicate names.'
}
foreach ($row in $nativeRows) {
    if ([long]$row.bytes -le 0 -or [string]$row.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Embedded native runtime row is incomplete: $($row.source_inside_executable)"
    }
}

$signatureGroups = @{
    eclipse = @($nativeRows | Where-Object signer_thumbprint -CEQ '78D53D67B05B47CE95E73A3D98EBC8B791860023')
    microsoft = @($nativeRows | Where-Object signer_thumbprint -CEQ 'ABDCA79AF9DD48A0EA702AD45260B3C03093FB4B')
    compatibility = @($nativeRows | Where-Object signer_thumbprint -CEQ 'C7D1B94B5229407F8903132FBCC8ECEC00DAA887')
    python = @($nativeRows | Where-Object signer_thumbprint -CEQ 'C91DCECB3A92A17B063059200B20F5CE251B5A95')
    unsigned = @($nativeRows | Where-Object authenticode_status -CEQ 'NotSigned')
    nonPe = @($nativeRows | Where-Object { [string]::IsNullOrWhiteSpace($_.authenticode_status) })
}
if (
    $signatureGroups.eclipse.Count -ne 40 -or
    $signatureGroups.microsoft.Count -ne 2 -or
    $signatureGroups.compatibility.Count -ne 1 -or
    $signatureGroups.python.Count -ne 19 -or
    $signatureGroups.unsigned.Count -ne 4 -or
    $signatureGroups.nonPe.Count -ne 1
) {
    throw 'FLOSS native Authenticode provenance groups drifted.'
}
Assert-ExactSet @('VCRUNTIME140.dll', 'VCRUNTIME140_1.dll') @($signatureGroups.microsoft.source_inside_executable) 'Microsoft-signed FLOSS runtime set'
Assert-ExactSet @('MSVCP140_CODECVT_IDS.dll') @($signatureGroups.compatibility.source_inside_executable) 'Compatibility-publisher FLOSS runtime set'
Assert-ExactSet @(
    'binary2strings.cp38-win_amd64.pyd', 'flirt\flirt.cp38-win_amd64.pyd',
    'msgpack\_cmsgpack.cp38-win_amd64.pyd', 'pydantic_core\_pydantic_core.cp38-win_amd64.pyd'
) @($signatureGroups.unsigned.source_inside_executable) 'Unsigned FLOSS native extension set'

$rustClosures = @($inventory.rustClosures)
if (
    $rustClosures.Count -ne 2 -or
    [string]$rustClosures[0].component -cne 'pydantic-core' -or
    [long]$rustClosures[0].dependencyCount -ne 92L -or
    -not ([string]$rustClosures[0].evidenceStatus).StartsWith('exact locked', [StringComparison]::Ordinal) -or
    [string]$rustClosures[1].component -cne 'python-flirt' -or
    [long]$rustClosures[1].conservativeDependencyCount -ne 65L -or
    -not ([string]$rustClosures[1].evidenceStatus).StartsWith('p2-limited:', [StringComparison]::Ordinal)
) {
    throw 'FLOSS Rust closure evidence status or counts drifted.'
}

if (-not $SkipVersionProbe) {
    Assert-Version $flossFile
}

if (-not [string]::IsNullOrWhiteSpace($StagedDirectory)) {
    $stagedRoot = Resolve-ExistingDirectory $StagedDirectory 'Staged bundle root'
    $stagedInventoryPath = Get-ContainedPath $stagedRoot 'licenses/floss-v3.1.1-win-x64.json' 'Staged FLOSS inventory'
    $null = Assert-ExactFile $stagedInventoryPath $expectedInventory.bytes $expectedInventory.sha256 'Staged FLOSS inventory'
    foreach ($asset in $expectedAssets) {
        $relative = 'licenses/' + [string]$asset.path
        $path = Get-ContainedPath $stagedRoot $relative 'Staged FLOSS asset'
        $null = Assert-ExactFile $path $asset.bytes $asset.sha256 "Staged FLOSS asset $relative"
    }
    $stagedAssetDirectory = Resolve-ExistingDirectory `
        (Get-ContainedPath $stagedRoot 'licenses/floss-v3.1.1' 'Staged FLOSS asset directory') `
        'Staged FLOSS asset directory'
    $actualStagedAssets = @(
        Get-ChildItem -LiteralPath $stagedAssetDirectory -Recurse -File | ForEach-Object {
            [IO.Path]::GetRelativePath((Join-Path $stagedRoot 'licenses'), $_.FullName).Replace('\', '/')
        }
    )
    Assert-ExactSet @($expectedAssets.path) $actualStagedAssets 'Staged FLOSS asset directory'
}

Write-Host ("FLOSS notices verified: 29 runtime packages, 67 native entries, 17 exact assets, and required tqdm source.")
