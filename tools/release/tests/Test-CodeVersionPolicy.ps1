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

function Assert-Output([scriptblock]$Action, [string]$Pattern) {
    $output = @(& $Action 6>&1) -join "`n"
    if ($output -notmatch $Pattern) {
        throw "Expected output matching '$Pattern', received '$output'."
    }
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $null = Invoke-TestGit -Arguments @('init', '--initial-branch=master')
    $null = Invoke-TestGit -Arguments @('config', 'user.name', 'bstrings release test')
    $null = Invoke-TestGit -Arguments @('config', 'user.email', 'release-test@example.invalid')
    $null = Invoke-TestGit -Arguments @('config', 'core.autocrlf', 'false')

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
    Assert-Output {
        & $policy -BaseRevision $base -HeadRevision $docsOnly -RepositoryRoot $testRoot
    } 'Ordinary change retains project Version 1\.9\.2'

    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'bstrings\Program.cs'),
        "internal static class Program { }`n",
        [Text.UTF8Encoding]::new($false)
    )
    $unversionedCode = Commit-All 'code without version'
    Assert-Output {
        & $policy -BaseRevision $docsOnly -HeadRevision $unversionedCode -RepositoryRoot $testRoot
    } 'Ordinary change retains project Version 1\.9\.2'

    Write-Version '1.9.1'
    $decreasedVersion = Commit-All 'decrease version'
    Assert-Throws {
        & $policy -BaseRevision $unversionedCode -HeadRevision $decreasedVersion -RepositoryRoot $testRoot
    } 'Project Version must not decrease.*Base=1\.9\.2 Head=1\.9\.1'

    Write-Version '1.9.3'
    $versionedCode = Commit-All 'bump version'
    Assert-Output {
        & $policy -BaseRevision $unversionedCode -HeadRevision $versionedCode -RepositoryRoot $testRoot
    } 'Forward unused project Version recognized as release preparation.*v1\.9\.3'

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
