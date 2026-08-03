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

    [ValidateRange(1, 20)]
    [int]$Repetitions = 3,

    [string[]]$Patterns = @('all'),

    [switch]$VerifyHashes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
$summaryPath = Join-Path $RunRoot 'summary.csv'

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

function Invoke-BulkExtractorMeasured {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string]$PatternFile,

        [Parameter(Mandatory)]
        [string]$OutputDirectory,

        [Parameter(Mandatory)]
        [string]$InputPath
    )

    # The current MinGW build can stall during thread-pool shutdown when both
    # standard streams are anonymous pipes, so keep its native console handles.
    $arguments = @(
        '-q',
        '-E',
        'find',
        '--find-case-sensitive',
        '-F',
        ('"' + $PatternFile + '"'),
        '-o',
        ('"' + $OutputDirectory + '"'),
        ('"' + $InputPath + '"')
    )
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process `
        -FilePath $FilePath `
        -ArgumentList $arguments `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    $stopwatch.Stop()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        ElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
    }
}

function Get-InlinePattern {
    param([Parameter(Mandatory)]$Manifest)

    $prefix = ''
    $options = [string]$Manifest.pattern.options
    if ($options -match 'IgnoreCase') {
        $prefix += '(?i)'
    }
    if ($options -match 'IgnorePatternWhitespace') {
        $prefix += '(?x)'
    }
    return $prefix + [string]$Manifest.pattern.pattern
}

function Get-NormalizedValue {
    param(
        [Parameter(Mandatory)]
        [string]$RawValue,

        [Parameter(Mandatory)]
        $Manifest,

        [Parameter(Mandatory)]
        [bool]$AlreadyNormalized
    )

    if ($AlreadyNormalized -or [string]::IsNullOrWhiteSpace([string]$Manifest.pattern.outputGroup)) {
        return $RawValue
    }

    $options = [Text.RegularExpressions.RegexOptions]::CultureInvariant
    $optionText = [string]$Manifest.pattern.options
    if ($optionText -match 'IgnoreCase') {
        $options = $options -bor [Text.RegularExpressions.RegexOptions]::IgnoreCase
    }
    if ($optionText -match 'IgnorePatternWhitespace') {
        $options = $options -bor [Text.RegularExpressions.RegexOptions]::IgnorePatternWhitespace
    }
    $regex = [regex]::new([string]$Manifest.pattern.pattern, $options)
    $match = $regex.Match($RawValue)
    if (-not $match.Success) {
        return "__UNNORMALIZED__:$RawValue"
    }
    return $match.Groups[[string]$Manifest.pattern.outputGroup].Value
}

function Get-ExpectedRecordKeys {
    param([Parameter(Mandatory)]$Manifest)

    return @(
        $Manifest.expected.records | ForEach-Object {
            '{0}`t{1}' -f ([long]$_.offset), [string]$_.value
        } | Sort-Object
    )
}

function Get-BstringsRecordKeys {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        $Manifest,

        [Parameter(Mandatory)]
        [bool]$AlreadyNormalized
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    return @(
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            $parts = $line -split "`t", 2
            if ($parts.Count -ne 2 -or $parts[1] -notmatch '^~0x([0-9A-Fa-f]+)') {
                "__UNPARSED__`t$line"
                continue
            }
            $offset = [Convert]::ToInt64($Matches[1], 16)
            $value = Get-NormalizedValue `
                -RawValue $parts[0] `
                -Manifest $Manifest `
                -AlreadyNormalized $AlreadyNormalized
            '{0}`t{1}' -f $offset, $value
        }
    ) | Sort-Object
}

function Get-RipgrepRecordKeys {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        $Manifest
    )

    return @(
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            if ($line -notmatch '^(\d+):(.*)$') {
                "__UNPARSED__`t$line"
                continue
            }
            $value = Get-NormalizedValue `
                -RawValue $Matches[2] `
                -Manifest $Manifest `
                -AlreadyNormalized $false
            '{0}`t{1}' -f [long]$Matches[1], $value
        }
    ) | Sort-Object
}

function Get-BulkExtractorRecordKeys {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        $Manifest
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    return @(
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            if ($line.Length -eq 0 -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
                continue
            }
            $parts = $line -split "`t", 3
            $offset = 0L
            if ($parts.Count -lt 2 -or -not [long]::TryParse($parts[0], [ref]$offset)) {
                "__UNPARSED__`t$line"
                continue
            }
            $value = Get-NormalizedValue `
                -RawValue $parts[1] `
                -Manifest $Manifest `
                -AlreadyNormalized $false
            '{0}`t{1}' -f $offset, $value
        }
    ) | Sort-Object
}

function Test-ExactRecords {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Actual
    )

    return @(Compare-Object -ReferenceObject $Expected -DifferenceObject $Actual).Count -eq 0
}

function Get-Median {
    param([Parameter(Mandatory)][double[]]$Values)

    $ordered = @($Values | Sort-Object)
    if ($ordered.Count % 2 -eq 1) {
        return $ordered[[int][Math]::Floor($ordered.Count / 2)]
    }
    return ($ordered[$ordered.Count / 2 - 1] + $ordered[$ordered.Count / 2]) / 2
}

$manifestPaths = @(Get-ChildItem -LiteralPath $DataRoot -Filter '*.manifest.json' | Sort-Object Name)
if ($Patterns.Count -ne 1 -or $Patterns[0] -ne 'all') {
    $selected = [Collections.Generic.HashSet[string]]::new(
        $Patterns,
        [StringComparer]::OrdinalIgnoreCase
    )
    $manifestPaths = @(
        $manifestPaths | Where-Object {
            $manifest = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
            $selected.Contains([string]$manifest.pattern.name)
        }
    )
}
if ($manifestPaths.Count -eq 0) {
    throw 'No pattern manifests were selected.'
}

$corpusValidation = [Collections.Generic.List[object]]::new()
$patternIndex = 0
foreach ($manifestPath in $manifestPaths) {
    $manifest = Get-Content -LiteralPath $manifestPath.FullName -Raw | ConvertFrom-Json
    $patternName = [string]$manifest.pattern.name
    $corpusEncoding = if ($null -ne $manifest.corpus.PSObject.Properties['encoding']) {
        [string]$manifest.corpus.encoding
    }
    else {
        'ascii'
    }
    $encodingArguments = if ($corpusEncoding -eq 'utf16le') {
        @('-a', 'false', '-u')
    }
    else {
        @('-a', '-u', 'false')
    }
    $inputPath = Join-Path $DataRoot ([string]$manifest.corpus.outputFile)
    if (-not (Test-Path -LiteralPath $inputPath)) {
        throw "Missing corpus: $inputPath"
    }
    $input = Get-Item -LiteralPath $inputPath
    if ($input.Length -ne [long]$manifest.corpus.sizeBytes) {
        throw "Size mismatch for $inputPath"
    }

    $actualHash = $null
    if ($VerifyHashes) {
        $actualHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne [string]$manifest.corpus.sha256) {
            throw "SHA-256 mismatch for $inputPath"
        }
    }

    $corpusValidation.Add([pscustomobject]@{
        Pattern = $patternName
        Path = $inputPath
        SizeBytes = $input.Length
        ExpectedRecords = [long]$manifest.expected.count
        Encoding = $corpusEncoding
        ManifestSha256 = [string]$manifest.corpus.sha256
        VerifiedSha256 = $actualHash
    })
    $corpusValidation | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $validationPath -Encoding utf8

    $expected = Get-ExpectedRecordKeys -Manifest $manifest
    $comparisonPattern = Get-InlinePattern -Manifest $manifest
    $patternFile = Join-Path $RunRoot "$patternName.re2.txt"
    [System.IO.File]::WriteAllText($patternFile, $comparisonPattern + "`n", [Text.UTF8Encoding]::new($false))

    for ($run = 1; $run -le $Repetitions; $run++) {
        $shift = ($patternIndex + $run - 1) % $toolNames.Count
        $orderedTools = 0..($toolNames.Count - 1) | ForEach-Object {
            $toolNames[($_ + $shift) % $toolNames.Count]
        }
        foreach ($tool in $orderedTools) {
            $prefix = '{0}-run{1:D2}-{2}' -f $patternName, $run, $tool
            $stdoutPath = Join-Path $RunRoot "$prefix.stdout.log"
            $stderrPath = Join-Path $RunRoot "$prefix.stderr.log"
            $outputPath = Join-Path $RunRoot "$prefix.matches.txt"
            $unsupported = $false
            $unsupportedReason = $null
            $actual = @()

            switch ($tool) {
                'current' {
                    $arguments = @(
                        '-f', $inputPath
                    ) + $encodingArguments + @(
                        '-m', '3', '-b', '16',
                        '--lr', $patternName, '--ro', '--off', '-s', '-o', $outputPath, '-q',
                        '--processor', 'cpu'
                    )
                    $measurement = Invoke-ProcessMeasured -FilePath $CurrentBstrings -Arguments $arguments `
                        -StandardOutputPath $stdoutPath -StandardErrorPath $stderrPath
                    if ($measurement.ExitCode -eq 0) {
                        $actual = @(
                            Get-BstringsRecordKeys `
                                -Path $outputPath `
                                -Manifest $manifest `
                                -AlreadyNormalized $true
                        )
                    }
                }
                'upstream' {
                    $arguments = @(
                        '-f', $inputPath
                    ) + $encodingArguments + @(
                        '-m', '3', '-b', '16',
                        '--lr', $comparisonPattern, '--ro', '--off', '-s', '-o', $outputPath, '-q'
                    )
                    $measurement = Invoke-ProcessMeasured -FilePath $UpstreamBstrings -Arguments $arguments `
                        -StandardOutputPath $stdoutPath -StandardErrorPath $stderrPath
                    if ($measurement.ExitCode -eq 0) {
                        $actual = @(
                            Get-BstringsRecordKeys `
                                -Path $outputPath `
                                -Manifest $manifest `
                                -AlreadyNormalized $false
                        )
                    }
                }
                'ripgrep' {
                    if ($corpusEncoding -eq 'utf16le') {
                        [System.IO.File]::WriteAllText($stdoutPath, '')
                        [System.IO.File]::WriteAllText($stderrPath, '')
                        [System.IO.File]::WriteAllText($outputPath, '')
                        $measurement = [pscustomobject]@{ ExitCode = -1; ElapsedSeconds = 0.0 }
                        $unsupported = $true
                        $unsupportedReason = 'ripgrep does not implement bstrings UTF-16LE extracted-string semantics.'
                    }
                    else {
                        $measurement = Invoke-ProcessMeasured -FilePath $ripgrep -Arguments @(
                            '--text', '--pcre2', '--only-matching', '--byte-offset', '--no-filename',
                            '--regexp', $comparisonPattern, '--', $inputPath
                        ) -StandardOutputPath $outputPath -StandardErrorPath $stderrPath
                        [System.IO.File]::WriteAllText($stdoutPath, '')
                        if ($measurement.ExitCode -in @(0, 1)) {
                            $actual = @(Get-RipgrepRecordKeys -Path $outputPath -Manifest $manifest)
                        }
                    }
                }
                'bulk_extractor' {
                    [System.IO.File]::WriteAllText($stdoutPath, '')
                    [System.IO.File]::WriteAllText($stderrPath, '')
                    if ($corpusEncoding -eq 'utf16le') {
                        $measurement = [pscustomobject]@{ ExitCode = -1; ElapsedSeconds = 0.0 }
                        $unsupported = $true
                        $unsupportedReason = 'bulk_extractor find/RE2 does not implement bstrings UTF-16LE extracted-string semantics.'
                    }
                    else {
                        $bulkOutput = Join-Path $RunRoot "$prefix.bulk"
                        $measurement = Invoke-BulkExtractorMeasured `
                            -FilePath $BulkExtractor `
                            -PatternFile $patternFile `
                            -OutputDirectory $bulkOutput `
                            -InputPath $inputPath
                        $outputPath = Join-Path $bulkOutput 'find.txt'
                        if ($measurement.ExitCode -eq 0) {
                            $actual = @(
                                Get-BulkExtractorRecordKeys -Path $outputPath -Manifest $manifest
                            )
                        }
                        else {
                            $unsupported = $true
                            $unsupportedReason = 'RE2 rejected the pattern or the find scanner failed; see bulk_extractor.log.'
                        }
                    }
                }
                default {
                    throw "Unknown tool: $tool"
                }
            }

            $acceptedExit =
                ($tool -eq 'ripgrep' -and $measurement.ExitCode -in @(0, 1)) `
                -or ($tool -ne 'ripgrep' -and $measurement.ExitCode -eq 0)
            $exact = $acceptedExit -and -not $unsupported -and (Test-ExactRecords -Expected $expected -Actual $actual)
            $result = [pscustomobject]@{
                Pattern = $patternName
                SizeBytes = $input.Length
                Tool = $tool
                Run = $run
                Order = $shift
                ElapsedSeconds = $measurement.ElapsedSeconds
                ThroughputMiBPerSecond = if ($measurement.ElapsedSeconds -gt 0) {
                    $input.Length / 1MB / $measurement.ElapsedSeconds
                }
                else {
                    $null
                }
                ExpectedMatches = $expected.Count
                ActualMatches = $actual.Count
                ExactRecords = $exact
                Comparable = $exact
                Unsupported = $unsupported
                UnsupportedReason = $unsupportedReason
                ExitCode = $measurement.ExitCode
                OutputPath = $outputPath
            }
            $result | Export-Csv -LiteralPath $resultsPath -NoTypeInformation -Append
            $result | ConvertTo-Json -Compress
        }
    }

    $patternIndex++
}

$rawResults = @(Import-Csv -LiteralPath $resultsPath)
$summary = foreach ($group in $rawResults | Group-Object Pattern, Tool) {
    $first = $group.Group[0]
    $exactRuns = @($group.Group | Where-Object ExactRecords -eq 'True')
    $median = if ($exactRuns.Count -eq $Repetitions) {
        Get-Median -Values @($exactRuns | ForEach-Object { [double]$_.ElapsedSeconds })
    }
    else {
        $null
    }
    [pscustomobject]@{
        Pattern = $first.Pattern
        Tool = $first.Tool
        ExactRuns = $exactRuns.Count
        Repetitions = $Repetitions
        Comparable = $exactRuns.Count -eq $Repetitions
        MedianSeconds = $median
        MedianMiBPerSecond = if ($null -ne $median) {
            [long]$first.SizeBytes / 1MB / $median
        }
        else {
            $null
        }
    }
}
$summary | Sort-Object Pattern, Tool | Export-Csv -LiteralPath $summaryPath -NoTypeInformation

$currentFailures = @(
    $summary | Where-Object { $_.Tool -eq 'current' -and -not $_.Comparable }
)
if ($currentFailures.Count -gt 0) {
    throw "Current bstrings failed exact validation for: $($currentFailures.Pattern -join ', ')"
}

$summary | Sort-Object Pattern, Tool | Format-Table -AutoSize
