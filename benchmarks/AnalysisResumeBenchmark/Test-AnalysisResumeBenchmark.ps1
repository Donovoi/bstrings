[CmdletBinding()]
param(
    [ValidateRange(5, 15)]
    [int]$Rounds = 5,
    [string]$BaselineCommit = 'v2.1.1',
    [string]$ResultsPath = 'benchmarks/results/analysis-resume-2.1.2-acceptance-2026-08.csv',
    [switch]$KeepTemporary,
    [switch]$FixtureSmokeTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# These sizes are frozen before acceptance timing. A failed qualification is evidence;
# do not tune a fixture after observing its result.
$sparseInputBytes = 64L * 1024 * 1024
$matchHeavyInputBytes = 1L * 1024 * 1024
# The first corrected run proved that 8 GiB contained only 34.820 seconds of
# reusable work. Before this next acceptance run, freeze 24 GiB to exceed the
# unchanged 60-second qualification without changing any timing margin.
$longSparseInputBytes = 24L * 1024 * 1024 * 1024
$resumeCheckpointOrdinal = 10
$resumeCheckpointName = '0010-enriched-merge.json'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resolvedResults = [IO.Path]::GetFullPath((Join-Path $repoRoot $ResultsPath))
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('bstrings-resume-benchmark-' + [Guid]::NewGuid().ToString('N'))
$baselineSource = Join-Path $temporaryRoot 'baseline-source'
$baselinePublish = Join-Path $temporaryRoot 'baseline-publish'
$candidatePublish = Join-Path $temporaryRoot 'candidate-publish'

function Remove-BoundedPath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($temporaryRoot).TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the benchmark root: $full"
    }
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description
    )
    $output = & $FilePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
    }
}

function Get-GitScalar {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = @(& git -C $repoRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1) {
        throw "Git query failed: git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    return ([string]$output[0]).Trim()
}

function Invoke-Analysis {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPrefix
    )
    $stdout = $LogPrefix + '.stdout.log'
    $stderr = $LogPrefix + '.stderr.log'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -Wait -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $watch.Stop()
    if ($process.ExitCode -ne 0) {
        throw "Analysis failed with exit code $($process.ExitCode). See $stderr"
    }
    return $watch.Elapsed.TotalSeconds
}

function Get-AnalysisArguments {
    param(
        [Parameter(Mandatory)][string]$InputPath,
        [Parameter(Mandatory)][string]$OutputDirectory
    )
    return @(
        'analyze',
        '-f', $InputPath,
        '--recover-executable-strings', 'off',
        '--ocr', 'off',
        '--translation', 'off',
        '--decode', 'off',
        '--processor', 'cpu',
        '--cpu-engine', 'dotnet',
        '--maximum-length', '4096',
        '--lr', 'email',
        '-o', $OutputDirectory
    )
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-CanonicalLineMultisetSha256 {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Artifact
    )
    $lines = [Collections.Generic.List[string]]::new()
    $tsvHeader = $null
    $compactFindingsColumns = @(
        'PatternName',
        'Match',
        'Context',
        'SourceFile',
        'ArtifactType',
        'Location',
        'MatchStart',
        'AttributesJson'
    )
    $compactFindingsIndexes = @()
    foreach ($rawLine in [IO.File]::ReadLines($Path)) {
        $line = $rawLine
        if ($Artifact.EndsWith('.jsonl', [StringComparison]::OrdinalIgnoreCase)) {
            $line = [regex]::Replace(
                $line,
                '("origin":\{"extractor":"bstrings","version":")[^"]+',
                '${1}<release-version>'
            )
        }
        elseif ($Artifact -ceq 'findings.tsv') {
            $fields = $line.Split([char]"`t")
            if ($null -eq $tsvHeader) {
                $tsvHeader = $fields
                $compactFindingsIndexes = @($compactFindingsColumns | ForEach-Object {
                    [Array]::IndexOf($tsvHeader, $_)
                })
                if (@($compactFindingsIndexes | Where-Object { $_ -lt 0 }).Count -ne 0) {
                    throw 'findings.tsv is missing a compact investigator column.'
                }
                $line = $compactFindingsColumns -join "`t"
            }
            elseif ($fields.Count -ne $tsvHeader.Count) {
                throw 'findings.tsv contains an invalid field count.'
            }
            else {
                $line = @($compactFindingsIndexes | ForEach-Object { $fields[$_] }) -join "`t"
            }
        }
        $lines.Add($line)
    }
    $ordered = @($lines | Sort-Object -CaseSensitive)
    $content = if ($ordered.Count -eq 0) { '' } else { ($ordered -join "`n") + "`n" }
    $bytes = [Text.Encoding]::UTF8.GetBytes($content)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-EvidenceParity {
    param(
        [Parameter(Mandatory)][string]$ExpectedDirectory,
        [Parameter(Mandatory)][string]$ActualDirectory
    )
    $excluded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('run.json', 'summary.json', '.incomplete', '.bstrings-run.lock')) {
        [void]$excluded.Add($name)
    }
    $expectedNames = @(Get-ChildItem -LiteralPath $ExpectedDirectory -File | Where-Object { -not $excluded.Contains($_.Name) } | ForEach-Object Name | Sort-Object)
    $actualNames = @(Get-ChildItem -LiteralPath $ActualDirectory -File | Where-Object { -not $excluded.Contains($_.Name) } | ForEach-Object Name | Sort-Object)
    Assert-SequenceEqual -Expected $expectedNames -Actual $actualNames -Description 'Evidence artifact names'
    $artifacts = $expectedNames
    $exactByteMismatches = [Collections.Generic.List[string]]::new()
    foreach ($artifact in $artifacts) {
        $expected = Join-Path $ExpectedDirectory $artifact
        $actual = Join-Path $ActualDirectory $artifact
        if (-not (Test-Path -LiteralPath $expected -PathType Leaf) -or -not (Test-Path -LiteralPath $actual -PathType Leaf)) {
            throw "Evidence parity artifact is missing: $artifact"
        }
        $expectedExactHash = Get-Sha256 $expected
        $actualExactHash = Get-Sha256 $actual
        if ($expectedExactHash -eq $actualExactHash) {
            continue
        }
        $canonicalLines = $artifact.EndsWith('.jsonl', [StringComparison]::OrdinalIgnoreCase) -or $artifact -eq 'findings.tsv'
        if (
            -not $canonicalLines -or
            (Get-CanonicalLineMultisetSha256 $expected $artifact) -ne
                (Get-CanonicalLineMultisetSha256 $actual $artifact)
        ) {
            throw "Canonical evidence parity failed for $artifact"
        }
        $exactByteMismatches.Add($artifact)
    }
    return [string[]]$exactByteMismatches.ToArray()
}

function New-DeterministicEvidence {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$Bytes,
        [Parameter(Mandatory)][ValidateSet('sparse', 'match-heavy')][string]$Kind
    )
    $line = [Text.Encoding]::UTF8.GetBytes('analyst@example.test')
    $block = [byte[]]::new(1024 * 1024)
    if ($Kind -eq 'match-heavy') {
        for ($offset = 128; $offset + $line.Length -lt $block.Length; $offset += 256) {
            [Array]::Copy($line, 0, $block, $offset, $line.Length)
        }
    }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $remaining = $Bytes
        $blockIndex = 0L
        while ($remaining -gt 0) {
            $hasSparseMarker = $Kind -eq 'sparse' -and ($blockIndex % 16) -eq 0
            if ($hasSparseMarker) {
                [Array]::Copy($line, 0, $block, 4096, $line.Length)
            }
            $count = [Math]::Min([long]$block.Length, $remaining)
            $stream.Write($block, 0, [int]$count)
            if ($hasSparseMarker) {
                [Array]::Clear($block, 4096, $line.Length)
            }
            $remaining -= $count
            $blockIndex++
        }
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Get-Median {
    param([Parameter(Mandatory)][double[]]$Values)
    if ($Values.Count -eq 0) {
        throw 'A benchmark median requires at least one value.'
    }
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) {
        return [double]$ordered[$middle]
    }
    return ([double]$ordered[$middle - 1] + [double]$ordered[$middle]) / 2.0
}

function Get-CheckpointPrefixInfo {
    param(
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][int]$ThroughOrdinal,
        [switch]$RequireExactTail
    )
    $checkpointDirectory = Join-Path $OutputDirectory '.bstrings-resume\checkpoints'
    $files = @(Get-ChildItem -LiteralPath $checkpointDirectory -Filter '*.json' -File | Sort-Object -Property Name)
    if ($files.Count -lt $ThroughOrdinal -or ($RequireExactTail -and $files.Count -ne $ThroughOrdinal)) {
        throw "Checkpoint prefix does not end exactly at ordinal $ThroughOrdinal. Found $($files.Count) checkpoint files."
    }
    $stageIds = [Collections.Generic.List[string]]::new()
    $stageTimings = [Collections.Generic.List[string]]::new()
    $checkpointPaths = [Collections.Generic.List[string]]::new()
    $elapsedTotal = 0.0
    for ($index = 0; $index -lt $ThroughOrdinal; $index++) {
        $expectedOrdinal = $index + 1
        $checkpoint = Get-Content -LiteralPath $files[$index].FullName -Raw | ConvertFrom-Json
        if (
            [int]$checkpoint.schemaVersion -ne 1 -or
            [string]$checkpoint.recordType -ne 'analysis-stage-checkpoint' -or
            [int]$checkpoint.ordinal -ne $expectedOrdinal
        ) {
            throw "Checkpoint metadata is not a canonical prefix at ordinal $expectedOrdinal."
        }
        $stageId = [string]$checkpoint.stageId
        $expectedName = '{0:D4}-{1}.json' -f $expectedOrdinal, $stageId
        if (-not [string]::Equals($files[$index].Name, $expectedName, [StringComparison]::Ordinal)) {
            throw "Checkpoint filename is not canonical at ordinal $expectedOrdinal."
        }
        $elapsed = [double]$checkpoint.elapsedSeconds
        if ($elapsed -lt 0 -or [double]::IsNaN($elapsed) -or [double]::IsInfinity($elapsed)) {
            throw "Checkpoint elapsed time is invalid at ordinal $expectedOrdinal."
        }
        $stageIds.Add($stageId)
        $stageTimings.Add(('{0}={1:R}' -f $stageId, $elapsed))
        $checkpointPaths.Add($files[$index].FullName)
        $elapsedTotal += $elapsed
    }
    return [pscustomobject]@{
        ElapsedSeconds = $elapsedTotal
        StageIds = [string[]]$stageIds.ToArray()
        StageTimings = $stageTimings -join ';'
        CheckpointPaths = [string[]]$checkpointPaths.ToArray()
    }
}

function Get-CommittedByteHashes {
    param(
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string[]]$CheckpointPaths
    )
    $outputFullPath = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
    $outputPrefix = $outputFullPath + '\'
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$paths.Add('.bstrings-resume/owner.json')
    foreach ($checkpointPath in $CheckpointPaths) {
        $checkpointRelative = [IO.Path]::GetRelativePath($outputFullPath, $checkpointPath).Replace('\', '/')
        [void]$paths.Add($checkpointRelative)
        $checkpoint = Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json
        foreach ($artifact in @($checkpoint.artifacts)) {
            $relative = [string]$artifact.path
            if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathFullyQualified($relative)) {
                throw "Checkpoint contains an unsafe artifact path: $relative"
            }
            $artifactPath = [IO.Path]::GetFullPath((Join-Path $outputFullPath $relative.Replace('/', '\')))
            if (-not $artifactPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Checkpoint artifact escapes the benchmark output: $relative"
            }
            [void]$paths.Add($relative.Replace('\', '/'))
        }
    }
    $hashes = @{}
    foreach ($relative in $paths) {
        $fullPath = [IO.Path]::GetFullPath((Join-Path $outputFullPath $relative.Replace('/', '\')))
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Committed benchmark artifact is missing: $relative"
        }
        $hashes[$relative] = Get-Sha256 $fullPath
    }
    return $hashes
}

function Assert-CommittedBytesUnchanged {
    param(
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][hashtable]$ExpectedHashes
    )
    foreach ($relative in $ExpectedHashes.Keys) {
        $fullPath = Join-Path $OutputDirectory ([string]$relative).Replace('/', '\')
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Resume removed committed artifact bytes: $relative"
        }
        if ($ExpectedHashes[$relative] -ne (Get-Sha256 $fullPath)) {
            throw "Resume changed committed artifact bytes: $relative"
        }
    }
}

function Assert-SequenceEqual {
    param(
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string[]]$Actual,
        [Parameter(Mandatory)][string]$Description
    )
    if ($Expected.Count -ne $Actual.Count) {
        throw "$Description count differs. Expected $($Expected.Count), found $($Actual.Count)."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if (-not [string]::Equals($Expected[$index], $Actual[$index], [StringComparison]::Ordinal)) {
            throw "$Description differs at index $index. Expected '$($Expected[$index])', found '$($Actual[$index])'."
        }
    }
}

function Start-AndInterruptAfterCheckpoint {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$InputPath,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$LogPrefix
    )
    $stdout = $LogPrefix + '.stdout.log'
    $stderr = $LogPrefix + '.stderr.log'
    $arguments = Get-AnalysisArguments -InputPath $InputPath -OutputDirectory $OutputDirectory
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        $checkpoint = Join-Path $OutputDirectory ('.bstrings-resume\checkpoints\' + $resumeCheckpointName)
        $deadline = [DateTime]::UtcNow.AddHours(2)
        while (-not (Test-Path -LiteralPath $checkpoint -PathType Leaf)) {
            if ($process.HasExited) {
                throw 'Candidate completed before the measured resume checkpoint could be observed.'
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                throw 'Timed out waiting for the measured resume checkpoint.'
            }
            Start-Sleep -Milliseconds 2
            $process.Refresh()
        }
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit()
        }
        $process.Dispose()
    }
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory '.incomplete') -PathType Leaf)) {
        throw 'Interrupted candidate did not retain the incomplete marker.'
    }
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'summary.json')) {
        throw 'Interrupted candidate published a final summary before termination.'
    }
    return Get-CheckpointPrefixInfo -OutputDirectory $OutputDirectory -ThroughOrdinal $resumeCheckpointOrdinal -RequireExactTail
}

function New-BenchmarkRow {
    param(
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][string]$Fixture,
        [Parameter(Mandatory)][int]$Pair,
        [Parameter(Mandatory)][string]$Order,
        [Parameter(Mandatory)][long]$InputBytes,
        [Parameter(Mandatory)][string]$InputSha256,
        [Parameter(Mandatory)][hashtable]$Identity,
        [AllowNull()][Nullable[double]]$BaselineSeconds,
        [AllowNull()][Nullable[double]]$CandidateSeconds,
        [AllowNull()][Nullable[double]]$AddedSeconds,
        [AllowNull()][Nullable[double]]$AddedPercent,
        [AllowNull()][Nullable[double]]$FreshSeconds,
        [AllowNull()][Nullable[double]]$ResumeSeconds,
        [AllowNull()][Nullable[double]]$SavingSeconds,
        [AllowNull()][Nullable[double]]$SavingPercent,
        [AllowNull()][Nullable[double]]$ReusableSeconds,
        [AllowNull()][Nullable[double]]$InterruptedReusableSeconds,
        [AllowNull()][Nullable[double]]$ReusableFraction,
        [string]$InterruptionCheckpoint = '',
        [string]$ReusedStages = '',
        [string]$ReusedStageTimings = '',
        [string]$ExactByteMismatches = ''
    )
    return [pscustomobject]@{
        scenario = $Scenario
        fixture = $Fixture
        pair = $Pair
        order = $Order
        baselineSeconds = $BaselineSeconds
        candidateSeconds = $CandidateSeconds
        addedSeconds = $AddedSeconds
        addedPercent = $AddedPercent
        freshSeconds = $FreshSeconds
        resumeSeconds = $ResumeSeconds
        savingSeconds = $SavingSeconds
        savingPercent = $SavingPercent
        reusableSeconds = $ReusableSeconds
        interruptedReusableSeconds = $InterruptedReusableSeconds
        reusableFraction = $ReusableFraction
        interruptionCheckpoint = $InterruptionCheckpoint
        reusedStages = $ReusedStages
        reusedStageTimings = $ReusedStageTimings
        orderingParity = if ($ExactByteMismatches.Length -eq 0) { 'exact' } else { 'canonical-only' }
        orderingLimitation = if ($ExactByteMismatches.Length -eq 0) { '' } else { 'existing-fresh-run-ordering-nondeterminism' }
        exactByteMismatches = $ExactByteMismatches
        baselineCommit = $Identity.BaselineCommit
        baselineExecutableSha256 = $Identity.BaselineExecutableSha256
        baselineManagedAssemblySha256 = $Identity.BaselineManagedAssemblySha256
        candidateCommit = $Identity.CandidateCommit
        candidateExecutableSha256 = $Identity.CandidateExecutableSha256
        candidateManagedAssemblySha256 = $Identity.CandidateManagedAssemblySha256
        inputBytes = $InputBytes
        inputSha256 = $InputSha256
        correctness = 'pass'
        gateStatus = 'pending'
    }
}

function Invoke-FixtureSmokeTest {
    $smokeFiles = [Collections.Generic.List[string]]::new()
    try {
        foreach ($kind in @('sparse', 'match-heavy')) {
            $first = Join-Path $temporaryRoot ("smoke-$kind-first.bin")
            $second = Join-Path $temporaryRoot ("smoke-$kind-second.bin")
            $smokeFiles.Add($first)
            $smokeFiles.Add($second)
            New-DeterministicEvidence -Path $first -Bytes (1L * 1024 * 1024) -Kind $kind
            New-DeterministicEvidence -Path $second -Bytes (1L * 1024 * 1024) -Kind $kind
            if ((Get-Item -LiteralPath $first).Length -ne 1L * 1024 * 1024) {
                throw "Fixture smoke produced the wrong byte count for $kind."
            }
            if ((Get-Sha256 $first) -ne (Get-Sha256 $second)) {
                throw "Fixture smoke was not deterministic for $kind."
            }
        }
        if ((Get-Sha256 $smokeFiles[0]) -eq (Get-Sha256 $smokeFiles[2])) {
            throw 'Sparse and match-heavy smoke fixtures must not have the same bytes.'
        }
        Write-Host 'Analysis resume fixture smoke passed. No acceptance timings were run.'
    }
    finally {
        foreach ($path in $smokeFiles) {
            Remove-BoundedPath $path
        }
    }
}

$rows = [Collections.Generic.List[object]]::new()
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    if ($FixtureSmokeTest) {
        Invoke-FixtureSmokeTest
        return
    }
    if (Test-Path -LiteralPath $resolvedResults) {
        throw "Refusing to overwrite existing benchmark evidence: $resolvedResults"
    }
    $dirty = @(& git -C $repoRoot status --porcelain 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect the candidate worktree.`n$($dirty -join [Environment]::NewLine)"
    }
    if ($dirty.Count -ne 0) {
        throw 'The corrected acceptance benchmark requires a clean candidate worktree.'
    }

    $archive = Join-Path $temporaryRoot 'baseline.zip'
    Invoke-Checked -FilePath 'git' -Arguments @('-C', $repoRoot, 'archive', '--format=zip', "--output=$archive", $BaselineCommit) -Description 'Baseline source export'
    Expand-Archive -LiteralPath $archive -DestinationPath $baselineSource
    Invoke-Checked -FilePath 'dotnet' -Arguments @('publish', (Join-Path $baselineSource 'bstrings\bstrings.csproj'), '-c', 'Release', '-f', 'net10.0', '--self-contained', 'false', '-o', $baselinePublish) -Description 'Baseline publish'
    Invoke-Checked -FilePath 'dotnet' -Arguments @('publish', (Join-Path $repoRoot 'bstrings\bstrings.csproj'), '-c', 'Release', '-f', 'net10.0', '--self-contained', 'false', '-o', $candidatePublish) -Description 'Candidate publish'

    $baselineExecutable = Join-Path $baselinePublish 'bstrings.exe'
    $candidateExecutable = Join-Path $candidatePublish 'bstrings.exe'
    $baselineAssembly = Join-Path $baselinePublish 'bstrings.dll'
    $candidateAssembly = Join-Path $candidatePublish 'bstrings.dll'
    foreach ($required in @($baselineExecutable, $candidateExecutable, $baselineAssembly, $candidateAssembly)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Benchmark publish identity is missing: $required"
        }
    }
    $identity = @{
        BaselineCommit = Get-GitScalar @('rev-parse', "$BaselineCommit^{commit}")
        BaselineExecutableSha256 = Get-Sha256 $baselineExecutable
        BaselineManagedAssemblySha256 = Get-Sha256 $baselineAssembly
        CandidateCommit = Get-GitScalar @('rev-parse', 'HEAD^{commit}')
        CandidateExecutableSha256 = Get-Sha256 $candidateExecutable
        CandidateManagedAssemblySha256 = Get-Sha256 $candidateAssembly
    }

    $overheadFixtures = @(
        [pscustomobject]@{ Name = 'sparse'; Kind = 'sparse'; Bytes = $sparseInputBytes },
        [pscustomobject]@{ Name = 'match-heavy'; Kind = 'match-heavy'; Bytes = $matchHeavyInputBytes }
    )
    foreach ($fixture in $overheadFixtures) {
        $inputPath = Join-Path $temporaryRoot ("overhead-$($fixture.Name)-input.bin")
        try {
            New-DeterministicEvidence -Path $inputPath -Bytes $fixture.Bytes -Kind $fixture.Kind
            $inputSha256 = Get-Sha256 $inputPath
            for ($pair = 1; $pair -le $Rounds; $pair++) {
                $baselineOutput = Join-Path $temporaryRoot ("overhead-$($fixture.Name)-$pair-baseline")
                $candidateOutput = Join-Path $temporaryRoot ("overhead-$($fixture.Name)-$pair-candidate")
                $verified = $false
                try {
                    if (($pair % 2) -eq 1) {
                        $baselineSeconds = Invoke-Analysis -Executable $baselineExecutable -Arguments (Get-AnalysisArguments -InputPath $inputPath -OutputDirectory $baselineOutput) -LogPrefix (Join-Path $temporaryRoot "overhead-$($fixture.Name)-$pair-baseline")
                        $candidateSeconds = Invoke-Analysis -Executable $candidateExecutable -Arguments (Get-AnalysisArguments -InputPath $inputPath -OutputDirectory $candidateOutput) -LogPrefix (Join-Path $temporaryRoot "overhead-$($fixture.Name)-$pair-candidate")
                        $order = 'baseline-candidate'
                    }
                    else {
                        $candidateSeconds = Invoke-Analysis -Executable $candidateExecutable -Arguments (Get-AnalysisArguments -InputPath $inputPath -OutputDirectory $candidateOutput) -LogPrefix (Join-Path $temporaryRoot "overhead-$($fixture.Name)-$pair-candidate")
                        $baselineSeconds = Invoke-Analysis -Executable $baselineExecutable -Arguments (Get-AnalysisArguments -InputPath $inputPath -OutputDirectory $baselineOutput) -LogPrefix (Join-Path $temporaryRoot "overhead-$($fixture.Name)-$pair-baseline")
                        $order = 'candidate-baseline'
                    }
                    $orderingMismatches = @(Assert-EvidenceParity -ExpectedDirectory $baselineOutput -ActualDirectory $candidateOutput)
                    $addedSeconds = $candidateSeconds - $baselineSeconds
                    $rows.Add((New-BenchmarkRow -Scenario 'new-run-overhead' -Fixture $fixture.Name -Pair $pair -Order $order -InputBytes $fixture.Bytes -InputSha256 $inputSha256 -Identity $identity -BaselineSeconds $baselineSeconds -CandidateSeconds $candidateSeconds -AddedSeconds $addedSeconds -AddedPercent (($addedSeconds / $baselineSeconds) * 100.0) -ExactByteMismatches ($orderingMismatches -join ';')))
                    $verified = $true
                }
                finally {
                    if ($verified) {
                        Remove-BoundedPath $baselineOutput
                        Remove-BoundedPath $candidateOutput
                    }
                }
            }
        }
        finally {
            Remove-BoundedPath $inputPath
        }
    }

    $driveRoot = [IO.Path]::GetPathRoot($temporaryRoot)
    $requiredFreeBytes = $longSparseInputBytes + 4L * 1024 * 1024 * 1024
    if ([IO.DriveInfo]::new($driveRoot).AvailableFreeSpace -lt $requiredFreeBytes) {
        throw "The frozen long fixture requires at least $requiredFreeBytes free bytes on $driveRoot."
    }
    $longInputPath = Join-Path $temporaryRoot 'long-sparse-input.bin'
    try {
        New-DeterministicEvidence -Path $longInputPath -Bytes $longSparseInputBytes -Kind 'sparse'
        $longInputSha256 = Get-Sha256 $longInputPath
        for ($pair = 1; $pair -le $Rounds; $pair++) {
            $resumeOutput = Join-Path $temporaryRoot ("resume-long-sparse-$pair-interrupted")
            $freshOutput = Join-Path $temporaryRoot ("resume-long-sparse-$pair-fresh")
            $verified = $false
            try {
                $interruptedPrefix = Start-AndInterruptAfterCheckpoint -Executable $candidateExecutable -InputPath $longInputPath -OutputDirectory $resumeOutput -LogPrefix (Join-Path $temporaryRoot "resume-long-sparse-$pair-interrupted")
                $committedBeforeResume = Get-CommittedByteHashes -OutputDirectory $resumeOutput -CheckpointPaths $interruptedPrefix.CheckpointPaths
                if (($pair % 2) -eq 1) {
                    $resumeSeconds = Invoke-Analysis -Executable $candidateExecutable -Arguments @('analyze', '-r', '-o', $resumeOutput) -LogPrefix (Join-Path $temporaryRoot "resume-long-sparse-$pair-resumed")
                    $freshSeconds = Invoke-Analysis -Executable $candidateExecutable -Arguments (Get-AnalysisArguments -InputPath $longInputPath -OutputDirectory $freshOutput) -LogPrefix (Join-Path $temporaryRoot "resume-long-sparse-$pair-fresh")
                    $order = 'resume-fresh'
                }
                else {
                    $freshSeconds = Invoke-Analysis -Executable $candidateExecutable -Arguments (Get-AnalysisArguments -InputPath $longInputPath -OutputDirectory $freshOutput) -LogPrefix (Join-Path $temporaryRoot "resume-long-sparse-$pair-fresh")
                    $resumeSeconds = Invoke-Analysis -Executable $candidateExecutable -Arguments @('analyze', '-r', '-o', $resumeOutput) -LogPrefix (Join-Path $temporaryRoot "resume-long-sparse-$pair-resumed")
                    $order = 'fresh-resume'
                }
                $orderingMismatches = @(Assert-EvidenceParity -ExpectedDirectory $freshOutput -ActualDirectory $resumeOutput)
                Assert-CommittedBytesUnchanged -OutputDirectory $resumeOutput -ExpectedHashes $committedBeforeResume
                $freshPrefix = Get-CheckpointPrefixInfo -OutputDirectory $freshOutput -ThroughOrdinal $resumeCheckpointOrdinal
                Assert-SequenceEqual -Expected $freshPrefix.StageIds -Actual $interruptedPrefix.StageIds -Description 'Fresh and interrupted reusable stage names'
                $run = Get-Content -LiteralPath (Join-Path $resumeOutput 'run.json') -Raw | ConvertFrom-Json
                $actualReusedStages = [string[]]@($run.resume.reusedStages)
                Assert-SequenceEqual -Expected $freshPrefix.StageIds -Actual $actualReusedStages -Description 'Recorded reused stage names'
                $savingSeconds = $freshSeconds - $resumeSeconds
                $rows.Add((New-BenchmarkRow -Scenario 'resume-value' -Fixture 'long-sparse' -Pair $pair -Order $order -InputBytes $longSparseInputBytes -InputSha256 $longInputSha256 -Identity $identity -FreshSeconds $freshSeconds -ResumeSeconds $resumeSeconds -SavingSeconds $savingSeconds -SavingPercent (($savingSeconds / $freshSeconds) * 100.0) -ReusableSeconds $freshPrefix.ElapsedSeconds -InterruptedReusableSeconds $interruptedPrefix.ElapsedSeconds -ReusableFraction ($freshPrefix.ElapsedSeconds / $freshSeconds) -InterruptionCheckpoint $resumeCheckpointName -ReusedStages ($actualReusedStages -join ';') -ReusedStageTimings $freshPrefix.StageTimings -ExactByteMismatches ($orderingMismatches -join ';')))
                $verified = $true
            }
            finally {
                if ($verified) {
                    Remove-BoundedPath $resumeOutput
                    Remove-BoundedPath $freshOutput
                }
            }
        }
    }
    finally {
        Remove-BoundedPath $longInputPath
    }

    $gateFailures = [Collections.Generic.List[string]]::new()
    foreach ($fixtureName in @('sparse', 'match-heavy')) {
        $fixtureRows = @($rows | Where-Object { $_.scenario -eq 'new-run-overhead' -and $_.fixture -eq $fixtureName })
        if ($fixtureRows.Count -lt 5) {
            throw "The $fixtureName overhead fixture did not produce at least five pairs."
        }
        $medianBaseline = Get-Median ([double[]]@($fixtureRows | ForEach-Object { [double]$_.baselineSeconds }))
        $medianAdded = Get-Median ([double[]]@($fixtureRows | ForEach-Object { [double]$_.addedSeconds }))
        $allowedAdded = [Math]::Max(1.0, 0.05 * $medianBaseline)
        $medianAddedPercent = ($medianAdded / $medianBaseline) * 100.0
        Write-Host ("{0} new-run median added time: {1:N3} s ({2:N3}%); allowed: {3:N3} s" -f $fixtureName, $medianAdded, $medianAddedPercent, $allowedAdded)
        if ($medianAdded -gt $allowedAdded) {
            $gateFailures.Add(("$fixtureName checkpoint overhead failed: {0:N3} s > {1:N3} s." -f $medianAdded, $allowedAdded))
        }
    }

    $resumeRows = @($rows | Where-Object { $_.scenario -eq 'resume-value' })
    if ($resumeRows.Count -lt 5) {
        throw 'The long resume fixture did not produce at least five pairs.'
    }
    $medianFresh = Get-Median ([double[]]@($resumeRows | ForEach-Object { [double]$_.freshSeconds }))
    $medianReusable = Get-Median ([double[]]@($resumeRows | ForEach-Object { [double]$_.reusableSeconds }))
    $medianSaving = Get-Median ([double[]]@($resumeRows | ForEach-Object { [double]$_.savingSeconds }))
    $reusableFraction = $medianReusable / $medianFresh
    $requiredSaving = [Math]::Max(30.0, 0.5 * $medianReusable)
    $medianSavingPercent = ($medianSaving / $medianFresh) * 100.0
    Write-Host ("Long sparse median reusable work: {0:N3} s ({1:P2} of fresh)" -f $medianReusable, $reusableFraction)
    Write-Host ("Long sparse median resume saving: {0:N3} s ({1:N3}%); required: {2:N3} s" -f $medianSaving, $medianSavingPercent, $requiredSaving)
    if ($medianReusable -lt 60.0) {
        $gateFailures.Add(("Long fixture qualification failed: {0:N3} reusable seconds < 60 seconds." -f $medianReusable))
    }
    if ($reusableFraction -lt 0.5) {
        $gateFailures.Add(("Long fixture qualification failed: reusable work is {0:P2} of fresh time, below 50%." -f $reusableFraction))
    }
    if ($medianSaving -lt $requiredSaving) {
        $gateFailures.Add(("Resume saving failed: {0:N3} s < {1:N3} s." -f $medianSaving, $requiredSaving))
    }

    $gateStatus = if ($gateFailures.Count -eq 0) { 'pass' } else { 'fail' }
    foreach ($row in $rows) {
        $row.gateStatus = $gateStatus
    }
    $resultDirectory = Split-Path -Parent $resolvedResults
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $rows | Export-Csv -LiteralPath $resolvedResults -NoTypeInformation -Encoding utf8
    $orderingLimitations = @(
        $rows |
            Where-Object { $_.orderingParity -eq 'canonical-only' } |
            ForEach-Object { $_.exactByteMismatches -split ';' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
    if ($orderingLimitations.Count -ne 0) {
        Write-Warning ("Canonical evidence parity passed, but existing fresh-run ordering nondeterminism prevented exact bytes for: {0}. Reused committed artifacts still passed exact SHA-256 checks." -f ($orderingLimitations -join ';'))
    }
    if ($gateFailures.Count -ne 0) {
        throw "Corrected analysis resume benchmark failed:`n$($gateFailures -join [Environment]::NewLine)"
    }
    Write-Host "Corrected analysis resume benchmark passed: $resolvedResults"
}
finally {
    if ($KeepTemporary) {
        Write-Host "Benchmark temporary directory retained: $temporaryRoot"
    }
    elseif (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $expectedPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\bstrings-resume-benchmark-'
        if (-not $resolvedTemporaryRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unexpected benchmark root: $resolvedTemporaryRoot"
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
