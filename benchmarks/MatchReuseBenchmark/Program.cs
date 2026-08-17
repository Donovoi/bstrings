using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bstrings;

var arguments = ParseArguments(args);
var mode = Require(arguments, "mode");
if (string.Equals(mode, "generate", StringComparison.OrdinalIgnoreCase))
{
    var path = Path.GetFullPath(Require(arguments, "input"));
    var workload = Require(arguments, "workload");
    var records = ParsePositiveInt(Require(arguments, "records"), "records");
    if (File.Exists(path))
    {
        throw new IOException($"Refusing to overwrite {path}.");
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await WriteFixtureAsync(path, workload, records);
    return;
}

if (!string.Equals(mode, "run", StringComparison.OrdinalIgnoreCase))
{
    throw new ArgumentException("--mode must be generate or run.");
}

var inputPath = Path.GetFullPath(Require(arguments, "input"));
var outputPath = Path.GetFullPath(Require(arguments, "output"));
var resultPath = Path.GetFullPath(Require(arguments, "result"));
var variant = Require(arguments, "variant");
var patternSelection = arguments.GetValueOrDefault("patterns", "all");
if (!File.Exists(inputPath))
{
    throw new FileNotFoundException("Benchmark input was not found.", inputPath);
}
if (File.Exists(outputPath) || File.Exists(resultPath))
{
    throw new IOException("Benchmark output and result paths must be new.");
}
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);

var cacheOptions = variant.ToLowerInvariant() switch
{
    "off" => MatchResultCacheOptions.Disabled,
    "on" => MatchResultCacheOptions.Experimental,
    _ => throw new ArgumentException("--variant must be off or on."),
};
var patterns = SearchCore.ParseRegexPatternsWithNames(
    patternSelection,
    BuiltInPatternCatalog.Patterns,
    BuiltInPatternCatalog.Groups,
    BuiltInPatternCatalog.DefaultPatterns
);
EnrichmentRegexPipelineCore.ValidatePatterns(patterns);

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
var process = Process.GetCurrentProcess();
var cpuStart = process.TotalProcessorTime;
var allocatedStart = GC.GetTotalAllocatedBytes(precise: true);
var stopwatch = Stopwatch.StartNew();
var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
    inputPath,
    outputPath,
    patterns,
    matchCacheOptions: cacheOptions
);
stopwatch.Stop();
var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedStart;
process.Refresh();
var result = new BenchmarkResult(
    variant.ToLowerInvariant(),
    patternSelection,
    stopwatch.Elapsed.TotalSeconds,
    (process.TotalProcessorTime - cpuStart).TotalSeconds,
    allocatedBytes,
    process.PeakWorkingSet64,
    new FileInfo(inputPath).Length,
    new FileInfo(outputPath).Length,
    HashFile(inputPath),
    HashFile(outputPath),
    stats
);
await File.WriteAllTextAsync(
    resultPath,
    JsonSerializer.Serialize(
        result,
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }
    ),
    new UTF8Encoding(false)
);

static async Task WriteFixtureAsync(string path, string workload, int records)
{
    var normalized = workload.ToLowerInvariant();
    if (
        normalized
        is not "unique-no-match"
            and not "unique-match-heavy"
            and not "hot-repeat"
            and not "cold-repeat"
            and not "repeated-feature-unique-context"
            and not "base64"
            and not "oversized"
    )
    {
        throw new ArgumentException($"Unsupported workload '{workload}'.");
    }

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
    var coldDistinct = Math.Max(1, Math.Min(records, 125_000));
    var strictBase64 = Convert.ToBase64String(
        Encoding.UTF8.GetBytes("synthetic forensic command text")
    );
    for (var index = 0; index < records; index++)
    {
        var text = normalized switch
        {
            "unique-no-match" => $"ordinary phrase item {Alpha(index)} punctuation",
            "unique-match-heavy" => $"contact user{index:D8}@example.com",
            "hot-repeat" => $"contact shared{index % 16:D2}@example.com",
            "cold-repeat" => $"contact cold{index % coldDistinct:D8}@example.com",
            "repeated-feature-unique-context" =>
                $"event {Alpha(index)} contact shared@example.com",
            "base64" => (index % 4) switch
            {
                0 => "token SoftwareDistribution",
                1 => $"token {strictBase64}",
                2 => "token 0123456789ABCDEF0123456789ABCDEF",
                _ => "token SGVsbG8=",
            },
            "oversized" => new string('Q', 65_537) + " contact shared@example.com",
            _ => throw new UnreachableException(),
        };
        var record = new
        {
            schemaVersion = 1,
            recordType = "string",
            recordId = $"record-{index:D12}",
            text,
            sourceFile = @"C:\Synthetic\evidence.bin",
            location = new { kind = "file_offset", value = $"0x{index * 64L:X}" },
            origin = new { extractor = "bstrings", version = "benchmark", kind = "static" },
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(record));
    }
}

static string Alpha(int value)
{
    Span<char> buffer = stackalloc char[8];
    for (var index = buffer.Length - 1; index >= 0; index--)
    {
        buffer[index] = (char)('a' + value % 26);
        value /= 26;
    }
    return new string(buffer);
}

static string HashFile(string path)
{
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        1024 * 1024,
        FileOptions.SequentialScan
    );
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index++)
    {
        var name = values[index];
        if (!name.StartsWith("--", StringComparison.Ordinal) || ++index >= values.Length)
        {
            throw new ArgumentException($"Expected --name value, received '{name}'.");
        }
        if (!parsed.TryAdd(name[2..], values[index]))
        {
            throw new ArgumentException($"Duplicate argument '{name}'.");
        }
    }
    return parsed;
}

static string Require(IReadOnlyDictionary<string, string> values, string name) =>
    values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"--{name} is required.");

static int ParsePositiveInt(string value, string name) =>
    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
    && parsed > 0
        ? parsed
        : throw new ArgumentOutOfRangeException(name, $"--{name} must be positive.");

internal sealed record BenchmarkResult(
    string Variant,
    string PatternSelection,
    double WallSeconds,
    double CpuSeconds,
    long ManagedAllocatedBytes,
    long PeakWorkingSetBytes,
    long InputBytes,
    long OutputBytes,
    string InputSha256,
    string OutputSha256,
    EnrichmentPipelineStats Stats
);
