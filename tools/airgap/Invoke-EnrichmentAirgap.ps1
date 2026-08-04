[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Set-AirgapEnvironment.ps1')
Enable-BstringsAirgapEnvironment -BundleRoot $PSScriptRoot

$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'airgap-config.json') -Raw |
    ConvertFrom-Json
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
$adapter = Resolve-BundlePath $config.enrichmentAdapter
$magika = Resolve-BundlePath $config.magikaExecutable
$floss = Resolve-BundlePath $config.flossExecutable
$llamaServer = Resolve-BundlePath $config.llamaServer
$model = Resolve-BundlePath $config.translationModel.path
foreach ($required in @($python, $adapter, $magika, $floss, $llamaServer, $model)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "A required bundled air-gap file is missing: $required"
    }
}

$llamaDirectory = Split-Path -Parent $llamaServer
[Environment]::SetEnvironmentVariable(
    'PATH',
    "$llamaDirectory$([IO.Path]::PathSeparator)$env:PATH",
    'Process'
)
$defaults = @(
    '--airgap',
    '--magika', $magika,
    '--floss', $floss,
    '--llama-server', $llamaServer,
    '--translation-model-path', $model,
    '--translation-model-id', $config.translationModel.id,
    '--translation-revision', $config.translationModel.revision,
    '--translation-model-sha256', $config.translationModel.sha256
)

& $python $adapter @defaults @RemainingArguments
exit $LASTEXITCODE
