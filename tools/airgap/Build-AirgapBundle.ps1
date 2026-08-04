[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [string]$PublishedBstringsDirectory,
    [Parameter(Mandatory = $true)]
    [string]$PythonDirectory,
    [Parameter(Mandatory = $true)]
    [string]$MagikaDirectory,
    [Parameter(Mandatory = $true)]
    [string]$FlossDirectory,
    [Parameter(Mandatory = $true)]
    [string]$LlamaDirectory,
    [Parameter(Mandatory = $true)]
    [string]$TranslationModelDirectory,
    [Parameter(Mandatory = $true)]
    [string]$TranslationModelRevision,
    [string]$TranslationModelId = 'tencent/Hy-MT2-1.8B-GGUF',
    [string]$BstringsExecutable = 'bstrings.exe',
    [string]$PythonExecutable = 'python.exe',
    [string]$MagikaExecutable = 'magika.exe',
    [string]$FlossExecutable = 'floss.exe',
    [string]$LlamaServerExecutable = 'llama-server.exe',
    [string]$TranslationModel = 'Hy-MT2-1.8B-Q8_0.gguf',
    [string]$RapidsPythonDirectory,
    [string]$RapidsPythonExecutable = 'python.exe',
    [string]$MadladModelDirectory,
    [string]$MadladModelId = 'google/madlad400-3b-mt',
    [string]$MadladModelRevision = 'fa184c675da0b5c9e1c8694fccd4e12e2d422094',
    [string]$MadladWeights = 'model.safetensors'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Resolve-RequiredDirectory([string]$Value, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Value -PathType Container)) {
        throw "$Name was not found or is not a directory: $Value"
    }
    return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Value).Path)
}

function Resolve-ChildFile([string]$Directory, [string]$RelativePath, [string]$Name) {
    if ([IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "$Name must be a safe path relative to its supplied directory."
    }
    $candidate = [IO.Path]::GetFullPath((Join-Path $Directory $RelativePath))
    $prefix = $Directory.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (
        -not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $candidate -PathType Leaf)
    ) {
        throw "$Name was not found beneath its supplied directory: $candidate"
    }
    return $candidate
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

$sources = [ordered]@{
    bstrings = Resolve-RequiredDirectory $PublishedBstringsDirectory 'Published bstrings directory'
    python = Resolve-RequiredDirectory $PythonDirectory 'Portable Python directory'
    magika = Resolve-RequiredDirectory $MagikaDirectory 'Magika directory'
    floss = Resolve-RequiredDirectory $FlossDirectory 'FLOSS directory'
    llama = Resolve-RequiredDirectory $LlamaDirectory 'llama.cpp directory'
    model = Resolve-RequiredDirectory $TranslationModelDirectory 'Translation model directory'
}
$null = Resolve-ChildFile $sources.bstrings $BstringsExecutable 'bstrings executable'
$sourcePython = Resolve-ChildFile $sources.python $PythonExecutable 'Python executable'
$null = Resolve-ChildFile $sources.magika $MagikaExecutable 'Magika executable'
$null = Resolve-ChildFile $sources.floss $FlossExecutable 'FLOSS executable'
$null = Resolve-ChildFile $sources.llama $LlamaServerExecutable 'llama.cpp server'
$sourceModel = Resolve-ChildFile $sources.model $TranslationModel 'Translation model'

$pythonPathFiles = @(Get-ChildItem -LiteralPath $sources.python -Filter 'python*._pth' -File)
if ($pythonPathFiles.Count -ne 1) {
    throw 'Portable Python must be an official-style isolated embeddable distribution with one python*._pth file.'
}
$activeSiteImport = @(Get-Content -LiteralPath $pythonPathFiles[0].FullName | Where-Object {
    $_.Trim() -match '^import\s+site\s*$'
})
if ($activeSiteImport.Count -ne 0) {
    throw 'Portable Python must keep import site disabled in its python*._pth file.'
}
& $sourcePython -I -c 'import hashlib, ipaddress, json, pathlib, socket, urllib.request; print("PYTHON_EMBED_OK")'
if ($LASTEXITCODE -ne 0) {
    throw 'Portable Python could not import the standard-library modules required by bstrings.'
}

if ($RapidsPythonDirectory) {
    $sources.rapids = Resolve-RequiredDirectory $RapidsPythonDirectory 'RAPIDS Python directory'
    $null = Resolve-ChildFile $sources.rapids $RapidsPythonExecutable 'RAPIDS Python executable'
}
if ($MadladModelDirectory) {
    $sources.madlad = Resolve-RequiredDirectory $MadladModelDirectory 'MADLAD model directory'
    $null = Resolve-ChildFile $sources.madlad $MadladWeights 'MADLAD weights'
}

$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar
)
if (Test-Path -LiteralPath $output) {
    throw "OutputDirectory must not already exist: $output"
}
foreach ($source in $sources.Values) {
    $prefix = $source.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if ($output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputDirectory cannot be placed inside an input directory: $source"
    }
}

[IO.Directory]::CreateDirectory($output) | Out-Null
$incomplete = Join-Path $output '.incomplete'
[IO.File]::WriteAllText($incomplete, "Air-gap bundle creation did not complete.`n")
try {
    Copy-DirectoryContents $sources.bstrings (Join-Path $output 'app')
    Copy-DirectoryContents $sources.python (Join-Path $output 'runtime\python')
    Copy-DirectoryContents $sources.magika (Join-Path $output 'tools\magika')
    Copy-DirectoryContents $sources.floss (Join-Path $output 'tools\floss')
    Copy-DirectoryContents $sources.llama (Join-Path $output 'runtime\llama')
    Copy-DirectoryContents $sources.model (Join-Path $output 'models\hy-mt2')
    if ($sources.Contains('rapids')) {
        Copy-DirectoryContents $sources.rapids (Join-Path $output 'runtime\rapids-python')
    }
    if ($sources.Contains('madlad')) {
        Copy-DirectoryContents $sources.madlad (Join-Path $output 'models\madlad')
    }

    [IO.Directory]::CreateDirectory((Join-Path $output 'tools\enrichment')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tools\enrichment\bstrings_enrich.py') `
        -Destination (Join-Path $output 'tools\enrichment\bstrings_enrich.py')
    [IO.Directory]::CreateDirectory((Join-Path $output 'tools\airgap')) | Out-Null
    foreach ($name in @('airgap_manifest.py', 'verify_network_guard.py', 'smoke-input.jsonl')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) `
            -Destination (Join-Path $output "tools\airgap\$name")
    }
    foreach ($name in @(
        'Invoke-BstringsAirgap.ps1',
        'Invoke-EnrichmentAirgap.ps1',
        'Set-AirgapEnvironment.ps1',
        'Verify-AirgapBundle.ps1'
    )) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) `
            -Destination (Join-Path $output $name)
    }
    [IO.Directory]::CreateDirectory((Join-Path $output 'docs')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\air-gapped-deployment.md') `
        -Destination (Join-Path $output 'docs\air-gapped-deployment.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.md') `
        -Destination (Join-Path $output 'LICENSE-bstrings.md')

    $modelPath = Join-Path (Join-Path $output 'models\hy-mt2') $TranslationModel
    $modelHash = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $madladConfig = $null
    if ($sources.Contains('madlad')) {
        $madladWeightsPath = Join-Path (Join-Path $output 'models\madlad') $MadladWeights
        $madladHash = (Get-FileHash -LiteralPath $madladWeightsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $madladConfig = [ordered]@{
            path = 'models/madlad'
            id = $MadladModelId
            revision = $MadladModelRevision
            sha256 = $madladHash
        }
    }
    $config = [ordered]@{
        schemaVersion = 1
        bstringsExecutable = "app/$($BstringsExecutable -replace '\\', '/')"
        pythonExecutable = "runtime/python/$($PythonExecutable -replace '\\', '/')"
        enrichmentAdapter = 'tools/enrichment/bstrings_enrich.py'
        magikaExecutable = "tools/magika/$($MagikaExecutable -replace '\\', '/')"
        flossExecutable = "tools/floss/$($FlossExecutable -replace '\\', '/')"
        llamaServer = "runtime/llama/$($LlamaServerExecutable -replace '\\', '/')"
        manifestTool = 'tools/airgap/airgap_manifest.py'
        networkGuardProbe = 'tools/airgap/verify_network_guard.py'
        smokeInput = 'tools/airgap/smoke-input.jsonl'
        translationModel = [ordered]@{
            path = "models/hy-mt2/$($TranslationModel -replace '\\', '/')"
            id = $TranslationModelId
            revision = $TranslationModelRevision
            sha256 = $modelHash
        }
        rapidsPythonExecutable = if ($sources.Contains('rapids')) {
            "runtime/rapids-python/$($RapidsPythonExecutable -replace '\\', '/')"
        } else {
            $null
        }
        madladModel = $madladConfig
    }
    $config | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $output 'airgap-config.json') -Encoding utf8

    [IO.File]::Delete($incomplete)
    $manifest = Join-Path $output 'airgap-manifest.json'
    $bundlePython = Join-Path (Join-Path $output 'runtime\python') $PythonExecutable
    $manifestTool = Join-Path $output 'tools\airgap\airgap_manifest.py'
    & $bundlePython $manifestTool create --root $output --manifest $manifest
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not create the air-gap bundle manifest.'
    }
    & $bundlePython $manifestTool verify --root $output --manifest $manifest
    if ($LASTEXITCODE -ne 0) {
        throw 'The newly created air-gap bundle did not verify.'
    }

    $totals = Get-ChildItem -LiteralPath $output -Recurse -File |
        Measure-Object -Property Length -Sum
    $manifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
    Write-Host "Air-gap bundle created: $output"
    Write-Host "Files: $($totals.Count); bytes: $($totals.Sum)"
    Write-Host "Record this manifest SHA-256 separately: $manifestHash"
    Write-Host 'Transfer the directory and verify it before use with Verify-AirgapBundle.ps1.'
}
catch {
    if (-not (Test-Path -LiteralPath $incomplete -PathType Leaf)) {
        [IO.File]::WriteAllText($incomplete, "Air-gap bundle creation failed.`n")
    }
    throw
}
