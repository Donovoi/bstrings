[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PrimaryArtifactsDirectory,
    [Parameter(Mandatory)]
    [string]$PydanticCoreSourceDirectory,
    [Parameter(Mandatory)]
    [string]$PydanticCoreCargoHome,
    [Parameter(Mandatory)]
    [string]$PythonFlirtSourceDirectory,
    [Parameter(Mandatory)]
    [string]$PythonFlirtCargoHome,
    [Parameter(Mandatory)]
    [string]$NativeExtractionDirectory,
    [Parameter(Mandatory)]
    [string]$DestinationDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$utf8 = [Text.UTF8Encoding]::new($false)

function Write-Utf8File([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Get-ExactFile([string]$Root, [string]$RelativePath) {
    $rootPath = [IO.Path]::GetFullPath($Root)
    $path = [IO.Path]::GetFullPath((Join-Path $rootPath ($RelativePath -replace '/', '\')))
    $prefix = $rootPath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Input path escapes its root: $RelativePath"
    }
    if (-not [IO.File]::Exists($path)) {
        throw "Required input file is missing: $path"
    }
    return $path
}

function Read-ZipEntry([string]$ArchivePath, [string]$EntryName) {
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = @($archive.Entries | Where-Object FullName -CEQ $EntryName)
        if ($entry.Count -ne 1) {
            throw "Expected one '$EntryName' entry in $ArchivePath; found $($entry.Count)."
        }
        $stream = $entry[0].Open()
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Read-TarEntry([string]$ArchivePath, [string]$EntryName) {
    $lines = @(& tar -xOf $ArchivePath $EntryName 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "tar could not read '$EntryName' from '$ArchivePath': $($lines -join [Environment]::NewLine)"
    }
    return ($lines -join "`n") + "`n"
}

function Read-ArchiveEntry([string]$ArchivePath, [string]$EntryName) {
    if ($ArchivePath.EndsWith('.whl', [StringComparison]::OrdinalIgnoreCase)) {
        return Read-ZipEntry $ArchivePath $EntryName
    }
    if ($ArchivePath.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase)) {
        return Read-TarEntry $ArchivePath $EntryName
    }
    throw "Unsupported package archive: $ArchivePath"
}

function Clean-Tsv([AllowNull()][object]$Value) {
    if ($null -eq $Value) {
        return ''
    }
    return ([string]$Value) -replace "[`r`n`t]+", ' '
}

function Invoke-CargoText([string[]]$Arguments, [string]$CargoHome) {
    $previousCargoHome = $env:CARGO_HOME
    try {
        $env:CARGO_HOME = $CargoHome
        $output = @(& cargo @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "cargo $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
        }
        return $output
    }
    finally {
        $env:CARGO_HOME = $previousCargoHome
    }
}

function New-CargoNotice(
    [string]$ManifestPath,
    [string]$CargoHome,
    [string]$Evidence,
    [string]$NoticePath,
    [string]$InventoryPath,
    [string[]]$HeaderLines
) {
    $metadataText = Invoke-CargoText @(
        'metadata',
        '--manifest-path', $ManifestPath,
        '--locked',
        '--offline',
        '--filter-platform', 'x86_64-pc-windows-msvc',
        '--format-version', '1'
    ) $CargoHome | Out-String
    $metadata = $metadataText | ConvertFrom-Json
    $packagesByKey = @{}
    foreach ($package in @($metadata.packages)) {
        $packagesByKey["$($package.name)|$($package.version)"] = $package
    }

    $tree = Invoke-CargoText @(
        'tree',
        '--manifest-path', $ManifestPath,
        '--locked',
        '--offline',
        '--target', 'x86_64-pc-windows-msvc',
        '--edges', 'normal',
        '--prefix', 'none',
        '--format', '{p}'
    ) $CargoHome
    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $tree) {
        if ([string]$line -match '^(?<name>[A-Za-z0-9_-]+) v(?<version>[^\s]+)') {
            $null = $keys.Add("$($Matches.name)|$($Matches.version)")
        }
    }
    if ($keys.Count -eq 0) {
        throw "Cargo returned no packages for $ManifestPath"
    }

    $rows = [Collections.Generic.List[object]]::new()
    $notice = [Text.StringBuilder]::new()
    foreach ($line in $HeaderLines) {
        $null = $notice.AppendLine($line)
    }
    $null = $notice.AppendLine()

    foreach ($key in @($keys | Sort-Object -CaseSensitive)) {
        if (-not $packagesByKey.ContainsKey($key)) {
            throw "cargo metadata does not contain tree package '$key'."
        }
        $package = $packagesByKey[$key]
        if ([string]::IsNullOrWhiteSpace([string]$package.license)) {
            throw "Cargo package '$key' has no declared license expression."
        }
        $packageRoot = [IO.Path]::GetDirectoryName([string]$package.manifest_path)
        $licenseSearchRoot = $packageRoot
        $licenseFiles = @(
            Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
                Where-Object {
                    $_.Name -match '^(?i:licen[cs]e|copying|notice|copyright|unlicense)(?:[._-].*)?$'
                } |
                Sort-Object FullName -CaseSensitive
        )
        if ($licenseFiles.Count -eq 0) {
            $licenseSearchRoot = [IO.Path]::GetDirectoryName($packageRoot)
            $licenseFiles = @(
                Get-ChildItem -LiteralPath $licenseSearchRoot -File |
                    Where-Object {
                        $_.Name -match '^(?i:licen[cs]e|copying|notice|copyright|unlicense)(?:[._-].*)?$'
                    } |
                    Sort-Object FullName -CaseSensitive
            )
        }
        if ($licenseFiles.Count -eq 0) {
            throw "Cargo package '$key' has no discoverable license text in '$packageRoot'."
        }
        $authors = @($package.authors | ForEach-Object { Clean-Tsv $_ }) -join '; '
        $rows.Add([pscustomobject]@{
            evidence = $Evidence
            package = [string]$package.name
            version = [string]$package.version
            declared_license = [string]$package.license
            authors = $authors
            repository = Clean-Tsv $package.repository
        })

        $null = $notice.AppendLine(('=' * 80))
        $null = $notice.AppendLine("Package: $($package.name) $($package.version)")
        $null = $notice.AppendLine("Evidence: $Evidence")
        $null = $notice.AppendLine("Declared license: $($package.license)")
        if (-not [string]::IsNullOrWhiteSpace($authors)) {
            $null = $notice.AppendLine("Authors: $authors")
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$package.repository)) {
            $null = $notice.AppendLine("Repository: $($package.repository)")
        }
        foreach ($licenseFile in $licenseFiles) {
            $relativeLicense = [IO.Path]::GetRelativePath($licenseSearchRoot, $licenseFile.FullName) -replace '\\', '/'
            $null = $notice.AppendLine("--- $relativeLicense ---")
            $null = $notice.AppendLine([IO.File]::ReadAllText($licenseFile.FullName))
        }
        $null = $notice.AppendLine()
    }

    $tsv = [Text.StringBuilder]::new()
    $null = $tsv.AppendLine("evidence`tpackage`tversion`tdeclared_license`tauthors`trepository")
    foreach ($row in @($rows | Sort-Object package, version)) {
        $null = $tsv.AppendLine((@(
            Clean-Tsv $row.evidence
            Clean-Tsv $row.package
            Clean-Tsv $row.version
            Clean-Tsv $row.declared_license
            Clean-Tsv $row.authors
            Clean-Tsv $row.repository
        ) -join "`t"))
    }
    Write-Utf8File $NoticePath $notice.ToString()
    Write-Utf8File $InventoryPath $tsv.ToString()
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory)
[IO.Directory]::CreateDirectory($destination) | Out-Null

$pythonPackages = @(
    @{ Name = 'annotated-types'; Version = '0.7.0'; Artifact = 'annotated_types-0.7.0-py3-none-any.whl'; Licenses = @('annotated_types-0.7.0.dist-info/licenses/LICENSE') },
    @{ Name = 'binary2strings'; Version = '0.1.13'; Artifact = 'binary2strings-0.1.13.tar.gz'; Licenses = @('binary2strings-0.1.13/LICENSE') },
    @{ Name = 'colorama'; Version = '0.4.6'; Artifact = 'colorama-0.4.6-py2.py3-none-any.whl'; Licenses = @('colorama-0.4.6.dist-info/licenses/LICENSE.txt') },
    @{ Name = 'cxxfilt'; Version = '0.3.0'; Artifact = 'cxxfilt-0.3.0-py2.py3-none-any.whl'; Licenses = @('cxxfilt-0.3.0.dist-info/LICENSE') },
    @{ Name = 'funcy'; Version = '2.0'; Artifact = 'funcy-2.0-py2.py3-none-any.whl'; Licenses = @('funcy-2.0.dist-info/LICENSE') },
    @{ Name = 'halo'; Version = '0.0.31'; Artifact = 'halo-0.0.31.tar.gz'; Licenses = @('halo-0.0.31/LICENSE') },
    @{ Name = 'intervaltree'; Version = '3.1.0'; Artifact = 'intervaltree-3.1.0.tar.gz'; Licenses = @('intervaltree-3.1.0/LICENSE.txt') },
    @{ Name = 'log-symbols'; Version = '0.0.14'; Artifact = 'log_symbols-0.0.14-py3-none-any.whl'; Licenses = @('log_symbols-0.0.14.dist-info/LICENSE') },
    @{ Name = 'markdown-it-py'; Version = '3.0.0'; Artifact = 'markdown_it_py-3.0.0-py3-none-any.whl'; Licenses = @('markdown_it_py-3.0.0.dist-info/LICENSE', 'markdown_it_py-3.0.0.dist-info/LICENSE.markdown-it') },
    @{ Name = 'mdurl'; Version = '0.1.2'; Artifact = 'mdurl-0.1.2-py3-none-any.whl'; Licenses = @('mdurl-0.1.2.dist-info/LICENSE') },
    @{ Name = 'msgpack'; Version = '1.0.8'; Artifact = 'msgpack-1.0.8-cp38-cp38-win_amd64.whl'; Licenses = @('msgpack-1.0.8.dist-info/COPYING') },
    @{ Name = 'networkx'; Version = '3.1'; Artifact = 'networkx-3.1-py3-none-any.whl'; Licenses = @('networkx-3.1.dist-info/LICENSE.txt') },
    @{ Name = 'pefile'; Version = '2023.2.7'; Artifact = 'pefile-2023.2.7-py3-none-any.whl'; Licenses = @('pefile-2023.2.7.dist-info/LICENSE') },
    @{ Name = 'pyasn1-modules'; Version = '0.3.0'; Artifact = 'pyasn1_modules-0.3.0-py2.py3-none-any.whl'; Licenses = @('pyasn1_modules-0.3.0.dist-info/LICENSE.txt') },
    @{ Name = 'pycparser'; Version = '2.22'; Artifact = 'pycparser-2.22-py3-none-any.whl'; Licenses = @('pycparser-2.22.dist-info/LICENSE') },
    @{ Name = 'pydantic-core'; Version = '2.23.3'; Artifact = 'pydantic_core-2.23.3-cp38-none-win_amd64.whl'; Licenses = @('pydantic_core-2.23.3.dist-info/licenses/LICENSE') },
    @{ Name = 'pydantic'; Version = '2.9.1'; Artifact = 'pydantic-2.9.1-py3-none-any.whl'; Licenses = @('pydantic-2.9.1.dist-info/licenses/LICENSE') },
    @{ Name = 'pygments'; Version = '2.18.0'; Artifact = 'pygments-2.18.0-py3-none-any.whl'; Licenses = @('pygments-2.18.0.dist-info/licenses/LICENSE') },
    @{ Name = 'python-flirt'; Version = '0.8.10'; Artifact = 'python_flirt-0.8.10-cp38-none-win_amd64.whl'; Licenses = @('python_flirt-0.8.10.dist-info/license_files/LICENSE.txt') },
    @{ Name = 'rich'; Version = '13.7.1'; Artifact = 'rich-13.7.1-py3-none-any.whl'; Licenses = @('rich-13.7.1.dist-info/LICENSE') },
    @{ Name = 'six'; Version = '1.16.0'; Artifact = 'six-1.16.0-py2.py3-none-any.whl'; Licenses = @('six-1.16.0.dist-info/LICENSE') },
    @{ Name = 'sortedcontainers'; Version = '2.4.0'; Artifact = 'sortedcontainers-2.4.0-py2.py3-none-any.whl'; Licenses = @('sortedcontainers-2.4.0.dist-info/LICENSE') },
    @{ Name = 'spinners'; Version = '0.0.24'; Artifact = 'spinners-0.0.24-py3-none-any.whl'; Licenses = @('spinners-0.0.24.dist-info/LICENSE') },
    @{ Name = 'tabulate'; Version = '0.9.0'; Artifact = 'tabulate-0.9.0-py3-none-any.whl'; Licenses = @('tabulate-0.9.0.dist-info/LICENSE') },
    @{ Name = 'termcolor'; Version = '2.4.0'; Artifact = 'termcolor-2.4.0-py3-none-any.whl'; Licenses = @('termcolor-2.4.0.dist-info/licenses/COPYING.txt') },
    @{ Name = 'tqdm'; Version = '4.66.4'; Artifact = 'tqdm-4.66.4-py3-none-any.whl'; Licenses = @('tqdm-4.66.4.dist-info/LICENCE') },
    @{ Name = 'typing_extensions'; Version = '4.12.2'; Artifact = 'typing_extensions-4.12.2-py3-none-any.whl'; Licenses = @('typing_extensions-4.12.2.dist-info/LICENSE') },
    @{ Name = 'viv-utils'; Version = '0.7.11'; Artifact = 'viv_utils-0.7.11-py2.py3-none-any.whl'; Licenses = @('viv_utils-0.7.11.dist-info/LICENSE') },
    @{ Name = 'vivisect'; Version = '1.2.1'; Artifact = 'vivisect-1.2.1-py3-none-any.whl'; Licenses = @('vivisect-1.2.1.dist-info/LICENSE.txt') }
)

$pythonNotice = [Text.StringBuilder]::new()
$null = $pythonNotice.AppendLine('FLOSS v3.1.1 embedded Python package notices')
$null = $pythonNotice.AppendLine('Generated from the exact package artifacts identified below.')
$null = $pythonNotice.AppendLine()
foreach ($package in $pythonPackages) {
    $artifact = Get-ExactFile $PrimaryArtifactsDirectory $package.Artifact
    $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    $null = $pythonNotice.AppendLine(('=' * 80))
    $null = $pythonNotice.AppendLine("Package: $($package.Name) $($package.Version)")
    $null = $pythonNotice.AppendLine("Artifact: $($package.Artifact)")
    $null = $pythonNotice.AppendLine("Artifact SHA-256: $hash")
    foreach ($licenseEntry in $package.Licenses) {
        $null = $pythonNotice.AppendLine("--- $licenseEntry ---")
        $null = $pythonNotice.AppendLine((Read-ArchiveEntry $artifact $licenseEntry))
    }
    $null = $pythonNotice.AppendLine()
}
Write-Utf8File (Join-Path $destination 'FLOSS-Python-Third-Party-Notices.txt') $pythonNotice.ToString()

New-CargoNotice `
    (Join-Path $PydanticCoreSourceDirectory 'Cargo.toml') `
    $PydanticCoreCargoHome `
    'exact locked x86_64-pc-windows-msvc normal dependency closure' `
    (Join-Path $destination 'Pydantic-Core-2.23.3-Rust-Third-Party-Notices.txt') `
    (Join-Path $destination 'Pydantic-Core-2.23.3-Rust-Inventory.tsv') `
    @(
        'pydantic-core 2.23.3 Rust dependency notices',
        'The exact source sdist contains Cargo.lock; this is its locked Windows x64 normal-edge closure.'
    )

New-CargoNotice `
    (Join-Path $PythonFlirtSourceDirectory 'pyflirt\Cargo.toml') `
    $PythonFlirtCargoHome `
    'conservative current manifest resolution; not the historical binary-exact closure' `
    (Join-Path $destination 'Python-Flirt-0.8.10-Rust-Third-Party-Notices.txt') `
    (Join-Path $destination 'Python-Flirt-0.8.10-Rust-Inventory.tsv') `
    @(
        'python-flirt 0.8.10 conservative Rust dependency notices',
        'Upstream tag v0.8.10 did not commit Cargo.lock. Do not interpret current resolver versions as binary-exact.',
        'Exact binary markers recovered: pyo3 0.17.3; nom 7.1.3; inflate 0.4.5; adler32 1.2.0;',
        'anyhow 1.0.80; parking_lot_core 0.9.9; smallvec 1.13.1; rustc commit',
        '2d24fe591f30386d6d5fc2bb941c78d7266bf10f.'
    )

$nativeRows = [Collections.Generic.List[string]]::new()
$nativeRows.Add("component`tsource_inside_executable`tbytes`tsha256`tfile_version`tauthenticode_status`tsigner_subject`tsigner_thumbprint")
foreach ($file in Get-ChildItem -LiteralPath $NativeExtractionDirectory -File | Sort-Object Name -CaseSensitive) {
    $name = $file.Name -replace '__', '\'
    $component = switch -Regex ($file.Name) {
        '^api-ms-win-' { 'Microsoft Universal CRT 10.0.17134.12'; break }
        '^ucrtbase\.dll$' { 'Microsoft Universal CRT 10.0.17134.12'; break }
        '^(MSVCP140|VCRUNTIME140)' { 'Microsoft Visual C++ runtime'; break }
        '^libcrypto-1_1\.dll$' { 'OpenSSL 1.1.1k'; break }
        '^libssl-1_1\.dll$' { 'OpenSSL 1.1.1k'; break }
        '^libffi-7\.dll$' { 'libffi 3.3 / CPython 3.8.10 embeddable distribution'; break }
        '^binary2strings' { 'binary2strings 0.1.13'; break }
        '^flirt__' { 'python-flirt 0.8.10'; break }
        '^msgpack__' { 'msgpack 1.0.8'; break }
        '^pydantic_core__' { 'pydantic-core 2.23.3'; break }
        default { 'CPython 3.8.10 embeddable distribution' }
    }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $isPortableExecutable = $file.Extension -in @('.dll', '.pyd')
    $version = if ($isPortableExecutable) { Clean-Tsv $file.VersionInfo.FileVersion } else { '' }
    $signature = if ($isPortableExecutable) { Get-AuthenticodeSignature -LiteralPath $file.FullName } else { $null }
    $signer = if ($null -eq $signature) { $null } else { $signature.SignerCertificate }
    $nativeRows.Add((@(
        $component,
        $name,
        $file.Length,
        $hash,
        $version,
        $(if ($null -eq $signature) { '' } else { Clean-Tsv $signature.Status }),
        $(if ($null -eq $signer) { '' } else { Clean-Tsv $signer.Subject }),
        $(if ($null -eq $signer) { '' } else { Clean-Tsv $signer.Thumbprint })
    ) -join "`t"))
}
Write-Utf8File (Join-Path $destination 'FLOSS-Embedded-Native-Runtime.tsv') (($nativeRows -join "`n") + "`n")

Write-Host "Generated FLOSS notice assets in $destination"
