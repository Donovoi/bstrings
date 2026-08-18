using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using bstrings;

const string legacyMacPattern =
    @"(?<![0-9A-Fa-f])(?<![0-9A-Fa-f]{2}[-:])[0-9A-Fa-f]{2}([-:]?)(?:[0-9A-Fa-f]{2}\1){4}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])(?![-:][0-9A-Fa-f]{2})";
const string legacyRegistryPattern =
    @"\b(?:(?:HKEY_LOCAL_MACHINE|HKLM|HKEY_CURRENT_USER|HKCU|HKEY_CLASSES_ROOT|HKCR|HKEY_USERS|HKU|HKEY_CURRENT_CONFIG|HKCC)\\)?(?:SAM|SECURITY|SOFTWARE|SYSTEM)(?:\\[A-Za-z0-9_. (){}-]+)*\b";

var options = ParseArguments(args);
var values = BuildValues(options.Workload);
var runners = options.Variant == "baseline" ? BuildBaselineRunners() : BuildCandidateRunners();

Run(Math.Min(options.Records, 10_000), values, runners);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
var stopwatch = Stopwatch.StartNew();
var result = Run(options.Records, values, runners);
stopwatch.Stop();
var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationStart;
var peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
var coverage = RunCoverage(
    options.Records,
    values,
    options.Variant == "baseline"
        ? BuildBaselineCoverageRunners()
        : BuildCandidateCoverageRunners()
);

Console.WriteLine(
    JsonSerializer.Serialize(
        new
        {
            schemaVersion = 1,
            options.Variant,
            options.Workload,
            options.Records,
            patternCount = runners.Count,
            wallMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            allocatedBytes,
            peakWorkingSetBytes,
            result.Matches,
            checksum = result.Checksum.ToString("x16"),
            coverageMatches = coverage.Matches,
            coverageChecksum = coverage.Checksum.ToString("x16"),
        }
    )
);

static IReadOnlyList<PatternRunner> BuildCandidateRunners() =>
    BuiltInPatternCatalog.Definitions
        .Where(definition => definition.SelectedByAll)
        .Select(definition => new PatternRunner(
            definition.Name,
            definition.Name,
            RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern)
        ))
        .ToArray();

static IReadOnlyList<PatternRunner> BuildBaselineRunners()
{
    var runners = BuiltInPatternCatalog.Definitions
        .Where(definition =>
            definition.SelectedByAll
            && definition.Name is not ("mac" or "ipv6" or "reg_path")
        )
        .Select(definition => new PatternRunner(
            definition.Name,
            definition.Name,
            RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern)
        ))
        .ToList();

    runners.Add(
        new PatternRunner(
            "baseline_mac",
            "mac",
            new Regex(legacyMacPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
        )
    );
    runners.Add(
        new PatternRunner(
            "baseline_ipv6",
            "ipv6",
            new Regex(
                BuiltInPatternCatalog.Patterns["ipv6"],
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2)
            )
        )
    );
    runners.Add(
        new PatternRunner(
            "baseline_reg_path",
            "reg_path",
            new Regex(
                legacyRegistryPattern,
                RegexOptions.CultureInvariant
                    | RegexOptions.IgnoreCase
                    | RegexOptions.NonBacktracking,
                TimeSpan.FromSeconds(2)
            )
        )
    );
    foreach (var name in new[] { "zip", "solana", "move_address" })
    {
        var definition = BuiltInPatternCatalog.ByName[name];
        runners.Add(
            new PatternRunner(
                definition.Name,
                definition.Name,
                RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern)
            )
        );
    }

    return runners;
}

static IReadOnlyList<PatternRunner> BuildBaselineCoverageRunners()
{
    var runners = new List<PatternRunner>
    {
        new(
            "baseline_mac",
            "mac",
            new Regex(legacyMacPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
        ),
        new(
            "baseline_ipv6",
            "ipv6",
            new Regex(
                BuiltInPatternCatalog.Patterns["ipv6"],
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2)
            )
        ),
        new(
            "baseline_reg_path",
            "reg_path",
            new Regex(
                legacyRegistryPattern,
                RegexOptions.CultureInvariant
                    | RegexOptions.IgnoreCase
                    | RegexOptions.NonBacktracking,
                TimeSpan.FromSeconds(2)
            )
        ),
    };
    AddUnchangedBroadRunners(runners);
    return runners;
}

static IReadOnlyList<PatternRunner> BuildCandidateCoverageRunners()
{
    string[] names =
    [
        "mac",
        "mac_candidate",
        "ipv6",
        "ipv6_candidate",
        "reg_path",
        "reg_path_candidate",
    ];
    var runners = names
        .Select(name =>
        {
            var definition = BuiltInPatternCatalog.ByName[name];
            return new PatternRunner(
                definition.Name,
                definition.Name,
                RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern)
            );
        })
        .ToList();
    AddUnchangedBroadRunners(runners);
    return runners;
}

static void AddUnchangedBroadRunners(ICollection<PatternRunner> runners)
{
    foreach (var name in new[] { "zip", "solana", "move_address" })
    {
        var definition = BuiltInPatternCatalog.ByName[name];
        runners.Add(
            new PatternRunner(
                definition.Name,
                definition.Name,
                RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern)
            )
        );
    }
}

static RunResult Run(
    int records,
    IReadOnlyList<string> values,
    IReadOnlyList<PatternRunner> runners
)
{
    var matches = 0L;
    var checksum = 14695981039346656037UL;
    for (var index = 0; index < records; index++)
    {
        var value = values[index % values.Count];
        foreach (var runner in runners)
        {
            foreach (
                var record in RegexOutputCore.CreateRecords(
                    new ParsedHit(value, value, string.Empty),
                    runner.RuntimeName,
                    runner.Regex,
                    regexOutput: true,
                    sourceFile: "synthetic.bin",
                    patternType: "Regex"
                )
            )
            {
                matches++;
                checksum = AddToChecksum(checksum, runner.OutputName);
                checksum = AddToChecksum(checksum, record.DataFound);
            }
        }
    }

    return new RunResult(matches, checksum);
}

static RunResult RunCoverage(
    int records,
    IReadOnlyList<string> values,
    IReadOnlyList<PatternRunner> runners
)
{
    var matches = 0L;
    var checksum = 14695981039346656037UL;
    for (var index = 0; index < records; index++)
    {
        var value = values[index % values.Count];
        foreach (var runner in runners)
        {
            foreach (
                var record in RegexOutputCore.CreateRecords(
                    new ParsedHit(value, value, string.Empty),
                    runner.RuntimeName,
                    runner.Regex,
                    regexOutput: true,
                    sourceFile: "synthetic.bin",
                    patternType: "Regex"
                )
            )
            {
                matches++;
                checksum = AddToChecksum(checksum, record.DataFound);
            }
        }
    }

    return new RunResult(matches, checksum);
}

static ulong AddToChecksum(ulong checksum, string value)
{
    foreach (var character in value)
    {
        checksum ^= character;
        checksum *= 1099511628211UL;
    }
    checksum ^= 0xff;
    checksum *= 1099511628211UL;
    return checksum;
}

static IReadOnlyList<string> BuildValues(string workload)
{
    string[] valid =
    [
        "00:11:22:AA:BB:CC",
        @"HKLM\SAM\Synthetic",
        "2001:db8::1",
        "00-11-22-AA-BB-CC",
        @"SOFTWARE\Synthetic",
        "::1",
    ];
    string[] confusion =
    [
        "001122AABBCC",
        "SOFTWARE",
        "::",
        "90210",
        "11111111111111111111111111111111",
        "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    ];

    return workload switch
    {
        "valid" => valid,
        "confusion" => confusion,
        "mixed" => valid.Zip(confusion, (accepted, rejected) => new[] { accepted, rejected })
            .SelectMany(pair => pair)
            .ToArray(),
        _ => throw new ArgumentException($"Unknown workload '{workload}'."),
    };
}

static Options ParseArguments(string[] arguments)
{
    string? variant = null;
    string? workload = null;
    var records = 0;
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length)
        {
            throw new ArgumentException($"Missing value for '{arguments[index]}'.");
        }

        switch (arguments[index])
        {
            case "--variant":
                variant = arguments[index + 1];
                break;
            case "--workload":
                workload = arguments[index + 1];
                break;
            case "--records":
                records = int.Parse(arguments[index + 1]);
                break;
            default:
                throw new ArgumentException($"Unknown argument '{arguments[index]}'.");
        }
    }

    if (variant is not ("baseline" or "candidate"))
    {
        throw new ArgumentException("Variant must be baseline or candidate.");
    }
    if (workload is not ("valid" or "confusion" or "mixed"))
    {
        throw new ArgumentException("Workload must be valid, confusion, or mixed.");
    }
    if (records <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(records));
    }

    return new Options(variant, workload, records);
}

internal sealed record Options(string Variant, string Workload, int Records);
internal sealed record PatternRunner(string RuntimeName, string OutputName, Regex Regex);
internal sealed record RunResult(long Matches, ulong Checksum);
