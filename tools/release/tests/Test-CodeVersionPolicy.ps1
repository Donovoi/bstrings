[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$policy = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assert-CodeVersion.ps1'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bstrings-version-policy-' + [Guid]::NewGuid().ToString('N')
)

function Invoke-TestGit([string[]]$Arguments) {
    $output = @(& git -C $testRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "test git $($Arguments -join ' ') failed:`n$($output -join "`n")"
    }
    return $output
}

function Write-Version([string]$Version) {
    $projectPath = Join-Path $testRoot 'bstrings\bstrings.csproj'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $projectPath)) | Out-Null
    [IO.File]::WriteAllText(
        $projectPath,
        (
            '<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>' +
            "<PropertyGroup><Version>$Version</Version></PropertyGroup></Project>`n"
        ),
        [Text.UTF8Encoding]::new($false)
    )
}

function Commit-All([string]$Message) {
    $null = Invoke-TestGit -Arguments @('add', '--all')
    $null = Invoke-TestGit -Arguments @('commit', '-m', $Message)
    $revision = @(Invoke-TestGit -Arguments @('rev-parse', 'HEAD'))[0]
    return [string]$revision
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $message = $null
    try {
        & $Action
    }
    catch {
        $message = $_.Exception.Message
    }
    if ($null -eq $message -or $message -notmatch $Pattern) {
        throw "Expected failure matching '$Pattern', received '$message'."
    }
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $null = Invoke-TestGit -Arguments @('init', '--initial-branch=master')
    $null = Invoke-TestGit -Arguments @('config', 'user.name', 'bstrings release test')
    $null = Invoke-TestGit -Arguments @('config', 'user.email', 'release-test@example.invalid')

    Write-Version '1.9.2'
    [IO.Directory]::CreateDirectory((Join-Path $testRoot 'docs')) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'docs\guide.md'),
        "# Guide`n",
        [Text.UTF8Encoding]::new($false)
    )
    $base = Commit-All 'base'

    [IO.File]::AppendAllText(
        (Join-Path $testRoot 'docs\guide.md'),
        "Documentation only.`n",
        [Text.UTF8Encoding]::new($false)
    )
    $docsOnly = Commit-All 'docs only'
    & $policy -BaseRevision $base -HeadRevision $docsOnly -RepositoryRoot $testRoot

    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'bstrings\Program.cs'),
        "internal static class Program { }`n",
        [Text.UTF8Encoding]::new($false)
    )
    $unversionedCode = Commit-All 'code without version'
    Assert-Throws {
        & $policy -BaseRevision $docsOnly -HeadRevision $unversionedCode -RepositoryRoot $testRoot
    } 'require a forward project Version bump'

    Write-Version '1.9.3'
    $versionedCode = Commit-All 'bump version'
    & $policy -BaseRevision $docsOnly -HeadRevision $versionedCode -RepositoryRoot $testRoot

    $null = Invoke-TestGit -Arguments @('tag', 'v1.9.3', $versionedCode)
    Assert-Throws {
        & $policy -BaseRevision $docsOnly -HeadRevision $versionedCode -RepositoryRoot $testRoot
    } 'already exists'

    Write-Host 'Code-version policy tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Get-ChildItem -LiteralPath $testRoot -Recurse -Force | ForEach-Object {
            $_.Attributes = [IO.FileAttributes]::Normal
        }
        [IO.Directory]::Delete($testRoot, $true)
    }
}
