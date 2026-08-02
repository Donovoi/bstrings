[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DataRoot,

    [Parameter(Mandatory)]
    [string]$CurrentBstrings,

    [Parameter(Mandatory)]
    [string]$UpstreamBstrings,

    [Parameter(Mandatory)]
    [string]$BulkExtractor,

    [Parameter(Mandatory)]
    [string]$RunRoot,

    [ValidateSet('256m', '1g', '10g', '100g')]
    [string[]]$Tiers = @('256m', '1g', '10g', '100g'),

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
$toolNames = @('current', 'upstream', 'ripgrep', 'bulk_extractor')

foreach ($path in @($DataRoot, $CurrentBstrings, $UpstreamBstrings, $BulkExtractor)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required path does not exist: $path"
    }
}

$ripgrep = (Get-Command rg -ErrorAction Stop).Source
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

        Write-Output -NoEnumerate ([pscustomobject]@{
            ExitCode = $process.ExitCode
            ElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
        })
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

function Invoke-BulkExtractorMeasured {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string]$OutputDirectory,

        [Parameter(Mandatory)]
        [string]$InputPath
    )

    # The current MinGW build can stall during thread-pool shutdown when both
    # standard streams are anonymous pipes. A hidden child process with the
    # scanner's own -q switch preserves its normal console-handle behavior.
    $arguments = @(
        '-q',
        '-E',
        'email',
        '-o',
        ('"' + $OutputDirectory + '"'),
        ('"' + $InputPath + '"')
    )
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $FilePath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    $stopwatch.Stop()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        ElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
    }
}

function Get-FeatureCount {
    param([Parameter(Mandatory)][string]$Path)

    $count = 0L
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line.Length -gt 0 -and -not $line.StartsWith('#', [StringComparison]::Ordinal)) {
            $count++
        }
    }
    return $count
}

function Test-ExpectedMarkerSet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Paths,

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
    foreach ($path in $Paths) {
        foreach ($line in [System.IO.File]::ReadLines($path)) {
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

function Get-RotatedTools {
    param([int]$Shift)

    return 0..($toolNames.Count - 1) | ForEach-Object {
        $toolNames[($_ + $Shift) % $toolNames.Count]
    }
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
        Path = $inputPath
        SizeBytes = $input.Length
        ManifestSha256 = [string]$manifest.sha256
        VerifiedSha256 = $actualHash
        Records = [long]$manifest.records.total
        ExpectedMatches = [long]$manifest.records.expectedUrlOrEmailMatches
        TerminalRecords = [long]$manifest.records.terminal
    })
    $corpusValidation | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $validationPath -Encoding utf8

    $repetitions = [int]$tierDefinitions[$tier].Repetitions
    for ($run = 1; $run -le $repetitions; $run++) {
        $shift = ($tierIndex + $run - 1) % $toolNames.Count
        foreach ($tool in (Get-RotatedTools -Shift $shift)) {
            $prefix = '{0}-run{1:D2}-{2}' -f $tier, $run, $tool
            $stdoutPath = Join-Path $RunRoot "$prefix.stdout.log"
            $stderrPath = Join-Path $RunRoot "$prefix.stderr.log"
            $outputPath = Join-Path $RunRoot "$prefix.matches.txt"
            $expected = [long]$manifest.records.expectedUrlOrEmailMatches
            $bytesVerified = 0L
            $markerPaths = @()

            switch ($tool) {
                'current' {
                    $measurement = Invoke-ProcessMeasured -FilePath $CurrentBstrings -Arguments @(
                        '-f', $inputPath, '-a', '-u', 'false', '-m', '3', '-b', '16',
                        '--lr', $pattern, '--ro', '--off', '-s', '-o', $outputPath, '-q',
                        '--processor', 'cpu'
                    ) -StandardOutputPath $stdoutPath -StandardErrorPath $stderrPath
                    $actual = Get-LineCount -Path $outputPath
                    $bytesVerified = $input.Length
                    $markerPaths = @($outputPath)
                }
                'upstream' {
                    $measurement = Invoke-ProcessMeasured -FilePath $UpstreamBstrings -Arguments @(
                        '-f', $inputPath, '-a', '-u', 'false', '-m', '3', '-b', '16',
                        '--lr', $pattern, '--ro', '--off', '-s', '-o', $outputPath, '-q'
                    ) -StandardOutputPath $stdoutPath -StandardErrorPath $stderrPath
                    $actual = Get-LineCount -Path $outputPath
                    $bytesVerified = $input.Length
                    $markerPaths = @($outputPath)
                }
                'ripgrep' {
                    $measurement = Invoke-ProcessMeasured -FilePath $ripgrep -Arguments @(
                        '--text', '--pcre2', '--only-matching', '--byte-offset', '--no-filename',
                        '--regexp', $pattern, '--', $inputPath
                    ) -StandardOutputPath $outputPath -StandardErrorPath $stderrPath
                    [System.IO.File]::WriteAllText($stdoutPath, '')
                    $actual = Get-LineCount -Path $outputPath
                    $bytesVerified = $input.Length
                    $markerPaths = @($outputPath)
                }
                'bulk_extractor' {
                    $bulkOutput = Join-Path $RunRoot "$prefix.bulk"
                    [System.IO.File]::WriteAllText($stdoutPath, '')
                    [System.IO.File]::WriteAllText($stderrPath, '')
                    $measurement = Invoke-BulkExtractorMeasured -FilePath $BulkExtractor -OutputDirectory $bulkOutput -InputPath $inputPath
                    $emailCount = Get-FeatureCount -Path (Join-Path $bulkOutput 'email.txt')
                    $urlCount = Get-FeatureCount -Path (Join-Path $bulkOutput 'url.txt')
                    $actual = $emailCount + $urlCount
                    $report = [System.IO.File]::ReadAllText((Join-Path $bulkOutput 'report.xml'))
                    $bytesMatch = [regex]::Match($report, '<total_bytes>(\d+)</total_bytes>')
                    if (-not $bytesMatch.Success) {
                        throw "bulk_extractor report lacks total_bytes for $prefix"
                    }
                    $bytesVerified = [long]$bytesMatch.Groups[1].Value
                    $markerPaths = @(
                        (Join-Path $bulkOutput 'url.txt'),
                        (Join-Path $bulkOutput 'email.txt')
                    )
                    $outputPath = $bulkOutput
                }
                default {
                    throw "Unknown tool: $tool"
                }
            }

            if ($measurement.ExitCode -ne 0) {
                throw "$tool failed for $tier run $run with exit code $($measurement.ExitCode)"
            }
            if ($actual -ne $expected) {
                throw "$tool returned $actual matches; expected $expected for $tier run $run"
            }
            if ($bytesVerified -ne $input.Length) {
                throw "$tool verified $bytesVerified bytes; expected $($input.Length) for $tier run $run"
            }
            $markersVerified = Test-ExpectedMarkerSet `
                -Paths $markerPaths `
                -SizeBytes $input.Length `
                -SegmentBytes ([long]$manifest.segmentBytes)
            if (-not $markersVerified) {
                throw "$tool did not return the exact marker set for $tier run $run"
            }

            $result = [pscustomobject]@{
                Tier = $tier
                SizeBytes = $input.Length
                Tool = $tool
                Run = $run
                Order = $shift
                ElapsedSeconds = $measurement.ElapsedSeconds
                ThroughputMiBPerSecond = $input.Length / 1MB / $measurement.ElapsedSeconds
                ExpectedMatches = $expected
                ActualMatches = $actual
                MarkersVerified = $markersVerified
                BytesVerified = $bytesVerified
                ExitCode = $measurement.ExitCode
                OutputPath = $outputPath
            }
            $result | Export-Csv -LiteralPath $resultsPath -NoTypeInformation -Append
            $result | ConvertTo-Json -Compress
        }
    }

    $tierIndex++
}
