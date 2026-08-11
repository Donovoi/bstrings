using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const int Success = 0;
const int UsageError = 2;

try
{
    var options = Options.Parse(args);
    var outputPath = PhysicalFiles.Destination(
        options.OutputPath,
        PhysicalFiles.Input(options.ModelPath, "model", 64L * 1024 * 1024),
        PhysicalFiles.Input(options.ManifestPath, "model manifest", 64 * 1024),
        PhysicalFiles.Input(options.CorpusPath, "corpus", 512L * 1024 * 1024)
    );
    var manifest = ExperimentManifest.Load(options.ManifestPath);
    var model = SparseLinearModel.Load(options.ModelPath, manifest);
    var rows = CorpusRow.LoadAll(options.CorpusPath);
    var predictions = rows
        .OrderBy(row => row.Identifier, StringComparer.Ordinal)
        .Select(row =>
        {
            var score = model.Score(row.Text);
            return new Prediction(
                row.Identifier,
                score,
                manifest.Decision(score)
            );
        })
        .ToArray();
    PredictionWriter.WriteAtomic(outputPath, predictions);
    Console.WriteLine(
        JsonSerializer.Serialize(
            new
            {
                reportType = "translation-worthiness-managed-reference",
                researchOnly = true,
                promotionEligible = false,
                records = predictions.Length,
                modelSha256 = manifest.ModelSha256,
                featureContractSha256 = manifest.FeatureContractSha256,
            }
        )
    );
    return Success;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"translation-worthiness benchmark error: {exception.Message}");
    return UsageError;
}
catch (InvalidDataException exception)
{
    Console.Error.WriteLine($"translation-worthiness benchmark error: {exception.Message}");
    return UsageError;
}
catch (IOException)
{
    Console.Error.WriteLine("translation-worthiness benchmark error: filesystem operation failed");
    return UsageError;
}

internal sealed record Options(
    string ModelPath,
    string ManifestPath,
    string CorpusPath,
    string OutputPath
)
{
    internal static Options Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Use --model <path> --manifest <path> --corpus <path> --output <path>."
                );
            }
            if (!values.TryAdd(arguments[index], arguments[index + 1]))
            {
                throw new ArgumentException($"Duplicate option: {arguments[index]}");
            }
        }
        var allowed = new HashSet<string>(
            ["--model", "--manifest", "--corpus", "--output"],
            StringComparer.Ordinal
        );
        if (values.Keys.Any(key => !allowed.Contains(key)) || values.Count != allowed.Count)
        {
            throw new ArgumentException(
                "Use exactly --model <path> --manifest <path> --corpus <path> --output <path>."
            );
        }
        return new Options(
            values["--model"],
            values["--manifest"],
            values["--corpus"],
            values["--output"]
        );
    }
}

internal static class PhysicalFiles
{
    internal static FileInfo Input(string path, string name, long maximumBytes)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || IsLink(info) || info.Length <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException($"{name} must be a bounded physical file.");
        }
        return info;
    }

    internal static string Destination(string path, params FileInfo[] inputs)
    {
        var fullPath = Path.GetFullPath(path);
        if (inputs.Any(input =>
                string.Equals(input.FullName, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Prediction output must not alias an input.");
        }
        var parent = Directory.GetParent(fullPath)
            ?? throw new InvalidDataException("Prediction output has no parent directory.");
        if (!parent.Exists || IsLink(parent))
        {
            throw new InvalidDataException("Prediction output parent must be a physical directory.");
        }
        var existing = new FileInfo(fullPath);
        if (existing.Exists && IsLink(existing))
        {
            throw new InvalidDataException("Prediction output must be a physical file or absent.");
        }
        return fullPath;
    }

    internal static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
}

internal sealed record ExperimentManifest(
    string Ablation,
    int BucketCount,
    double SuppressMax,
    double RetainMin,
    string ModelSha256,
    string FeatureContractSha256
)
{
    private static readonly HashSet<string> Ablations =
        new(["engineered-only", "character-ngrams-only", "combined"], StringComparer.Ordinal);

    internal static ExperimentManifest Load(string path)
    {
        var manifestFile = PhysicalFiles.Input(path, "model manifest", 64 * 1024);
        using var stream = new FileStream(
            manifestFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan
        );
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }
        );
        var root = document.RootElement;
        RequireObject(root, "model manifest");
        if (!RequiredBoolean(root, "researchOnly") || RequiredBoolean(root, "promotionEligible"))
        {
            throw new InvalidDataException("Model manifest is not research-only.");
        }
        if (!string.Equals(RequiredString(root, "scoreOrientation"), "higher-contains-any-human", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Model score orientation is unsupported.");
        }
        var ablation = RequiredString(root, "ablation");
        if (!Ablations.Contains(ablation))
        {
            throw new InvalidDataException("Model ablation is unsupported.");
        }
        var thresholds = RequiredProperty(root, "thresholds");
        RequireObject(thresholds, "thresholds");
        var suppressMax = RequiredFiniteDouble(thresholds, "suppressMax");
        var retainMin = RequiredFiniteDouble(thresholds, "retainMin");
        if (suppressMax >= retainMin)
        {
            throw new InvalidDataException("Model thresholds do not leave an abstention interval.");
        }
        var hashes = RequiredProperty(root, "hashes");
        RequireObject(hashes, "hashes");
        var modelSha256 = RequiredSha256(hashes, "modelSha256");
        var featureContractSha256 = RequiredSha256(hashes, "featureContractSha256");
        var model = RequiredProperty(root, "model");
        RequireObject(model, "model");
        var bucketCount = RequiredInt32(model, "bucketCount");
        if (bucketCount is <= 0 or > 4_194_304)
        {
            throw new InvalidDataException("Model bucket count is outside bounds.");
        }
        if (RequiredInt32(model, "formatVersion") != 1
            || RequiredInt32(model, "ngramMin") != 2
            || RequiredInt32(model, "ngramMax") != 5)
        {
            throw new InvalidDataException("Model feature identity is unsupported.");
        }
        return new ExperimentManifest(
            ablation,
            bucketCount,
            suppressMax,
            retainMin,
            modelSha256,
            featureContractSha256
        );
    }

    internal string Decision(double score)
    {
        if (!double.IsFinite(score))
        {
            throw new InvalidDataException("Managed reference emitted a non-finite score.");
        }
        return score <= SuppressMax ? "suppress" : score >= RetainMin ? "retain" : "abstain";
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"Model manifest is missing {name}.");
        }
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Model manifest has an invalid {name}.");
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Model manifest has an invalid {name}."),
        };
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (!value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"Model manifest has an invalid {name}.");
        }
        return result;
    }

    private static double RequiredFiniteDouble(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (!value.TryGetDouble(out var result) || !double.IsFinite(result))
        {
            throw new InvalidDataException($"Model manifest has an invalid {name}.");
        }
        return result;
    }

    private static string RequiredSha256(JsonElement parent, string name)
    {
        var value = RequiredString(parent, name);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"Model manifest has an invalid {name}.");
        }
        return value;
    }

    private static void RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{name} must be an object.");
        }
    }
}

internal sealed class SparseLinearModel
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _ablation;
    private readonly double[] _weights;
    private readonly double _bias;

    private SparseLinearModel(string ablation, double[] weights, double bias)
    {
        _ablation = ablation;
        _weights = weights;
        _bias = bias;
    }

    internal static SparseLinearModel Load(string path, ExperimentManifest manifest)
    {
        var modelFile = PhysicalFiles.Input(path, "model", 64L * 1024 * 1024);
        var raw = File.ReadAllBytes(modelFile.FullName);
        var actualHash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        if (!string.Equals(actualHash, manifest.ModelSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Model SHA-256 does not match its manifest.");
        }
        const int headerSize = 40;
        if (raw.Length < headerSize || !raw.AsSpan(0, 8).SequenceEqual("BSTRWFM1"u8))
        {
            throw new InvalidDataException("Model is truncated or has the wrong magic.");
        }
        var ablationCode = ReadUInt32(raw, 12);
        var ablation = ablationCode switch
        {
            1 => "engineered-only",
            2 => "character-ngrams-only",
            3 => "combined",
            _ => throw new InvalidDataException("Model ablation code is unsupported."),
        };
        var bucketCount = checked((int)ReadUInt32(raw, 16));
        var weightCount = checked((int)ReadUInt32(raw, 28));
        if (ReadUInt32(raw, 8) != 1
            || bucketCount != manifest.BucketCount
            || weightCount != bucketCount
            || ReadUInt32(raw, 20) != 2
            || ReadUInt32(raw, 24) != 5
            || !string.Equals(ablation, manifest.Ablation, StringComparison.Ordinal)
            || raw.LongLength != headerSize + weightCount * sizeof(double))
        {
            throw new InvalidDataException("Model binary identity does not match its manifest.");
        }
        var bias = ReadDouble(raw, 32);
        if (!double.IsFinite(bias))
        {
            throw new InvalidDataException("Model bias is invalid.");
        }
        var weights = new double[weightCount];
        for (var index = 0; index < weightCount; index++)
        {
            weights[index] = ReadDouble(raw, headerSize + index * sizeof(double));
            if (!double.IsFinite(weights[index]))
            {
                throw new InvalidDataException("Model contains an invalid weight.");
            }
        }
        return new SparseLinearModel(ablation, weights, bias);
    }

    internal double Score(string text)
    {
        var features = ExtractFeatures(text, _ablation, _weights.Length);
        var score = _bias;
        foreach (var pair in features.OrderBy(pair => pair.Key))
        {
            score += _weights[pair.Key] * pair.Value;
        }
        return double.IsFinite(score)
            ? score
            : throw new InvalidDataException("Managed reference emitted a non-finite score.");
    }

    private static Dictionary<int, int> ExtractFeatures(string text, string ablation, int buckets)
    {
        byte[] encoded;
        try
        {
            encoded = StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("Corpus text is not valid Unicode.", exception);
        }
        var features = new Dictionary<int, int>();
        if (ablation is "engineered-only" or "combined")
        {
            AddEngineered(text, encoded.Length, features, buckets);
        }
        if (ablation is "character-ngrams-only" or "combined")
        {
            AddNgrams(encoded, features, buckets);
        }
        return features;
    }

    private static void AddEngineered(
        string text,
        int utf8Length,
        Dictionary<int, int> features,
        int buckets
    )
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var longestAlpha = 0;
        var longestDigit = 0;
        var currentAlpha = 0;
        var currentDigit = 0;
        var scalarCount = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            scalarCount++;
            Increment(counts, $"range:{UnicodeRange(rune.Value)}");
            if (rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                Increment(counts, "ascii:alpha");
                currentAlpha++;
            }
            else
            {
                longestAlpha = Math.Max(longestAlpha, currentAlpha);
                currentAlpha = 0;
            }
            if (rune.Value is >= '0' and <= '9')
            {
                Increment(counts, "ascii:digit");
                currentDigit++;
            }
            else
            {
                longestDigit = Math.Max(longestDigit, currentDigit);
                currentDigit = 0;
            }
            if (rune.Value is ' ' or '\t' or '\r' or '\n')
            {
                Increment(counts, "ascii:whitespace");
            }
            if (rune.Value < 0x20 || rune.Value == 0x7f)
            {
                Increment(counts, "ascii:control");
            }
            if (rune.Value <= 0x7f && "{}[]()<>=:+-*/\\|&;,.!?_'\"`~@#$%^".Contains((char)rune.Value, StringComparison.Ordinal))
            {
                Increment(counts, "ascii:punctuation-symbol");
            }
        }
        counts["length:unicode-scalars"] = scalarCount;
        counts["length:utf8-bytes"] = utf8Length;
        counts["run:ascii-alpha"] = Math.Max(longestAlpha, currentAlpha);
        counts["run:ascii-digit"] = Math.Max(longestDigit, currentDigit);
        foreach (var pair in counts)
        {
            AddFeature(features, StrictUtf8.GetBytes("e:" + pair.Key), pair.Value, buckets);
        }
    }

    private static void AddNgrams(byte[] encoded, Dictionary<int, int> features, int buckets)
    {
        Span<byte> key = stackalloc byte[9];
        key[0] = (byte)'n';
        key[1] = (byte)':';
        key[3] = (byte)':';
        for (var width = 2; width <= 5; width++)
        {
            key[2] = (byte)width;
            for (var start = 0; start + width <= encoded.Length; start++)
            {
                encoded.AsSpan(start, width).CopyTo(key[4..]);
                AddFeature(features, key[..(4 + width)], 1, buckets);
            }
        }
    }

    private static void AddFeature(
        Dictionary<int, int> features,
        ReadOnlySpan<byte> key,
        int value,
        int buckets
    )
    {
        if (value <= 0)
        {
            return;
        }
        var index = checked((int)(Fnv1a64(key) % (ulong)buckets));
        features.TryGetValue(index, out var existing);
        features[index] = Math.Min(255, existing + value);
    }

    private static ulong Fnv1a64(ReadOnlySpan<byte> value)
    {
        var result = 0xcbf29ce484222325UL;
        foreach (var item in value)
        {
            result ^= item;
            result = unchecked(result * 0x100000001b3UL);
        }
        return result;
    }

    private static string UnicodeRange(int codepoint) => codepoint switch
    {
        >= 0x0000 and <= 0x007f => "basic-latin",
        >= 0x0080 and <= 0x024f => "extended-latin",
        >= 0x0370 and <= 0x03ff => "greek",
        >= 0x0400 and <= 0x052f => "cyrillic",
        >= 0x0590 and <= 0x05ff => "hebrew",
        >= 0x0600 and <= 0x06ff => "arabic",
        >= 0x0900 and <= 0x097f => "devanagari",
        >= 0x3040 and <= 0x309f => "hiragana",
        >= 0x30a0 and <= 0x30ff => "katakana",
        >= 0x3400 and <= 0x4dbf => "han-a",
        >= 0x4e00 and <= 0x9fff => "han",
        >= 0xac00 and <= 0xd7af => "hangul",
        _ => "other",
    };

    private static void Increment(SortedDictionary<string, int> counts, string name)
    {
        counts.TryGetValue(name, out var value);
        counts[name] = value + 1;
    }

    private static uint ReadUInt32(byte[] raw, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset, sizeof(uint)));

    private static double ReadDouble(byte[] raw, int offset) =>
        BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(offset, sizeof(long)))
        );
}

internal sealed record CorpusRow(string Identifier, string Text)
{
    internal static CorpusRow[] LoadAll(string path)
    {
        var corpusFile = PhysicalFiles.Input(path, "corpus", 512L * 1024 * 1024);
        var rows = new List<CorpusRow>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        using var stream = new FileStream(
            corpusFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan
        );
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 64 * 1024);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line.Length > 65_536 || rows.Count >= 1_000_000)
            {
                throw new InvalidDataException("Corpus row count or length is outside bounds.");
            }
            using var document = JsonDocument.Parse(
                line,
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }
            );
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var idValue)
                || !root.TryGetProperty("text", out var textValue)
                || idValue.ValueKind != JsonValueKind.String
                || textValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Corpus row does not provide id and text strings.");
            }
            var identifier = idValue.GetString()!;
            var text = textValue.GetString()!;
            if (identifier.Length is < 1 or > 64
                || text.Length is < 1 or > 4096
                || !identifiers.Add(identifier))
            {
                throw new InvalidDataException("Corpus row identity or text is outside bounds.");
            }
            rows.Add(new CorpusRow(identifier, text));
        }
        if (rows.Count == 0)
        {
            throw new InvalidDataException("Corpus is empty.");
        }
        return rows.ToArray();
    }
}

internal sealed record Prediction(string Identifier, double Score, string Decision);

internal static class PredictionWriter
{
    internal static void WriteAtomic(string path, IReadOnlyList<Prediction> predictions)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Directory.GetParent(fullPath)
            ?? throw new InvalidDataException("Prediction output has no parent directory.");
        if (!parent.Exists || PhysicalFiles.IsLink(parent))
        {
            throw new InvalidDataException("Prediction output parent must be a physical directory.");
        }
        var existing = new FileInfo(fullPath);
        if (existing.Exists && PhysicalFiles.IsLink(existing))
        {
            throw new InvalidDataException("Prediction output must be a physical file or absent.");
        }
        var temporary = Path.Combine(parent.FullName, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan
            ))
            {
                foreach (var prediction in predictions)
                {
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("decision", prediction.Decision);
                        writer.WriteString("id", prediction.Identifier);
                        writer.WriteNumber("score", prediction.Score);
                        writer.WriteEndObject();
                    }
                    buffer.WriteByte((byte)'\n');
                    buffer.Position = 0;
                    buffer.CopyTo(stream);
                }
                stream.Flush(true);
            }
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
