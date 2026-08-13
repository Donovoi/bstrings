[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$readmePath = Join-Path $repositoryRoot 'README.md'
$readme = [IO.File]::ReadAllText($readmePath)

if ($readme -cnotmatch '(?m)^The next release uses `Install-Bstrings\.ps1`\.') {
    throw 'README.md must name the installer for the next release.'
}
if ($readme -cnotmatch '(?m)^`bstrings-kit` by default\.$') {
    throw 'README.md must name the default kit directory.'
}
if (
    $readme -cnotmatch (
        '\[v1\.9\.17\]\(https://github\.com/Donovoi/bstrings/releases/tag/v1\.9\.17\)'
    ) -or
    $readme -cnotmatch (
        '\[v1\.9\.17 README\]\(https://github\.com/Donovoi/bstrings/blob/v1\.9\.17/README\.md#get-started\)'
    )
) {
    throw 'README.md must send current users to the immutable v1.9.17 instructions.'
}
if (
    $readme -cmatch (
        'https://github\.com/Donovoi/bstrings/releases/download/' +
        'v2\.0\.0/Install-Bstrings\.ps1'
    )
) {
    throw 'README.md must not publish a v2.0.0 installer URL before that release exists.'
}

$powershellBlocks = [regex]::Matches(
    $readme,
    '(?ms)^```powershell\r?\n(?<code>.*?)^```\s*$'
)
foreach ($block in $powershellBlocks) {
    if ($block.Groups['code'].Value -cmatch '(?m)Install-Bstrings(?:Quality)?\.ps1') {
        throw 'README.md must not publish an installer bootstrap before its release exists.'
    }
}

Write-Host 'README installer transition checks passed.'
