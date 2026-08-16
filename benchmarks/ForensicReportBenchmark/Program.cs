using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using bstrings;

var options = ParseArguments(args);
var recordCount = ParsePositiveInt(options.GetValueOrDefault("records", "100000"), "records");
var distinctFeatures = ParsePositiveInt(
    options.GetValueOrDefault("distinct-features", "10000"),
    "distinct-features"
);
if (distinctFeatures > recordCount)
{
    throw new ArgumentOutOfRangeException(
        "distinct-features",
        "--distinct-features cannot exceed --records."
    );
}
var rounds = ParsePositiveInt(options.GetValueOrDefault("rounds", "5"), "rounds");
var outputPath = Path.GetFullPath(
    options.GetValueOrDefault("output", "forensic-report-benchmark.csv")
);
if (File.Exists(outputPath))
{
    throw new IOException($"Refusing to overwrite {outputPath}.");
}
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var workRoot = Path.Combine(
    Path.GetTempPath(),
    "bstrings-forensic-report-benchmark-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(workRoot);
var matchesPath = Path.Combine(workRoot, "matches.jsonl");
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

try
{
    await WriteFixtureAsync(matchesPath, recordCount, distinctFeatures, jsonOptions);
    var inputBytes = new FileInfo(matchesPath).Length;
    var results = new List<BenchmarkResult>(rounds);
    for (var round = 1; round <= rounds; round++)
    {
        var roundDirectory = Path.Combine(workRoot, $"round-{round:D2}");
        Directory.CreateDirectory(roundDirectory);
        var findingsPath = Path.Combine(roundDirectory, "findings.tsv");
        var patternHistogramPath = Path.Combine(roundDirectory, "pattern-histogram.tsv");
        var featureHistogramPath = Path.Combine(roundDirectory, "feature-histogram.tsv");
        var visualizationPath = Path.Combine(roundDirectory, "pattern-histogram.html");

        var process = Process.GetCurrentProcess();
        var stopwatch = Stopwatch.StartNew();
        var stats = await ForensicReportCore.WriteAsync(
            matchesPath,
            findingsPath,
            patternHistogramPath,
            featureHistogramPath,
            visualizationPath,
            [
                ("email", BuiltInPatternCatalog.Patterns["email"]),
                ("jwt", BuiltInPatternCatalog.Patterns["jwt"]),
            ]
        );
        stopwatch.Stop();
        process.Refresh();

        var exact = stats == new ForensicReportStats(recordCount, 2, distinctFeatures)
            && CountPhysicalLines(findingsPath, ForensicReportCore.FindingsHeader) == recordCount + 1L
            && CountPhysicalLines(
                patternHistogramPath,
                ForensicReportCore.PatternHistogramHeader
            ) == 3
            && CountPhysicalLines(
                featureHistogramPath,
                ForensicReportCore.FeatureHistogramHeader
            ) == distinctFeatures + 1L
            && File.ReadAllText(patternHistogramPath).Contains(
                "jwt\tcredential",
                StringComparison.Ordinal
            );
        if (!exact)
        {
            throw new InvalidDataException($"Round {round} failed exact report validation.");
        }

        var outputBytes = Directory.EnumerateFiles(roundDirectory)
            .Sum(path => new FileInfo(path).Length);
        var result = new BenchmarkResult(
            round,
            recordCount,
            distinctFeatures,
            inputBytes,
            outputBytes,
            stopwatch.Elapsed.TotalSeconds,
            recordCount / stopwatch.Elapsed.TotalSeconds,
            process.PeakWorkingSet64,
            exact
        );
        results.Add(result);
        Console.WriteLine(
            $"round {round}: {result.ElapsedSeconds:F3} s, "
                + $"{result.RowsPerSecond:N0} rows/s, exact={result.Exact.ToString().ToLowerInvariant()}"
        );
        Directory.Delete(roundDirectory, recursive: true);
    }

    WriteResults(outputPath, results);
}
finally
{
    if (Directory.Exists(workRoot))
    {
        Directory.Delete(workRoot, recursive: true);
    }
}

static async Task WriteFixtureAsync(
    string path,
    int recordCount,
    int distinctFeatures,
    JsonSerializerOptions options
)
{
    await using var writer = new StreamWriter(
        new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan
        ),
        new UTF8Encoding(false),
        1024 * 1024
    );
    var emailPattern = BuiltInPatternCatalog.ByName["email"];
    for (var index = 0; index < recordCount; index++)
    {
        var feature = $"user-{index % distinctFeatures:D8}@example.test";
        var record = new
        {
            SchemaVersion = EnrichmentRegexPipelineCore.CurrentSchemaVersion,
            RecordType = "regex-match",
            PatternName = "email",
            Pattern = BuiltInPatternCatalog.Patterns["email"],
            PatternDescription = emailPattern.Description,
            PatternSource = emailPattern.Source,
            PatternValidation = "Email",
            Match = feature,
            MatchStart = 16,
            MatchLength = feature.Length,
            MatchLine = 1,
            ContextStart = 0,
            Context = $"account active {feature} source=synthetic",
            SourceRecordId = $"record-{index:D12}",
            SourceFile = $@"C:\Synthetic\Profiles\profile-{index % 256:D3}\Login Data",
            Location = new
            {
                Kind = "file_offset",
                Value = $"0x{index * 64L:X}",
            },
            Origin = new
            {
                Extractor = "bstrings",
                Version = "benchmark",
                Kind = "static",
            },
            EvidenceClass = "byte-native",
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(record, options));
    }
}

static long CountPhysicalLines(string path, string expectedHeader)
{
    using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
    var header = reader.ReadLine();
    if (header is null || !string.Equals(header, expectedHeader, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Unexpected header in {path}.");
    }
    var expectedColumns = header.Split('\t').Length;
    long count = 1;
    while (reader.ReadLine() is { } line)
    {
        if (line.Split('\t').Length != expectedColumns)
        {
            throw new InvalidDataException($"Column-count mismatch in {path} at line {count + 1}.");
        }
        count++;
    }
    return count;
}

static void WriteResults(string path, IEnumerable<BenchmarkResult> results)
{
    var csv = new StringBuilder();
    csv.AppendLine(
        "Round,Records,DistinctFeatures,InputBytes,OutputBytes,ElapsedSeconds,RowsPerSecond,PeakWorkingSetBytes,Exact"
    );
    foreach (var result in results)
    {
        csv.Append(result.Round.ToString(CultureInfo.InvariantCulture)).Append(',');
        csv.Append(result.Records.ToString(CultureInfo.InvariantCulture)).Append(',');
        csv.Append(result.DistinctFeatures.ToString(CultureInfo.InvariantCulture)).Append(',');
        csv.Append(result.InputBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
        csv.Append(result.OutputBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
        csv.Append(result.ElapsedSeconds.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        csv.Append(result.RowsPerSecond.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        csv.Append(result.PeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
        csv.AppendLine(result.Exact ? "true" : "false");
    }
    File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal) || ++index >= arguments.Length)
        {
            throw new ArgumentException($"Expected --name value, received {argument}.");
        }
        if (!parsed.TryAdd(argument[2..], arguments[index]))
        {
            throw new ArgumentException($"Duplicate argument {argument}.");
        }
    }
    return parsed;
}

static int ParsePositiveInt(string value, string name) =>
    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
    && parsed > 0
        ? parsed
        : throw new ArgumentOutOfRangeException(name, $"--{name} must be a positive integer.");

internal sealed record BenchmarkResult(
    int Round,
    int Records,
    int DistinctFeatures,
    long InputBytes,
    long OutputBytes,
    double ElapsedSeconds,
    double RowsPerSecond,
    long PeakWorkingSetBytes,
    bool Exact
);
