param(
    [int]$Records = 1000000,
    [int]$Rounds = 7,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if ($Records -le 0 -or $Rounds -le 0) {
    throw 'Records and Rounds must be positive.'
}

$project = Join-Path $PSScriptRoot 'EmailPrecisionBenchmark.csproj'
$workloads = @('valid', 'noise', 'mixed')
$rows = [Collections.Generic.List[object]]::new()

dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Email precision benchmark build failed.' }

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

        $expectedCandidate = if ($workload -eq 'noise') { 0L }
            elseif ($workload -eq 'mixed') { [long][Math]::Ceiling($Records / 2.0) }
            else { [long]$Records }
        if ($pair.baseline.matches -ne $Records -or
            $pair.candidate.matches -ne $expectedCandidate) {
            throw "Cardinality failed for $workload round $round."
        }
        if ($workload -eq 'valid' -and
            $pair.baseline.checksum -cne $pair.candidate.checksum) {
            throw "Valid-address parity failed for round $round."
        }
        if ($pair.baseline.ianaSha256 -cne $pair.candidate.ianaSha256) {
            throw "IANA identity drifted for $workload round $round."
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
            overheadNanosecondsPerRecord = (([double]$pair.candidate.wallMilliseconds -
                [double]$pair.baseline.wallMilliseconds) * 1000000 / $Records)
            baselineAllocatedBytes = [long]$pair.baseline.allocatedBytes
            candidateAllocatedBytes = [long]$pair.candidate.allocatedBytes
            baselineMatches = [long]$pair.baseline.matches
            candidateMatches = [long]$pair.candidate.matches
            reductionPercent = (1 - ([double]$pair.candidate.matches /
                [double]$pair.baseline.matches)) * 100
            baselineChecksum = [string]$pair.baseline.checksum
            candidateChecksum = [string]$pair.candidate.checksum
            ianaVersion = [string]$pair.candidate.ianaVersion
            ianaSha256 = [string]$pair.candidate.ianaSha256
        })
    }
}

$validRows = @($rows | Where-Object workload -eq 'valid')
$orderedDeltas = @($validRows.wallDeltaPercent | Sort-Object)
$medianDelta = [double]$orderedDeltas[[int][Math]::Floor($orderedDeltas.Count / 2)]
$maxDelta = [double]($orderedDeltas | Measure-Object -Maximum).Maximum
$orderedOverhead = @($validRows.overheadNanosecondsPerRecord | Sort-Object)
$medianOverhead = [double]$orderedOverhead[[int][Math]::Floor($orderedOverhead.Count / 2)]
$maxOverhead = [double]($orderedOverhead | Measure-Object -Maximum).Maximum
$allocationIncreasePerRecord = [double](($validRows | ForEach-Object {
    ($_.candidateAllocatedBytes - $_.baselineAllocatedBytes) / $_.records
} | Measure-Object -Maximum).Maximum)

$gateEligible = $Records -ge 1000000 -and $Rounds -ge 7
$gatePassed = -not $gateEligible -or (
    $medianOverhead -le 750.0 -and
    $maxOverhead -le 1000.0 -and
    $allocationIncreasePerRecord -le 64.0 -and
    @($rows | Where-Object { $_.workload -eq 'noise' -and $_.reductionPercent -lt 100.0 }).Count -eq 0
)

if ($OutputPath) {
    $rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding utf8
}

[pscustomobject]@{
    records = $Records
    rounds = $Rounds
    validMedianWallDeltaPercent = $medianDelta
    validMaximumWallDeltaPercent = $maxDelta
    validMedianOverheadNanosecondsPerRecord = $medianOverhead
    validMaximumOverheadNanosecondsPerRecord = $maxOverhead
    validMaximumAllocationIncreasePerRecord = $allocationIncreasePerRecord
    noiseReductionPercent = [double]($rows | Where-Object workload -eq 'noise' |
        Select-Object -First 1 -ExpandProperty reductionPercent)
    gateEligible = $gateEligible
    gatePassed = $gatePassed
} | Format-List

if (-not $gatePassed) { throw 'Email precision acceptance gate failed.' }
