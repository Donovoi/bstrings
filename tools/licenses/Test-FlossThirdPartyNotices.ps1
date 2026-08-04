[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FlossExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$stager = Join-Path $PSScriptRoot 'Stage-FlossThirdPartyNotices.ps1'
$verifier = Join-Path $PSScriptRoot 'Verify-FlossThirdPartyNotices.ps1'
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporaryRoot = Join-Path $temporaryParent ('bstrings-floss-notice-tests-' + [guid]::NewGuid().ToString('N'))

function Assert-Fails([scriptblock]$Action, [string]$ExpectedMessage, [string]$Name) {
    try {
        & $Action
    }
    catch {
        if (-not $_.Exception.Message.Contains($ExpectedMessage, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Name failed for an unexpected reason: $($_.Exception.Message)"
        }
        Write-Host "PASS (rejected): $Name"
        return
    }
    throw "$Name unexpectedly succeeded."
}

function New-VerifiedStage([string]$Name) {
    $path = Join-Path $temporaryRoot $Name
    $null = & $stager -FlossExecutable $FlossExecutable -DestinationDirectory $path
    return $path
}

function Remove-TestRoot {
    if (-not (Test-Path -LiteralPath $temporaryRoot -PathType Container)) {
        return
    }
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $temporaryRoot).Path)
    $expectedPrefix = $temporaryParent + [IO.Path]::DirectorySeparatorChar
    $leaf = [IO.Path]::GetFileName($resolved)
    if (
        -not $resolved.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $leaf -notmatch '^bstrings-floss-notice-tests-[0-9a-f]{32}$' -or
        ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "Refusing to remove an unverified test directory: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

try {
    $null = New-Item -ItemType Directory -Path $temporaryRoot

    $positive = New-VerifiedStage 'positive'
    & $verifier -FlossExecutable $FlossExecutable -StagedDirectory $positive
    Write-Host 'PASS: exact stage and independent verification'

    Assert-Fails {
        & $verifier -FlossExecutable $env:ComSpec -SkipVersionProbe
    } 'FLOSS executable byte length mismatch' 'wrong executable'

    $tampered = New-VerifiedStage 'tampered'
    [IO.File]::AppendAllText(
        (Join-Path $tampered 'licenses\floss-v3.1.1\FLOSS-Apache-2.0.txt'),
        'tamper'
    )
    Assert-Fails {
        & $verifier -FlossExecutable $FlossExecutable -StagedDirectory $tampered -SkipVersionProbe
    } 'Staged FLOSS asset' 'tampered notice asset'

    $missing = New-VerifiedStage 'missing'
    Remove-Item -LiteralPath (Join-Path $missing 'licenses\floss-v3.1.1\sources\tqdm-4.66.4.tar.gz')
    Assert-Fails {
        & $verifier -FlossExecutable $FlossExecutable -StagedDirectory $missing -SkipVersionProbe
    } 'tqdm-4.66.4.tar.gz was not found' 'missing required source archive'

    $extra = New-VerifiedStage 'extra'
    [IO.File]::WriteAllText(
        (Join-Path $extra 'licenses\floss-v3.1.1\unexpected.txt'),
        'unexpected'
    )
    Assert-Fails {
        & $verifier -FlossExecutable $FlossExecutable -StagedDirectory $extra -SkipVersionProbe
    } 'Staged FLOSS asset directory drifted' 'unexpected notice asset'

    $inventoryTamper = New-VerifiedStage 'inventory-tamper'
    [IO.File]::AppendAllText(
        (Join-Path $inventoryTamper 'licenses\floss-v3.1.1-win-x64.json'),
        'tamper'
    )
    Assert-Fails {
        & $verifier -FlossExecutable $FlossExecutable -StagedDirectory $inventoryTamper -SkipVersionProbe
    } 'Staged FLOSS inventory byte length mismatch' 'tampered staged inventory'

    Write-Host 'FLOSS notice tests passed: 1 exact stage plus 5 fail-closed cases.'
}
finally {
    Remove-TestRoot
}
