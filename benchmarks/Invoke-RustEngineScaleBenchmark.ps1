[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DataRoot,

    [Parameter(Mandatory)]
    [string]$Bstrings,

    [Parameter(Mandatory)]
    [string]$RunRoot,

    [ValidateSet('256m', '1g', '10g', '100g')]
    [string[]]$Tiers = @('1g', '10g'),

    [switch]$VerifyHashes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pattern = 'https://example\.com/scale/[0-9a-f]{16}|bench[0-9a-f]{16}@example\.com'
$tierDefinitions = [ordered]@{
    '256m' = @{ Repetitions = 5 }
    '1g' = @{ Repetitions = 5 }
    '10g' = @{ Repetitions = 3 }
    '100g' = @{ Repetitions = 1 }
}
$engines = @('dotnet', 'rust')

foreach ($path in @($DataRoot, $Bstrings)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required path does not exist: $path"
    }
}
if (Test-Path -LiteralPath $RunRoot) {
    throw "RunRoot must not already exist: $RunRoot"
}

New-Item -ItemType Directory -Path $RunRoot | Out-Null
$resultsPath = Join-Path $RunRoot 'raw-results.csv'
$validationPath = Join-Path $RunRoot 'corpus-validation.json'
$corpusValidation = [System.Collections.Generic.List[object]]::new()

function Invoke-ProcessMeasured {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$StandardOutputPath,

        [Parameter(Mandatory)]
        [string]$StandardErrorPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdout = [System.IO.File]::Create($StandardOutputPath)
    $stderr = [System.IO.File]::Create($StandardErrorPath)
    try {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        if (-not $process.Start()) {
            throw "Failed to start $FilePath"
        }

        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrTask = $process.StandardError.BaseStream.CopyToAsync($stderr)
        [void]$process.WaitForExit()
        [void]$stdoutTask.GetAwaiter().GetResult()
        [void]$stderrTask.GetAwaiter().GetResult()
        [void]$stdout.Flush($true)
        [void]$stderr.Flush($true)
        $stopwatch.Stop()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            ElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
        }
    }
    finally {
        $stdout.Dispose()
        $stderr.Dispose()
        $process.Dispose()
    }
}

function Get-LineCount {
    param([Parameter(Mandatory)][string]$Path)

    $count = 0L
    foreach ($null in [System.IO.File]::ReadLines($Path)) {
        $count++
    }
    return $count
}

function Get-CanonicalOutputHash {
    param([Parameter(Mandatory)][string]$Path)

    $orderedLines = [System.IO.File]::ReadAllLines($Path) | Sort-Object
    $bytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $orderedLines))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Test-ExpectedMarkerSet {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [long]$SizeBytes,

        [Parameter(Mandatory)]
        [long]$SegmentBytes
    )

    $expected = [System.Collections.Generic.Dictionary[string, int]]::new(
        [StringComparer]::Ordinal
    )
    $segmentCount = $SizeBytes / $SegmentBytes
    for ($segmentIndex = 0L; $segmentIndex -lt $segmentCount; $segmentIndex++) {
        $interiorAnchor = $segmentIndex * $SegmentBytes + $SegmentBytes / 2L
        $expected[$interiorAnchor.ToString('x16')] = 2

        $edgeAnchor = if ($segmentIndex + 1 -lt $segmentCount) {
            ($segmentIndex + 1) * $SegmentBytes
        }
        else {
            $SizeBytes - 1
        }
        $expected[$edgeAnchor.ToString('x16')] = 2
    }

    $actual = [System.Collections.Generic.Dictionary[string, int]]::new(
        [StringComparer]::Ordinal
    )
    $markerExpression = [regex]::new(
        '(?:/scale/|bench)([0-9a-f]{16})(?:@|\b)',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant
    )
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line.Length -eq 0 -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
            continue
        }

        $match = $markerExpression.Match($line)
        if (-not $match.Success) {
            return $false
        }

        $id = $match.Groups[1].Value
        $currentCount = 0
        if ($actual.TryGetValue($id, [ref]$currentCount)) {
            $actual[$id] = $currentCount + 1
        }
        else {
            $actual[$id] = 1
        }
    }

    if ($actual.Count -ne $expected.Count) {
        return $false
    }
    foreach ($pair in $expected.GetEnumerator()) {
        $count = 0
        if (-not $actual.TryGetValue($pair.Key, [ref]$count) -or $count -ne $pair.Value) {
            return $false
        }
    }
    return $true
}

$tierIndex = 0
foreach ($tier in $Tiers) {
    $inputPath = Join-Path $DataRoot "scale-$tier.bin"
    $manifestPath = "$inputPath.manifest.json"
    if (-not (Test-Path -LiteralPath $inputPath) -or -not (Test-Path -LiteralPath $manifestPath)) {
        throw "Corpus or manifest missing for tier $tier"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $input = Get-Item -LiteralPath $inputPath
    if ($input.Length -ne [long]$manifest.sizeBytes) {
        throw "Size mismatch for $inputPath"
    }

    $actualHash = $null
    if ($VerifyHashes) {
        $actualHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne [string]$manifest.sha256) {
            throw "SHA-256 mismatch for $inputPath"
        }
    }

    $corpusValidation.Add([pscustomobject]@{
        Tier = $tier
        SizeBytes = $input.Length
        ManifestSha256 = [string]$manifest.sha256
        VerifiedSha256 = $actualHash
        ExpectedMatches = [long]$manifest.records.expectedUrlOrEmailMatches
    })
    $corpusValidation | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $validationPath -Encoding utf8

    $repetitions = [int]$tierDefinitions[$tier].Repetitions
    for ($run = 1; $run -le $repetitions; $run++) {
        $orderedEngines = if ((($run + $tierIndex) -band 1) -eq 1) {
            $engines
        }
        else {
            @($engines[1], $engines[0])
        }
        $runHashes = @{}

        for ($order = 0; $order -lt $orderedEngines.Count; $order++) {
            $engine = $orderedEngines[$order]
            $prefix = '{0}-run{1:D2}-{2}' -f $tier, $run, $engine
            $stdoutPath = Join-Path $RunRoot "$prefix.stdout.log"
            $stderrPath = Join-Path $RunRoot "$prefix.stderr.log"
            $outputPath = Join-Path $RunRoot "$prefix.matches.txt"

            $measurement = Invoke-ProcessMeasured -FilePath $Bstrings -Arguments @(
                '-f', $inputPath, '-a', '-u', 'false', '-m', '3', '-b', '16',
                '--lr', $pattern, '--ro', '--off', '-s', '-o', $outputPath, '-q',
                '--processor', 'cpu', '--cpu-engine', $engine
            ) -StandardOutputPath $stdoutPath -StandardErrorPath $stderrPath

            if ($measurement.ExitCode -ne 0) {
                $details = [System.IO.File]::ReadAllText($stderrPath)
                throw "$prefix failed with exit code $($measurement.ExitCode): $details"
            }

            $expected = [long]$manifest.records.expectedUrlOrEmailMatches
            $actual = Get-LineCount -Path $outputPath
            if ($actual -ne $expected) {
                throw "$prefix returned $actual matches; expected $expected"
            }
            $markersVerified = Test-ExpectedMarkerSet -Path $outputPath -SizeBytes $input.Length -SegmentBytes ([long]$manifest.segmentBytes)
            if (-not $markersVerified) {
                throw "$prefix did not return the exact marker set"
            }

            $rawOutputHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $canonicalOutputHash = Get-CanonicalOutputHash -Path $outputPath
            $runHashes[$engine] = $canonicalOutputHash
            $result = [pscustomobject]@{
                Tier = $tier
                SizeBytes = $input.Length
                Engine = $engine
                Run = $run
                Order = $order
                ElapsedSeconds = $measurement.ElapsedSeconds
                ThroughputMiBPerSecond = $input.Length / 1MB / $measurement.ElapsedSeconds
                ExpectedMatches = $expected
                ActualMatches = $actual
                ExactMarkers = $markersVerified
                BytesVerified = $input.Length
                ExitCode = $measurement.ExitCode
                RawOutputSha256 = $rawOutputHash
                CanonicalOutputSha256 = $canonicalOutputHash
                StrictEngineSucceeded = $true
            }
            $result | Export-Csv -LiteralPath $resultsPath -NoTypeInformation -Append
            $result | ConvertTo-Json -Compress
        }

        if ($runHashes['dotnet'] -ne $runHashes['rust']) {
            throw "$tier run $run produced different canonical outputs between engines"
        }
    }

    $tierIndex++
}
