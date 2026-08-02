using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const int generatorVersion = 1;
const long defaultSeed = 0x42535452494E4753;

var options = ParseArguments(args);
var outputPath = Path.GetFullPath(Require(options, "output"));
var sizeBytes = ParsePositiveLong(Require(options, "size-bytes"), "size-bytes");
var segmentBytes = checked(
    ParsePositiveInt(options.GetValueOrDefault("segment-mib", "16"), "segment-mib")
        * 1024
        * 1024
);
var overwrite = options.ContainsKey("overwrite");

if (sizeBytes % segmentBytes != 0)
{
    throw new ArgumentException("--size-bytes must be an exact multiple of --segment-mib.");
}

if (File.Exists(outputPath) && !overwrite)
{
    throw new IOException($"Output already exists: {outputPath}. Pass --overwrite to replace it.");
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var segmentCount = checked((int)(sizeBytes / segmentBytes));
var background = CreateBackground(segmentBytes, unchecked((ulong)defaultSeed));
var working = new byte[segmentBytes];
byte[] pendingBoundarySuffix = [];
var interiorRecords = 0L;
var boundaryRecords = 0L;
var terminalRecords = 0L;
var samples = new List<MarkerSample>();
var stopwatch = Stopwatch.StartNew();
var nextProgress = 1L << 30;

using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
using (
    var output = new FileStream(
        outputPath,
        FileMode.Create,
        FileAccess.Write,
        FileShare.Read,
        4 * 1024 * 1024,
        FileOptions.SequentialScan
    )
)
{
    for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
    {
        Buffer.BlockCopy(background, 0, working, 0, background.Length);

        if (pendingBoundarySuffix.Length > 0)
        {
            Buffer.BlockCopy(
                pendingBoundarySuffix,
                0,
                working,
                0,
                pendingBoundarySuffix.Length
            );
            pendingBoundarySuffix = [];
        }

        var interiorOffset = checked((long)segmentIndex * segmentBytes + segmentBytes / 2L);
        var interior = CreateRecord('I', interiorOffset);
        var interiorIndex = segmentBytes / 2;
        Buffer.BlockCopy(interior, 0, working, interiorIndex, interior.Length);
        interiorRecords++;
        AddSample(samples, interiorOffset, interior);

        if (segmentIndex + 1 < segmentCount)
        {
            var boundaryOffset = checked((long)(segmentIndex + 1) * segmentBytes);
            var boundary = CreateRecord('B', boundaryOffset);
            var prefixLength = Math.Min(48, boundary.Length - 1);
            Buffer.BlockCopy(
                boundary,
                0,
                working,
                segmentBytes - prefixLength,
                prefixLength
            );
            pendingBoundarySuffix = boundary[prefixLength..];
            boundaryRecords++;
            AddSample(samples, boundaryOffset - prefixLength, boundary);
        }
        else
        {
            var terminalAnchor = sizeBytes - 1;
            var terminal = CreateRecord('T', terminalAnchor);
            var terminalIndex = segmentBytes - terminal.Length - 1;
            Buffer.BlockCopy(terminal, 0, working, terminalIndex, terminal.Length);
            terminalRecords++;
            AddSample(samples, sizeBytes - terminal.Length - 1, terminal);
        }

        output.Write(working);
        hasher.AppendData(working);

        var written = checked((long)(segmentIndex + 1) * segmentBytes);
        if (written >= nextProgress || written == sizeBytes)
        {
            var throughput = written / 1024d / 1024d / stopwatch.Elapsed.TotalSeconds;
            Console.Error.WriteLine(
                $"{written / (1024d * 1024d * 1024d):F2} GiB / "
                    + $"{sizeBytes / (1024d * 1024d * 1024d):F2} GiB "
                    + $"({throughput:F1} MiB/s)"
            );
            nextProgress = checked(nextProgress + (1L << 30));
        }
    }

    output.Flush(flushToDisk: true);
}

stopwatch.Stop();
var recordCount = checked(interiorRecords + boundaryRecords + terminalRecords);
var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
var manifest = new
{
    schemaVersion = 1,
    generatorVersion,
    outputFile = Path.GetFileName(outputPath),
    sizeBytes,
    sizeGiB = sizeBytes / (1024d * 1024d * 1024d),
    segmentBytes,
    segmentCount,
    seed = $"0x{defaultSeed:X16}",
    sha256 = hash,
    elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
    records = new
    {
        interior = interiorRecords,
        boundary = boundaryRecords,
        terminal = terminalRecords,
        total = recordCount,
        expectedLiteralMatches = recordCount,
        expectedUrlMatches = recordCount,
        expectedEmailMatches = recordCount,
        expectedUrlOrEmailMatches = checked(recordCount * 2),
    },
    samples,
};
var manifestPath = outputPath + ".manifest.json";
File.WriteAllText(
    manifestPath,
    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n",
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
);

Console.WriteLine(manifestPath);

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

static string Require(IReadOnlyDictionary<string, string> options, string name)
{
    return options.TryGetValue(name, out var value)
        ? value
        : throw new ArgumentException($"Missing required option --{name}.");
}

static long ParsePositiveLong(string value, string name)
{
    if (
        !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        || parsed <= 0
    )
    {
        throw new ArgumentOutOfRangeException(name, $"--{name} must be a positive integer.");
    }

    return parsed;
}

static int ParsePositiveInt(string value, string name)
{
    var parsed = ParsePositiveLong(value, name);
    return parsed <= int.MaxValue
        ? (int)parsed
        : throw new ArgumentOutOfRangeException(name, $"--{name} is too large.");
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

static byte[] CreateRecord(char kind, long anchorOffset)
{
    var id = anchorOffset.ToString("x16", CultureInfo.InvariantCulture);
    var value =
        $"SCALE_LITERAL_{kind}_{id} https://example.com/scale/{id} "
        + $"bench{id}@example.com";
    return Encoding.ASCII.GetBytes(value);
}

static void AddSample(List<MarkerSample> samples, long offset, byte[] record)
{
    if (samples.Count < 4)
    {
        samples.Add(new MarkerSample(offset, Encoding.ASCII.GetString(record)));
    }
}

internal sealed record MarkerSample(long Offset, string Value);
