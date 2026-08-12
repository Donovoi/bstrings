using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using bstrings;

const string BaselineCommit = "90e9c11e9d72975d8804dbd5bd534d6923287d87";
const int CallsPerSample = 5_000_000;
const int WarmupCalls = 1_000_000;
const int MeasuredSamples = 15;
const double MaximumMedianNanoseconds = 250;
const double MaximumSampleNanoseconds = 500;
const int StableOrderSeed = 0x19_08_17;

if (args.Length > 0 && string.Equals(args[0], "--child", StringComparison.Ordinal))
{
    var sample = int.Parse(args[1], CultureInfo.InvariantCulture);
    var candidateFirst = bool.Parse(args[2]);
    var result = RunSample(sample, candidateFirst);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}

var outputPath = Path.GetFullPath(
    args.Length == 0
        ? Path.Combine("benchmarks", "results", "engine-exclusion-resolver-2026-08.csv")
        : args[0]
);
var cases = CreateMatrix();
AssertMatrixCoverage(cases);
AssertResolvedModeParity(cases);

var candidateFirstOrders = Enumerable
    .Range(0, MeasuredSamples)
    .Select(index => index % 2 == 0)
    .ToArray();
var random = new Random(StableOrderSeed);
random.Shuffle(candidateFirstOrders);

var rows = new List<BenchmarkRow>(MeasuredSamples);
for (var sample = 1; sample <= MeasuredSamples; sample++)
{
    rows.Add(await RunChildAsync(sample, candidateFirstOrders[sample - 1]));
}

AssertParity(rows);
var candidateMedianNanoseconds = Median(rows.Select(row => row.CandidateNanosecondsPerCall));
var candidateMaximumNanoseconds = rows.Max(row => row.CandidateNanosecondsPerCall);
var candidateMaximumAllocatedBytes = rows.Max(row => row.CandidateAllocatedBytes);
await WriteCsvAsync(outputPath, rows);

Console.WriteLine($"Baseline commit: {BaselineCommit}");
Console.WriteLine($"Matrix cases: {cases.Count:N0}");
Console.WriteLine($"Warmup calls per variant/process: {WarmupCalls:N0}");
Console.WriteLine($"Calls per variant/sample: {CallsPerSample:N0}");
Console.WriteLine($"Fresh-process samples: {MeasuredSamples:N0}");
Console.WriteLine(
    $"Baseline median: {Median(rows.Select(row => row.BaselineNanosecondsPerCall)):F3} ns/call (diagnostic)"
);
Console.WriteLine(
    $"Candidate: median={candidateMedianNanoseconds:F3} ns/call, maximum={candidateMaximumNanoseconds:F3} ns/call, maximum allocated={candidateMaximumAllocatedBytes:N0} bytes/sample"
);
Console.WriteLine("Static and per-sample checksum parity: passed");
Console.WriteLine($"Results: {outputPath}");

if (
    candidateMedianNanoseconds > MaximumMedianNanoseconds
    || candidateMaximumNanoseconds > MaximumSampleNanoseconds
    || candidateMaximumAllocatedBytes != 0
)
{
    throw new InvalidDataException(
        "Engine-exclusion resolver absolute gate failed: "
            + $"candidateMedian={candidateMedianNanoseconds:F3}ns (limit {MaximumMedianNanoseconds:F3}ns), "
            + $"candidateMaximum={candidateMaximumNanoseconds:F3}ns (limit {MaximumSampleNanoseconds:F3}ns), "
            + $"candidateMaximumAllocated={candidateMaximumAllocatedBytes:N0} bytes (limit 0)."
    );
}
Console.WriteLine("Engine-exclusion resolver absolute gate: passed");

static BenchmarkRow RunSample(int sample, bool candidateFirst)
{
    var cases = CreateMatrix();
    AssertMatrixCoverage(cases);
    AssertResolvedModeParity(cases);

    _ = Measure(BenchmarkVariant.Baseline, cases, WarmupCalls);
    _ = Measure(BenchmarkVariant.Candidate, cases, WarmupCalls);

    var first = candidateFirst ? BenchmarkVariant.Candidate : BenchmarkVariant.Baseline;
    var second = candidateFirst ? BenchmarkVariant.Baseline : BenchmarkVariant.Candidate;
    var firstMeasurement = MeasureCollected(first, cases, CallsPerSample);
    var secondMeasurement = MeasureCollected(second, cases, CallsPerSample);
    var baseline = first == BenchmarkVariant.Baseline ? firstMeasurement : secondMeasurement;
    var candidate = first == BenchmarkVariant.Candidate ? firstMeasurement : secondMeasurement;
    if (baseline.Checksum != candidate.Checksum)
    {
        throw new InvalidDataException(
            $"Resolver checksum parity failed in sample {sample}: baseline={baseline.Checksum}, candidate={candidate.Checksum}."
        );
    }
    return new BenchmarkRow(
        BaselineCommit,
        CallsPerSample,
        cases.Count,
        sample,
        candidateFirst ? "candidate-first" : "baseline-first",
        baseline.Elapsed.TotalMilliseconds * 1_000_000 / CallsPerSample,
        candidate.Elapsed.TotalMilliseconds * 1_000_000 / CallsPerSample,
        baseline.AllocatedBytes,
        candidate.AllocatedBytes,
        baseline.Checksum,
        candidate.Checksum
    );
}

static Measurement MeasureCollected(
    BenchmarkVariant variant,
    IReadOnlyList<ResolverCase> cases,
    int calls
)
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    return Measure(variant, cases, calls);
}

static Measurement Measure(
    BenchmarkVariant variant,
    IReadOnlyList<ResolverCase> cases,
    int calls
)
{
    long checksum = 17;
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var started = Stopwatch.GetTimestamp();
    for (var call = 0; call < calls; call++)
    {
        var item = cases[call % cases.Count];
        var modes = variant == BenchmarkVariant.Baseline
            ? ResolveBaseline(item)
            : AnalysisCli.ResolveEngineModes(
                full: true,
                item.NativeValue,
                item.FlossValue,
                item.OcrValue,
                item.TranslationValue,
                item.Exclusions
            );
        checksum = unchecked(
            (checksum * 31)
                + (int)modes.Native
                + ((int)modes.Floss * 3)
                + ((int)modes.Ocr * 5)
                + ((int)modes.Translation * 7)
        );
    }
    var elapsed = Stopwatch.GetElapsedTime(started);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    return new Measurement(elapsed, allocated, checksum);
}

// This is the effective-mode resolution performed by AnalysisCli.cs at the
// pinned baseline. Exclusions are converted to equivalent explicit-off values
// because that commit predates --exclude-engine.
static AnalysisEngineModes ResolveBaseline(ResolverCase item)
{
    var nativeValue = item.ExclusionMask.HasFlag(EngineMask.Native)
        ? "off"
        : item.NativeValue;
    var flossValue = item.ExclusionMask.HasFlag(EngineMask.Floss) ? "off" : item.FlossValue;
    var ocrValue = item.ExclusionMask.HasFlag(EngineMask.Ocr) ? "off" : item.OcrValue;
    var translationValue = item.ExclusionMask.HasFlag(EngineMask.Translation)
        ? "off"
        : item.TranslationValue;
    return new AnalysisEngineModes(
        ParseNative(nativeValue),
        ParseFloss(flossValue ?? "auto"),
        ParseOcr(ocrValue ?? "auto"),
        ParseTranslation(translationValue ?? "auto")
    );
}

static NativeExtractionMode ParseNative(string? value) =>
    (value ?? "on").Trim().ToLowerInvariant() switch
    {
        "on" => NativeExtractionMode.On,
        "off" => NativeExtractionMode.Off,
        _ => throw new ArgumentException("Native extraction must be on or off."),
    };

static ExecutableRecoveryMode ParseFloss(string value) =>
    value.Trim().ToLowerInvariant() switch
    {
        "off" => ExecutableRecoveryMode.Off,
        "auto" => ExecutableRecoveryMode.Auto,
        "force" => ExecutableRecoveryMode.Force,
        _ => throw new ArgumentException("Executable recovery must be off, auto, or force."),
    };

static OcrWorkflowMode ParseOcr(string value) =>
    value.Trim().ToLowerInvariant() switch
    {
        "off" => OcrWorkflowMode.Off,
        "auto" => OcrWorkflowMode.Auto,
        "force" => OcrWorkflowMode.Force,
        _ => throw new ArgumentException("OCR must be off, auto, or force."),
    };

static TranslationWorkflowMode ParseTranslation(string value) =>
    value.Trim().ToLowerInvariant() switch
    {
        "off" => TranslationWorkflowMode.Off,
        "auto" => TranslationWorkflowMode.Auto,
        "all" or "translate-all" => TranslationWorkflowMode.All,
        "detect-only" or "detect" => TranslationWorkflowMode.DetectOnly,
        _ => throw new ArgumentException("Translation must be off, auto, all, or detect-only."),
    };

static IReadOnlyList<ResolverCase> CreateMatrix()
{
    var result = new List<ResolverCase>
    {
        new("full-unchanged", EngineMask.None, null, null, null, null, []),
    };
    foreach (var maskValue in Enumerable.Range(1, 15))
    {
        var mask = (EngineMask)maskValue;
        if (
            mask.HasFlag(EngineMask.Native)
            && mask.HasFlag(EngineMask.Floss)
            && mask.HasFlag(EngineMask.Ocr)
        )
        {
            continue;
        }
        var names = Names(mask).ToArray();
        result.Add(
            new("exclude-" + string.Join('-', names), mask, null, null, null, null, names)
        );
    }
    result.Add(
        new(
            "comma-ocr-translation",
            EngineMask.Ocr | EngineMask.Translation,
            null,
            null,
            null,
            null,
            ["ocr,translation"]
        )
    );
    result.Add(
        new(
            "case-repeat-ocr-translation",
            EngineMask.Ocr | EngineMask.Translation,
            null,
            null,
            null,
            null,
            ["OCR", "Translation"]
        )
    );
    result.Add(
        new(
            "trimmed-comma-native-translation",
            EngineMask.Native | EngineMask.Translation,
            null,
            null,
            null,
            null,
            [" native , translation "]
        )
    );
    result.Add(new("explicit-native-off", EngineMask.None, "off", null, null, null, []));
    result.Add(new("explicit-floss-off", EngineMask.None, null, "off", null, null, []));
    result.Add(new("explicit-ocr-off", EngineMask.None, null, null, "off", null, []));
    result.Add(
        new("explicit-translation-off", EngineMask.None, null, null, null, "off", [])
    );
    return result;
}

static IEnumerable<string> Names(EngineMask mask)
{
    if (mask.HasFlag(EngineMask.Native))
    {
        yield return "native";
    }
    if (mask.HasFlag(EngineMask.Floss))
    {
        yield return "floss";
    }
    if (mask.HasFlag(EngineMask.Ocr))
    {
        yield return "ocr";
    }
    if (mask.HasFlag(EngineMask.Translation))
    {
        yield return "translation";
    }
}

static void AssertMatrixCoverage(IReadOnlyCollection<ResolverCase> cases)
{
    if (
        cases.Count != 21
        || cases.Count(item => BitOperations.PopCount((uint)item.ExclusionMask) == 1) != 4
        || cases.Count(item => BitOperations.PopCount((uint)item.ExclusionMask) >= 2) != 12
        || cases.Count(item => item.Name.StartsWith("explicit-", StringComparison.Ordinal)) != 4
    )
    {
        throw new InvalidDataException("The fixed resolver benchmark matrix is incomplete.");
    }
}

static void AssertParity(IReadOnlyCollection<BenchmarkRow> rows)
{
    foreach (var row in rows)
    {
        if (row.BaselineChecksum != row.CandidateChecksum)
        {
            throw new InvalidDataException($"Resolver checksum parity failed in sample {row.Sample}.");
        }
    }
}

static void AssertResolvedModeParity(IReadOnlyList<ResolverCase> cases)
{
    foreach (var item in cases)
    {
        var candidate = AnalysisCli.ResolveEngineModes(
            full: true,
            item.NativeValue,
            item.FlossValue,
            item.OcrValue,
            item.TranslationValue,
            item.Exclusions
        );
        var expected = ResolveBaseline(item);
        if (candidate != expected)
        {
            throw new InvalidDataException(
                $"Resolved-mode parity failed for matrix case '{item.Name}'."
            );
        }
    }
}

static async Task<BenchmarkRow> RunChildAsync(int sample, bool candidateFirst)
{
    var startInfo = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    startInfo.ArgumentList.Add("--child");
    startInfo.ArgumentList.Add(sample.ToString(CultureInfo.InvariantCulture));
    startInfo.ArgumentList.Add(candidateFirst.ToString(CultureInfo.InvariantCulture));
    startInfo.Environment["DOTNET_TieredCompilation"] = "0";
    startInfo.Environment["DOTNET_ReadyToRun"] = "0";

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start benchmark child process.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    if (process.ExitCode != 0)
    {
        throw new InvalidDataException(
            $"Benchmark child {sample} failed with exit code {process.ExitCode}: {stderr.Trim()}"
        );
    }
    var row = JsonSerializer.Deserialize<BenchmarkRow>(stdout.Trim());
    if (row.Sample != sample)
    {
        throw new InvalidDataException($"Benchmark child {sample} returned an invalid result.");
    }
    return row;
}

static async Task WriteCsvAsync(string path, IReadOnlyCollection<BenchmarkRow> rows)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await using var writer = new StreamWriter(
        path,
        append: false,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    );
    await writer.WriteLineAsync(
        "baselineCommit,calls,matrixCases,sample,order,baselineNanosecondsPerCall,candidateNanosecondsPerCall,baselineAllocatedBytes,candidateAllocatedBytes,baselineChecksum,candidateChecksum"
    );
    foreach (var row in rows)
    {
        await writer.WriteLineAsync(
            string.Join(
                ',',
                row.BaselineCommit,
                row.Calls.ToString(CultureInfo.InvariantCulture),
                row.MatrixCases.ToString(CultureInfo.InvariantCulture),
                row.Sample.ToString(CultureInfo.InvariantCulture),
                row.Order,
                row.BaselineNanosecondsPerCall.ToString("0.000", CultureInfo.InvariantCulture),
                row.CandidateNanosecondsPerCall.ToString("0.000", CultureInfo.InvariantCulture),
                row.BaselineAllocatedBytes.ToString(CultureInfo.InvariantCulture),
                row.CandidateAllocatedBytes.ToString(CultureInfo.InvariantCulture),
                row.BaselineChecksum.ToString(CultureInfo.InvariantCulture),
                row.CandidateChecksum.ToString(CultureInfo.InvariantCulture)
            )
        );
    }
}

static double Median(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    if (ordered.Length == 0)
    {
        throw new InvalidOperationException("No benchmark values were recorded.");
    }
    return ordered.Length % 2 == 1
        ? ordered[ordered.Length / 2]
        : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;
}

[Flags]
enum EngineMask
{
    None = 0,
    Native = 1,
    Floss = 2,
    Ocr = 4,
    Translation = 8,
}

enum BenchmarkVariant
{
    Baseline,
    Candidate,
}

readonly record struct ResolverCase(
    string Name,
    EngineMask ExclusionMask,
    string? NativeValue,
    string? FlossValue,
    string? OcrValue,
    string? TranslationValue,
    string[] Exclusions
);

readonly record struct Measurement(TimeSpan Elapsed, long AllocatedBytes, long Checksum);

readonly record struct BenchmarkRow(
    string BaselineCommit,
    int Calls,
    int MatrixCases,
    int Sample,
    string Order,
    double BaselineNanosecondsPerCall,
    double CandidateNanosecondsPerCall,
    long BaselineAllocatedBytes,
    long CandidateAllocatedBytes,
    long BaselineChecksum,
    long CandidateChecksum
);
