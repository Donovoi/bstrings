using System.Diagnostics;
using bstrings;

var hitCount =
    args.Length > 0 && int.TryParse(args[0], out var requestedHits)
        ? requestedHits
        : 250_000;
var repetitions =
    args.Length > 1 && int.TryParse(args[1], out var requestedRepetitions)
        ? requestedRepetitions
        : 7;
var patternCount =
    args.Length > 2 && int.TryParse(args[2], out var requestedPatternCount)
        ? requestedPatternCount
        : 5;

if (hitCount < 10_000 || repetitions < 3 || patternCount is < 1 or > 5)
{
    throw new ArgumentOutOfRangeException(
        nameof(args),
        "Use at least 10,000 hits, three repetitions, and one to five patterns."
    );
}

var patternNames = new[] { "email", "ipv4", "cve", "sha256", "ethereum" };
var patterns = patternNames
    .Take(patternCount)
    .Select(name => (name, BuiltInPatternCatalog.Patterns[name]))
    .ToList();
var hits = CreateCorpus(hitCount);

RunAdaptive(hits.Take(10_000).ToHashSet(), patterns);
RunForced(hits.Take(10_000).ToHashSet(), patterns, RegexWorkPartition.Hits);
RunForced(hits.Take(10_000).ToHashSet(), patterns, RegexWorkPartition.Patterns);

var adaptiveTimes = new List<double>();
var hitMajorTimes = new List<double>();
var patternMajorTimes = new List<double>();
var expectedMatches = -1;

for (var repetition = 0; repetition < repetitions; repetition++)
{
    (int Count, TimeSpan Elapsed) adaptive;
    (int Count, TimeSpan Elapsed) hitMajor;
    (int Count, TimeSpan Elapsed) patternMajor;

    // Rotate measurement order so thread-pool, cache, and GC effects are not
    // systematically charged to one topology.
    switch (repetition % 3)
    {
        case 0:
            adaptive = Measure(() => RunAdaptive(hits, patterns));
            hitMajor = Measure(() => RunForced(hits, patterns, RegexWorkPartition.Hits));
            patternMajor = Measure(() =>
                RunForced(hits, patterns, RegexWorkPartition.Patterns)
            );
            break;
        case 1:
            hitMajor = Measure(() => RunForced(hits, patterns, RegexWorkPartition.Hits));
            patternMajor = Measure(() =>
                RunForced(hits, patterns, RegexWorkPartition.Patterns)
            );
            adaptive = Measure(() => RunAdaptive(hits, patterns));
            break;
        default:
            patternMajor = Measure(() =>
                RunForced(hits, patterns, RegexWorkPartition.Patterns)
            );
            adaptive = Measure(() => RunAdaptive(hits, patterns));
            hitMajor = Measure(() => RunForced(hits, patterns, RegexWorkPartition.Hits));
            break;
    }

    expectedMatches = expectedMatches < 0 ? adaptive.Count : expectedMatches;
    if (
        adaptive.Count != expectedMatches
        || hitMajor.Count != expectedMatches
        || patternMajor.Count != expectedMatches
    )
    {
        throw new InvalidOperationException(
            $"Parity failure: adaptive={adaptive.Count}, hit-major={hitMajor.Count}, pattern-major={patternMajor.Count}, expected={expectedMatches}."
        );
    }

    adaptiveTimes.Add(adaptive.Elapsed.TotalMilliseconds);
    hitMajorTimes.Add(hitMajor.Elapsed.TotalMilliseconds);
    patternMajorTimes.Add(patternMajor.Elapsed.TotalMilliseconds);
}

adaptiveTimes.Sort();
hitMajorTimes.Sort();
patternMajorTimes.Sort();
var adaptiveMedian = adaptiveTimes[adaptiveTimes.Count / 2];
var hitMedian = hitMajorTimes[hitMajorTimes.Count / 2];
var patternMedian = patternMajorTimes[patternMajorTimes.Count / 2];
var bestForcedMedian = Math.Min(hitMedian, patternMedian);

Console.WriteLine(
    $"runtime={Environment.Version}; os={Environment.OSVersion}; logical_processors={Environment.ProcessorCount}"
);
Console.WriteLine(
    $"hits={hits.Count:N0}; patterns={patterns.Count}; repetitions={repetitions}; matches={expectedMatches:N0}"
);
Console.WriteLine($"adaptive_median_ms={adaptiveMedian:F3}");
Console.WriteLine($"hit_major_median_ms={hitMedian:F3}");
Console.WriteLine($"pattern_major_median_ms={patternMedian:F3}");
Console.WriteLine($"adaptive_vs_best_forced={bestForcedMedian / adaptiveMedian:F3}x");

static HashSet<string> CreateCorpus(int count)
{
    var corpus = new HashSet<string>(count, StringComparer.Ordinal);
    for (var index = 0; index < count; index++)
    {
        var value = (index % 1_000) switch
        {
            0 => $"contact analyst{index}@example.technology",
            1 => $"peer-{index}=192.0.2.{index % 250}",
            2 => $"patched CVE-2026-{10_000 + index}",
            3 => "sha256=" + index.ToString("x16") + new string('a', 48),
            4 => "to=0x" + index.ToString("x16") + new string('a', 24),
            _ => $"noise-{index:X8}-The_quick_brown_fox_jumps_over_13_lazy_dogs",
        };
        corpus.Add(value);
    }

    return corpus;
}

static (int Count, TimeSpan Elapsed) Measure(Func<int> action)
{
    var stopwatch = Stopwatch.StartNew();
    var count = action();
    stopwatch.Stop();
    return (count, stopwatch.Elapsed);
}

static int RunAdaptive(
    HashSet<string> hits,
    List<(string name, string pattern)> patterns
)
{
    return bstrings.Program
        .ProcessRegexPatternsConcurrentlyAsync(
            hits,
            patterns,
            ro: false,
            off: false,
            s: true,
            sw: null!,
            q: true,
            o: string.Empty
        )
        .GetAwaiter()
        .GetResult();
}

static int RunForced(
    HashSet<string> hits,
    List<(string name, string pattern)> patterns,
    RegexWorkPartition partition
)
{
    return bstrings.Program
        .ProcessRegexPatternsConcurrentlyAsync(
            hits,
            patterns,
            ro: false,
            off: false,
            s: true,
            sw: null!,
            q: true,
            o: string.Empty,
            forcedPartition: partition
        )
        .GetAwaiter()
        .GetResult();
}
