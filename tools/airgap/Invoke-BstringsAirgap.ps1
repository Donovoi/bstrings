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

$executable = Resolve-BundlePath $config.bstringsExecutable
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "The bundled bstrings executable is missing: $executable"
}
if ($config.rapidsPythonExecutable) {
    $rapidsPython = Resolve-BundlePath $config.rapidsPythonExecutable
    if (-not (Test-Path -LiteralPath $rapidsPython -PathType Leaf)) {
        throw "The bundled RAPIDS Python executable is missing: $rapidsPython"
    }
    [Environment]::SetEnvironmentVariable('BSTRINGS_RAPIDS_PYTHON', $rapidsPython, 'Process')
}

& $executable @RemainingArguments
exit $LASTEXITCODE
