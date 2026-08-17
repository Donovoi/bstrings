using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using bstrings;

const string baselinePattern =
    @"(?<![A-Za-z0-9!#$%&'*+/=?^_`{|}~.-])[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?![A-Za-z0-9.-])";

var options = ParseArguments(args);
var values = BuildValues(options.Workload);
var regex = options.Variant == "baseline"
    ? new Regex(
        baselinePattern,
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2)
    )
    : RegexOutputCore.GetOrCreateRegex(
        "email",
        BuiltInPatternCatalog.Patterns["email"]
    );

Run(Math.Min(options.Records, 20_000), values, regex, options.Variant);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
var stopwatch = Stopwatch.StartNew();
var result = Run(options.Records, values, regex, options.Variant);
stopwatch.Stop();
var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationStart;

Console.WriteLine(
    JsonSerializer.Serialize(
        new
        {
            schemaVersion = 1,
            options.Variant,
            options.Workload,
            options.Records,
            wallMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            allocatedBytes,
            result.Matches,
            checksum = result.Checksum.ToString("x16"),
            ianaVersion = IanaTopLevelDomains.Version,
            ianaSha256 = IanaTopLevelDomains.SourceSha256,
        }
    )
);

static RunResult Run(
    int records,
    IReadOnlyList<string> values,
    Regex regex,
    string variant
)
{
    var matches = 0L;
    var checksum = 14695981039346656037UL;
    for (var index = 0; index < records; index++)
    {
        var value = values[index % values.Count];
        foreach (
            var record in RegexOutputCore.CreateRecords(
                new ParsedHit(value, value, string.Empty),
                variant == "candidate" ? "email" : "email-regex-only",
                regex,
                regexOutput: true,
                sourceFile: "synthetic.bin",
                patternType: "Regex"
            )
        )
        {
            if (variant == "baseline" && !IsBaselineEmail(record.DataFound))
            {
                continue;
            }
            matches++;
            checksum = AddToChecksum(checksum, record.DataFound);
        }
    }

    return new RunResult(matches, checksum);
}

static bool IsBaselineEmail(string value)
{
    var at = value.LastIndexOf('@');
    if (at is < 1 or > 64 || value.Length > 254)
    {
        return false;
    }
    var domainLength = value.Length - at - 1;
    return domainLength is > 0 and <= 253;
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
        "owner@example.com",
        "security@example.net",
        "alpha.beta+tag@example.org",
        "analyst_01@subdomain.example.com",
        "first-last@sample.technology",
        "user123@sample.io",
        "mailbox@service.dev",
        "contact@research.au",
    ];
    string[] noise =
    [
        "Q7%abc@random.drfm",
        "%QA@q.o",
        "xsxbxQx@x.x",
        "Ks@h.f",
        "screenshots@vendor.org.xpi",
        "er@vendor.org.xpi",
        "sy@module.dll",
        "-32769|Desc=@Resource.dll",
    ];

    return workload switch
    {
        "valid" => valid,
        "noise" => noise,
        "mixed" => valid.Zip(noise, (accepted, rejected) => new[] { accepted, rejected })
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

    if (variant is not ("baseline" or "candidate" or "candidate-regex"))
    {
        throw new ArgumentException(
            "Variant must be baseline, candidate-regex, or candidate."
        );
    }
    if (workload is not ("valid" or "noise" or "mixed"))
    {
        throw new ArgumentException("Workload must be valid, noise, or mixed.");
    }
    if (records <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(records));
    }

    return new Options(variant, workload, records);
}

internal sealed record Options(string Variant, string Workload, int Records);
internal sealed record RunResult(long Matches, ulong Checksum);
