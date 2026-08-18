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
if ($bootstrap -cnotmatch '(?m)^  \$tag = ''v3\.0\.0''\r?$') {
    throw 'The README installer bootstrap must pin v3.0.0.'
}
if ($bootstrap -cnotmatch '(?m)^  \$name = ''Install-Bstrings\.ps1''\r?$') {
    throw 'The README installer bootstrap must pin Install-Bstrings.ps1.'
}
if ($bootstrap -cnotmatch '(?m)^  Set-StrictMode -Version Latest\r?$') {
    throw 'The README installer bootstrap must enable strict mode.'
}
if ($bootstrap -cnotmatch '(?m)^  \$ErrorActionPreference = ''Stop''\r?$') {
    throw 'The README installer bootstrap must stop on command errors.'
}
if ($bootstrap -cmatch '(?m)\bGet-FileHash\b') {
    throw 'The README installer bootstrap must not depend on Get-FileHash.'
}
if ($bootstrap -cnotmatch '\[Security\.Cryptography\.SHA256\]::Create\(\)') {
    throw 'The README installer bootstrap must hash through the .NET SHA-256 API.'
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
if ($script:releaseLookups -ne 1 -or $script:downloads -ne 0 -or $script:installerLaunches -ne 0) {
    throw 'The bootstrap did not stop at its failed release lookup.'
}

$script:installerPayload = [Text.Encoding]::UTF8.GetBytes("synthetic installer`n")
$payloadHasher = [Security.Cryptography.SHA256]::Create()
try {
    $script:payloadDigest = (
        [BitConverter]::ToString($payloadHasher.ComputeHash($script:installerPayload))
    ).Replace('-', '').ToLowerInvariant()
}
finally {
    $payloadHasher.Dispose()
}
$script:reportedDigest = $script:payloadDigest
$script:releaseLookups = 0
$script:downloads = 0
$script:installerLaunches = 0
$script:downloadTargets = [Collections.Generic.List[string]]::new()
$script:installerArguments = @()

function Invoke-RestMethod {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [object]$Uri,
        [hashtable]$Headers,
        [switch]$UseBasicParsing
    )
    $script:releaseLookups++
    return [pscustomobject]@{
        tag_name = 'v3.0.0'
        draft = $false
        prerelease = $false
        immutable = $true
        assets = @(
            [pscustomobject]@{
                name = 'Install-Bstrings.ps1'
                browser_download_url = (
                    'https://github.com/Donovoi/bstrings/releases/download/' +
                    'v3.0.0/Install-Bstrings.ps1'
                )
                digest = "sha256:$script:reportedDigest"
            }
        )
    }
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
    $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
        $OutFile
    )
    $script:downloadTargets.Add($resolved)
    [IO.File]::WriteAllBytes($resolved, $script:installerPayload)
}

function powershell.exe {
    $script:installerLaunches++
    $script:installerArguments = @($args)
    $script:LASTEXITCODE = 0
}

$mismatchRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bstrings-readme-bootstrap-mismatch-' + [Guid]::NewGuid().ToString('N')
)
[IO.Directory]::CreateDirectory($mismatchRoot) | Out-Null
$mismatchInstaller = Join-Path $mismatchRoot 'Install-Bstrings.ps1'
$stalePayload = [Text.Encoding]::UTF8.GetBytes('authenticated old installer')
[IO.File]::WriteAllBytes($mismatchInstaller, $stalePayload)
$script:reportedDigest = '0' * 64
$mismatchCaught = $null
try {
    Push-Location $mismatchRoot
    try {
        & $scriptBlock
    }
    catch {
        $mismatchCaught = $_
    }
    finally {
        Pop-Location
    }
    if (
        $null -eq $mismatchCaught -or
        $mismatchCaught.Exception.Message -cne 'Installer SHA-256 mismatch.'
    ) {
        throw "The digest-mismatch bootstrap returned an unexpected error: $mismatchCaught"
    }
    if ($script:downloads -ne 1 -or $script:installerLaunches -ne 0) {
        throw 'The digest-mismatch bootstrap did not stop after its temporary download.'
    }
    if (
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($mismatchInstaller)) -cne
        [Convert]::ToBase64String($stalePayload)
    ) {
        throw 'A failed installer authentication changed the existing installer bytes.'
    }
    if (@(Get-ChildItem -LiteralPath $mismatchRoot -Force -Filter '*.partial').Count -ne 0) {
        throw 'The digest-mismatch bootstrap left a partial installer download behind.'
    }
}
finally {
    [IO.Directory]::Delete($mismatchRoot, $true)
}

$script:reportedDigest = $script:payloadDigest
$script:releaseLookups = 0
$script:downloads = 0
$script:installerLaunches = 0
$script:downloadTargets.Clear()
$script:installerArguments = @()
$successRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bstrings-readme-bootstrap-test-' + [Guid]::NewGuid().ToString('N')
)
[IO.Directory]::CreateDirectory($successRoot) | Out-Null
try {
    $downloadedInstaller = Join-Path $successRoot 'Install-Bstrings.ps1'
    [IO.File]::WriteAllText($downloadedInstaller, 'stale installer')
    Push-Location $successRoot
    try {
        & $scriptBlock
    }
    finally {
        Pop-Location
    }
    if ($script:releaseLookups -ne 1 -or $script:downloads -ne 1) {
        throw 'The successful bootstrap did not perform one lookup and one download.'
    }
    if ($script:installerLaunches -ne 1) {
        throw "Expected one installer launch, found $script:installerLaunches."
    }
    if (
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($downloadedInstaller)) -cne
        [Convert]::ToBase64String($script:installerPayload)
    ) {
        throw 'The successful bootstrap did not replace the stale installer bytes.'
    }
    if ($script:downloadTargets.Count -ne 1) {
        throw 'The successful bootstrap did not use one temporary download path.'
    }
    if (
        [IO.Path]::GetFullPath($script:downloadTargets[0]).Equals(
            [IO.Path]::GetFullPath($downloadedInstaller),
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        throw 'The bootstrap downloaded over the existing installer before verification.'
    }
    if (
        -not ($script:installerArguments -ccontains '-ReleaseTag') -or
        -not ($script:installerArguments -ccontains 'v3.0.0')
    ) {
        throw 'The bootstrap did not launch the installer with the exact release tag.'
    }
    if (@(Get-ChildItem -LiteralPath $successRoot -Force -Filter '*.partial').Count -ne 0) {
        throw 'The successful bootstrap left a partial download behind.'
    }
    if (@(Get-ChildItem -LiteralPath $successRoot -Force -Filter '*.tmp').Count -ne 0) {
        throw 'The successful bootstrap left a backup behind.'
    }
}
finally {
    $resolvedSuccessRoot = [IO.Path]::GetFullPath($successRoot)
    if (
        [IO.Path]::GetFileName($resolvedSuccessRoot) -notmatch
            '^bstrings-readme-bootstrap-test-[0-9a-f]{32}$'
    ) {
        throw "Refusing to remove unexpected test path: $resolvedSuccessRoot"
    }
    [IO.Directory]::Delete($resolvedSuccessRoot, $true)
}

Write-Host 'README installer bootstrap tests passed.'
