using System.Diagnostics;
using System.Globalization;
using bstrings;

var rounds = ReadPositiveInt(args, "--rounds", 5);
var targetMiB = ReadPositiveInt(args, "--target-mib", 256);
var outputPath = ReadOptionalString(args, "--output");

if (
    !RustAsciiEngine.Configure(
        CpuStringEngineMode.Rust,
        out var rustStatus,
        out var rustError
    )
)
{
    Console.Error.WriteLine($"Rust engine unavailable: {rustError}");
    return 2;
}

Console.WriteLine(rustStatus);
Console.WriteLine(
    $"Each timed sample scans at least {targetMiB:N0} MiB; {rounds} alternating rounds per engine."
);
Console.WriteLine();

int[] sizes = [64 * 1024, 1024 * 1024, 16 * 1024 * 1024];
string[] scenarios = ["sparse-binary", "random-bytes", "dense-ascii", "fragmented-ascii"];
var rows = new List<BenchmarkRow>();

foreach (var scenario in scenarios)
{
    foreach (var size in sizes)
    {
        var data = BuildData(size, scenario);
        var managedExpected = SearchCore.FindAsciiStringHitsManaged(data, 3, -1, 0, 0x20, 0x7E);
        if (
            !RustAsciiEngine.TryRentNativeForTesting(
                data,
                3,
                -1,
                0,
                0x20,
                0x7E,
                out var nativeExpected,
                out rustError
            )
        )
        {
            Console.Error.WriteLine($"Rust engine failed during parity setup: {rustError}");
            return 3;
        }

        using (nativeExpected)
        {
            RequireExactParity(managedExpected, nativeExpected, scenario, size);
        }

        var repetitions = Math.Max(1, checked(targetMiB * 1024 * 1024 / size));
        _ = MeasureManaged(data, 1);
        _ = MeasureRust(data, 1);

        var managedSamples = new List<double>(rounds);
        var rustSamples = new List<double>(rounds);
        for (var round = 0; round < rounds; round++)
        {
            if ((round & 1) == 0)
            {
                managedSamples.Add(MeasureManaged(data, repetitions).ElapsedSeconds);
                rustSamples.Add(MeasureRust(data, repetitions).ElapsedSeconds);
            }
            else
            {
                rustSamples.Add(MeasureRust(data, repetitions).ElapsedSeconds);
                managedSamples.Add(MeasureManaged(data, repetitions).ElapsedSeconds);
            }
        }

        var scannedMiB = (double)size * repetitions / (1024 * 1024);
        var managedMedian = Median(managedSamples);
        var rustMedian = Median(rustSamples);
        var row = new BenchmarkRow(
            scenario,
            size,
            managedExpected.Count,
            repetitions,
            managedMedian,
            rustMedian,
            scannedMiB / managedMedian,
            scannedMiB / rustMedian,
            managedMedian / rustMedian
        );
        rows.Add(row);
        Console.WriteLine(
            $"{scenario,-18} {size / 1024,7:N0} KiB  hits {row.Hits,8:N0}  "
                + $".NET {row.DotNetMiBPerSecond,9:N1} MiB/s  "
                + $"Rust {row.RustMiBPerSecond,9:N1} MiB/s  "
                + $"Rust speedup {row.RustSpeedup,6:N2}x"
        );
    }
}

if (!string.IsNullOrWhiteSpace(outputPath))
{
    var fullOutputPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
    using var writer = new StreamWriter(fullOutputPath, false);
    writer.WriteLine(
        "Scenario,ChunkBytes,Hits,Repetitions,DotNetMedianSeconds,RustMedianSeconds,DotNetMiBPerSecond,RustMiBPerSecond,RustSpeedup"
    );
    foreach (var row in rows)
    {
        writer.WriteLine(
            string.Join(
                ',',
                row.Scenario,
                row.ChunkBytes.ToString(CultureInfo.InvariantCulture),
                row.Hits.ToString(CultureInfo.InvariantCulture),
                row.Repetitions.ToString(CultureInfo.InvariantCulture),
                row.DotNetMedianSeconds.ToString("R", CultureInfo.InvariantCulture),
                row.RustMedianSeconds.ToString("R", CultureInfo.InvariantCulture),
                row.DotNetMiBPerSecond.ToString("R", CultureInfo.InvariantCulture),
                row.RustMiBPerSecond.ToString("R", CultureInfo.InvariantCulture),
                row.RustSpeedup.ToString("R", CultureInfo.InvariantCulture)
            )
        );
    }

    Console.WriteLine();
    Console.WriteLine($"Wrote {fullOutputPath}");
}

return 0;

static Measurement MeasureManaged(byte[] data, int repetitions)
{
    ForceCollection();
    var checksum = 0L;
    var stopwatch = Stopwatch.StartNew();
    for (var index = 0; index < repetitions; index++)
    {
        checksum = FoldHits(
            checksum,
            SearchCore.FindAsciiStringHitsManaged(data, 3, -1, 0, 0x20, 0x7E)
        );
    }
    stopwatch.Stop();
    GC.KeepAlive(checksum);
    return new Measurement(stopwatch.Elapsed.TotalSeconds, checksum);
}

static Measurement MeasureRust(byte[] data, int repetitions)
{
    ForceCollection();
    var checksum = 0L;
    var stopwatch = Stopwatch.StartNew();
    for (var index = 0; index < repetitions; index++)
    {
        if (
            !RustAsciiEngine.TryRentNativeForTesting(
                data,
                3,
                -1,
                0,
                0x20,
                0x7E,
                out var hits,
                out var error
            )
        )
        {
            throw new InvalidOperationException(error);
        }

        using (hits)
        {
            checksum = FoldNativeHits(checksum, hits);
        }
    }
    stopwatch.Stop();
    GC.KeepAlive(checksum);
    return new Measurement(stopwatch.Elapsed.TotalSeconds, checksum);
}

static void ForceCollection()
{
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();
}

static long FoldHits(long checksum, IReadOnlyList<StringHitPosition> hits)
{
    unchecked
    {
        checksum = checksum * 31 + hits.Count;
        foreach (var hit in hits)
        {
            checksum = checksum * 31 + hit.Start;
            checksum = checksum * 31 + hit.Length;
        }
    }
    return checksum;
}

static long FoldNativeHits(long checksum, RustAsciiEngine.HitBatch hits)
{
    unchecked
    {
        checksum = checksum * 31 + hits.Count;
        foreach (var hit in hits.Hits)
        {
            checksum = checksum * 31 + hit.Start;
            checksum = checksum * 31 + hit.Length;
        }
    }
    return checksum;
}

static void RequireExactParity(
    IReadOnlyList<StringHitPosition> expected,
    RustAsciiEngine.HitBatch actual,
    string scenario,
    int size
)
{
    if (expected.Count != actual.Count)
    {
        throw new InvalidOperationException(
            $"Parity failed for {scenario}/{size}: {expected.Count} != {actual.Count}"
        );
    }

    var actualHits = actual.Hits;
    for (var index = 0; index < expected.Count; index++)
    {
        if (
            expected[index].Start != actualHits[index].Start
            || expected[index].Length != actualHits[index].Length
            || expected[index].FileOffset != actual.FileOffset
        )
        {
            throw new InvalidOperationException(
                $"Parity failed for {scenario}/{size} at hit {index}"
            );
        }
    }
}

static byte[] BuildData(int size, string scenario)
{
    var data = new byte[size];
    var scenarioSeed = scenario switch
    {
        "sparse-binary" => 0x1020_3040,
        "random-bytes" => 0x2030_4050,
        "dense-ascii" => 0x3040_5060,
        "fragmented-ascii" => 0x4050_6070,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
    var random = new Random(unchecked((int)0xB57A_2026) ^ size ^ scenarioSeed);
    random.NextBytes(data);

    switch (scenario)
    {
        case "sparse-binary":
            for (var index = 0; index < data.Length; index++)
            {
                if (data[index] is >= 0x20 and <= 0x7E)
                {
                    data[index] = 0;
                }
            }
            InjectAsciiRuns(data, 1024 * 1024);
            break;
        case "random-bytes":
            break;
        case "dense-ascii":
            Array.Fill(data, (byte)'A');
            break;
        case "fragmented-ascii":
            Array.Fill(data, (byte)0);
            for (var block = 0; block < data.Length; block += 64)
            {
                var length = Math.Min(8, data.Length - block);
                for (var index = 0; index < length; index++)
                {
                    data[block + index] = (byte)('A' + index);
                }
            }
            break;
        default:
            throw new ArgumentOutOfRangeException(nameof(scenario));
    }

    return data;
}

static void InjectAsciiRuns(byte[] data, int stride)
{
    ReadOnlySpan<byte> marker = "https://example.com/scale/0123456789abcdef"u8;
    for (var offset = stride / 2; offset + marker.Length < data.Length; offset += stride)
    {
        marker.CopyTo(data.AsSpan(offset));
    }
}

static double Median(IEnumerable<double> samples)
{
    var ordered = samples.Order().ToArray();
    var middle = ordered.Length / 2;
    return (ordered.Length & 1) == 0
        ? (ordered[middle - 1] + ordered[middle]) / 2
        : ordered[middle];
}

static int ReadPositiveInt(string[] args, string name, int defaultValue)
{
    var value = ReadOptionalString(args, name);
    if (value is null)
    {
        return defaultValue;
    }

    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
    {
        throw new ArgumentException($"{name} must be a positive integer");
    }
    return parsed;
}

static string? ReadOptionalString(string[] args, string name)
{
    for (var index = 0; index < args.Length; index++)
    {
        if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{name} requires a value");
        }
        return args[index + 1];
    }
    return null;
}

internal readonly record struct Measurement(double ElapsedSeconds, long Checksum);

internal readonly record struct BenchmarkRow(
    string Scenario,
    int ChunkBytes,
    int Hits,
    int Repetitions,
    double DotNetMedianSeconds,
    double RustMedianSeconds,
    double DotNetMiBPerSecond,
    double RustMiBPerSecond,
    double RustSpeedup
);
