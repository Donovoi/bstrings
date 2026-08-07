[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DataRoot,

    [Parameter(Mandatory)]
    [string]$BstringsDll,

    [Parameter(Mandatory)]
    [string]$RunRoot,

    [string[]]$Tiers = @('256m', '512m', '1g', '2g', '4g', '8g', '16g'),

    [ValidateSet('cpu', 'gpu', 'hybrid')]
    [string[]]$Modes = @('cpu', 'gpu', 'hybrid'),

    [ValidateRange(1, 20)]
    [int]$Repetitions = 3,

    [ValidateRange(0, 1024)]
    [int]$ChunkSizeMB = 0,

    [switch]$IncludeUnicode,

    [switch]$VerifyHashes,

    [ValidateRange(50, 5000)]
    [int]$GpuSampleIntervalMs = 100
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pattern = 'https://example\.com/scale/[0-9a-f]{16}|bench[0-9a-f]{16}@example\.com'
$logicalProcessors = [Environment]::ProcessorCount
$modeList = @($Modes)
$tierList = @($Tiers)

foreach ($path in @($DataRoot, $BstringsDll)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required path does not exist: $path"
    }
}
if (Test-Path -LiteralPath $RunRoot) {
    throw "RunRoot must not already exist: $RunRoot"
}

New-Item -ItemType Directory -Path $RunRoot | Out-Null
$resultsPath = Join-Path $RunRoot 'raw-results.csv'
$environmentPath = Join-Path $RunRoot 'environment.json'

$processor = Get-CimInstance Win32_Processor | Select-Object -First 1
$gpuInventory = & nvidia-smi `
    --query-gpu=index,name,driver_version,compute_cap,memory.total `
    --format=csv,noheader,nounits
[pscustomobject]@{
    capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    os = [Environment]::OSVersion.VersionString
    runtime = [Environment]::Version.ToString()
    processor = $processor.Name
    physicalCores = $processor.NumberOfCores
    logicalProcessors = $logicalProcessors
    gpu = @($gpuInventory)
    includeUnicode = [bool]$IncludeUnicode
    chunkSizeMB = $ChunkSizeMB
    repetitions = $Repetitions
    modes = $modeList
    tiers = $tierList
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $environmentPath -Encoding utf8

function Get-LineCount {
    param([Parameter(Mandatory)][string]$Path)

    $count = 0L
    foreach ($null in [IO.File]::ReadLines($Path)) {
        $count++
    }
    return $count
}

function Get-CanonicalOutputHash {
    param([Parameter(Mandatory)][string]$Path)

    $orderedLines = [IO.File]::ReadAllLines($Path) | Sort-Object
    $bytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $orderedLines))
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)
    ).ToLowerInvariant()
}

function Test-ExpectedMarkerSet {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$SizeBytes,
        [Parameter(Mandatory)][long]$SegmentBytes
    )

    $expected = [Collections.Generic.Dictionary[string, int]]::new(
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

    $actual = [Collections.Generic.Dictionary[string, int]]::new(
        [StringComparer]::Ordinal
    )
    $markerExpression = [regex]::new(
        '(?:/scale/|bench)([0-9a-f]{16})(?:@|\b)',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant
    )
    foreach ($line in [IO.File]::ReadLines($Path)) {
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

function Stop-MonitorProcess {
    param([Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }
    if (-not $Process.HasExited) {
        $Process.Kill($true)
    }
    $Process.WaitForExit()
}

function Get-GpuSummary {
    param([string]$RawText)

    $samples = [Collections.Generic.List[object]]::new()
    foreach ($line in ($RawText -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $parts = $line.Split(',')
        if ($parts.Count -ne 5) {
            continue
        }
        $gpuUtil = 0.0
        $memoryUsed = 0.0
        $power = 0.0
        $clock = 0.0
        if (
            -not [double]::TryParse($parts[1].Trim(), [ref]$gpuUtil) -or
            -not [double]::TryParse($parts[2].Trim(), [ref]$memoryUsed) -or
            -not [double]::TryParse($parts[3].Trim(), [ref]$power) -or
            -not [double]::TryParse($parts[4].Trim(), [ref]$clock)
        ) {
            continue
        }
        $samples.Add([pscustomobject]@{
            Timestamp = $parts[0].Trim()
            GpuUtilPercent = $gpuUtil
            MemoryUsedMiB = $memoryUsed
            PowerW = $power
            SmClockMHz = $clock
        })
    }

    if ($samples.Count -eq 0) {
        return [pscustomobject]@{
            Samples = 0
            AverageGpuPercent = 0.0
            PeakGpuPercent = 0.0
            PeakMemoryMiB = 0.0
            AveragePowerW = 0.0
            PeakSmClockMHz = 0.0
        }
    }

    return [pscustomobject]@{
        Samples = $samples.Count
        AverageGpuPercent = ($samples | Measure-Object GpuUtilPercent -Average).Average
        PeakGpuPercent = ($samples | Measure-Object GpuUtilPercent -Maximum).Maximum
        PeakMemoryMiB = ($samples | Measure-Object MemoryUsedMiB -Maximum).Maximum
        AveragePowerW = ($samples | Measure-Object PowerW -Average).Average
        PeakSmClockMHz = ($samples | Measure-Object SmClockMHz -Maximum).Maximum
    }
}

function Invoke-BstringsMeasured {
    param(
        [Parameter(Mandatory)][string]$InputPath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$Mode,
        [Parameter(Mandatory)][string]$StdoutPath,
        [Parameter(Mandatory)][string]$StderrPath,
        [Parameter(Mandatory)][string]$GpuLogPath
    )

    $gpuStartInfo = [Diagnostics.ProcessStartInfo]::new()
    $gpuStartInfo.FileName = 'nvidia-smi'
    $gpuStartInfo.UseShellExecute = $false
    $gpuStartInfo.CreateNoWindow = $true
    $gpuStartInfo.RedirectStandardOutput = $true
    $gpuStartInfo.RedirectStandardError = $true
    foreach ($argument in @(
        '--query-gpu=timestamp,utilization.gpu,memory.used,power.draw,clocks.current.sm',
        '--format=csv,noheader,nounits',
        '-lms',
        [string]$GpuSampleIntervalMs
    )) {
        [void]$gpuStartInfo.ArgumentList.Add($argument)
    }

    $gpuProcess = [Diagnostics.Process]::new()
    $gpuProcess.StartInfo = $gpuStartInfo
    [void]$gpuProcess.Start()
    $gpuStdoutTask = $gpuProcess.StandardOutput.ReadToEndAsync()
    $gpuStderrTask = $gpuProcess.StandardError.ReadToEndAsync()

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $arguments = @(
        $BstringsDll,
        '-f', $InputPath,
        '-a',
        '-u', ([bool]$IncludeUnicode).ToString().ToLowerInvariant(),
        '-m', '3',
        '-b', [string]$ChunkSizeMB,
        '--lr', $pattern,
        '--ro',
        '--off',
        '-s',
        '-o', $OutputPath,
        '-q',
        '--processor', $Mode,
        '--cpu-engine', 'dotnet'
    )
    foreach ($argument in $arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        if (-not $process.Start()) {
            throw 'Failed to start bstrings.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $peakWorkingSet = 0L
        while (-not $process.WaitForExit(50)) {
            $process.Refresh()
            $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.WorkingSet64)
        }
        $process.Refresh()
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.PeakWorkingSet64)
        $stopwatch.Stop()

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $cpuSeconds = $process.TotalProcessorTime.TotalSeconds
        $exitCode = $process.ExitCode
    }
    finally {
        Stop-MonitorProcess -Process $gpuProcess
    }

    $gpuText = $gpuStdoutTask.GetAwaiter().GetResult()
    $gpuError = $gpuStderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText($StdoutPath, $stdout)
    [IO.File]::WriteAllText($StderrPath, $stderr)
    [IO.File]::WriteAllText($GpuLogPath, $gpuText + $gpuError)
    $gpuSummary = Get-GpuSummary -RawText $gpuText

    return [pscustomobject]@{
        ExitCode = $exitCode
        ElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
        ProcessCpuSeconds = $cpuSeconds
        AverageCpuPercent = 100.0 * $cpuSeconds / (
            $stopwatch.Elapsed.TotalSeconds * $logicalProcessors
        )
        PeakWorkingSetBytes = $peakWorkingSet
        GpuSamples = $gpuSummary.Samples
        AverageGpuPercent = $gpuSummary.AverageGpuPercent
        PeakGpuPercent = $gpuSummary.PeakGpuPercent
        PeakGpuMemoryMiB = $gpuSummary.PeakMemoryMiB
        AverageGpuPowerW = $gpuSummary.AveragePowerW
        PeakGpuSmClockMHz = $gpuSummary.PeakSmClockMHz
    }
}

$tierIndex = 0
foreach ($tier in $tierList) {
    $inputPath = Join-Path $DataRoot "scale-$tier.bin"
    $manifestPath = "$inputPath.manifest.json"
    if (-not (Test-Path -LiteralPath $inputPath) -or -not (Test-Path -LiteralPath $manifestPath)) {
        throw "Corpus or manifest is missing for tier $tier."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $input = Get-Item -LiteralPath $inputPath
    if ($input.Length -ne [long]$manifest.sizeBytes) {
        throw "Size mismatch for $inputPath"
    }
    if ($VerifyHashes) {
        $actualHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne [string]$manifest.sha256) {
            throw "SHA-256 mismatch for $inputPath"
        }
    }

    for ($run = 1; $run -le $Repetitions; $run++) {
        $orderedModes = @(
            for ($index = 0; $index -lt $modeList.Count; $index++) {
                $modeList[($index + $run + $tierIndex - 1) % $modeList.Count]
            }
        )
        $canonicalHashes = @{}

        for ($order = 0; $order -lt $orderedModes.Count; $order++) {
            $mode = $orderedModes[$order]
            $prefix = '{0}-run{1:D2}-{2}' -f $tier, $run, $mode
            $outputPath = Join-Path $RunRoot "$prefix.matches.txt"
            $stdoutPath = Join-Path $RunRoot "$prefix.stdout.log"
            $stderrPath = Join-Path $RunRoot "$prefix.stderr.log"
            $gpuLogPath = Join-Path $RunRoot "$prefix.gpu.csv"

            $measurement = Invoke-BstringsMeasured `
                -InputPath $inputPath `
                -OutputPath $outputPath `
                -Mode $mode `
                -StdoutPath $stdoutPath `
                -StderrPath $stderrPath `
                -GpuLogPath $gpuLogPath

            if ($measurement.ExitCode -ne 0) {
                $details = [IO.File]::ReadAllText($stderrPath)
                throw "$prefix failed with exit code $($measurement.ExitCode): $details"
            }

            $expected = [long]$manifest.records.expectedUrlOrEmailMatches
            $actual = Get-LineCount -Path $outputPath
            if ($actual -ne $expected) {
                throw "$prefix returned $actual matches; expected $expected."
            }
            $exactMarkers = Test-ExpectedMarkerSet `
                -Path $outputPath `
                -SizeBytes $input.Length `
                -SegmentBytes ([long]$manifest.segmentBytes)
            if (-not $exactMarkers) {
                throw "$prefix did not return the exact marker set."
            }

            $canonicalHash = Get-CanonicalOutputHash -Path $outputPath
            $canonicalHashes[$mode] = $canonicalHash
            $result = [pscustomobject]@{
                Tier = $tier
                SizeBytes = $input.Length
                Mode = $mode
                Run = $run
                Order = $order
                IncludeUnicode = [bool]$IncludeUnicode
                ChunkSizeMB = $ChunkSizeMB
                ElapsedSeconds = $measurement.ElapsedSeconds
                ThroughputMiBPerSecond = $input.Length / 1MB / $measurement.ElapsedSeconds
                ProcessCpuSeconds = $measurement.ProcessCpuSeconds
                AverageCpuPercent = $measurement.AverageCpuPercent
                PeakWorkingSetBytes = $measurement.PeakWorkingSetBytes
                GpuSamples = $measurement.GpuSamples
                AverageGpuPercent = $measurement.AverageGpuPercent
                PeakGpuPercent = $measurement.PeakGpuPercent
                PeakGpuMemoryMiB = $measurement.PeakGpuMemoryMiB
                AverageGpuPowerW = $measurement.AverageGpuPowerW
                PeakGpuSmClockMHz = $measurement.PeakGpuSmClockMHz
                ExpectedMatches = $expected
                ActualMatches = $actual
                ExactMarkers = $exactMarkers
                CanonicalOutputSha256 = $canonicalHash
                ExitCode = $measurement.ExitCode
            }
            $result | Export-Csv -LiteralPath $resultsPath -NoTypeInformation -Append
            $result | ConvertTo-Json -Compress
        }

        if (@($canonicalHashes.Values | Select-Object -Unique).Count -ne 1) {
            throw "$tier run $run produced different canonical outputs between backends."
        }
    }

    $tierIndex++
}
