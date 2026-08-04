[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$Target = 'x86_64-pc-windows-msvc'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$cargoManifest = Join-Path $repoRoot 'native\bstrings_core\Cargo.toml'
$inventoryPath = Join-Path $repoRoot 'licenses\bstrings-core-win-x64.tsv'
$noticePath = Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md'
$projectPath = Join-Path $repoRoot 'bstrings\bstrings.csproj'

$licenseHashes = [ordered]@{
    'Apache-2.0.txt' = 'c95bae1d1ce0235ecccd3560b772ec1efb97f348a79f0fbe0a634f0c2ccefe2c'
    'MIT.txt' = '2cb8004be6235d503daf41a32acdce780299e8879085fc9fc164f9d0fed7287a'
    'Unicode-3.0.txt' = 'f7db81051789b729fea528a63ec4c938fdcb93d9d61d97dc8cc2e9df6d47f2a1'
}

function Invoke-Cargo([string[]]$Arguments) {
    $output = @(& cargo @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "cargo $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return $output
}

function Select-DistributionLicense([string]$Expression) {
    if ($Expression -match 'Unicode-3\.0') {
        return 'Apache-2.0 AND Unicode-3.0'
    }
    if ($Expression -eq 'Apache-2.0') {
        return 'Apache-2.0'
    }
    if ($Expression -eq 'MIT') {
        return 'MIT'
    }
    if ($Expression -match 'Apache-2\.0') {
        return 'Apache-2.0'
    }
    if ($Expression -match 'MIT') {
        return 'MIT'
    }
    throw "No reviewed distribution-license choice exists for '$Expression'."
}

function Format-InventoryRow($Row) {
    return @(
        $Row.package,
        $Row.version,
        $Row.declared_license,
        $Row.selected_license,
        $Row.authors,
        $Row.repository
    ) -join "`t"
}

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'cargo is required to verify the Rust third-party inventory.'
}
foreach ($path in @($cargoManifest, $inventoryPath, $noticePath, $projectPath)) {
    if (-not [IO.File]::Exists($path)) {
        throw "Required license-verification input is missing: $path"
    }
}

foreach ($entry in $licenseHashes.GetEnumerator()) {
    $path = Join-Path (Join-Path $repoRoot 'licenses') $entry.Key
    if (-not [IO.File]::Exists($path)) {
        throw "Required selected license text is missing: $path"
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $entry.Value) {
        throw "Selected license text changed without review: $($entry.Key)"
    }
}

$metadataJson = Invoke-Cargo @(
    'metadata',
    '--manifest-path', $cargoManifest,
    '--locked',
    '--offline',
    '--format-version', '1'
) | Out-String
$metadata = $metadataJson | ConvertFrom-Json
$packagesByKey = @{}
foreach ($package in $metadata.packages) {
    $key = "$($package.name)|$($package.version)"
    $packagesByKey[$key] = $package
}

$treeLines = Invoke-Cargo @(
    'tree',
    '--manifest-path', $cargoManifest,
    '--locked',
    '--offline',
    '--target', $Target,
    '--edges', 'normal',
    '--prefix', 'none',
    '--format', '{p}'
)
$treeKeys = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal
)
foreach ($line in $treeLines) {
    if ($line -match '^(?<name>[A-Za-z0-9_-]+) v(?<version>[^\s]+)') {
        if ($Matches.name -ne 'bstrings_core') {
            $null = $treeKeys.Add("$($Matches.name)|$($Matches.version)")
        }
    }
}
if ($treeKeys.Count -eq 0) {
    throw "cargo tree returned no normal dependencies for target '$Target'."
}

$actualRows = foreach ($key in $treeKeys) {
    if (-not $packagesByKey.ContainsKey($key)) {
        throw "cargo metadata did not contain the cargo-tree package '$key'."
    }
    $package = $packagesByKey[$key]
    $authors = @($package.authors | ForEach-Object {
        $_ -replace '\s*<[^>]+>\s*$', ''
    }) -join '; '
    [pscustomobject]@{
        package = [string]$package.name
        version = [string]$package.version
        declared_license = [string]$package.license
        selected_license = Select-DistributionLicense ([string]$package.license)
        authors = $authors
        repository = [string]$package.repository
    }
}

$expectedRows = @(Import-Csv -LiteralPath $inventoryPath -Delimiter "`t")
if ($expectedRows.Count -eq 0) {
    throw 'The reviewed Rust third-party inventory is empty.'
}
$requiredColumns = @(
    'package',
    'version',
    'declared_license',
    'selected_license',
    'authors',
    'repository'
)
$actualColumns = @($expectedRows[0].PSObject.Properties.Name)
if (Compare-Object $requiredColumns $actualColumns -CaseSensitive) {
    throw 'The Rust third-party inventory columns do not match the reviewed schema.'
}

$expectedLines = @($expectedRows | ForEach-Object {
    Format-InventoryRow $_
} | Sort-Object -CaseSensitive)
$actualLines = @($actualRows | ForEach-Object {
    Format-InventoryRow $_
} | Sort-Object -CaseSensitive)
$differences = @(Compare-Object $expectedLines $actualLines -CaseSensitive)
if ($differences.Count -ne 0) {
    $details = $differences | ForEach-Object {
        $meaning = if ($_.SideIndicator -eq '<=') {
            'inventory only'
        }
        else {
            'cargo tree only'
        }
        "[$meaning] $($_.InputObject)"
    }
    throw "The locked $Target dependency/license inventory drifted:`n$($details -join "`n")"
}

$notice = Get-Content -LiteralPath $noticePath -Raw
$project = Get-Content -LiteralPath $projectPath -Raw
$packagedFiles = @(
    'bstrings-core-win-x64.tsv',
    'Apache-2.0.txt',
    'MIT.txt',
    'Unicode-3.0.txt'
)
foreach ($name in $packagedFiles) {
    if (-not $notice.Contains($name, [StringComparison]::Ordinal)) {
        throw "THIRD_PARTY_NOTICES.md does not reference '$name'."
    }
    if (-not $project.Contains("licenses\$name", [StringComparison]::Ordinal)) {
        throw "bstrings.csproj does not package 'licenses\$name'."
    }
}

$successMessage = (
    "Rust third-party notices verified: {0} locked {1} dependencies; " +
    "selected license texts are packaged and unchanged."
) -f $actualRows.Count, $Target
Write-Host $successMessage
