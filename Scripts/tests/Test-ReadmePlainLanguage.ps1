[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$readmePath = Join-Path $repoRoot 'README.md'
$readme = [IO.File]::ReadAllText($readmePath)

$requiredHeadings = @(
    '## What bstrings does',
    '## Requirements',
    '## Install',
    '## Verify',
    '## Run an analysis',
    '## Select engines',
    '## Get help'
)
foreach ($heading in $requiredHeadings) {
    if (-not $readme.Contains($heading)) {
        throw "README is missing the required heading: $heading"
    }
}

if ($readme -match '(?im)^## Why use it\?\s*$') {
    throw 'README must use the short What bstrings does section.'
}

$plainLines = [Collections.Generic.List[string]]::new()
$insideFence = $false
foreach ($line in [IO.File]::ReadAllLines($readmePath)) {
    if ($line.TrimStart().StartsWith('```', [StringComparison]::Ordinal)) {
        $insideFence = -not $insideFence
        continue
    }
    if ($insideFence -or $line.TrimStart().StartsWith('#', [StringComparison]::Ordinal)) {
        continue
    }
    if ($line -match '^\s*[-*]\s+') {
        $plainLines.Add('')
        $plainLines.Add($line)
        $plainLines.Add('')
    }
    else {
        $plainLines.Add($line)
    }
}
if ($insideFence) {
    throw 'README has an unclosed code fence.'
}

$plainText = $plainLines -join "`n"
$wordCount = [regex]::Matches(
    $plainText,
    "\b[\p{L}\p{N}][\p{L}\p{N}'-]*\b"
).Count
if ($wordCount -gt 900) {
    throw "README prose is too long for the quick-start contract: $wordCount words."
}
$paragraphs = [regex]::Split($plainText.Trim(), '(?:\r?\n){2,}')
foreach ($rawParagraph in $paragraphs) {
    $paragraph = ($rawParagraph -replace '(?m)^\s*[-*]\s+', '') -replace '\r?\n', ' '
    $paragraph = $paragraph -replace '!?' + '\[([^\]]+)\]\([^\)]+\)', '$1'
    $paragraph = $paragraph.Trim()
    if ([string]::IsNullOrWhiteSpace($paragraph)) {
        continue
    }

    $sentences = @(
        [regex]::Split($paragraph, '(?<=[.!?])\s+') |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($sentences.Count -gt 6) {
        throw "README paragraph has more than six sentences: $paragraph"
    }
    foreach ($sentence in $sentences) {
        $sentenceWords = [regex]::Matches(
            $sentence,
            "\b[\p{L}\p{N}][\p{L}\p{N}'-]*\b"
        ).Count
        if ($sentenceWords -gt 25) {
            throw "README sentence has $sentenceWords words; the limit is 25: $sentence"
        }
    }
}

$activeFiles = @(
    'README.md',
    'VERSIONING.md',
    'BASE_PACK_NOTICE.md',
    'docs/command-reference.md',
    'docs/download-and-install.md',
    'docs/air-gapped-deployment.md',
    'docs/offline-release-maintenance.md',
    'docs/enrichment-pipeline.md',
    'docs/output-and-provenance.md',
    'docs/ocr-and-document-analysis.md',
    'docs/document-reading-research-2026-08.md',
    'docs/translation-benchmark-2026-08-04.md',
    'docs/floss-standalone-redistribution.md',
    'docs/magika-cli-redistribution.md',
    'benchmarks/README.md',
    'bstrings/AnalysisCli.cs',
    'bstrings/AnalysisToolchain.cs',
    'bstrings/Program.cs'
)
$forbidden = @(
    'quality kit',
    'quality bundle',
    'quality installer',
    'quality profile',
    'quality tier',
    'bstrings-quality',
    'Install-BstringsQuality.ps1',
    'bundle-packs-quality.json',
    'airgap-config-quality.json',
    'airgap-manifest-quality.json',
    'core-only installation',
    'core release',
    'core ZIP',
    'quality channel',
    'standalone core',
    'translation profiles',
    'Full profile'
)
foreach ($relativePath in $activeFiles) {
    $path = Join-Path $repoRoot $relativePath
    if (-not [IO.File]::Exists($path)) {
        throw "Current documentation contract file is missing: $relativePath"
    }
    $text = [IO.File]::ReadAllText($path)
    foreach ($term in $forbidden) {
        if ($text.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Current user guidance contains retired kit terminology '$term': $relativePath"
        }
    }
}

$neutralGuidanceFiles = @(
    'README.md',
    'docs/command-reference.md',
    'docs/download-and-install.md',
    'docs/air-gapped-deployment.md',
    'docs/enrichment-pipeline.md',
    'docs/output-and-provenance.md',
    'docs/ocr-and-document-analysis.md',
    'docs/forensic-reporting-2026-08.md',
    'docs/releases/v2.0.0.md',
    'docs/releases/v2.1.0.md',
    'docs/releases/v2.1.1.md',
    'docs/releases/v2.1.2.md',
    'docs/releases/v3.0.0.md'
)
$professionalPracticeDirectives = @(
    'Do not analyze evidence',
    'Do not use incomplete output',
    'Do not treat partial reports',
    'Protect the complete output directory',
    'Keep incomplete output',
    'Keep failed output',
    'Keep cancelled or failed output',
    'Store and transfer it as evidence',
    'Treat the result directory as one examination artifact',
    'Treat both as leads',
    'Confirm consequential',
    'Review the parent whenever',
    'must be checked against source evidence',
    'Protect, transfer, and dispose'
)
foreach ($relativePath in $neutralGuidanceFiles) {
    $path = Join-Path $repoRoot $relativePath
    if (-not [IO.File]::Exists($path)) {
        throw "Neutral user-guidance file is missing: $relativePath"
    }
    $text = [IO.File]::ReadAllText($path)
    foreach ($directive in $professionalPracticeDirectives) {
        if ($text.IndexOf($directive, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Current user guidance contains professional-practice direction '$directive': $relativePath"
        }
    }
}

$reportingGuide = [IO.File]::ReadAllText(
    (Join-Path $repoRoot 'docs/forensic-reporting-2026-08.md')
)
if (
    $reportingGuide.IndexOf(
        '`findings.tsv` and `feature-histogram.tsv` contain matched values.',
        [StringComparison]::Ordinal
    ) -lt 0
) {
    throw 'The reporting guide must state that the review files contain matched values.'
}

Write-Host "README plain-language, neutral guidance, and one-kit terminology checks passed ($wordCount words)."
