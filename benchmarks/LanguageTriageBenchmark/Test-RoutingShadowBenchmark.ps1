Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPath = Join-Path $PSScriptRoot 'LanguageTriageBenchmark.csproj'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bstrings-routing-shadow-benchmark-test-' + [Guid]::NewGuid().ToString('N')
)
try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $output = @(& dotnet build $projectPath -c Release 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Routing-shadow benchmark build failed: $($output -join [Environment]::NewLine)"
    }
    $scenarios = @(
        @{ corpus = 'mixed'; records = 28; retained = 20; prospective = 8 },
        @{ corpus = 'natural'; records = 32; retained = 32; prospective = 0 },
        @{ corpus = 'machine'; records = 32; retained = 20; prospective = 12 },
        @{ corpus = 'short'; records = 32; retained = 24; prospective = 8 },
        @{ corpus = 'max'; records = 32; retained = 32; prospective = 0 },
        @{ corpus = 'encoded'; records = 32; retained = 24; prospective = 8 },
        @{ corpus = 'provenance'; records = 32; retained = 24; prospective = 8 }
    )
    foreach ($scenario in $scenarios) {
        $outputPath = Join-Path $testRoot ("routing-shadow-$($scenario.corpus).csv")
        $output = @(
            & dotnet run --project $projectPath -c Release --no-build -- `
                --experiment routing-shadow `
                --routing-corpus $scenario.corpus `
                --records $scenario.records `
                --duplicate-percent 0 `
                --rounds 1 `
                --mode accurate `
                --output $outputPath 2>&1
        )
        if ($LASTEXITCODE -ne 0) {
            throw "Routing-shadow '$($scenario.corpus)' smoke failed: $($output -join [Environment]::NewLine)"
        }
        $rows = @(Import-Csv -LiteralPath $outputPath)
        $disabled = @($rows | Where-Object variant -CEQ 'routing-disabled')
        $shadow = @($rows | Where-Object variant -CEQ 'routing-shadow')
        if ($rows.Count -ne 2 -or $disabled.Count -ne 1 -or $shadow.Count -ne 1) {
            throw "Routing-shadow '$($scenario.corpus)' did not emit one exact A/B pair."
        }
        if (
            $shadow[0].routingCorpus -CNE $scenario.corpus -or
            $disabled[0].candidatesBytes -CNE $shadow[0].candidatesBytes -or
            $disabled[0].candidatesSha256 -CNE $shadow[0].candidatesSha256 -or
            $disabled[0].assessmentProjectionBytes -CNE $shadow[0].assessmentProjectionBytes -or
            $disabled[0].assessmentProjectionSha256 -CNE $shadow[0].assessmentProjectionSha256
        ) {
            throw "Routing-shadow '$($scenario.corpus)' did not preserve exact parity."
        }
        if (
            [int64]$disabled[0].routingObjectCount -ne 0 -or
            [int64]$shadow[0].routingObjectCount -ne $scenario.records -or
            [int64]$disabled[0].sampledPeakWorkingSetBytes -le 0 -or
            [int64]$shadow[0].sampledPeakWorkingSetBytes -le 0 -or
            [int64]$shadow[0].assessmentBytesDeltaFromDisabled -le 0 -or
            [int64]$shadow[0].assessmentBytesDeltaFromDisabled -gt ($scenario.records * 256) -or
            [int64]$shadow[0].translationRoutingRetained -ne $scenario.retained -or
            [int64]$shadow[0].translationRoutingProspectiveBypasses -ne $scenario.prospective -or
            [int64]$shadow[0].translationRoutingUnknown -ne 0
        ) {
            throw "Routing-shadow '$($scenario.corpus)' routing counts or bounds drifted."
        }
    }

    Write-Host 'Routing-shadow benchmark corpus smokes passed.'
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
