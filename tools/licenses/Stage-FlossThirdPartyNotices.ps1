[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FlossExecutable,
    [Parameter(Mandatory)]
    [string]$DestinationDirectory,
    [string]$InventoryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$licenseRoot = Join-Path $repoRoot 'licenses'
$verifier = Join-Path $PSScriptRoot 'Verify-FlossThirdPartyNotices.ps1'
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath = Join-Path $licenseRoot 'floss-v3.1.1-win-x64.json'
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

$flossFile = Resolve-ExistingFile $FlossExecutable 'FLOSS executable'
$inventoryFile = Resolve-ExistingFile $InventoryPath 'FLOSS inventory'
$sourceAssetDirectory = Join-Path $licenseRoot 'floss-v3.1.1'
if (-not (Test-Path -LiteralPath $sourceAssetDirectory -PathType Container)) {
    throw "FLOSS asset directory was not found: $sourceAssetDirectory"
}
Assert-NoReparseTraversal $sourceAssetDirectory 'FLOSS asset directory'

# Refuse to overlay an existing tree. The caller can safely merge this verified
# staging directory into its bundle after this script succeeds.
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$destinationRoot = [IO.Path]::GetPathRoot($destination)
if ($destination.TrimEnd([IO.Path]::DirectorySeparatorChar) -ceq $destinationRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)) {
    throw "DestinationDirectory cannot be a filesystem root: $destination"
}
Assert-NoReparseTraversal $destination 'Destination directory'
if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw "DestinationDirectory is not a directory: $destination"
    }
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
        throw "DestinationDirectory must be empty: $destination"
    }
}
else {
    $null = New-Item -ItemType Directory -Path $destination
}

# Validate the exact executable, inventory, notice set, package closure, native
# inventory, and source obligation before copying any byte into the stage.
& $verifier -FlossExecutable $flossFile -InventoryPath $inventoryFile

$destinationLicenseRoot = Join-Path $destination 'licenses'
$destinationAssetRoot = Join-Path $destinationLicenseRoot 'floss-v3.1.1'
$null = New-Item -ItemType Directory -Path $destinationAssetRoot
Copy-Item -LiteralPath $inventoryFile -Destination (Join-Path $destinationLicenseRoot 'floss-v3.1.1-win-x64.json')

$sourceFiles = @(Get-ChildItem -LiteralPath $sourceAssetDirectory -Recurse -File | Sort-Object FullName)
foreach ($source in $sourceFiles) {
    $relative = [IO.Path]::GetRelativePath($sourceAssetDirectory, $source.FullName)
    $destinationFile = Join-Path $destinationAssetRoot $relative
    $destinationParent = [IO.Path]::GetDirectoryName($destinationFile)
    if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $destinationParent
    }
    Copy-Item -LiteralPath $source.FullName -Destination $destinationFile
}

# Verify the staged bytes independently and reject omissions, substitutions,
# or unexpected files under the FLOSS notice asset directory.
& $verifier `
    -FlossExecutable $flossFile `
    -InventoryPath $inventoryFile `
    -StagedDirectory $destination `
    -SkipVersionProbe

$stagedFiles = @(Get-ChildItem -LiteralPath $destination -Recurse -File)
$totalBytes = [long](($stagedFiles | Measure-Object -Property Length -Sum).Sum)
[pscustomobject]@{
    DestinationDirectory = $destination
    FileCount = $stagedFiles.Count
    TotalBytes = $totalBytes
    InventorySha256 = (Get-FileHash -LiteralPath (Join-Path $destinationLicenseRoot 'floss-v3.1.1-win-x64.json') -Algorithm SHA256).Hash.ToLowerInvariant()
}
