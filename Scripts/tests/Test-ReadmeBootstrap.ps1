[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$readmePath = Join-Path $repositoryRoot 'README.md'
$readme = [IO.File]::ReadAllText($readmePath)
$blocks = [regex]::Matches(
    $readme,
    '(?ms)^```powershell\r?\n(?<code>.*?)^```\s*$'
)
if ($blocks.Count -lt 1) {
    throw 'README.md does not contain a PowerShell installer bootstrap.'
}

$bootstrap = $blocks[0].Groups['code'].Value
if ($bootstrap -cnotmatch '^& \{\r?\n') {
    throw 'The README installer bootstrap must be one fail-fast script block.'
}
if ($bootstrap -cnotmatch "(?m)^  Set-StrictMode -Version Latest\r?$") {
    throw 'The README installer bootstrap must enable strict mode.'
}
if ($bootstrap -cnotmatch '(?m)^  \$ErrorActionPreference = ''Stop''\r?$') {
    throw 'The README installer bootstrap must stop on command errors.'
}

$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseInput(
    $bootstrap,
    [ref]$tokens,
    [ref]$parseErrors
) | Out-Null
if (@($parseErrors).Count -ne 0) {
    $messages = @($parseErrors | ForEach-Object { $_.Message }) -join '; '
    throw "The README installer bootstrap does not parse: $messages"
}
$scriptBlock = [scriptblock]::Create($bootstrap)

$script:releaseLookups = 0
$script:downloads = 0
$script:installerLaunches = 0
$sentinel = 'README_BOOTSTRAP_RELEASE_LOOKUP_FAILED'

function Invoke-RestMethod {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [object]$Uri,
        [hashtable]$Headers,
        [switch]$UseBasicParsing
    )
    $script:releaseLookups++
    throw $sentinel
}

function Invoke-WebRequest {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [object]$Uri,
        [string]$OutFile,
        [switch]$UseBasicParsing
    )
    $script:downloads++
    throw 'The bootstrap continued to its download after release lookup failed.'
}

function powershell.exe {
    $script:installerLaunches++
    throw 'The bootstrap launched the installer after release lookup failed.'
}

$caught = $null
try {
    & $scriptBlock
}
catch {
    $caught = $_
}

if ($null -eq $caught -or $caught.Exception.Message -cne $sentinel) {
    throw "The bootstrap did not surface the release lookup error exactly: $caught"
}
if ($script:releaseLookups -ne 1) {
    throw "Expected one release lookup, found $script:releaseLookups."
}
if ($script:downloads -ne 0) {
    throw "The bootstrap attempted $script:downloads download(s) after lookup failure."
}
if ($script:installerLaunches -ne 0) {
    throw "The bootstrap attempted $script:installerLaunches installer launch(es) after lookup failure."
}

Write-Host 'README installer bootstrap fail-fast test passed.'
