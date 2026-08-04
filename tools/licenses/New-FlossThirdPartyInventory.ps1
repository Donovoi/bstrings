[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PrimaryArtifactsDirectory,
    [string]$AssetDirectory,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($AssetDirectory)) {
    $AssetDirectory = Join-Path $repoRoot 'licenses\floss-v3.1.1'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot 'licenses\floss-v3.1.1-win-x64.json'
}

function Get-FileSpec([string]$Path) {
    if (-not [IO.File]::Exists($Path)) {
        throw "Required inventory input is missing: $Path"
    }
    $file = Get-Item -LiteralPath $Path -Force
    return [ordered]@{
        bytes = [long]$file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Package(
    [string]$Name,
    [string]$Version,
    [string]$LicenseExpression,
    [string]$Artifact,
    [bool]$Included,
    [string]$NoticeAsset
) {
    $artifactPath = Join-Path $PrimaryArtifactsDirectory $Artifact
    $spec = Get-FileSpec $artifactPath
    $entry = [ordered]@{
        name = $Name
        version = $Version
        licenseExpression = $LicenseExpression
        artifact = $Artifact
        artifactBytes = $spec.bytes
        artifactSha256 = $spec.sha256
        includedInExecutable = $Included
        evidence = if ($Included) {
            'recursive PyInstaller PYZ/native inventory plus exact upstream build pins'
        }
        else {
            'upstream build pin; package root absent from recursive PyInstaller PYZ inventory'
        }
    }
    if ($Included) {
        $entry.noticeAsset = $NoticeAsset
    }
    return $entry
}

$pythonNotice = 'floss-v3.1.1/FLOSS-Python-Third-Party-Notices.txt'
$runtimePackages = @(
    Package 'annotated-types' '0.7.0' 'MIT' 'annotated_types-0.7.0-py3-none-any.whl' $true $pythonNotice
    Package 'binary2strings' '0.1.13' 'MIT' 'binary2strings-0.1.13.tar.gz' $true $pythonNotice
    Package 'colorama' '0.4.6' 'BSD-3-Clause' 'colorama-0.4.6-py2.py3-none-any.whl' $true $pythonNotice
    Package 'cxxfilt' '0.3.0' 'BSD' 'cxxfilt-0.3.0-py2.py3-none-any.whl' $true $pythonNotice
    Package 'funcy' '2.0' 'BSD' 'funcy-2.0-py2.py3-none-any.whl' $true $pythonNotice
    Package 'halo' '0.0.31' 'MIT' 'halo-0.0.31.tar.gz' $true $pythonNotice
    Package 'intervaltree' '3.1.0' 'Apache-2.0' 'intervaltree-3.1.0.tar.gz' $true $pythonNotice
    Package 'log-symbols' '0.0.14' 'MIT' 'log_symbols-0.0.14-py3-none-any.whl' $true $pythonNotice
    Package 'markdown-it-py' '3.0.0' 'MIT' 'markdown_it_py-3.0.0-py3-none-any.whl' $true $pythonNotice
    Package 'mdurl' '0.1.2' 'MIT' 'mdurl-0.1.2-py3-none-any.whl' $true $pythonNotice
    Package 'msgpack' '1.0.8' 'Apache-2.0' 'msgpack-1.0.8-cp38-cp38-win_amd64.whl' $true $pythonNotice
    Package 'networkx' '3.1' 'BSD-3-Clause' 'networkx-3.1-py3-none-any.whl' $true $pythonNotice
    Package 'pefile' '2023.2.7' 'MIT' 'pefile-2023.2.7-py3-none-any.whl' $true $pythonNotice
    Package 'pyasn1-modules' '0.3.0' 'BSD' 'pyasn1_modules-0.3.0-py2.py3-none-any.whl' $true $pythonNotice
    Package 'pycparser' '2.22' 'BSD-3-Clause' 'pycparser-2.22-py3-none-any.whl' $true $pythonNotice
    Package 'pydantic-core' '2.23.3' 'MIT' 'pydantic_core-2.23.3-cp38-none-win_amd64.whl' $true $pythonNotice
    Package 'pydantic' '2.9.1' 'MIT' 'pydantic-2.9.1-py3-none-any.whl' $true $pythonNotice
    Package 'pygments' '2.18.0' 'BSD-2-Clause' 'pygments-2.18.0-py3-none-any.whl' $true $pythonNotice
    Package 'python-flirt' '0.8.10' 'Apache-2.0' 'python_flirt-0.8.10-cp38-none-win_amd64.whl' $true $pythonNotice
    Package 'rich' '13.7.1' 'MIT' 'rich-13.7.1-py3-none-any.whl' $true $pythonNotice
    Package 'six' '1.16.0' 'MIT' 'six-1.16.0-py2.py3-none-any.whl' $true $pythonNotice
    Package 'sortedcontainers' '2.4.0' 'Apache-2.0' 'sortedcontainers-2.4.0-py2.py3-none-any.whl' $true $pythonNotice
    Package 'spinners' '0.0.24' 'MIT' 'spinners-0.0.24-py3-none-any.whl' $true $pythonNotice
    Package 'tabulate' '0.9.0' 'MIT' 'tabulate-0.9.0-py3-none-any.whl' $true $pythonNotice
    Package 'termcolor' '2.4.0' 'MIT' 'termcolor-2.4.0-py3-none-any.whl' $true $pythonNotice
    Package 'tqdm' '4.66.4' 'MPL-2.0 AND MIT' 'tqdm-4.66.4-py3-none-any.whl' $true $pythonNotice
    Package 'typing_extensions' '4.12.2' 'PSF-2.0' 'typing_extensions-4.12.2-py3-none-any.whl' $true $pythonNotice
    Package 'viv-utils' '0.7.11' 'Apache-2.0' 'viv_utils-0.7.11-py2.py3-none-any.whl' $true $pythonNotice
    Package 'vivisect' '1.2.1' 'Apache-2.0' 'vivisect-1.2.1-py3-none-any.whl' $true $pythonNotice
)

$buildOnlyPackages = @(
    Package 'click' '8.1.7' 'BSD-3-Clause' 'click-8.1.7-py3-none-any.whl' $false ''
    Package 'dncil' '1.0.2' 'UNKNOWN' 'dncil-1.0.2-py3-none-any.whl' $false ''
    Package 'pyasn1' '0.5.1' 'BSD-2-Clause' 'pyasn1-0.5.1-py2.py3-none-any.whl' $false ''
    Package 'PyYAML' '6.0.1' 'MIT' 'PyYAML-6.0.1-cp38-cp38-win_amd64.whl' $false ''
)

$assetRoot = [IO.Path]::GetFullPath($AssetDirectory)
$assets = @(
    Get-ChildItem -LiteralPath $assetRoot -Recurse -File |
        Sort-Object FullName -CaseSensitive |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath((Join-Path $repoRoot 'licenses'), $_.FullName) -replace '\\', '/'
            $spec = Get-FileSpec $_.FullName
            [ordered]@{
                path = $relative
                bytes = $spec.bytes
                sha256 = $spec.sha256
                purpose = if ($relative -ceq 'floss-v3.1.1/sources/tqdm-4.66.4.tar.gz') {
                    'MPL-2.0 corresponding source snapshot'
                }
                elseif ($relative.EndsWith('.tsv', [StringComparison]::Ordinal)) {
                    'reviewed dependency/native inventory'
                }
                else {
                    'license, notice, or provenance evidence'
                }
            }
        }
)

$nativeRows = @(Import-Csv -LiteralPath (Join-Path $assetRoot 'FLOSS-Embedded-Native-Runtime.tsv') -Delimiter "`t")
$pydanticRows = @(Import-Csv -LiteralPath (Join-Path $assetRoot 'Pydantic-Core-2.23.3-Rust-Inventory.tsv') -Delimiter "`t")
$flirtRows = @(Import-Csv -LiteralPath (Join-Path $assetRoot 'Python-Flirt-0.8.10-Rust-Inventory.tsv') -Delimiter "`t")

$inventory = [ordered]@{
    schemaVersion = 1
    profile = 'floss-v3.1.1-windows-x64-offline'
    generatedFrom = 'byte-pinned upstream executable plus exact package/source audits'
    product = [ordered]@{
        name = 'FLARE FLOSS'
        version = '3.1.1'
        commandVersion = 'floss.exe v3.1.1-0-g3cd3ee6'
        upstreamRepository = 'https://github.com/mandiant/flare-floss'
        upstreamTag = 'v3.1.1'
        upstreamCommit = '3cd3ee6a74992d98e91463c2658d9af446504821'
        licenseExpression = 'Apache-2.0'
        licenseAsset = 'floss-v3.1.1/FLOSS-Apache-2.0.txt'
    }
    executable = [ordered]@{
        fileName = 'floss.exe'
        bytes = 31866819
        sha256 = '3a208ab834b4791e81592d66e91a7f07b3458504484d87c44ed32bc7384df7ef'
        authenticodeStatus = 'NotSigned'
        pyInstallerVersion = '6.8.0'
        pythonVersion = '3.8.10'
    }
    upstreamReleaseArchive = [ordered]@{
        url = 'https://github.com/mandiant/flare-floss/releases/download/v3.1.1/floss-v3.1.1-windows.zip'
        bytes = 31580022
        sha256 = '6c71089b8c629c69424b042769f1565f71adc6cd24b2f8d3713c96fa7fdac2fb'
    }
    embeddedPython = [ordered]@{
        distribution = 'CPython 3.8.10 embeddable package for Windows x64'
        sourceUrl = 'https://www.python.org/ftp/python/3.8.10/python-3.8.10-embed-amd64.zip'
        sourceBytes = 8211403
        sourceSha256 = 'abbe314e9b41603dde0a823b76f5bbbe17b3de3e5ac4ef06b759da5466711271'
        python38DllBytes = 4211376
        python38DllSha256 = '2f3e368f5bcc1dda5e951682008a509751e6395f7328fd0f02c4e1a11f67c128'
        licenseAsset = 'floss-v3.1.1/Python-3.8.10-LICENSE.txt'
    }
    pyInstaller = [ordered]@{
        version = '6.8.0'
        sourceArtifact = 'pyinstaller-6.8.0.tar.gz'
        sourceBytes = 4161521
        sourceSha256 = '3f4b6520f4423fe19bcc2fd63ab7238851ae2bdcbc98f25bc5d2f97cc62012e9'
        noticeAssets = @(
            'floss-v3.1.1/PyInstaller-6.8.0-COPYING.txt',
            'floss-v3.1.1/PyInstaller-6.8.0-bootloader-zlib-LICENSE.txt',
            'floss-v3.1.1/PyInstaller-6.8.0-bootloader-waflib-LICENSE.txt'
        )
    }
    pythonRuntimePackages = $runtimePackages
    buildOnlyPackages = $buildOnlyPackages
    nativeRuntime = [ordered]@{
        inventoryAsset = 'floss-v3.1.1/FLOSS-Embedded-Native-Runtime.tsv'
        entryCount = $nativeRows.Count
        microsoftRuntimeNotice = 'floss-v3.1.1/Microsoft-Embedded-Runtime-Notice.md'
        provenanceStatus = 'p2-limited: 40 Microsoft-resource runtime files are Eclipse-signed; preserve upstream executable unchanged'
        notableFingerprints = @(
            [ordered]@{ name = 'base_library.zip'; bytes = 843910; sha256 = '25b9e038ab3ccaafba554e90d5b6198a34e064d5c332a89ffc960fba278deb8a' },
            [ordered]@{ name = 'libcrypto-1_1.dll'; bytes = 3406016; sha256 = '296426e7ce11bc3d1cfa9f2aeb42f60c974da4af3b3efbeb0ba40e92e5299fdf' },
            [ordered]@{ name = 'libssl-1_1.dll'; bytes = 690368; sha256 = 'fddd0da02dcd41786e9aa04ba17ba391ce39dae6b1f54cfa1e2bb55bc753fce9' },
            [ordered]@{ name = 'libffi-7.dll'; bytes = 32792; sha256 = 'f60dd9f2fcbd495674dfc1555effb710eb081fc7d4cae5fa58c438ab50405081' },
            [ordered]@{ name = 'binary2strings.cp38-win_amd64.pyd'; bytes = 376832; sha256 = 'c8f87d2de53c4146ce708052064e0cf73d6e56336af225fd83dccac05b6526ab' },
            [ordered]@{ name = 'flirt\flirt.cp38-win_amd64.pyd'; bytes = 424960; sha256 = 'f38e6f975b358feaee3aeae97f063824673098d39b174e934f89add061aa43ce' },
            [ordered]@{ name = 'msgpack\_cmsgpack.cp38-win_amd64.pyd'; bytes = 134656; sha256 = '31bbfe73562f4815174ba7ef860088545d411cd4b667cc7594a3f322ea9e8b81' },
            [ordered]@{ name = 'pydantic_core\_pydantic_core.cp38-win_amd64.pyd'; bytes = 5046272; sha256 = '28cd4b7ad94650c7d547d4281a986e2cd61cac9a077f0d0ef5d8a05e6aad490c' }
        )
    }
    rustClosures = @(
        [ordered]@{
            component = 'pydantic-core'
            version = '2.23.3'
            evidenceStatus = 'exact locked x86_64-pc-windows-msvc normal dependency closure'
            dependencyCount = $pydanticRows.Count
            sourceArtifact = 'pydantic_core-2.23.3.tar.gz'
            sourceBytes = 402277
            sourceSha256 = '3cb0f65d8b4121c1b015c60104a685feb929a29d7cf204387c7f2688c7974690'
            rustcCommit = 'eeb90cda1969383f56a2637cbd3037bdf598841c'
            inventoryAsset = 'floss-v3.1.1/Pydantic-Core-2.23.3-Rust-Inventory.tsv'
            noticeAsset = 'floss-v3.1.1/Pydantic-Core-2.23.3-Rust-Third-Party-Notices.txt'
        },
        [ordered]@{
            component = 'python-flirt'
            version = '0.8.10'
            evidenceStatus = 'p2-limited: upstream tag has no Cargo.lock; current manifest-family resolution is conservative, not binary-exact'
            conservativeDependencyCount = $flirtRows.Count
            wheelBytes = 205061
            wheelSha256 = '2f4cebbf3cba105f5f0db64a030109c966f4aecb29f94c7ac6e4594f3d4b3c63'
            upstreamTagCommit = '2eed707fe1b76a681672a5731467dd92d43e18ff'
            upstreamSourceArchiveUrl = 'https://github.com/williballenthin/lancelot/archive/2eed707fe1b76a681672a5731467dd92d43e18ff.zip'
            upstreamSourceArchiveBytes = 16580247
            upstreamSourceArchiveSha256 = '16a982bed08ca9076880b21ed6b75cb40b6d7884bba9554272631e41198876b1'
            rustcCommit = '2d24fe591f30386d6d5fc2bb941c78d7266bf10f'
            exactBinaryMarkers = @(
                'pyo3 0.17.3', 'nom 7.1.3', 'inflate 0.4.5', 'adler32 1.2.0',
                'anyhow 1.0.80', 'parking_lot_core 0.9.9', 'smallvec 1.13.1'
            )
            inventoryAsset = 'floss-v3.1.1/Python-Flirt-0.8.10-Rust-Inventory.tsv'
            noticeAsset = 'floss-v3.1.1/Python-Flirt-0.8.10-Rust-Third-Party-Notices.txt'
        }
    )
    sourceObligations = @(
        [ordered]@{
            component = 'tqdm'
            version = '4.66.4'
            licenseExpression = 'MPL-2.0 AND MIT'
            asset = 'floss-v3.1.1/sources/tqdm-4.66.4.tar.gz'
            bytes = 169392
            sha256 = 'e4d936c9de8727928f3be6079590e97d9abfe8d39a590be678eb5919ffc186bb'
        }
    )
    assets = $assets
}

$json = $inventory | ConvertTo-Json -Depth 12
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))) | Out-Null
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Generated FLOSS inventory: $([IO.Path]::GetFullPath($OutputPath))"
