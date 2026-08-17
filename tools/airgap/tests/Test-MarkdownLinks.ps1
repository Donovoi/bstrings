[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Verify-MarkdownLinks.ps1'))
$builder = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-AirgapBundle.ps1'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bstrings-markdown-links-' + [Guid]::NewGuid().ToString('N')
)

function Write-Utf8File([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Assert-ThrowsMissingTarget([string]$BundleRoot) {
    $message = $null
    try {
        & $verifier -BundleDirectory $BundleRoot
    }
    catch {
        $message = $_.Exception.Message
    }
    if ($null -eq $message -or $message -notmatch 'target is missing') {
        throw 'The verifier did not reject a missing first-party documentation target.'
    }
}

function Get-LiteralArrayAssignment([string]$ScriptPath, [string]$VariableName) {
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $ScriptPath,
        [ref]$tokens,
        [ref]$parseErrors
    )
    if (@($parseErrors).Count -ne 0) {
        throw "Could not parse $ScriptPath while reading `$${VariableName}."
    }
    $assignments = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -ceq $VariableName
    }, $true))
    if ($assignments.Count -ne 1) {
        throw "Expected one literal `$${VariableName} assignment in $ScriptPath."
    }
    $values = @($assignments[0].Right.FindAll({
        param($node)
        $node -is [Management.Automation.Language.StringConstantExpressionAst]
    }, $true) | ForEach-Object { $_.Value })
    if (
        $values.Count -eq 0 -or
        @($values | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0 -or
        @($values | Sort-Object -Unique).Count -ne $values.Count
    ) {
        throw "`$${VariableName} must contain unique, non-empty literal paths."
    }
    return $values
}

try {
    $builderBundle = Join-Path $testRoot 'builder-document-set'
    $bundleDocs = Join-Path $builderBundle 'docs'
    [IO.Directory]::CreateDirectory((Join-Path $bundleDocs 'releases')) | Out-Null
    $rootDocuments = @(Get-LiteralArrayAssignment $builder 'bundleRootDocumentNames')
    $expectedRootDocuments = @('BASE_PACK_NOTICE.md', 'VERSIONING.md')
    if (($rootDocuments -join '|') -cne ($expectedRootDocuments -join '|')) {
        throw 'The bundle root-document fixture differs from its exact reviewed inventory.'
    }
    foreach ($documentName in $rootDocuments) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $documentName) -Destination $builderBundle
    }
    foreach ($documentName in Get-LiteralArrayAssignment $builder 'bundleDocumentNames') {
        Copy-Item `
            -LiteralPath (Join-Path $repoRoot "docs\$documentName") `
            -Destination $bundleDocs
    }
    $bundleArchitectureDocs = Join-Path $bundleDocs 'architecture'
    [IO.Directory]::CreateDirectory($bundleArchitectureDocs) | Out-Null
    $architectureDocuments = @(
        Get-LiteralArrayAssignment $builder 'bundleArchitectureDocumentNames'
    )
    $expectedArchitectureDocuments = @(
        'adr-0001-early-fail-open-content-routing.md',
        'adr-0003-persistent-verified-bytes-and-batched-releases.md',
        'adr-0004-bounded-language-detection-reuse.md',
        'adr-0005-translation-integrity-and-run-dedup.md',
        'adr-0006-q4-cuda-full-translation.md',
        'adr-0007-translation-worthiness-routing.md',
        'adr-0008-independent-engine-execution.md',
        'adr-0009-bounded-forensic-pattern-expansion.md',
        'adr-0010-bounded-reversible-decoding.md',
        'adr-0011-single-windows-kit-and-plain-documentation.md',
        'adr-0012-explicit-whole-stage-resume.md',
          'adr-0013-resume-with-translation-off.md',
          'adr-0014-compact-investigator-findings.md',
          'adr-0015-high-confidence-base64-and-bounded-match-reuse.md',
          'adr-0016-high-confidence-email-candidates.md',
          'decision-review-policy.md'
    )
    if (($architectureDocuments -join '|') -cne ($expectedArchitectureDocuments -join '|')) {
        throw 'The bundle architecture-document fixture differs from its exact reviewed inventory.'
    }
    foreach ($documentName in $architectureDocuments) {
        Copy-Item `
            -LiteralPath (Join-Path $repoRoot "docs\architecture\$documentName") `
            -Destination $bundleArchitectureDocs
    }
    $bundleBenchmarkResults = Join-Path $builderBundle 'benchmarks\results'
    [IO.Directory]::CreateDirectory($bundleBenchmarkResults) | Out-Null
    foreach ($resultName in Get-LiteralArrayAssignment $builder 'bundleBenchmarkResultNames') {
        Copy-Item `
            -LiteralPath (Join-Path $repoRoot "benchmarks\results\$resultName") `
            -Destination $bundleBenchmarkResults
    }
    foreach ($documentName in Get-LiteralArrayAssignment $builder 'bundleReleaseDocumentNames') {
        Copy-Item `
            -LiteralPath (Join-Path $repoRoot "docs\releases\$documentName") `
            -Destination (Join-Path $bundleDocs 'releases')
    }
    foreach ($documentName in @('README.md', 'LICENSE.md', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $documentName) -Destination $builderBundle
    }
    $bundleEnrichmentTools = Join-Path $builderBundle 'tools\enrichment'
    [IO.Directory]::CreateDirectory($bundleEnrichmentTools) | Out-Null
    $enrichmentTools = @(
        Get-LiteralArrayAssignment $builder 'bundleEnrichmentToolNames'
    )
    $expectedEnrichmentTools = @(
        'bstrings_enrich.py',
        'bstrings_ocr.py',
        'benchmark_ocr.py',
        'benchmark_translation_cache.py',
        'benchmark_translation.py'
    )
    if (($enrichmentTools -join '|') -cne ($expectedEnrichmentTools -join '|')) {
        throw 'The bundle enrichment-tool fixture differs from its exact reviewed inventory.'
    }
    foreach ($toolName in $enrichmentTools) {
        Copy-Item `
            -LiteralPath (Join-Path $repoRoot "tools\enrichment\$toolName") `
            -Destination $bundleEnrichmentTools
    }
    $bundleAirgapTools = Join-Path $builderBundle 'tools\airgap'
    [IO.Directory]::CreateDirectory($bundleAirgapTools) | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'tools\airgap\offline-components.lock.json') `
        -Destination $bundleAirgapTools
    Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $builderBundle -Recurse
    Write-Utf8File `
        (Join-Path $builderBundle 'runtime\ocr-cpu\Privacy.md') `
        '[Unbundled upstream target](../Privacy-policy.md)'
    & $verifier -BundleDirectory $builderBundle

    $validBundle = Join-Path $testRoot 'valid'
    Write-Utf8File `
        (Join-Path $validBundle 'README.md') `
        '[Guide](docs/guide.md)'
    Write-Utf8File `
        (Join-Path $validBundle 'docs\guide.md') `
        '[Release notes](releases/v1.9.0.md)'
    Write-Utf8File `
        (Join-Path $validBundle 'docs\releases\v1.9.0.md') `
        '# Release notes'
    Write-Utf8File `
        (Join-Path $validBundle 'runtime\ocr-cpu\Privacy.md') `
        '[Vendor source-tree link](../missing-vendor-target.md)'
    Write-Utf8File `
        (Join-Path $validBundle 'licenses\vendor\README.md') `
        '[Another vendor source-tree link](README.ijg)'

    & $verifier -BundleDirectory $validBundle

    $missingRootTarget = Join-Path $testRoot 'missing-root-target'
    Write-Utf8File `
        (Join-Path $missingRootTarget 'README.md') `
        '[Missing guide](docs/missing.md)'
    Assert-ThrowsMissingTarget $missingRootTarget

    $missingDocsTarget = Join-Path $testRoot 'missing-docs-target'
    Write-Utf8File `
        (Join-Path $missingDocsTarget 'docs\guide.md') `
        '[Missing release](releases/missing.md)'
    Assert-ThrowsMissingTarget $missingDocsTarget

    Write-Host 'Bundled Markdown-link verifier tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
