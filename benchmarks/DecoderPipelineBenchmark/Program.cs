using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using bstrings;

const int DefaultRounds = 7;
const int DefaultMaximumCandidateCharacters = 16_384;
const int DefaultMaximumDecodedBytesPerRecord = 12_288;
const long DefaultMaximumCandidates = 100_000;
const long DefaultMaximumTotalDecodedBytes = 64L * 1024 * 1024;
const double MaximumMedianRegressionPercent = 3.0;
const double MaximumPairRegressionPercent = 5.0;
const int CandidateKinds = 6;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true,
};
var jsonLineOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

if (args.Length > 0 && string.Equals(args[0], "--child", StringComparison.Ordinal))
{
    if (args.Length != 8)
    {
        throw new ArgumentException("The internal child invocation is incomplete.");
    }

    var childResult = await RunChildAsync(
        Path.GetFullPath(args[1]),
        Path.GetFullPath(args[2]),
        ParseWorkload(args[3]),
        int.Parse(args[4], CultureInfo.InvariantCulture),
        int.Parse(args[5], CultureInfo.InvariantCulture),
        ParseVariant(args[6]),
        args[7],
        jsonLineOptions
    );
    Console.WriteLine(JsonSerializer.Serialize(childResult, jsonLineOptions));
    return;
}

if (args.Any(argument => argument is "--help" or "-h" or "/?"))
{
    WriteHelp();
    return;
}

var settings = ParseArguments(args);
AssertStaticFixtureContract();
var outputPath = Path.GetFullPath(settings.OutputPath);
if (File.Exists(outputPath) && !settings.Overwrite)
{
    throw new IOException(
        $"Benchmark output already exists: '{outputPath}'. Pass --overwrite to replace it."
    );
}
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var runRoot = Path.Combine(
    Path.GetTempPath(),
    "bstrings-decoder-benchmark-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(runRoot);
var completed = false;
try
{
    var pairs = new List<PairResult>();
    var scenarioOrdinal = 0;
    foreach (var recordCount in settings.RecordCounts)
    {
        foreach (var workload in settings.Workloads)
        {
            var scenarioDirectory = Path.Combine(
                runRoot,
                $"{WorkloadName(workload)}-{recordCount.ToString(CultureInfo.InvariantCulture)}"
            );
            Directory.CreateDirectory(scenarioDirectory);
            var inputPath = Path.Combine(scenarioDirectory, "raw-strings.jsonl");
            var input = await GenerateCorpusAsync(
                inputPath,
                workload,
                recordCount,
                jsonLineOptions
            );

            for (var sample = 1; sample <= settings.Rounds; sample++)
            {
                var autoFirst = (sample + scenarioOrdinal) % 2 == 0;
                var order = autoFirst ? "auto-first" : "off-first";
                var first = autoFirst ? BenchmarkVariant.Auto : BenchmarkVariant.Off;
                var second = autoFirst ? BenchmarkVariant.Off : BenchmarkVariant.Auto;
                var firstResult = await RunChildProcessAsync(
                    inputPath,
                    scenarioDirectory,
                    workload,
                    recordCount,
                    sample,
                    first,
                    order,
                    jsonLineOptions
                );
                var secondResult = await RunChildProcessAsync(
                    inputPath,
                    scenarioDirectory,
                    workload,
                    recordCount,
                    sample,
                    second,
                    order,
                    jsonLineOptions
                );
                var off = first == BenchmarkVariant.Off ? firstResult : secondResult;
                var auto = first == BenchmarkVariant.Auto ? firstResult : secondResult;
                AssertPairCorrectness(input, off, auto);
                pairs.Add(CreatePair(input, sample, order, off, auto));
            }

            AssertScenarioDeterminism(pairs, workload, recordCount);
            scenarioOrdinal++;
        }
    }

    var gateEligible = IsGateEligible(settings);
    var summaries = CreateSummaries(pairs, gateEligible);
    AssertRunShape(settings, pairs, summaries, gateEligible);
    var gatePassed = !gateEligible || summaries.All(summary => summary.GatePassed);
    var report = new BenchmarkReport(
        SchemaVersion: 1,
        Benchmark: "decoder-pipeline",
        PolicyVersion: DecoderPipelineCore.PolicyVersion,
        Framework: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
        ProcessorCount: Environment.ProcessorCount,
        Rounds: settings.Rounds,
        RecordCounts: settings.RecordCounts,
        Workloads: settings.Workloads.Select(WorkloadName).ToArray(),
        GateEligible: gateEligible,
        GatePassed: gatePassed,
        GateDefinition:
            "Natural and no-candidate median wall-time and peak-working-set regression must be <=3%; candidate-heavy medians must be <=5%; every individual gate-eligible pair in every workload must be <=5%. Functional output and record growth are reported separately.",
        Pairs: pairs,
        Summaries: summaries
    );
    var temporaryOutput = outputPath + ".partial." + Guid.NewGuid().ToString("N");
    await File.WriteAllTextAsync(
        temporaryOutput,
        JsonSerializer.Serialize(report, jsonOptions) + "\n",
        new UTF8Encoding(false)
    );
    File.Move(temporaryOutput, outputPath, overwrite: settings.Overwrite);
    completed = true;

    Console.WriteLine($"Decoder benchmark pairs: {pairs.Count:N0}");
    foreach (var summary in summaries)
    {
        Console.WriteLine(
            $"{summary.Workload}/{summary.RecordCount:N0}: "
                + $"median wall={summary.MedianWallRegressionPercent:F3}%, "
                + $"median peak WS={summary.MedianPeakWorkingSetRegressionPercent:F3}%, "
                + $"max pair wall={summary.MaximumPairWallRegressionPercent:F3}%, "
                + $"max pair peak WS={summary.MaximumPairPeakWorkingSetRegressionPercent:F3}%, "
                + $"gate={(summary.GateApplicable ? summary.GatePassed : "informational")}"
        );
    }
    Console.WriteLine($"Results: {outputPath}");
    Console.WriteLine(
        gateEligible
            ? $"Decoder overhead gate: {(gatePassed ? "passed" : "failed")}"
            : "Decoder overhead gate: not activated (requires at least seven rounds plus both the 100,000-row and 1,000,000-row tiers for all three workloads)."
    );

    if (!gatePassed)
    {
        throw new InvalidDataException(
            "Decoder overhead gate failed. Preserve the result and review the paired measurements before changing policy or implementation."
        );
    }
}
finally
{
    if (completed && !settings.KeepWork)
    {
        Directory.Delete(runRoot, recursive: true);
    }
    else if (Directory.Exists(runRoot))
    {
        Console.Error.WriteLine($"Benchmark work retained for diagnosis: {runRoot}");
    }
}

static BenchmarkSettings ParseArguments(string[] arguments)
{
    string? output = null;
    var rounds = DefaultRounds;
    var recordCounts = new[] { 100_000, 1_000_000 };
    var workloads = Enum.GetValues<BenchmarkWorkload>();
    var overwrite = false;
    var keepWork = false;

    for (var index = 0; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--output":
                output = RequireValue(arguments, ref index, "--output");
                break;
            case "--rounds":
                rounds = ParsePositiveInt(RequireValue(arguments, ref index, "--rounds"));
                break;
            case "--records":
                recordCounts = RequireValue(arguments, ref index, "--records")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParsePositiveInt)
                    .Distinct()
                    .Order()
                    .ToArray();
                if (recordCounts.Length == 0)
                {
                    throw new ArgumentException("--records must name at least one positive count.");
                }
                break;
            case "--workloads":
                workloads = RequireValue(arguments, ref index, "--workloads")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseWorkload)
                    .Distinct()
                    .ToArray();
                if (workloads.Length == 0)
                {
                    throw new ArgumentException("--workloads must name at least one workload.");
                }
                break;
            case "--overwrite": overwrite = true; break;
            case "--keep-work": keepWork = true; break;
            default:
                throw new ArgumentException($"Unknown benchmark argument '{arguments[index]}'.");
        }
    }

    if (string.IsNullOrWhiteSpace(output))
    {
        throw new ArgumentException("--output is required.");
    }
    return new BenchmarkSettings(output, rounds, recordCounts, workloads, overwrite, keepWork);
}

static string RequireValue(string[] arguments, ref int index, string option)
{
    if (++index >= arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal))
    {
        throw new ArgumentException($"{option} requires a value.");
    }
    return arguments[index];
}

static int ParsePositiveInt(string value)
{
    if (
        !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        || parsed < 1
    )
    {
        throw new ArgumentException($"Expected a positive integer, received '{value}'.");
    }
    return parsed;
}

static async Task<CorpusInfo> GenerateCorpusAsync(
    string path,
    BenchmarkWorkload workload,
    int recordCount,
    JsonSerializerOptions jsonOptions
)
{
    await using var stream = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.Read,
        1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan
    );
    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024 * 1024)
    {
        NewLine = "\n",
    };
    for (var index = 0; index < recordCount; index++)
    {
        var record = new EnrichmentStringRecord
        {
            SchemaVersion = EnrichmentRegexPipelineCore.CurrentSchemaVersion,
            RecordType = "string",
            RecordId = ParentRecordId(index),
            Text = CreateText(workload, index),
            SourceFile = "synthetic://decoder-pipeline-benchmark",
            Location = new EnrichmentLocation
            {
                Kind = "synthetic_index",
                Value = index.ToString(CultureInfo.InvariantCulture),
            },
            Origin = new EnrichmentOrigin
            {
                Extractor = "decoder-pipeline-benchmark",
                Version = "1.0.0",
                Kind = "synthetic",
            },
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(record, jsonOptions));
    }
    await writer.FlushAsync();
    var bytes = stream.Length;
    await writer.DisposeAsync();
    return new CorpusInfo(
        WorkloadName(workload),
        recordCount,
        bytes,
        await HashFileAsync(path),
        ExpectedStats(workload, recordCount)
    );
}

static string CreateText(BenchmarkWorkload workload, int index) =>
    workload switch
    {
        BenchmarkWorkload.Natural => NaturalText(index),
        BenchmarkWorkload.NoCandidate =>
            $"SYNTHETIC_MACHINE_EVENT_{index:D9}_STATUS_READY_"
            + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA-",
        BenchmarkWorkload.CandidateHeavy => CandidateText(index),
        _ => throw new ArgumentOutOfRangeException(nameof(workload)),
    };

static string NaturalText(int index)
{
    string[] sentences =
    [
        "This synthetic narrative describes a routine system event with no encoded payload.",
        "Esta frase sintética describe una actividad normal sin datos codificados.",
        "Cette phrase synthétique décrit un événement ordinaire sans charge encodée.",
        "Diese synthetische Meldung beschreibt einen normalen Vorgang ohne Nutzdaten.",
        "この合成文章は、符号化された内容を含まない通常の出来事を説明します。",
        "هذه جملة اصطناعية تصف حدثا عاديا دون حمولة مشفرة.",
    ];
    return $"{sentences[index % sentences.Length]} Synthetic row {index:D9}.";
}

static string CandidateText(int index)
{
    switch (index % CandidateKinds)
    {
        case 0:
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(ExpectedChildText(index)));
        case 1:
            return "pwsh -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(ExpectedChildText(index)));
        case 2:
            return Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"%PDF-1.7 synthetic public fixture {index:D9}")
            );
        case 3:
            return Convert.ToBase64String(
                Enumerable.Range(0, 47).Select(offset => (byte)((index + offset) % 8)).ToArray()
            );
        case 4:
            return Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"synthetic text {index:D9}\0with disallowed NUL")
            );
        case 5:
            return CreateNonCanonicalBase64(index);
        default:
            throw new InvalidOperationException("Candidate workload schedule is invalid.");
    }
}

static string ExpectedChildText(int index) =>
    index % CandidateKinds == 0
        ? $"synthetic decoded URL https://example.invalid/item/{index:D9}"
        : $"Write-Output 'synthetic decoded command {index:D9}'";

static string CreateNonCanonicalBase64(int index)
{
    var bytes = Encoding.ASCII.GetBytes($"noncanonical-{index:D9}");
    Array.Resize(ref bytes, 16);
    var value = Convert.ToBase64String(bytes);
    var dataIndex = value.Length - 3;
    var replacement = value[dataIndex] switch
    {
        'A' => 'B',
        'Q' => 'R',
        'g' => 'h',
        'w' => 'x',
        _ => throw new InvalidOperationException("Unexpected terminal Base64 sextet."),
    };
    return value[..dataIndex] + replacement + value[(dataIndex + 1)..];
}

static ExpectedDecoderStats ExpectedStats(BenchmarkWorkload workload, int recordCount)
{
    if (workload != BenchmarkWorkload.CandidateHeavy)
    {
        return new ExpectedDecoderStats(recordCount, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    var attempted = Math.Min((long)recordCount, DefaultMaximumCandidates);
    long published = 0;
    long known = 0;
    long opaque = 0;
    long rejectedText = 0;
    long rejectedCanonical = 0;
    long decodedBytes = 0;
    for (var index = 0; index < attempted; index++)
    {
        var bytes = CandidateDecodedBytes(index);
        switch (index % CandidateKinds)
        {
            case 0:
            case 1:
                published++;
                decodedBytes += bytes.Length;
                break;
            case 2:
                known++;
                decodedBytes += bytes.Length;
                break;
            case 3:
                opaque++;
                decodedBytes += bytes.Length;
                break;
            case 4:
                rejectedText++;
                decodedBytes += bytes.Length;
                break;
            case 5:
                rejectedCanonical++;
                break;
        }
    }
    return new ExpectedDecoderStats(
        recordCount,
        recordCount,
        attempted,
        attempted,
        published,
        known,
        opaque,
        rejectedText,
        rejectedCanonical,
        recordCount - attempted,
        decodedBytes
    );
}

static byte[] CandidateDecodedBytes(int index) =>
    (index % CandidateKinds) switch
    {
        0 => Encoding.UTF8.GetBytes(ExpectedChildText(index)),
        1 => Encoding.Unicode.GetBytes(ExpectedChildText(index)),
        2 => Encoding.ASCII.GetBytes($"%PDF-1.7 synthetic public fixture {index:D9}"),
        3 => Enumerable.Range(0, 47).Select(offset => (byte)((index + offset) % 8)).ToArray(),
        4 => Encoding.UTF8.GetBytes($"synthetic text {index:D9}\0with disallowed NUL"),
        5 => Convert.FromBase64String(CreateNonCanonicalBase64(index)),
        _ => throw new InvalidOperationException("Candidate workload schedule is invalid."),
    };

static async Task<VariantResult> RunChildProcessAsync(
    string inputPath,
    string scenarioDirectory,
    BenchmarkWorkload workload,
    int recordCount,
    int sample,
    BenchmarkVariant variant,
    string order,
    JsonSerializerOptions jsonOptions
)
{
    var outputDirectory = Path.Combine(
        scenarioDirectory,
        $"sample-{sample:D2}-{VariantName(variant)}"
    );
    Directory.CreateDirectory(outputDirectory);
    var startInfo = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    startInfo.ArgumentList.Add("--child");
    startInfo.ArgumentList.Add(inputPath);
    startInfo.ArgumentList.Add(outputDirectory);
    startInfo.ArgumentList.Add(WorkloadName(workload));
    startInfo.ArgumentList.Add(recordCount.ToString(CultureInfo.InvariantCulture));
    startInfo.ArgumentList.Add(sample.ToString(CultureInfo.InvariantCulture));
    startInfo.ArgumentList.Add(VariantName(variant));
    startInfo.ArgumentList.Add(order);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start decoder benchmark child process.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    if (process.ExitCode != 0)
    {
        throw new InvalidDataException(
            $"Decoder benchmark child {sample}/{VariantName(variant)} failed with exit code {process.ExitCode}: {stderr.Trim()}"
        );
    }
    var result = JsonSerializer.Deserialize<VariantResult>(stdout.Trim(), jsonOptions)
        ?? throw new InvalidDataException("Decoder benchmark child returned no result.");
    Directory.Delete(outputDirectory, recursive: true);
    return result;
}

static async Task<VariantResult> RunChildAsync(
    string inputPath,
    string outputDirectory,
    BenchmarkWorkload workload,
    int recordCount,
    int sample,
    BenchmarkVariant variant,
    string order,
    JsonSerializerOptions jsonOptions
)
{
    var finalPath = Path.Combine(outputDirectory, "final-strings.jsonl");
    var decodedPath = Path.Combine(outputDirectory, "decoded-strings.jsonl");
    var assessmentsPath = Path.Combine(outputDirectory, "decoder-assessments.jsonl");
    var statsPath = Path.Combine(outputDirectory, "decoder-work-stats.json");
    var inputBytes = new FileInfo(inputPath).Length;
    var inputSha256 = await HashFileAsync(inputPath);
    var options = DefaultDecoderOptions();

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    using var currentProcess = Process.GetCurrentProcess();
    currentProcess.Refresh();
    var cpuBefore = currentProcess.TotalProcessorTime;
    var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
    var started = Stopwatch.GetTimestamp();
    DecoderPipelineStats? decoderStats = null;
    DecoderCompletionStats? completion = null;
    EnrichmentMergeStats mergeStats;
    if (variant == BenchmarkVariant.Off)
    {
        mergeStats = await EnrichmentMergeCore.ConcatenateAsync([inputPath], finalPath);
    }
    else
    {
        decoderStats = await DecoderPipelineCore.ProcessAsync(
            inputPath,
            decodedPath,
            assessmentsPath,
            statsPath,
            options
        );
        mergeStats = await EnrichmentMergeCore.ConcatenateAsync(
            [inputPath, decodedPath],
            finalPath
        );
        completion = await DecoderCompletionCore.ValidateAsync(
            inputPath,
            decodedPath,
            assessmentsPath,
            statsPath,
            options
        );
    }
    var elapsed = Stopwatch.GetElapsedTime(started);
    var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
    currentProcess.Refresh();
    var cpu = currentProcess.TotalProcessorTime - cpuBefore;
    var peakWorkingSet = currentProcess.PeakWorkingSet64;

    if (variant == BenchmarkVariant.Auto)
    {
        await ValidateChildrenAsync(decodedPath, workload, decoderStats!);
        await AssertConcatenationAsync(inputPath, decodedPath, finalPath);
    }
    else
    {
        if (mergeStats.OutputRecords != recordCount || mergeStats.InputRecords.Single() != recordCount)
        {
            throw new InvalidDataException("Decoder-off merge cardinality is incorrect.");
        }
        if (!string.Equals(inputSha256, await HashFileAsync(finalPath), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Decoder-off final stream is not byte-identical to raw input.");
        }
    }

    var finalBytes = new FileInfo(finalPath).Length;
    var decodedBytes = File.Exists(decodedPath) ? new FileInfo(decodedPath).Length : 0;
    var assessmentBytes = File.Exists(assessmentsPath) ? new FileInfo(assessmentsPath).Length : 0;
    var statsBytes = File.Exists(statsPath) ? new FileInfo(statsPath).Length : 0;
    var finalSha256 = await HashFileAsync(finalPath);
    return new VariantResult(
        Workload: WorkloadName(workload),
        RecordCount: recordCount,
        Sample: sample,
        Variant: VariantName(variant),
        Order: order,
        WallMilliseconds: elapsed.TotalMilliseconds,
        CpuMilliseconds: cpu.TotalMilliseconds,
        AllocatedBytes: allocatedBytes,
        PeakWorkingSetBytes: peakWorkingSet,
        InputBytes: inputBytes,
        InputSha256: inputSha256,
        FinalBytes: finalBytes,
        DecoderArtifactBytes: decodedBytes + assessmentBytes + statsBytes,
        TotalOutputBytes: finalBytes + decodedBytes + assessmentBytes + statsBytes,
        FinalRecords: mergeStats.OutputRecords,
        FinalSha256: finalSha256,
        CandidateOccurrences: decoderStats?.CandidateOccurrences ?? 0,
        AttemptedCandidates: decoderStats?.AttemptedCandidates ?? 0,
        AssessmentRecords: decoderStats?.AssessmentRecords ?? 0,
        PublishedTextChildren: decoderStats?.PublishedTextChildren ?? 0,
        DecodedBinaryKnown: decoderStats?.DecodedBinaryKnown ?? 0,
        DecodedBinaryOpaque: decoderStats?.DecodedBinaryOpaque ?? 0,
        TextRejected: decoderStats?.TextRejected ?? 0,
        CanonicalRejected: decoderStats?.CanonicalRejected ?? 0,
        ResourceLimited: decoderStats?.ResourceLimited ?? 0,
        SkippedAfterCandidateLimit: decoderStats?.SkippedAfterCandidateLimit ?? 0,
        SkippedAfterTotalByteLimit: decoderStats?.SkippedAfterTotalByteLimit ?? 0,
        DecodedBytesAttempted: decoderStats?.DecodedBytesAttempted ?? 0,
        DecodedStringsSha256: completion?.DecodedStringsSha256,
        AssessmentsSha256: completion?.AssessmentsSha256,
        WorkStatsSha256: completion?.WorkStatsSha256,
        Correctness: "passed"
    );
}

static DecoderPipelineOptions DefaultDecoderOptions() =>
    new(
        DecoderWorkflowMode.Auto,
        DefaultMaximumCandidateCharacters,
        DefaultMaximumDecodedBytesPerRecord,
        DefaultMaximumCandidates,
        DefaultMaximumTotalDecodedBytes
    );

static async Task ValidateChildrenAsync(
    string decodedPath,
    BenchmarkWorkload workload,
    DecoderPipelineStats stats
)
{
    if (workload != BenchmarkWorkload.CandidateHeavy)
    {
        if (stats.PublishedTextChildren != 0 || new FileInfo(decodedPath).Length != 0)
        {
            throw new InvalidDataException("A no-candidate workload published a decoded child.");
        }
        return;
    }

    long children = 0;
    var expectedIndex = 0;
    await foreach (var item in EnrichmentJsonlReader.ReadAsync(decodedPath))
    {
        while (
            expectedIndex < stats.AttemptedCandidates
            && expectedIndex % CandidateKinds is not (0 or 1)
        )
        {
            expectedIndex++;
        }
        if (
            expectedIndex >= stats.AttemptedCandidates
            || item.Record.ParentRecordId != ParentRecordId(expectedIndex)
            || item.Record.Text != ExpectedChildText(expectedIndex)
        )
        {
            throw new InvalidDataException(
                $"Decoded child {children + 1:N0} does not match its authored synthetic witness."
            );
        }
        children++;
        expectedIndex++;
    }
    if (children != stats.PublishedTextChildren)
    {
        throw new InvalidDataException("Decoded child count does not match decoder statistics.");
    }
}

static async Task AssertConcatenationAsync(
    string rawPath,
    string decodedPath,
    string finalPath
)
{
    await using var final = File.OpenRead(finalPath);
    await AssertStreamPrefixAsync(File.OpenRead(rawPath), final, "raw input");
    await AssertStreamPrefixAsync(File.OpenRead(decodedPath), final, "decoded children");
    if (final.ReadByte() != -1)
    {
        throw new InvalidDataException("Final stream contains bytes after the decoded-child suffix.");
    }
}

static async Task AssertStreamPrefixAsync(Stream expected, Stream actual, string label)
{
    await using (expected)
    {
        var expectedBuffer = new byte[1024 * 1024];
        var actualBuffer = new byte[1024 * 1024];
        int read;
        while ((read = await expected.ReadAsync(expectedBuffer)) > 0)
        {
            var actualRead = 0;
            while (actualRead < read)
            {
                var count = await actual.ReadAsync(actualBuffer.AsMemory(actualRead, read - actualRead));
                if (count == 0)
                {
                    throw new InvalidDataException($"Final stream truncates the expected {label}.");
                }
                actualRead += count;
            }
            if (!expectedBuffer.AsSpan(0, read).SequenceEqual(actualBuffer.AsSpan(0, read)))
            {
                throw new InvalidDataException($"Final stream mutates the expected {label}.");
            }
        }
    }
}

static void AssertPairCorrectness(CorpusInfo input, VariantResult off, VariantResult auto)
{
    var expected = input.Expected;
    if (
        off.Correctness != "passed"
        || auto.Correctness != "passed"
        || off.InputBytes != input.Bytes
        || auto.InputBytes != input.Bytes
        || off.InputSha256 != input.Sha256
        || auto.InputSha256 != input.Sha256
        || off.FinalRecords != input.RecordCount
        || off.FinalBytes != input.Bytes
        || off.FinalSha256 != input.Sha256
        || off.CandidateOccurrences != 0
        || off.AttemptedCandidates != 0
        || off.PublishedTextChildren != 0
    )
    {
        throw new InvalidDataException("Decoder-off/raw parity failed.");
    }
    if (
        auto.CandidateOccurrences != expected.CandidateOccurrences
        || auto.AttemptedCandidates != expected.AttemptedCandidates
        || auto.AssessmentRecords != expected.AssessmentRecords
        || auto.PublishedTextChildren != expected.PublishedTextChildren
        || auto.DecodedBinaryKnown != expected.DecodedBinaryKnown
        || auto.DecodedBinaryOpaque != expected.DecodedBinaryOpaque
        || auto.TextRejected != expected.TextRejected
        || auto.CanonicalRejected != expected.CanonicalRejected
        || auto.ResourceLimited != 0
        || auto.SkippedAfterCandidateLimit != expected.SkippedAfterCandidateLimit
        || auto.SkippedAfterTotalByteLimit != 0
        || auto.DecodedBytesAttempted != expected.DecodedBytesAttempted
        || auto.FinalRecords != input.RecordCount + expected.PublishedTextChildren
        || string.IsNullOrWhiteSpace(auto.DecodedStringsSha256)
        || string.IsNullOrWhiteSpace(auto.AssessmentsSha256)
        || string.IsNullOrWhiteSpace(auto.WorkStatsSha256)
    )
    {
        throw new InvalidDataException("Decoder-auto correctness or cardinality failed.");
    }
    if (
        expected.PublishedTextChildren == 0
        && (auto.FinalBytes != input.Bytes || auto.FinalSha256 != input.Sha256)
    )
    {
        throw new InvalidDataException("Decoder-auto changed a final stream with no children.");
    }
}

static void AssertScenarioDeterminism(
    IReadOnlyList<PairResult> allPairs,
    BenchmarkWorkload workload,
    int recordCount
)
{
    var pairs = allPairs
        .Where(pair => pair.Workload == WorkloadName(workload) && pair.RecordCount == recordCount)
        .ToArray();
    if (
        pairs.Select(pair => pair.Off.FinalSha256).Distinct(StringComparer.Ordinal).Count() != 1
        || pairs.Select(pair => pair.Auto.FinalSha256).Distinct(StringComparer.Ordinal).Count() != 1
        || pairs
            .Select(pair => pair.Auto.DecodedStringsSha256)
            .Distinct(StringComparer.Ordinal)
            .Count() != 1
        || pairs
            .Select(pair => pair.Auto.AssessmentsSha256)
            .Distinct(StringComparer.Ordinal)
            .Count() != 1
        || pairs.Select(pair => pair.Auto.WorkStatsSha256).Distinct(StringComparer.Ordinal).Count()
            != 1
    )
    {
        throw new InvalidDataException(
            $"Repeated {WorkloadName(workload)}/{recordCount:N0} runs were not byte-deterministic."
        );
    }
}

static PairResult CreatePair(
    CorpusInfo input,
    int sample,
    string order,
    VariantResult off,
    VariantResult auto
) =>
    new(
        input.Workload,
        input.RecordCount,
        input.Bytes,
        input.Sha256,
        sample,
        order,
        off,
        auto,
        RequiredPercent(auto.WallMilliseconds, off.WallMilliseconds, "wall time"),
        Percent(auto.CpuMilliseconds, off.CpuMilliseconds),
        RequiredPercent(auto.AllocatedBytes, off.AllocatedBytes, "managed allocation"),
        RequiredPercent(
            auto.PeakWorkingSetBytes,
            off.PeakWorkingSetBytes,
            "peak working set"
        ),
        auto.TotalOutputBytes - off.TotalOutputBytes,
        auto.FinalRecords - off.FinalRecords
    );

static IReadOnlyList<ScenarioSummary> CreateSummaries(
    IReadOnlyList<PairResult> pairs,
    bool gateEligible
) =>
    pairs
        .GroupBy(pair => (pair.Workload, pair.RecordCount))
        .OrderBy(group => group.Key.RecordCount)
        .ThenBy(group => group.Key.Workload, StringComparer.Ordinal)
        .Select(group =>
        {
            var wallMedian = Median(group.Select(pair => pair.WallRegressionPercent));
            var peakMedian = Median(group.Select(pair => pair.PeakWorkingSetRegressionPercent));
            var wallMaximum = group.Max(pair => pair.WallRegressionPercent);
            var peakMaximum = group.Max(pair => pair.PeakWorkingSetRegressionPercent);
            var applicable = gateEligible;
            var medianLimit =
                group.Key.Workload == "candidate-heavy"
                    ? MaximumPairRegressionPercent
                    : MaximumMedianRegressionPercent;
            var passed =
                !applicable
                || (
                    wallMedian <= medianLimit
                    && peakMedian <= medianLimit
                    && wallMaximum <= MaximumPairRegressionPercent
                    && peakMaximum <= MaximumPairRegressionPercent
                );
            return new ScenarioSummary(
                group.Key.Workload,
                group.Key.RecordCount,
                group.Count(),
                wallMedian,
                MedianOrNull(group.Select(pair => pair.CpuRegressionPercent)),
                Median(group.Select(pair => pair.AllocationRegressionPercent)),
                peakMedian,
                wallMaximum,
                peakMaximum,
                Median(group.Select(pair => (double)pair.FunctionalOutputGrowthBytes)),
                Median(group.Select(pair => (double)pair.FunctionalRecordGrowth)),
                medianLimit,
                applicable,
                passed
            );
        })
        .ToArray();

static bool IsGateEligible(BenchmarkSettings settings) =>
    settings.Rounds >= DefaultRounds
    && settings.RecordCounts.Contains(100_000)
    && settings.RecordCounts.Contains(1_000_000)
    && Enum.GetValues<BenchmarkWorkload>().All(settings.Workloads.Contains);

static void AssertStaticFixtureContract()
{
    for (var index = 0; index < CandidateKinds; index++)
    {
        if (!DecoderPipelineCore.IsCandidate(CandidateText(index), DecoderWorkflowMode.Auto))
        {
            throw new InvalidDataException(
                $"Candidate-heavy fixture class {index} does not reach the decoder in auto mode."
            );
        }
    }
    for (var index = 0; index < 120; index++)
    {
        if (
            DecoderPipelineCore.IsCandidate(NaturalText(index), DecoderWorkflowMode.Auto)
            || DecoderPipelineCore.IsCandidate(
                CreateText(BenchmarkWorkload.NoCandidate, index),
                DecoderWorkflowMode.Auto
            )
        )
        {
            throw new InvalidDataException(
                "A preregistered no-candidate fixture reaches the decoder in auto mode."
            );
        }
    }
    var nonCanonical = CreateNonCanonicalBase64(5);
    if (
        string.Equals(
            Convert.ToBase64String(Convert.FromBase64String(nonCanonical)),
            nonCanonical,
            StringComparison.Ordinal
        )
    )
    {
        throw new InvalidDataException("The noncanonical pad-bit fixture became canonical.");
    }
    var expected = ExpectedStats(BenchmarkWorkload.CandidateHeavy, 60);
    if (
        expected.CandidateOccurrences != 60
        || expected.AttemptedCandidates != 60
        || expected.AssessmentRecords != 60
        || expected.PublishedTextChildren != 20
        || expected.DecodedBinaryKnown != 10
        || expected.DecodedBinaryOpaque != 10
        || expected.TextRejected != 10
        || expected.CanonicalRejected != 10
        || expected.SkippedAfterCandidateLimit != 0
    )
    {
        throw new InvalidDataException("The fixed candidate-heavy fixture schedule drifted.");
    }
}

static void AssertRunShape(
    BenchmarkSettings settings,
    IReadOnlyList<PairResult> pairs,
    IReadOnlyList<ScenarioSummary> summaries,
    bool gateEligible
)
{
    var expectedScenarios = checked(settings.RecordCounts.Length * settings.Workloads.Length);
    var expectedPairs = checked(expectedScenarios * settings.Rounds);
    if (pairs.Count != expectedPairs || summaries.Count != expectedScenarios)
    {
        throw new InvalidDataException(
            $"Benchmark shape is incomplete: pairs={pairs.Count:N0}/{expectedPairs:N0}, summaries={summaries.Count:N0}/{expectedScenarios:N0}."
        );
    }
    if (gateEligible != IsGateEligible(settings))
    {
        throw new InvalidDataException("Benchmark gate eligibility does not match the frozen tier contract.");
    }
    foreach (var group in pairs.GroupBy(pair => (pair.Workload, pair.RecordCount)))
    {
        if (
            group.Count() != settings.Rounds
            || !group.Select(pair => pair.Sample).Order().SequenceEqual(
                Enumerable.Range(1, settings.Rounds)
            )
            || group.Any(pair => pair.Off.Variant != "off" || pair.Auto.Variant != "auto")
            || Math.Abs(
                group.Count(pair => pair.Order == "auto-first")
                    - group.Count(pair => pair.Order == "off-first")
            ) > 1
        )
        {
            throw new InvalidDataException(
                $"Rotated pair shape is invalid for {group.Key.Workload}/{group.Key.RecordCount:N0}."
            );
        }
    }
    foreach (var summary in summaries)
    {
        var expectedMedianLimit =
            summary.Workload == "candidate-heavy"
                ? MaximumPairRegressionPercent
                : MaximumMedianRegressionPercent;
        if (
            summary.Pairs != settings.Rounds
            || summary.GateApplicable != gateEligible
            || summary.MedianLimitPercent != expectedMedianLimit
        )
        {
            throw new InvalidDataException(
                $"Summary gate shape is invalid for {summary.Workload}/{summary.RecordCount:N0}."
            );
        }
    }
}

static double? Percent(double candidate, double baseline) =>
    baseline == 0 ? null : ((candidate / baseline) - 1) * 100;

static double RequiredPercent(double candidate, double baseline, string metric) =>
    Percent(candidate, baseline)
    ?? throw new InvalidDataException($"Decoder-off {metric} was zero; paired regression is undefined.");

static double? MedianOrNull(IEnumerable<double?> values)
{
    var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
    return present.Length == 0 ? null : Median(present);
}

static double Median(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    if (ordered.Length == 0)
    {
        throw new InvalidOperationException("No benchmark measurements were recorded.");
    }
    return ordered.Length % 2 == 1
        ? ordered[ordered.Length / 2]
        : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
}

static string ParentRecordId(int index) => $"synthetic-raw-{index:D9}";

static async Task<string> HashFileAsync(string path)
{
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan
    );
    return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
}

static BenchmarkWorkload ParseWorkload(string value) =>
    value.Trim().ToLowerInvariant() switch
    {
        "natural" => BenchmarkWorkload.Natural,
        "no-candidate" => BenchmarkWorkload.NoCandidate,
        "candidate-heavy" => BenchmarkWorkload.CandidateHeavy,
        _ => throw new ArgumentException(
            $"Unknown workload '{value}'. Expected natural, no-candidate, or candidate-heavy."
        ),
    };

static BenchmarkVariant ParseVariant(string value) =>
    value switch
    {
        "off" => BenchmarkVariant.Off,
        "auto" => BenchmarkVariant.Auto,
        _ => throw new ArgumentException($"Unknown benchmark variant '{value}'."),
    };

static string WorkloadName(BenchmarkWorkload workload) =>
    workload switch
    {
        BenchmarkWorkload.Natural => "natural",
        BenchmarkWorkload.NoCandidate => "no-candidate",
        BenchmarkWorkload.CandidateHeavy => "candidate-heavy",
        _ => throw new ArgumentOutOfRangeException(nameof(workload)),
    };

static string VariantName(BenchmarkVariant variant) =>
    variant == BenchmarkVariant.Off ? "off" : "auto";

static void WriteHelp() =>
    Console.WriteLine(
        """
        DecoderPipelineBenchmark

        Reproducible synthetic/public ADR-0010 decoder overhead and correctness gate.

        Usage:
          dotnet run --project .\benchmarks\DecoderPipelineBenchmark -c Release -- [options]

        Options:
          --output <path>       Required JSON result path; existing files are refused.
          --records <csv>       Row tiers (default: 100000,1000000).
          --rounds <count>      Rotated off/auto pairs per scenario (default: 7).
          --workloads <csv>     natural,no-candidate,candidate-heavy (default: all).
          --overwrite           Explicitly replace an existing result.
          --keep-work           Retain generated corpora and child artifacts.
          --help                Show this help.

        The timing gate activates only with at least seven rounds and both exact ADR
        tiers (100,000 and 1,000,000 rows) across all workloads. Correctness,
        cardinality, hashes, and deterministic replay are always enforced.
        Candidate-heavy timing has a 5% median cap; functional output and record
        growth remain separate informational metrics.
        """
    );

internal enum BenchmarkWorkload
{
    Natural,
    NoCandidate,
    CandidateHeavy,
}

internal enum BenchmarkVariant
{
    Off,
    Auto,
}

internal sealed record BenchmarkSettings(
    string OutputPath,
    int Rounds,
    int[] RecordCounts,
    BenchmarkWorkload[] Workloads,
    bool Overwrite,
    bool KeepWork
);

internal sealed record ExpectedDecoderStats(
    long InputRecords,
    long CandidateOccurrences,
    long AttemptedCandidates,
    long AssessmentRecords,
    long PublishedTextChildren,
    long DecodedBinaryKnown,
    long DecodedBinaryOpaque,
    long TextRejected,
    long CanonicalRejected,
    long SkippedAfterCandidateLimit,
    long DecodedBytesAttempted
);

internal sealed record CorpusInfo(
    string Workload,
    int RecordCount,
    long Bytes,
    string Sha256,
    ExpectedDecoderStats Expected
);

internal sealed record VariantResult(
    string Workload,
    int RecordCount,
    int Sample,
    string Variant,
    string Order,
    double WallMilliseconds,
    double CpuMilliseconds,
    long AllocatedBytes,
    long PeakWorkingSetBytes,
    long InputBytes,
    string InputSha256,
    long FinalBytes,
    long DecoderArtifactBytes,
    long TotalOutputBytes,
    long FinalRecords,
    string FinalSha256,
    long CandidateOccurrences,
    long AttemptedCandidates,
    long AssessmentRecords,
    long PublishedTextChildren,
    long DecodedBinaryKnown,
    long DecodedBinaryOpaque,
    long TextRejected,
    long CanonicalRejected,
    long ResourceLimited,
    long SkippedAfterCandidateLimit,
    long SkippedAfterTotalByteLimit,
    long DecodedBytesAttempted,
    string? DecodedStringsSha256,
    string? AssessmentsSha256,
    string? WorkStatsSha256,
    string Correctness
);

internal sealed record PairResult(
    string Workload,
    int RecordCount,
    long InputBytes,
    string InputSha256,
    int Sample,
    string Order,
    VariantResult Off,
    VariantResult Auto,
    double WallRegressionPercent,
    double? CpuRegressionPercent,
    double AllocationRegressionPercent,
    double PeakWorkingSetRegressionPercent,
    long FunctionalOutputGrowthBytes,
    long FunctionalRecordGrowth
);

internal sealed record ScenarioSummary(
    string Workload,
    int RecordCount,
    int Pairs,
    double MedianWallRegressionPercent,
    double? MedianCpuRegressionPercent,
    double MedianAllocationRegressionPercent,
    double MedianPeakWorkingSetRegressionPercent,
    double MaximumPairWallRegressionPercent,
    double MaximumPairPeakWorkingSetRegressionPercent,
    double MedianFunctionalOutputGrowthBytes,
    double MedianFunctionalRecordGrowth,
    double MedianLimitPercent,
    bool GateApplicable,
    bool GatePassed
);

internal sealed record BenchmarkReport(
    int SchemaVersion,
    string Benchmark,
    string PolicyVersion,
    string Framework,
    string OperatingSystem,
    string ProcessArchitecture,
    int ProcessorCount,
    int Rounds,
    int[] RecordCounts,
    string[] Workloads,
    bool GateEligible,
    bool GatePassed,
    string GateDefinition,
    IReadOnlyList<PairResult> Pairs,
    IReadOnlyList<ScenarioSummary> Summaries
);
