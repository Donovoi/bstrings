[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaseRevision,

    [Parameter(Mandatory)]
    [string]$HeadRevision,

    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..'),

    [string]$ProjectPath = 'bstrings/bstrings.csproj'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not [IO.Directory]::Exists((Join-Path $repoRoot '.git'))) {
    throw "RepositoryRoot is not a Git checkout: $repoRoot"
}

function Invoke-Git([string[]]$Arguments) {
    $output = @(& git -C $repoRoot -c core.quotepath=false @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed:`n$($output -join "`n")"
    }
    return $output
}

function Get-ProjectVersion([string]$Revision) {
    $objectName = '{0}:{1}' -f $Revision, $ProjectPath.Replace('\', '/')
    $projectText = (Invoke-Git -Arguments @('show', $objectName)) -join "`n"
    [xml]$project = $projectText
    $versions = @(
        $project.SelectNodes('/Project/PropertyGroup/Version') |
            ForEach-Object { [string]$_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
    )
    if ($versions.Count -ne 1) {
        throw "Expected exactly one project Version at $Revision, found $($versions.Count)."
    }
    if ($versions[0] -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Project Version at $Revision is not stable MAJOR.MINOR.PATCH: $($versions[0])"
    }
    return [Version]$versions[0]
}

$null = Invoke-Git -Arguments @('rev-parse', '--verify', "$BaseRevision^{commit}")
$null = Invoke-Git -Arguments @('rev-parse', '--verify', "$HeadRevision^{commit}")
$baseVersion = Get-ProjectVersion $BaseRevision
$headVersion = Get-ProjectVersion $HeadRevision
$comparison = $headVersion.CompareTo($baseVersion)

if ($comparison -lt 0) {
    throw "Project Version must not decrease. Base=$baseVersion Head=$headVersion"
}

if ($comparison -eq 0) {
    Write-Host "Ordinary change retains project Version $headVersion; no release is being prepared."
    return
}

$releaseTag = "v$headVersion"
$existingTags = @(Invoke-Git -Arguments @('tag', '--list', $releaseTag))
if ($existingTags.Count -ne 0) {
    throw "Release tag $releaseTag already exists; choose a new project Version."
}

Write-Host (
    "Forward unused project Version recognized as release preparation: " +
    "$baseVersion -> $headVersion ($releaseTag)."
)
