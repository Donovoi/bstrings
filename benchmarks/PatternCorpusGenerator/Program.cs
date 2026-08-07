using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bstrings;
using bstrings.Benchmarks;

const int generatorVersion = 5;
const int defaultSegmentMiB = 16;
const ulong defaultSeed = 0x5041545445524E53;

var options = ParseArguments(args);
var outputDirectory = Path.GetFullPath(Require(options, "output-dir"));
var sizeMiB = ParsePositiveInt(options.GetValueOrDefault("size-mib", "256"), "size-mib");
var segmentMiB = ParsePositiveInt(
    options.GetValueOrDefault("segment-mib", defaultSegmentMiB.ToString(CultureInfo.InvariantCulture)),
    "segment-mib"
);
var overwrite = options.ContainsKey("overwrite");
var corpusEncoding = options.GetValueOrDefault("encoding", "ascii").ToLowerInvariant();
if (corpusEncoding is not ("ascii" or "utf16le"))
{
    throw new ArgumentException("--encoding must be ascii or utf16le.");
}
var complexity = options.GetValueOrDefault("complexity", "sparse").ToLowerInvariant();
if (complexity is not ("sparse" or "dense" or "adversarial"))
{
    throw new ArgumentException("--complexity must be sparse, dense, or adversarial.");
}
var sizeBytes = checked((long)sizeMiB * 1024L * 1024L);
var segmentBytes = checked(segmentMiB * 1024 * 1024);
if (sizeBytes % segmentBytes != 0)
{
    throw new ArgumentException("--size-mib must be an exact multiple of --segment-mib.");
}

var selectedNames = options.TryGetValue("patterns", out var requestedPatterns)
    ? requestedPatterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : BuiltInPatternCatalog.Definitions.Select(definition => definition.Name).ToArray();
var definitions = selectedNames
    .Select(name =>
        BuiltInPatternCatalog.ByName.TryGetValue(name, out var definition)
            ? definition
            : throw new ArgumentException($"Unknown built-in pattern: {name}")
    )
    .DistinctBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
    .ToArray();

Directory.CreateDirectory(outputDirectory);
var background = CreateBackground(segmentBytes, defaultSeed);
foreach (var definition in definitions)
{
    if (!PatternWitnessCatalog.ByName.TryGetValue(definition.Name, out var witness))
    {
        throw new InvalidOperationException($"No witness is defined for {definition.Name}.");
    }

    ValidateWitness(definition, witness);
    var outputPath = Path.Combine(
        outputDirectory,
        $"pattern-{definition.Name}-{corpusEncoding}-{complexity}-{sizeMiB}m.bin"
    );
    var manifestPath = outputPath + ".manifest.json";
    if ((File.Exists(outputPath) || File.Exists(manifestPath)) && !overwrite)
    {
        throw new IOException($"Output already exists for {definition.Name}; pass --overwrite.");
    }

    var expected = GenerateCorpus(
        outputPath,
        sizeBytes,
        segmentBytes,
        background,
        witness,
        corpusEncoding,
        complexity
    );
    var manifest = new
    {
        schemaVersion = 1,
        generatorVersion,
        pattern = new
        {
            definition.Name,
            definition.Description,
            definition.Pattern,
            options = definition.Options.ToString(),
            definition.UseNonBacktracking,
            definition.OutputGroup,
        },
        corpus = new
        {
            outputFile = Path.GetFileName(outputPath),
            sizeBytes,
            sizeMiB,
            segmentBytes,
            segmentCount = sizeBytes / segmentBytes,
            encoding = corpusEncoding,
            complexity,
            expected.NegativeRecordsPerSegment,
            seed = $"0x{defaultSeed:X16}",
            sha256 = expected.Sha256,
        },
        witness,
        expected = new
        {
            count = expected.Records.Count,
            records = expected.Records,
        },
    };
    File.WriteAllText(
        manifestPath,
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n",
        new UTF8Encoding(false)
    );
    Console.WriteLine($"{definition.Name}: {expected.Records.Count} records, {expected.Sha256}");
}

static CorpusResult GenerateCorpus(
    string outputPath,
    long sizeBytes,
    int segmentBytes,
    byte[] background,
    PatternWitness witness,
    string corpusEncoding,
    string complexity
)
{
    var encoding = corpusEncoding == "utf16le" ? Encoding.Unicode : Encoding.ASCII;
    var positive = encoding.GetBytes(witness.Positive);
    var negative = encoding.GetBytes(witness.Negative);
    if (positive.Length >= segmentBytes / 8 || negative.Length >= segmentBytes / 8)
    {
        throw new InvalidOperationException("A witness is too large for the selected segment size.");
    }

    var segmentCount = checked((int)(sizeBytes / segmentBytes));
    var working = new byte[segmentBytes];
    byte[] pendingBoundarySuffix = [];
    var positiveRecordsPerSegment = complexity == "dense" ? 64 : 1;
    var requestedNegativeRecordsPerSegment =
        complexity == "adversarial" ? 4096 : complexity == "dense" ? 64 : 1;
    var negativeStride = negative.Length + (corpusEncoding == "utf16le" ? 2 : 1);
    var negativeStart = segmentBytes / 2 + segmentBytes / 16;
    var negativeEnd = segmentBytes - segmentBytes / 16 - negative.Length;
    var maximumNonOverlappingNegatives =
        negativeEnd < negativeStart
            ? 0
            : 1 + (negativeEnd - negativeStart) / negativeStride;
    var negativeRecordsPerSegment = Math.Min(
        requestedNegativeRecordsPerSegment,
        maximumNonOverlappingNegatives
    );
    if (negativeRecordsPerSegment == 0)
    {
        throw new InvalidOperationException("The negative witness does not fit in a segment.");
    }
    var records = new List<ExpectedRecord>(
        segmentCount * positiveRecordsPerSegment + segmentCount
    );
    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    using var output = new FileStream(
        outputPath,
        FileMode.Create,
        FileAccess.Write,
        FileShare.Read,
        4 * 1024 * 1024,
        FileOptions.SequentialScan
    );

    for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
    {
        Buffer.BlockCopy(background, 0, working, 0, background.Length);
        if (pendingBoundarySuffix.Length > 0)
        {
            Buffer.BlockCopy(pendingBoundarySuffix, 0, working, 0, pendingBoundarySuffix.Length);
            pendingBoundarySuffix = [];
        }

        var segmentStart = checked((long)segmentIndex * segmentBytes);
        foreach (
            var interiorIndex in GetPlacementOffsets(
                segmentBytes / 16,
                segmentBytes / 2 - positive.Length,
                positiveRecordsPerSegment,
                corpusEncoding
            )
        )
        {
            Buffer.BlockCopy(positive, 0, working, interiorIndex, positive.Length);
            records.Add(
                new ExpectedRecord(
                    segmentStart + interiorIndex,
                    witness.ExpectedOutput,
                    complexity == "dense" ? "dense-interior" : "interior"
                )
            );
        }

        foreach (
            var negativeIndex in GetPlacementOffsets(
                negativeStart,
                negativeEnd,
                negativeRecordsPerSegment,
                corpusEncoding,
                minimumSpacing: negativeStride
            )
        )
        {
            Buffer.BlockCopy(negative, 0, working, negativeIndex, negative.Length);
        }

        if (segmentIndex + 1 < segmentCount)
        {
            var prefixLength = corpusEncoding == "utf16le"
                ? Math.Max(2, (positive.Length / 4) * 2)
                : Math.Max(1, positive.Length / 2);
            Buffer.BlockCopy(positive, 0, working, segmentBytes - prefixLength, prefixLength);
            pendingBoundarySuffix = positive[prefixLength..];
            records.Add(
                new ExpectedRecord(
                    segmentStart + segmentBytes - prefixLength,
                    witness.ExpectedOutput,
                    "boundary"
                )
            );
        }
        else
        {
            var terminalIndex =
                segmentBytes - positive.Length - (corpusEncoding == "utf16le" ? 2 : 1);
            Buffer.BlockCopy(positive, 0, working, terminalIndex, positive.Length);
            records.Add(
                new ExpectedRecord(
                    segmentStart + terminalIndex,
                    witness.ExpectedOutput,
                    "terminal"
                )
            );
        }

        output.Write(working);
        hasher.AppendData(working);
    }

    output.Flush(flushToDisk: true);
    return new CorpusResult(
        Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
        records,
        negativeRecordsPerSegment
    );
}

static IEnumerable<int> GetPlacementOffsets(
    int start,
    int end,
    int count,
    string corpusEncoding,
    int minimumSpacing = 1
)
{
    if (count <= 0 || end < start)
    {
        yield break;
    }

    var alignment = corpusEncoding == "utf16le" ? 2 : 1;
    var previous = -1;
    for (var index = 0; index < count; index++)
    {
        var offset = count == 1
            ? start + (end - start) / 2
            : start + (int)((long)(end - start) * index / (count - 1));
        offset -= offset % alignment;
        if (previous >= 0 && offset - previous < minimumSpacing)
        {
            throw new InvalidOperationException(
                "The segment is too small to place the requested records without overlap."
            );
        }
        previous = offset;
        yield return offset;
    }
}

static void ValidateWitness(BuiltInPatternDefinition definition, PatternWitness witness)
{
    var regex = RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern);
    var positive = RegexOutputCore
        .CreateRecords(
            new ParsedHit(witness.Positive, witness.Positive, string.Empty),
            definition.Name,
            regex,
            regexOutput: true,
            sourceFile: string.Empty,
            patternType: "Regex"
        )
        .Select(record => record.DataFound)
        .ToArray();
    var negative = RegexOutputCore
        .CreateRecords(
            new ParsedHit(witness.Negative, witness.Negative, string.Empty),
            definition.Name,
            regex,
            regexOutput: true,
            sourceFile: string.Empty,
            patternType: "Regex"
        )
        .ToArray();
    if (positive.Length != 1 || !string.Equals(positive[0], witness.ExpectedOutput, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Positive witness for {definition.Name} produced [{string.Join(", ", positive)}]."
        );
    }
    if (negative.Length != 0)
    {
        throw new InvalidOperationException($"Negative witness for {definition.Name} matched.");
    }
}

static byte[] CreateBackground(int length, ulong seed)
{
    var bytes = new byte[length];
    var state = seed;
    for (var index = 0; index < bytes.Length; index++)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        var value = (byte)state;
        bytes[index] = value is >= 0x20 and <= 0x7E ? (byte)0 : value;
    }
    return bytes;
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument: {argument}");
        }
        var name = argument[2..];
        if (name.Equals("overwrite", StringComparison.OrdinalIgnoreCase))
        {
            parsed[name] = "true";
            continue;
        }
        if (++index >= arguments.Length)
        {
            throw new ArgumentException($"Missing value for {argument}");
        }
        parsed[name] = arguments[index];
    }
    return parsed;
}

static string Require(IReadOnlyDictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value)
        ? value
        : throw new ArgumentException($"Missing required option --{name}.");

static int ParsePositiveInt(string value, string name) =>
    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
    && parsed > 0
        ? parsed
        : throw new ArgumentOutOfRangeException(name, $"--{name} must be a positive integer.");

internal sealed record ExpectedRecord(long Offset, string Value, string Placement);

internal sealed record CorpusResult(
    string Sha256,
    List<ExpectedRecord> Records,
    int NegativeRecordsPerSegment
);
