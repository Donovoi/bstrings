using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bstrings;

var arguments = ParseArguments(args);
var recordCount = ParsePositiveInt(arguments.GetValueOrDefault("records", "100000"), "records");
var duplicatePercent = ParsePercentage(
    arguments.GetValueOrDefault("duplicate-percent", "50"),
    "duplicate-percent"
);
var rounds = ParsePositiveInt(arguments.GetValueOrDefault("rounds", "7"), "rounds");
if (
    !LanguageDetectionCore.TryParseMode(
        arguments.GetValueOrDefault("mode", "accurate"),
        out var detectionMode,
        out var modeError
    )
)
{
    throw new ArgumentException(modeError, "mode");
}

var outputPath = Path.GetFullPath(
    arguments.GetValueOrDefault("output", "language-triage-benchmark.csv")
);
if (File.Exists(outputPath))
{
    throw new IOException($"Refusing to overwrite {outputPath}.");
}
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

if (!LanguageDetectionCore.TryVerifyAvailability(out var availabilityError))
{
    throw new InvalidOperationException(availabilityError);
}

var workRoot = Path.Combine(
    Path.GetTempPath(),
    "bstrings-language-triage-benchmark-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(workRoot);
var inputPath = Path.Combine(workRoot, "synthetic-records.jsonl");
var benchmarkRows = new List<BenchmarkRow>(rounds * 2);
var completed = false;

try
{
    await WriteFixtureAsync(inputPath, recordCount, duplicatePercent);
    WarmDetector(detectionMode);

    for (var round = 1; round <= rounds; round++)
    {
        var variants = (round & 1) == 1
            ? new[] { BenchmarkVariant.Baseline, BenchmarkVariant.Reuse }
            : new[] { BenchmarkVariant.Reuse, BenchmarkVariant.Baseline };
        var pair = new List<BenchmarkRow>(2);

        for (var order = 0; order < variants.Length; order++)
        {
            var variant = variants[order];
            var prefix = $"round-{round:D2}-{VariantName(variant)}";
            var candidatesPath = Path.Combine(workRoot, prefix + "-candidates.jsonl");
            var assessmentsPath = Path.Combine(workRoot, prefix + "-assessments.jsonl");

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var gen0Before = GC.CollectionCount(0);
            var gen1Before = GC.CollectionCount(1);
            var gen2Before = GC.CollectionCount(2);
            var stopwatch = Stopwatch.StartNew();

            var stats = await LanguageTriageCore.ProcessAsync(
                inputPath,
                candidatesPath,
                assessmentsPath,
                new LanguageTriageOptions(
                    TargetLanguage: "en",
                    DetectionMode: detectionMode,
                    Policy: LanguageTriagePolicy.HighRecall,
                    MinimumConfidence: 0.65,
                    MinimumTargetMargin: 0.08,
                    MinimumCharacters: 8,
                    MaximumCharacters: 2048,
                    BatchSize: 2048,
                    MaxDegreeOfParallelism: Math.Max(1, Environment.ProcessorCount)
                ),
                detector: null,
                reuseSuccessfulDetections: variant == BenchmarkVariant.Reuse
            );

            stopwatch.Stop();
            process.Refresh();
            var row = new BenchmarkRow(
                recordCount,
                duplicatePercent,
                detectionMode.ToString().ToLowerInvariant(),
                VariantName(variant),
                round,
                order,
                stopwatch.Elapsed.TotalSeconds,
                (process.TotalProcessorTime - cpuBefore).TotalSeconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                GC.CollectionCount(0) - gen0Before,
                GC.CollectionCount(1) - gen1Before,
                GC.CollectionCount(2) - gen2Before,
                process.WorkingSet64,
                stats,
                new FileInfo(candidatesPath).Length,
                new FileInfo(assessmentsPath).Length,
                GetSha256(candidatesPath),
                GetSha256(assessmentsPath)
            );
            pair.Add(row);
            benchmarkRows.Add(row);
        }

        AssertPairExact(pair, recordCount);
    }

    await WriteCsvAsync(outputPath, benchmarkRows);
    WriteSummary(benchmarkRows, outputPath);
    completed = true;
}
finally
{
    if (completed && Directory.Exists(workRoot))
    {
        Directory.Delete(workRoot, recursive: true);
    }
    else if (Directory.Exists(workRoot))
    {
        Console.Error.WriteLine($"Failed benchmark retained for diagnosis: {workRoot}");
    }
}

static void WarmDetector(LanguageDetectionMode mode)
{
    foreach (
        var text in new[]
        {
            "This deterministic English benchmark sentence warms the bundled language detector before measurement.",
            "Esta frase espanola determinista prepara el detector de idioma incluido antes de la medicion.",
        }
    )
    {
        if (!LanguageDetectionCore.TryDetect(text, mode, "en", out _, out var error))
        {
            throw new InvalidOperationException($"Language-detector warmup failed: {error}");
        }
    }
}

static async Task WriteFixtureAsync(string path, int records, double duplicatePercent)
{
    await using var stream = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.Read,
        1024 * 1024,
        FileOptions.SequentialScan
    );
    await using var writer = new StreamWriter(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        1024 * 1024
    );
    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    const int batchSize = 2048;

    for (var batchStart = 0; batchStart < records; batchStart += batchSize)
    {
        var batchCount = Math.Min(batchSize, records - batchStart);
        var duplicateCount = (int)Math.Round(
            batchCount * duplicatePercent / 100,
            MidpointRounding.AwayFromZero
        );
        duplicateCount = Math.Clamp(duplicateCount, 0, Math.Max(0, batchCount - 1));
        var uniqueCount = batchCount - duplicateCount;
        var duplicatePool = Math.Max(1, Math.Min(uniqueCount, 16));

        for (var localIndex = 0; localIndex < batchCount; localIndex++)
        {
            var recordIndex = batchStart + localIndex;
            var textIdentity = localIndex < uniqueCount
                ? recordIndex
                : batchStart + ((localIndex - uniqueCount) % duplicatePool);
            var text = CreateText(textIdentity);
            var record = new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId = $"synthetic-language-{recordIndex:D10}",
                text,
                sourceFile = "synthetic-language-benchmark.jsonl",
                location = new
                {
                    kind = "file-offset",
                    value = recordIndex.ToString(CultureInfo.InvariantCulture),
                },
                origin = new { extractor = "benchmark", kind = "native" },
                attributes = new { benchmark = true },
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, jsonOptions));
        }
    }
}

static string CreateText(int identity) =>
    (identity & 1) == 0
        ? $"This is deterministic English forensic benchmark sentence {identity:D10}. It contains enough natural language words to exercise accurate and fast classification while retaining a unique synthetic identity."
        : $"Esta es la frase forense espanola determinista {identity:D10}. Contiene suficientes palabras de lenguaje natural para ejercitar la clasificacion precisa y rapida con una identidad sintetica unica.";

static void AssertPairExact(IReadOnlyList<BenchmarkRow> pair, int expectedRecords)
{
    if (pair.Count != 2)
    {
        throw new InvalidOperationException("A benchmark round did not produce two variants.");
    }
    var baseline = pair.Single(row => row.Variant == "baseline");
    var reuse = pair.Single(row => row.Variant == "reuse");
    if (
        baseline.Stats != reuse.Stats
        || baseline.Stats.InputRecords != expectedRecords
        || baseline.CandidatesBytes != reuse.CandidatesBytes
        || baseline.AssessmentsBytes != reuse.AssessmentsBytes
        || !string.Equals(
            baseline.CandidatesSha256,
            reuse.CandidatesSha256,
            StringComparison.Ordinal
        )
        || !string.Equals(
            baseline.AssessmentsSha256,
            reuse.AssessmentsSha256,
            StringComparison.Ordinal
        )
    )
    {
        throw new InvalidDataException(
            $"Round {baseline.Round} produced different baseline and reuse outputs: "
                + $"statsEqual={baseline.Stats == reuse.Stats}, "
                + $"candidateBytes={baseline.CandidatesBytes}/{reuse.CandidatesBytes}, "
                + $"assessmentBytes={baseline.AssessmentsBytes}/{reuse.AssessmentsBytes}, "
                + $"candidateHashesEqual={string.Equals(baseline.CandidatesSha256, reuse.CandidatesSha256, StringComparison.Ordinal)}, "
                + $"assessmentHashesEqual={string.Equals(baseline.AssessmentsSha256, reuse.AssessmentsSha256, StringComparison.Ordinal)}."
        );
    }
}

static string GetSha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static async Task WriteCsvAsync(string path, IReadOnlyCollection<BenchmarkRow> rows)
{
    await using var writer = new StreamWriter(
        path,
        append: false,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    );
    await writer.WriteLineAsync(
        "records,duplicatePercent,mode,variant,round,order,elapsedSeconds,recordsPerSecond,cpuSeconds,allocatedBytes,gen0,gen1,gen2,workingSetBytes,inputRecords,targetRecords,translationCandidates,ambiguousRecords,nonLinguisticRecords,detectorFailures,candidatesBytes,assessmentsBytes,candidatesSha256,assessmentsSha256"
    );
    foreach (var row in rows)
    {
        await writer.WriteLineAsync(
            string.Join(
                ',',
                row.Records.ToString(CultureInfo.InvariantCulture),
                row.DuplicatePercent.ToString("0.####", CultureInfo.InvariantCulture),
                row.Mode,
                row.Variant,
                row.Round.ToString(CultureInfo.InvariantCulture),
                row.Order.ToString(CultureInfo.InvariantCulture),
                row.ElapsedSeconds.ToString("0.000000", CultureInfo.InvariantCulture),
                (row.Records / row.ElapsedSeconds).ToString("0.0", CultureInfo.InvariantCulture),
                row.CpuSeconds.ToString("0.000000", CultureInfo.InvariantCulture),
                row.AllocatedBytes.ToString(CultureInfo.InvariantCulture),
                row.Gen0.ToString(CultureInfo.InvariantCulture),
                row.Gen1.ToString(CultureInfo.InvariantCulture),
                row.Gen2.ToString(CultureInfo.InvariantCulture),
                row.WorkingSetBytes.ToString(CultureInfo.InvariantCulture),
                row.Stats.InputRecords.ToString(CultureInfo.InvariantCulture),
                row.Stats.TargetLanguageRecords.ToString(CultureInfo.InvariantCulture),
                row.Stats.TranslationCandidates.ToString(CultureInfo.InvariantCulture),
                row.Stats.AmbiguousRecords.ToString(CultureInfo.InvariantCulture),
                row.Stats.NonLinguisticRecords.ToString(CultureInfo.InvariantCulture),
                row.Stats.DetectorFailures.ToString(CultureInfo.InvariantCulture),
                row.CandidatesBytes.ToString(CultureInfo.InvariantCulture),
                row.AssessmentsBytes.ToString(CultureInfo.InvariantCulture),
                row.CandidatesSha256,
                row.AssessmentsSha256
            )
        );
    }
}

static void WriteSummary(IReadOnlyCollection<BenchmarkRow> rows, string outputPath)
{
    var baseline = Median(rows.Where(row => row.Variant == "baseline").Select(row => row.ElapsedSeconds));
    var reuse = Median(rows.Where(row => row.Variant == "reuse").Select(row => row.ElapsedSeconds));
    Console.WriteLine($"Baseline median: {baseline:F3} s");
    Console.WriteLine($"Reuse median: {reuse:F3} s");
    Console.WriteLine($"Speedup: {baseline / reuse:F3}x");
    Console.WriteLine("Output parity: passed");
    Console.WriteLine($"Results: {outputPath}");
}

static double Median(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    if (ordered.Length == 0)
    {
        throw new InvalidOperationException("No benchmark values were recorded.");
    }
    return (ordered.Length & 1) == 1
        ? ordered[ordered.Length / 2]
        : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (
            !arguments[index].StartsWith("--", StringComparison.Ordinal)
            || index + 1 >= arguments.Length
        )
        {
            throw new ArgumentException($"Invalid benchmark argument near '{arguments[index]}'.");
        }
        parsed.Add(arguments[index][2..], arguments[index + 1]);
    }
    return parsed;
}

static int ParsePositiveInt(string value, string name)
{
    if (
        !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        || parsed < 1
    )
    {
        throw new ArgumentOutOfRangeException(name, $"--{name} must be a positive integer.");
    }
    return parsed;
}

static double ParsePercentage(string value, string name)
{
    if (
        !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        || !double.IsFinite(parsed)
        || parsed is < 0 or > 100
    )
    {
        throw new ArgumentOutOfRangeException(name, $"--{name} must be between 0 and 100.");
    }
    return parsed;
}

static string VariantName(BenchmarkVariant variant) =>
    variant == BenchmarkVariant.Reuse ? "reuse" : "baseline";

internal enum BenchmarkVariant
{
    Baseline,
    Reuse,
}

internal sealed record BenchmarkRow(
    int Records,
    double DuplicatePercent,
    string Mode,
    string Variant,
    int Round,
    int Order,
    double ElapsedSeconds,
    double CpuSeconds,
    long AllocatedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    long WorkingSetBytes,
    LanguageTriageStats Stats,
    long CandidatesBytes,
    long AssessmentsBytes,
    string CandidatesSha256,
    string AssessmentsSha256
);
