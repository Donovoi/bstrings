param(
    [int]$Records = 100000,
    [int]$Rounds = 7,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if ($Records -le 0 -or $Rounds -le 0) {
    throw 'Records and Rounds must be positive.'
}

$project = Join-Path $PSScriptRoot 'CatalogPrecisionBenchmark.csproj'
$workloads = @('valid', 'confusion', 'mixed')
$rows = [Collections.Generic.List[object]]::new()

dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Catalog precision benchmark build failed.' }

foreach ($workload in $workloads) {
    for ($round = 1; $round -le $Rounds; $round++) {
        $order = if ((($round + [Array]::IndexOf($workloads, $workload)) % 2) -eq 0) {
            @('baseline', 'candidate')
        }
        else {
            @('candidate', 'baseline')
        }
        $pair = @{}
        foreach ($variant in $order) {
            $json = dotnet run --project $project -c Release --no-build -- `
                --variant $variant --workload $workload --records $Records
            if ($LASTEXITCODE -ne 0) { throw "$workload $variant failed." }
            $pair[$variant] = $json | ConvertFrom-Json
        }

        $expectedBaseline = [long]$Records
        $expectedCandidate = if ($workload -eq 'confusion') { 0L }
            elseif ($workload -eq 'mixed') { [long][Math]::Ceiling($Records / 2.0) }
            else { [long]$Records }
        if ($pair.baseline.patternCount -ne 77 -or $pair.candidate.patternCount -ne 74) {
            throw "Pattern inventory failed for $workload round $round."
        }
        if ($pair.baseline.matches -ne $expectedBaseline -or
            $pair.candidate.matches -ne $expectedCandidate) {
            throw "Cardinality failed for $workload round $round."
        }
        if ($workload -eq 'valid' -and
            $pair.baseline.checksum -cne $pair.candidate.checksum) {
            throw "Valid-value parity failed for round $round."
        }
        if ($pair.baseline.coverageMatches -ne $pair.candidate.coverageMatches -or
            $pair.baseline.coverageChecksum -cne $pair.candidate.coverageChecksum) {
            throw "Strict-plus-candidate coverage parity failed for $workload round $round."
        }

        $rows.Add([pscustomobject]@{
            workload = $workload
            records = $Records
            round = $round
            order = ($order[0] + '-first')
            baselineWallMilliseconds = [double]$pair.baseline.wallMilliseconds
            candidateWallMilliseconds = [double]$pair.candidate.wallMilliseconds
            wallDeltaPercent = (([double]$pair.candidate.wallMilliseconds /
                [double]$pair.baseline.wallMilliseconds) - 1) * 100
            baselinePeakWorkingSetBytes = [long]$pair.baseline.peakWorkingSetBytes
            candidatePeakWorkingSetBytes = [long]$pair.candidate.peakWorkingSetBytes
            peakWorkingSetDeltaPercent = (([double]$pair.candidate.peakWorkingSetBytes /
                [double]$pair.baseline.peakWorkingSetBytes) - 1) * 100
            baselineAllocatedBytes = [long]$pair.baseline.allocatedBytes
            candidateAllocatedBytes = [long]$pair.candidate.allocatedBytes
            baselineMatches = [long]$pair.baseline.matches
            candidateMatches = [long]$pair.candidate.matches
            reductionPercent = if ($pair.baseline.matches -eq 0) { 0.0 } else {
                (1 - ([double]$pair.candidate.matches / [double]$pair.baseline.matches)) * 100
            }
            baselineChecksum = [string]$pair.baseline.checksum
            candidateChecksum = [string]$pair.candidate.checksum
            baselineCoverageMatches = [long]$pair.baseline.coverageMatches
            candidateCoverageMatches = [long]$pair.candidate.coverageMatches
            baselineCoverageChecksum = [string]$pair.baseline.coverageChecksum
            candidateCoverageChecksum = [string]$pair.candidate.coverageChecksum
        })
    }
}

$summaries = foreach ($workload in $workloads) {
    $samples = @($rows | Where-Object workload -eq $workload)
    $wall = @($samples.wallDeltaPercent | Sort-Object)
    $peak = @($samples.peakWorkingSetDeltaPercent | Sort-Object)
    [pscustomobject]@{
        workload = $workload
        medianWallDeltaPercent = [double]$wall[[int][Math]::Floor($wall.Count / 2)]
        medianPeakWorkingSetDeltaPercent = [double]$peak[[int][Math]::Floor($peak.Count / 2)]
    }
}

$gateEligible = $Records -ge 100000 -and $Rounds -ge 7
$gatePassed = -not $gateEligible -or (
    @($summaries | Where-Object {
        $_.medianWallDeltaPercent -gt 5.0 -or
        $_.medianPeakWorkingSetDeltaPercent -gt 5.0
    }).Count -eq 0 -and
    @($rows | Where-Object {
        $_.workload -eq 'confusion' -and $_.reductionPercent -lt 100.0
    }).Count -eq 0
)

if ($OutputPath) {
    $rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding utf8
}

$summaries | Format-Table -AutoSize
[pscustomobject]@{
    records = $Records
    rounds = $Rounds
    gateEligible = $gateEligible
    gatePassed = $gatePassed
} | Format-List

if (-not $gatePassed) { throw 'Catalog precision acceptance gate failed.' }
