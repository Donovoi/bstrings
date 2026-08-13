[CmdletBinding()]
param(
    [string[]]$DestinationDirectory,
    [string]$VisualCppRuntimeDirectory,
    [string]$ComponentLockPath,
    [switch]$InspectOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The Visual C++ app-local runtime staging profile is Windows-only.'
}
if ([string]::IsNullOrWhiteSpace($ComponentLockPath)) {
    $ComponentLockPath = Join-Path $PSScriptRoot 'offline-components.lock.json'
}
if (-not $InspectOnly -and @($DestinationDirectory).Count -eq 0) {
    throw 'At least one -DestinationDirectory is required unless -InspectOnly is used.'
}

function Resolve-ExistingFile([string]$Path, [string]$Name) {
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

function Resolve-ExistingDirectory([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Name was not found: $Path"
    }
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $item = Get-Item -LiteralPath $resolved -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $resolved"
    }
    return $resolved
}

function Assert-NoReparsePoints([string]$Root, [string]$Name) {
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $Root"
    }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Name contains a link or reparse point: $($item.FullName)"
        }
    }
}

function Assert-ExactRuntimeFile(
    [string]$Path,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$ExpectedVersion,
    [string]$Name
) {
    $resolved = Resolve-ExistingFile $Path $Name
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Length -ne $ExpectedBytes) {
        throw "$Name byte length mismatch: expected $ExpectedBytes, found $($item.Length): $resolved"
    }
    $actualHash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw "$Name SHA-256 mismatch: expected $ExpectedSha256, found ${actualHash}: $resolved"
    }
    $actualVersion = [string]$item.VersionInfo.FileVersion
    if ($actualVersion -ne $ExpectedVersion) {
        throw "$Name version mismatch: expected $ExpectedVersion, found ${actualVersion}: $resolved"
    }
}

function Resolve-VisualCppRuntimeDirectory(
    [string]$Path,
    [string[]]$RuntimeDlls,
    [string]$Name
) {
    $resolved = Resolve-ExistingDirectory $Path $Name
    $system32 = [IO.Path]::GetFullPath(
        (Join-Path ([Environment]::GetFolderPath('Windows')) 'System32')
    ).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    if (
        $resolved.Equals($system32, [StringComparison]::OrdinalIgnoreCase) -or
        $resolved.StartsWith(
            $system32 + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        throw "$Name must be a licensed Microsoft Visual C++ redistributable directory, not System32: $resolved"
    }

    foreach ($runtimeDll in $RuntimeDlls) {
        $runtimePath = Resolve-ExistingFile `
            (Join-Path $resolved $runtimeDll) `
            "$Name file $runtimeDll"
        $runtimeVersion = [string](Get-Item -LiteralPath $runtimePath -Force).VersionInfo.FileVersion
        if ([string]::IsNullOrWhiteSpace($runtimeVersion)) {
            throw "$Name file does not expose a file version: $runtimePath"
        }
    }
    return $resolved
}

function Find-VisualCppRuntimeDirectory(
    [string]$ExplicitPath,
    [string[]]$RuntimeDlls
) {
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return Resolve-VisualCppRuntimeDirectory `
            $ExplicitPath `
            $RuntimeDlls `
            'Visual C++ x64 app-local runtime directory'
    }

    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        throw 'Visual C++ runtime auto-discovery requires Program Files (x86); pass -VisualCppRuntimeDirectory explicitly.'
    }
    $vswhere = Resolve-ExistingFile `
        (Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe') `
        'Visual Studio locator (vswhere.exe)'
    $installationOutput = @(
        & $vswhere `
            -latest `
            -products '*' `
            -requires 'Microsoft.VisualStudio.Component.VC.Redist.14.Latest' `
            -property installationPath 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Visual C++ runtime auto-discovery failed through vswhere.exe: $($installationOutput -join [Environment]::NewLine)"
    }
    $installationPaths = @(
        $installationOutput |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($installationPaths.Count -ne 1) {
        throw 'Visual C++ runtime auto-discovery found no unique Visual Studio installation; pass -VisualCppRuntimeDirectory explicitly.'
    }
    $installation = Resolve-ExistingDirectory `
        $installationPaths[0] `
        'Visual Studio installation containing the x64 redistributable runtime'
    $redistRoot = Join-Path $installation 'VC\Redist\MSVC'
    if (-not (Test-Path -LiteralPath $redistRoot -PathType Container)) {
        throw "Visual Studio does not contain a VC redistributable directory; pass -VisualCppRuntimeDirectory explicitly: $redistRoot"
    }

    $versionDirectories = @(
        Get-ChildItem -LiteralPath $redistRoot -Directory -Force |
            Sort-Object -Property @{
                Expression = {
                    $parsedVersion = $null
                    if ([Version]::TryParse($_.Name, [ref]$parsedVersion)) {
                        return $parsedVersion
                    }
                    return [Version]'0.0'
                }
                Descending = $true
            }
    )
    foreach ($versionDirectory in $versionDirectories) {
        $x64Directory = Join-Path $versionDirectory.FullName 'x64'
        if (-not (Test-Path -LiteralPath $x64Directory -PathType Container)) {
            continue
        }
        $crtDirectories = @(
            Get-ChildItem `
                -LiteralPath $x64Directory `
                -Directory `
                -Filter 'Microsoft.VC*.CRT' `
                -Force |
                Sort-Object -Property Name -Descending
        )
        foreach ($crtDirectory in $crtDirectories) {
            try {
                return Resolve-VisualCppRuntimeDirectory `
                    $crtDirectory.FullName `
                    $RuntimeDlls `
                    'Auto-discovered Visual C++ x64 app-local runtime directory'
            }
            catch {
                continue
            }
        }
    }
    throw 'No installed licensed x64 Visual C++ app-local runtime contains every locked DLL; install the Visual C++ redistributable build component or pass -VisualCppRuntimeDirectory explicitly.'
}

$lockPath = Resolve-ExistingFile $ComponentLockPath 'Offline component lock'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 2 -or $lock.profile -ne 'windows-x64-offline-v3') {
    throw "Unsupported offline component lock schema or profile: $lockPath"
}
$runtimeDlls = @($lock.runtimeDlls | ForEach-Object { [string]$_ })
$expectedRuntimeDlls = @(
    'vcruntime140.dll',
    'vcruntime140_1.dll',
    'msvcp140.dll',
    'msvcp140_1.dll'
)
if (($runtimeDlls -join '|') -cne ($expectedRuntimeDlls -join '|')) {
    throw "Offline component lock must name the four required Visual C++ runtime DLLs in the expected order: $($expectedRuntimeDlls -join ', ')"
}
$sourceDirectory = Find-VisualCppRuntimeDirectory `
    $VisualCppRuntimeDirectory `
    $runtimeDlls
$inventory = @()
foreach ($runtimeDll in $runtimeDlls) {
    $sourcePath = Resolve-ExistingFile `
        (Join-Path $sourceDirectory $runtimeDll) `
        "Visual C++ runtime $runtimeDll"
    $sourceItem = Get-Item -LiteralPath $sourcePath -Force
    $sourceVersion = [string]$sourceItem.VersionInfo.FileVersion
    $inventory += [pscustomobject]@{
        name = $runtimeDll
        bytes = $sourceItem.Length
        sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        fileVersion = $sourceVersion
    }
}

$resolvedDestinations = @()
if (-not $InspectOnly) {
    foreach ($destinationValue in @($DestinationDirectory)) {
        $destination = Resolve-ExistingDirectory `
            $destinationValue `
            'Visual C++ runtime staging destination'
        Assert-NoReparsePoints $destination 'Visual C++ runtime staging destination'
        foreach ($runtimeFile in $inventory) {
            $sourcePath = Join-Path $sourceDirectory ([string]$runtimeFile.name)
            $targetPath = Join-Path $destination ([string]$runtimeFile.name)
            $targetIsExact = $false
            if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                $targetItem = Get-Item -LiteralPath $targetPath -Force
                if (($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Refusing to replace a linked Visual C++ runtime destination: $targetPath"
                }
                try {
                    Assert-ExactRuntimeFile `
                        $targetPath `
                        ([long]$runtimeFile.bytes) `
                        ([string]$runtimeFile.sha256) `
                        ([string]$runtimeFile.fileVersion) `
                        "Staged Visual C++ runtime $($runtimeFile.name)"
                    $targetIsExact = $true
                }
                catch {
                    $targetIsExact = $false
                }
            }
            elseif (Test-Path -LiteralPath $targetPath) {
                throw "Visual C++ runtime destination is not a regular file: $targetPath"
            }
            if (-not $targetIsExact) {
                $temporaryPath = Join-Path `
                    $destination `
                    ('.bstrings-vc-runtime-' + [Guid]::NewGuid().ToString('N') + '.tmp')
                $backupPath = $null
                try {
                    [IO.File]::Copy($sourcePath, $temporaryPath, $false)
                    Assert-ExactRuntimeFile `
                        $temporaryPath `
                        ([long]$runtimeFile.bytes) `
                        ([string]$runtimeFile.sha256) `
                        ([string]$runtimeFile.fileVersion) `
                        "Temporary Visual C++ runtime $($runtimeFile.name)"
                    if ([IO.File]::Exists($targetPath)) {
                        $backupPath = Join-Path `
                            $destination `
                            ('.bstrings-vc-runtime-' + [Guid]::NewGuid().ToString('N') + '.backup')
                        [IO.File]::Replace($temporaryPath, $targetPath, $backupPath, $true)
                        [IO.File]::Delete($backupPath)
                        $backupPath = $null
                    }
                    else {
                        [IO.File]::Move($temporaryPath, $targetPath)
                    }
                }
                finally {
                    if ([IO.File]::Exists($temporaryPath)) {
                        [IO.File]::Delete($temporaryPath)
                    }
                    if ($null -ne $backupPath -and [IO.File]::Exists($backupPath)) {
                        [IO.File]::Delete($backupPath)
                    }
                }
            }
            Assert-ExactRuntimeFile `
                $targetPath `
                ([long]$runtimeFile.bytes) `
                ([string]$runtimeFile.sha256) `
                ([string]$runtimeFile.fileVersion) `
                "Staged Visual C++ runtime $($runtimeFile.name)"
        }
        Assert-NoReparsePoints $destination 'Visual C++ runtime staging destination'
        $resolvedDestinations += $destination
        Write-Host "Staged and verified the licensed x64 Visual C++ app-local runtime: $destination"
    }
}

[pscustomobject]@{
    sourceDirectory = $sourceDirectory
    destinations = @($resolvedDestinations)
    files = @($inventory)
}
