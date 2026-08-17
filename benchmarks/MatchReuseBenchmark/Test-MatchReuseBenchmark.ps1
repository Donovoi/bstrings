param(
    [int]$Records = 1000,
    [int]$Rounds = 2,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if ($Records -le 0 -or $Rounds -le 0) {
    throw 'Records and Rounds must be positive.'
}
$project = Join-Path $PSScriptRoot 'MatchReuseBenchmark.csproj'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('bstrings-match-reuse-' + [guid]::NewGuid().ToString('N'))
$workloads = @(
    'unique-no-match',
    'unique-match-heavy',
    'hot-repeat',
    'cold-repeat',
    'repeated-feature-unique-context',
    'base64',
    'oversized'
)
$rows = [System.Collections.Generic.List[object]]::new()

try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Match-reuse benchmark build failed.' }
    foreach ($workload in $workloads) {
        $input = Join-Path $workRoot ($workload + '.jsonl')
        $workloadRecords = if ($workload -eq 'oversized') {
            if ($Records -ge 1000000) { 2000 } else { [Math]::Min($Records, 20) }
        }
        else { $Records }
        dotnet run --project $project -c Release --no-build -- `
            --mode generate --input $input --workload $workload --records $workloadRecords
        if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed for $workload." }
        $patterns = if ($workload -eq 'base64') { 'all,b64_candidate' } else { 'all' }
        for ($round = 1; $round -le $Rounds; $round++) {
            $order = if ((($round + [Array]::IndexOf($workloads, $workload)) % 2) -eq 0) {
                @('off', 'on')
            }
            else {
                @('on', 'off')
            }
            $pair = @{}
            foreach ($variant in $order) {
                $output = Join-Path $workRoot ("$workload-$round-$variant.jsonl")
                $result = Join-Path $workRoot ("$workload-$round-$variant.json")
                dotnet run --project $project -c Release --no-build -- `
                    --mode run --input $input --output $output --result $result `
                    --variant $variant --patterns $patterns
                if ($LASTEXITCODE -ne 0) { throw "Benchmark failed for $workload round $round $variant." }
                $pair[$variant] = Get-Content -Raw -LiteralPath $result | ConvertFrom-Json
                Remove-Item -LiteralPath $output, $result -Force
            }
            if ($pair.off.inputSha256 -cne $pair.on.inputSha256 -or
                $pair.off.outputSha256 -cne $pair.on.outputSha256 -or
                $pair.off.outputBytes -ne $pair.on.outputBytes -or
                $pair.off.stats.inputRecords -ne $pair.on.stats.inputRecords -or
                $pair.off.stats.matchRecords -ne $pair.on.stats.matchRecords) {
                throw "Output parity failed for $workload round $round."
            }
            $rows.Add([pscustomobject]@{
                workload = $workload
                records = $workloadRecords
                round = $round
                order = ($order[0] + '-first')
                offWallSeconds = [double]$pair.off.wallSeconds
                onWallSeconds = [double]$pair.on.wallSeconds
                wallDeltaPercent = (([double]$pair.on.wallSeconds / [double]$pair.off.wallSeconds) - 1) * 100
                offCpuSeconds = [double]$pair.off.cpuSeconds
                onCpuSeconds = [double]$pair.on.cpuSeconds
                offAllocatedBytes = [long]$pair.off.managedAllocatedBytes
                onAllocatedBytes = [long]$pair.on.managedAllocatedBytes
                offPeakWorkingSetBytes = [long]$pair.off.peakWorkingSetBytes
                onPeakWorkingSetBytes = [long]$pair.on.peakWorkingSetBytes
                inputBytes = [long]$pair.on.inputBytes
                outputBytes = [long]$pair.on.outputBytes
                inputSha256 = [string]$pair.on.inputSha256
                outputSha256 = [string]$pair.on.outputSha256
                cacheHits = [long]$pair.on.stats.matchCacheHits
                cacheMisses = [long]$pair.on.stats.matchCacheMisses
                probationObservations = [long]$pair.on.stats.matchCacheProbationObservations
                probationBypasses = [long]$pair.on.stats.matchCacheProbationBypasses
                stores = [long]$pair.on.stats.matchCacheStores
                evictions = [long]$pair.on.stats.matchCacheEvictions
                preLookupBypasses = [long]$pair.on.stats.matchCachePreLookupBypasses
                postComputationBypasses = [long]$pair.on.stats.matchCachePostComputationBypasses
                peakLogicalBytes = [long]$pair.on.stats.matchCachePeakLogicalBytes
                peakEntries = [int]$pair.on.stats.matchCachePeakEntries
                exactOutputParity = $true
            })
        }
    }
    if ($OutputPath) {
        $parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        if (Test-Path -LiteralPath $OutputPath) { throw "Refusing to overwrite $OutputPath." }
        $rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding utf8NoBOM
    }
    $summaries = @($rows | Group-Object workload | ForEach-Object {
        [double[]]$sorted = @($_.Group | ForEach-Object { [double]$_.wallDeltaPercent })
        [Array]::Sort($sorted)
        $median = if (($sorted.Count % 2) -eq 1) {
            $sorted[[int]($sorted.Count / 2)]
        }
        else {
            ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2
        }
        Write-Host ("{0}: median wall delta {1:N3}%" -f $_.Name, $median)
        [pscustomobject]@{
            workload = $_.Name
            medianWallDeltaPercent = $median
            maximumWallDeltaPercent = ($sorted | Measure-Object -Maximum).Maximum
        }
    })
    $gateEligible = $Records -ge 1000000 -and $Rounds -ge 7
    if ($gateEligible) {
        $byName = @{}
        foreach ($summary in $summaries) { $byName[$summary.workload] = $summary }
        foreach ($name in @('unique-no-match', 'unique-match-heavy', 'repeated-feature-unique-context')) {
            if ($byName[$name].medianWallDeltaPercent -gt 3 -or
                $byName[$name].maximumWallDeltaPercent -gt 5) {
                throw "The $name unique-input performance gate failed."
            }
        }
        foreach ($name in @('cold-repeat', 'oversized')) {
            if ($byName[$name].maximumWallDeltaPercent -gt 5) {
                throw "The $name bypass/cold performance gate failed."
            }
        }
        if ($byName['hot-repeat'].medianWallDeltaPercent -gt -20) {
            throw 'The hot-repeat improvement gate failed.'
        }
        foreach ($row in $rows) {
            $allowedGrowth = [Math]::Max([long]($row.offPeakWorkingSetBytes * 0.10), 96MB)
            if (($row.onPeakWorkingSetBytes - $row.offPeakWorkingSetBytes) -gt $allowedGrowth) {
                throw "The peak-working-set gate failed for $($row.workload) round $($row.round)."
            }
            if ($row.peakLogicalBytes -gt 64MB) {
                throw "The logical-cache limit failed for $($row.workload) round $($row.round)."
            }
        }
        Write-Host 'The gate-eligible match-reuse benchmark passed every performance gate.'
    }
    else {
        Write-Host 'This bounded run did not activate the 1,000,000-record, seven-pair gate.'
    }
    Write-Host 'Match-reuse benchmark completed with exact output parity.'
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
