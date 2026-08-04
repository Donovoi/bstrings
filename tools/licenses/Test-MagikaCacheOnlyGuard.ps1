[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$stageScript = Join-Path $PSScriptRoot 'Stage-MagikaRedistribution.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
$testRoot = Join-Path $tempRoot ('bstrings-magika-cache-only-' + [Guid]::NewGuid().ToString('N'))
$cache = Join-Path $testRoot 'empty-cache'
$stage = Join-Path $testRoot 'stage'
$expectedPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
if (-not $testRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unsafe cache-only test path: $testRoot"
}

[IO.Directory]::CreateDirectory($cache) | Out-Null
$failedClosed = $false
try {
    try {
        & $stageScript `
            -DestinationDirectory $stage `
            -DownloadCacheDirectory $cache `
            -CacheOnly
    }
    catch {
        if ($_.Exception.Message -notlike 'Cache-only validation is missing locked artifact*') {
            throw
        }
        $failedClosed = $true
    }
    if (-not $failedClosed) {
        throw 'Magika cache-only staging unexpectedly succeeded with an empty cache.'
    }
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTestRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing unsafe cache-only test cleanup: $resolvedTestRoot"
        }
        [IO.Directory]::Delete($resolvedTestRoot, $true)
    }
}

Write-Host 'Magika cache-only guard rejected a missing artifact without network fallback.'
