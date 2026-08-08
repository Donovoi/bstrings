Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$gate = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Test-DecisionRecords.ps1'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bstrings-decision-record-tests-' + [Guid]::NewGuid().ToString('N')
)
$decisionRelativePath = 'docs/architecture/adr-0001-content-routing.md'
$highLevelBody = @'
- [ ] Ordinary change — no new or changed high-level decision
- [x] High-level decision — introduces, changes, or implements an architecture decision

ADR: [ADR-0001](docs/architecture/adr-0001-content-routing.md)
'@
$ordinaryBody = @'
- [x] Ordinary change — no new or changed high-level decision
- [ ] High-level decision — introduces, changes, or implements an architecture decision
'@

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-ValidDecisionContent {
    return @'
# ADR-0001: Early content routing

- **Status:** Accepted target architecture; implementation is benchmark gated
- **Date:** 2026-08-08
- **Scope:** integrated analysis pipeline
- **Decision type:** forensic coverage, stage ordering, and performance
- **Review method:** Robin round under the
  [decision policy](decision-review-policy.md)
- **Perspectives:** repository audit, forensic detractor, vendor research, and performance review

## Context

Classification currently occurs inside recovery and cannot safely guide other specialist startup.

## Considered options

Keep recovery-only classification, use an authoritative gate, or add advisory fail-open routing.

## Decision

Create one identity-bound advisory route manifest before native and specialist extraction stages.

## Non-negotiable invariants

Native extraction covers every input and no uncertain classifier result removes a plausible route.

## Acceptance gates

Measure end-to-end performance and require exact native coverage plus specialist fixture recall.

## Strongest detractor and resolution

Central routing can correlate false negatives; monotonic union routes and worker checks address it.

## Falsifiers and revisit triggers

Any native record loss, specialist miss, identity mismatch, or absent measured benefit reopens this ADR.

## Consequences

The design adds an auditable manifest and validation cost while avoiding unnecessary heavy startup.

## Primary references

- [Magika repository](https://github.com/google/magika)
- [repository source](../../bstrings/AnalysisOrchestrator.cs)
'@
}

function Invoke-Gate {
    param(
        [string]$Body = '',
        [string[]]$Paths = @('bstrings/ordinary.cs')
    )
    try {
        $output = @(& $gate `
            -RepositoryRoot $testRoot `
            -ChangedPath $Paths `
            -PullRequestBody $Body 2>&1)
        return [pscustomobject]@{ Passed = $true; Output = ($output -join "`n") }
    }
    catch {
        return [pscustomobject]@{ Passed = $false; Output = $_.Exception.Message }
    }
}

function Assert-Passed {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if (-not $Result.Passed) {
        throw "$Name unexpectedly failed: $($Result.Output)"
    }
}

function Assert-FailedLike {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($Result.Passed -or $Result.Output -notmatch $Pattern) {
        throw "$Name did not fail with '$Pattern': $($Result.Output)"
    }
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Write-Utf8File (Join-Path $testRoot 'bstrings/ordinary.cs') '// ordinary fixture'
    Write-Utf8File (Join-Path $testRoot 'bstrings/AnalysisOrchestrator.cs') '// source fixture'
    Write-Utf8File (Join-Path $testRoot 'docs/architecture/decision-review-policy.md') '# policy'
    $decisionPath = Join-Path $testRoot $decisionRelativePath
    Write-Utf8File $decisionPath (New-ValidDecisionContent)

    Assert-Passed (Invoke-Gate) 'Unmarked ordinary change'
    Assert-Passed (Invoke-Gate -Body $ordinaryBody) 'Declared ordinary change'
    Assert-Passed (
        Invoke-Gate -Paths @('docs/architecture/decision-review-policy.md')
    ) 'Non-ADR architecture documentation'
    Assert-Passed (
        Invoke-Gate -Body $highLevelBody -Paths @($decisionRelativePath)
    ) 'Valid high-level decision'
    Assert-Passed (
        Invoke-Gate -Paths @($decisionRelativePath)
    ) 'Push validation without pull-request body'

    $bothBody = $ordinaryBody.Replace('- [ ] High-level', '- [x] High-level')
    Assert-FailedLike (
        Invoke-Gate -Body $bothBody
    ) 'both ordinary and high-level' 'Conflicting declarations'
    Assert-FailedLike (
        Invoke-Gate -Body ($highLevelBody -replace '(?m)^ADR:.*$', 'ADR:')
    ) 'requires a canonical Markdown ADR link' 'High-level change without ADR'
    Assert-FailedLike (
        Invoke-Gate -Body $ordinaryBody -Paths @($decisionRelativePath)
    ) 'ordinary change but changes a numbered ADR' 'Ordinary declaration with ADR change'

    $valid = New-ValidDecisionContent
    Write-Utf8File $decisionPath (
        $valid -replace '(?ms)## Falsifiers and revisit triggers.*?(?=## Consequences)', ''
    )
    Assert-FailedLike (
        Invoke-Gate -Body $highLevelBody -Paths @($decisionRelativePath)
    ) 'Falsifiers and revisit triggers' 'Missing required section'

    Write-Utf8File $decisionPath (
        $valid.Replace(
            '[Magika repository](https://github.com/google/magika)',
            '[policy](decision-review-policy.md)'
        )
    )
    Assert-FailedLike (
        Invoke-Gate -Body $highLevelBody -Paths @($decisionRelativePath)
    ) 'HTTPS source' 'Missing primary-source link'

    Write-Utf8File $decisionPath (
        $valid.Replace('../../bstrings/AnalysisOrchestrator.cs', '../../bstrings/missing.cs')
    )
    Assert-FailedLike (
        Invoke-Gate -Body $highLevelBody -Paths @($decisionRelativePath)
    ) 'missing or out-of-repository link' 'Missing local artifact'

    Write-Utf8File $decisionPath (
        $valid.Replace(
            'repository audit, forensic detractor, vendor research, and performance review',
            'single reviewer'
        )
    )
    Assert-FailedLike (
        Invoke-Gate -Body $highLevelBody -Paths @($decisionRelativePath)
    ) 'three independent review perspectives' 'Insufficient review perspectives'

Write-Host 'Decision-record policy tests passed.'
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
