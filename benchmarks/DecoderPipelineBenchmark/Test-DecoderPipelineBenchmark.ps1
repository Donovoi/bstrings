param()

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("bstrings-decoder-smoke-" + [guid]::NewGuid().ToString('N'))
$resultPath = Join-Path $workRoot 'decoder-smoke.json'

try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    dotnet run --project (Join-Path $PSScriptRoot 'DecoderPipelineBenchmark.csproj') -c Release -- `
        --output $resultPath `
        --records 60 `
        --rounds 2 `
        --workloads natural,no-candidate,candidate-heavy
    if ($LASTEXITCODE -ne 0) {
        throw "Decoder pipeline benchmark smoke failed with exit code $LASTEXITCODE."
    }
    $report = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if ($report.pairs.Count -ne 6 -or $report.summaries.Count -ne 3 -or $report.gateEligible -or -not $report.gatePassed) {
        throw 'Decoder pipeline benchmark smoke returned an unexpected report shape.'
    }
    if (@($report.summaries | Where-Object { $_.gateApplicable }).Count -ne 0) {
        throw 'The subscale smoke unexpectedly activated a scenario timing gate.'
    }
    foreach ($workload in @('natural', 'no-candidate', 'candidate-heavy')) {
        $pairs = @($report.pairs | Where-Object { $_.workload -eq $workload })
        if ($pairs.Count -ne 2 -or @($pairs | Where-Object { $_.order -eq 'off-first' }).Count -ne 1 -or @($pairs | Where-Object { $_.order -eq 'auto-first' }).Count -ne 1) {
            throw "The $workload smoke pairs are not balanced and rotated."
        }
    }
    if (($report.pairs | Where-Object { $_.off.correctness -ne 'passed' -or $_.auto.correctness -ne 'passed' }).Count -ne 0) {
        throw 'Decoder pipeline benchmark smoke did not pass every correctness check.'
    }
    $candidate = $report.pairs | Where-Object { $_.workload -eq 'candidate-heavy' } | Select-Object -First 1
    if ($candidate.auto.candidateOccurrences -ne 60 -or
        $candidate.auto.attemptedCandidates -ne 60 -or
        $candidate.auto.assessmentRecords -ne 60 -or
        $candidate.auto.publishedTextChildren -ne 20 -or
        $candidate.auto.decodedBinaryKnown -ne 10 -or
        $candidate.auto.decodedBinaryOpaque -ne 10 -or
        $candidate.auto.textRejected -ne 10 -or
        $candidate.auto.canonicalRejected -ne 10) {
        throw 'The candidate-heavy synthetic outcome schedule drifted.'
    }
    if (@($report.pairs | Where-Object { $_.workload -ne 'candidate-heavy' -and ($_.auto.candidateOccurrences -ne 0 -or $_.auto.publishedTextChildren -ne 0) }).Count -ne 0) {
        throw 'A no-candidate smoke workload reached the decoder.'
    }
    Write-Host 'Decoder pipeline benchmark smoke passed.'
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
