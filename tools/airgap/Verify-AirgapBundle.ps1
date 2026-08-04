[CmdletBinding()]
param(
    [switch]$TranslationSmoke,
    [switch]$SkipExecutableProbes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Set-AirgapEnvironment.ps1')
Enable-BstringsAirgapEnvironment -BundleRoot $PSScriptRoot

$configPath = Join-Path $PSScriptRoot 'airgap-config.json'
$manifestPath = Join-Path $PSScriptRoot 'airgap-manifest.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.schemaVersion -ne 1) {
    throw "Unsupported air-gap configuration schema: $($config.schemaVersion)"
}
function Resolve-BundlePath([string]$RelativePath) {
    $candidate = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot ($RelativePath -replace '/', '\'))
    )
    $root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $candidate.StartsWith(
        $root + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Bundle configuration escapes its root: $RelativePath"
    }
    return $candidate
}

$python = Resolve-BundlePath $config.pythonExecutable
$manifestTool = Resolve-BundlePath $config.manifestTool
& $python $manifestTool verify --root $PSScriptRoot --manifest $manifestPath
if ($LASTEXITCODE -ne 0) {
    throw 'Air-gap bundle manifest verification failed.'
}

$guardProbe = Resolve-BundlePath $config.networkGuardProbe
& $python $guardProbe
if ($LASTEXITCODE -ne 0) {
    throw 'Python air-gap network guard verification failed.'
}

if (-not $SkipExecutableProbes) {
    $probes = @(
        @((Resolve-BundlePath $config.bstringsExecutable), '--version'),
        @((Resolve-BundlePath $config.magikaExecutable), '--version'),
        @((Resolve-BundlePath $config.flossExecutable), '--version'),
        @((Resolve-BundlePath $config.llamaServer), '--version')
    )
    foreach ($probe in $probes) {
        & $probe[0] $probe[1] | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Bundled executable probe failed: $($probe[0])"
        }
    }
}

if ($TranslationSmoke) {
    $smokeRoot = Join-Path (
        [IO.Path]::GetTempPath()
    ) ("bstrings-airgap-smoke-" + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($smokeRoot) | Out-Null
    try {
        $inputPath = Resolve-BundlePath $config.smokeInput
        $outputPath = Join-Path $smokeRoot 'translated.jsonl'
        & (Join-Path $PSScriptRoot 'Invoke-EnrichmentAirgap.ps1') `
            --input-jsonl $inputPath `
            --translate `
            --translation-device cpu `
            --translation-strict-determinism `
            -o $outputPath
        if ($LASTEXITCODE -ne 0) {
            throw 'Bundled offline translation smoke failed.'
        }
        $records = @(Get-Content -LiteralPath $outputPath | ForEach-Object {
            $_ | ConvertFrom-Json
        })
        $translation = @($records | Where-Object {
            $_.PSObject.Properties.Name -contains 'transform' -and
                $_.transform.kind -eq 'translation'
        })
        if ($translation.Count -ne 1) {
            throw "Expected one translated child, found $($translation.Count)."
        }
        if ($translation[0].text -notmatch [regex]::Escape('analyst@example.com')) {
            throw 'The offline translation smoke did not retain its evidence identifier.'
        }
        if ($translation[0].transform.execution.airgap -ne $true) {
            throw 'The translated child does not record enforced air-gap execution.'
        }
    }
    finally {
        $resolvedSmoke = [IO.Path]::GetFullPath($smokeRoot)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (
            $resolvedSmoke.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedSmoke).StartsWith(
                'bstrings-airgap-smoke-',
                [StringComparison]::Ordinal
            ) -and
            [IO.Directory]::Exists($resolvedSmoke)
        ) {
            [IO.Directory]::Delete($resolvedSmoke, $true)
        }
    }
}

Write-Host 'Air-gap bundle verification passed.'
