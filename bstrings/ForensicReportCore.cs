#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal readonly record struct ForensicReportStats(
    long FindingRows,
    long PatternRows,
    long FeatureRows
);

internal static class ForensicReportCore
{
    internal const int MaximumHistogramFeatureCharacters = 512;
    internal const int HistogramChunkEntryLimit = 100_000;

    internal const string FindingsHeader =
        "PatternName\tMatch\tContext\tSourceFile\tArtifactType\tLocation\tMatchStart\tAttributesJson";

    internal const string PatternHistogramHeader =
        "PatternName\tPatternCategory\tPatternDescription\tMatchCount\tPercentOfAllMatches"
        + "\tByteNativeCount\tDerivedExtractorCount\tDerivedTranslationCount\tOtherEvidenceCount"
        + "\tSourceFileCount\tVisualization";

    internal const string PatternHistogramHeaderWithDecoding =
        "PatternName\tPatternCategory\tPatternDescription\tMatchCount\tPercentOfAllMatches"
        + "\tByteNativeCount\tDerivedExtractorCount\tDerivedTranslationCount\tDerivedDecodingCount"
        + "\tOtherEvidenceCount\tSourceFileCount\tVisualization";

    internal const string FeatureHistogramHeader =
        "BulkExtractorCount\tCount\tPatternName\tPatternCategory\tFeature\tFeatureTruncated";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HistogramKeyComparer HistogramComparer = new();

    internal static async Task<ForensicReportStats> WriteAsync(
        string matchesPath,
        string findingsPath,
        string patternHistogramPath,
        string featureHistogramPath,
        string visualizationPath,
        IReadOnlyList<(string name, string pattern)> patterns,
        CancellationToken cancellationToken = default,
        int histogramChunkEntryLimit = HistogramChunkEntryLimit,
        bool includeDecodingEvidence = false
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(findingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(patternHistogramPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureHistogramPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(visualizationPath);
        ArgumentNullException.ThrowIfNull(patterns);
        if (histogramChunkEntryLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(histogramChunkEntryLimit),
                "The histogram chunk entry limit must be positive."
            );
        }

        if (!File.Exists(matchesPath))
        {
            throw new FileNotFoundException("Regex match JSONL was not found.", matchesPath);
        }

        var matchesFullPath = Path.GetFullPath(matchesPath);
        var outputPaths = new[]
        {
            Path.GetFullPath(findingsPath),
            Path.GetFullPath(patternHistogramPath),
            Path.GetFullPath(featureHistogramPath),
            Path.GetFullPath(visualizationPath),
        };
        if (outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputPaths.Length)
        {
            throw new ArgumentException("Forensic report output paths must be distinct.");
        }
        if (outputPaths.Contains(matchesFullPath, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Forensic report outputs must not replace the match JSONL input.");
        }

        var reportDirectory = Path.GetDirectoryName(outputPaths[0])!;
        Directory.CreateDirectory(reportDirectory);
        if (
            outputPaths.Any(path =>
                !string.Equals(
                    Path.GetDirectoryName(path),
                    reportDirectory,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            throw new ArgumentException("Forensic report outputs must use one directory.");
        }

        var suffix = ".partial." + Guid.NewGuid().ToString("N");
        var temporaryPaths = outputPaths.Select(path => path + suffix).ToArray();
        var chunkPaths = new List<string>();
        var patternMetadata = CreatePatternMetadata(patterns);
        var patternStats = patternMetadata.ToDictionary(
            pair => pair.Key,
            pair => new PatternStatistics(pair.Value),
            StringComparer.OrdinalIgnoreCase
        );
        var featureCounts = new Dictionary<HistogramKey, long>();
        long findingRows = 0;

        try
        {
            await using (var findingsWriter = CreateWriter(temporaryPaths[0]))
            using (
                var matchesReader = new StreamReader(
                    new FileStream(
                        matchesFullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.SequentialScan
                    ),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    1024 * 1024
                )
            )
            {
                await findingsWriter.WriteLineAsync(FindingsHeader.AsMemory(), cancellationToken);
                long lineNumber = 0;
                while (await matchesReader.ReadLineAsync(cancellationToken) is { } line)
                {
                    lineNumber++;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    if (line.Length > EnrichmentRegexPipelineCore.MaxJsonLineCharacters)
                    {
                        throw new InvalidDataException(
                            $"Regex match JSONL line {lineNumber:N0} exceeds the safety limit."
                        );
                    }

                    EnrichmentRegexMatchRecord record;
                    try
                    {
                        record = JsonSerializer.Deserialize<EnrichmentRegexMatchRecord>(line, JsonOptions)
                            ?? throw new JsonException("The match record was null.");
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidDataException(
                            $"Invalid regex match JSONL at line {lineNumber:N0}: {ex.Message}",
                            ex
                        );
                    }
                    ValidateMatchRecord(
                        record,
                        lineNumber,
                        patternMetadata
                    );
                    if (
                        !includeDecodingEvidence
                        && string.Equals(
                            record.EvidenceClass,
                            "derived-decoding",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        throw new InvalidDataException(
                            $"Regex match JSONL line {lineNumber:N0} contains decoding evidence, but decoder report projection is disabled."
                        );
                    }

                    patternStats[record.PatternName].Add(record);
                    var histogramFeature = NormalizeHistogramFeature(
                        record.Match,
                        out _
                    );
                    var histogramKey = new HistogramKey(record.PatternName, histogramFeature);
                    featureCounts.TryGetValue(histogramKey, out var priorCount);
                    featureCounts[histogramKey] = priorCount + 1;
                    if (featureCounts.Count >= histogramChunkEntryLimit)
                    {
                        chunkPaths.Add(FlushHistogramChunk(featureCounts, reportDirectory));
                    }

                    await WriteFindingsRowAsync(
                        findingsWriter,
                        record,
                        cancellationToken
                    );
                    findingRows++;
                }
                await findingsWriter.FlushAsync(cancellationToken);
            }

            if (featureCounts.Count > 0)
            {
                chunkPaths.Add(FlushHistogramChunk(featureCounts, reportDirectory));
            }

            await WritePatternHistogramAsync(
                temporaryPaths[1],
                patternStats.Values,
                findingRows,
                cancellationToken,
                includeDecodingEvidence
            );
            var featureRows = await WriteFeatureHistogramAsync(
                temporaryPaths[2],
                chunkPaths,
                patternMetadata,
                cancellationToken
            );
            await WriteVisualizationAsync(
                temporaryPaths[3],
                patternStats.Values,
                findingRows,
                cancellationToken
            );

            for (var index = 0; index < outputPaths.Length; index++)
            {
                File.Move(temporaryPaths[index], outputPaths[index], overwrite: true);
                temporaryPaths[index] = string.Empty;
            }

            return new ForensicReportStats(findingRows, patternStats.Count, featureRows);
        }
        finally
        {
            foreach (var path in temporaryPaths.Concat(chunkPaths))
            {
                if (!string.IsNullOrEmpty(path))
                {
                    DeleteTemporaryFile(path);
                }
            }
        }
    }

    private static Dictionary<string, PatternMetadata> CreatePatternMetadata(
        IReadOnlyList<(string name, string pattern)> patterns
    )
    {
        var result = new Dictionary<string, PatternMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, pattern) in patterns)
        {
            if (BuiltInPatternCatalog.TryGetDefinition(name, pattern, out var definition))
            {
                result[name] = new PatternMetadata(
                    definition.Name,
                    definition.Pattern,
                    definition.Description,
                    definition.Source,
                    GetCategory(definition.Name),
                    BuiltInPatternCatalog.GetValidationLabel(definition)
                );
            }
            else
            {
                result[name] = PatternMetadata.CreateCustom(name, pattern);
            }
        }
        return result;
    }

    private static async Task WriteFindingsRowAsync(
        StreamWriter writer,
        EnrichmentRegexMatchRecord record,
        CancellationToken cancellationToken
    )
    {
        var artifactType = ClassifySourceArtifact(record.SourceFile);
        if (artifactType.Length == 0)
        {
            artifactType = ClassifySourceArtifact(record.Match);
        }
        var attributesJson = CreateFindingsAttributesJson(record);

        await WriteTsvRowAsync(
            writer,
            [
                record.PatternName,
                record.Match,
                record.Context ?? string.Empty,
                record.SourceFile,
                artifactType,
                record.Location?.Value ?? string.Empty,
                record.MatchStart.ToString(CultureInfo.InvariantCulture),
                attributesJson,
            ],
            cancellationToken
        );
    }

    private static string CreateFindingsAttributesJson(EnrichmentRegexMatchRecord record)
    {
        if (record.Attributes?.ContainsKey("sourceRecordId") == true)
        {
            throw new InvalidDataException(
                "Regex match attributes use reserved projection key 'sourceRecordId'."
            );
        }
        var encodedRecordId = EncodeJsonStringContent(record.SourceRecordId);
        if (record.Attributes is null || record.Attributes.Count == 0)
        {
            return $"{{\"sourceRecordId\":\"{encodedRecordId}\"}}";
        }

        var serializedAttributes = JsonSerializer.Serialize(record.Attributes, JsonOptions);
        return $"{{\"sourceRecordId\":\"{encodedRecordId}\",{serializedAttributes[1..]}";
    }

    private static string EncodeJsonStringContent(string value)
    {
        foreach (var character in value)
        {
            if (character is '"' or '\\' || character < ' ')
            {
                return JsonEncodedText.Encode(value).ToString();
            }
        }
        return value;
    }

    private static async Task WritePatternHistogramAsync(
        string path,
        IEnumerable<PatternStatistics> statistics,
        long totalMatches,
        CancellationToken cancellationToken,
        bool includeDecodingEvidence
    )
    {
        var ordered = statistics
            .OrderByDescending(value => value.MatchCount)
            .ThenBy(value => value.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var maximum = ordered.Length == 0 ? 0 : ordered.Max(value => value.MatchCount);
        await using var writer = CreateWriter(path);
        await writer.WriteLineAsync(
            (includeDecodingEvidence ? PatternHistogramHeaderWithDecoding : PatternHistogramHeader).AsMemory(),
            cancellationToken
        );
        foreach (var value in ordered)
        {
            var percentage = totalMatches == 0
                ? 0
                : value.MatchCount * 100.0 / totalMatches;
            var row = new List<string>
            {
                value.Metadata.Name,
                value.Metadata.Category,
                value.Metadata.Description,
                value.MatchCount.ToString(CultureInfo.InvariantCulture),
                percentage.ToString("0.0000", CultureInfo.InvariantCulture),
                value.ByteNativeCount.ToString(CultureInfo.InvariantCulture),
                value.DerivedExtractorCount.ToString(CultureInfo.InvariantCulture),
                value.DerivedTranslationCount.ToString(CultureInfo.InvariantCulture),
            };
            if (includeDecodingEvidence)
            {
                row.Add(value.DerivedDecodingCount.ToString(CultureInfo.InvariantCulture));
            }
            row.Add(value.OtherEvidenceCount.ToString(CultureInfo.InvariantCulture));
            row.Add(value.SourceFiles.Count.ToString(CultureInfo.InvariantCulture));
            row.Add(CreateBar(value.MatchCount, maximum, 40));
            await WriteTsvRowAsync(writer, row, cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task<long> WriteFeatureHistogramAsync(
        string path,
        IReadOnlyList<string> chunkPaths,
        IReadOnlyDictionary<string, PatternMetadata> metadata,
        CancellationToken cancellationToken
    )
    {
        await using var writer = CreateWriter(path);
        await writer.WriteLineAsync(FeatureHistogramHeader.AsMemory(), cancellationToken);
        if (chunkPaths.Count == 0)
        {
            return 0;
        }

        var readers = chunkPaths.Select(path => new HistogramChunkReader(path)).ToArray();
        var queue = new PriorityQueue<int, HistogramKey>(HistogramComparer);
        long rows = 0;
        try
        {
            for (var index = 0; index < readers.Length; index++)
            {
                if (readers[index].MoveNext())
                {
                    queue.Enqueue(index, readers[index].Current.Key);
                }
            }

            while (queue.TryDequeue(out var readerIndex, out var key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long count = 0;
                AddCurrentAndAdvance(readerIndex, readers, queue, ref count);
                while (
                    queue.TryPeek(out _, out var nextKey)
                    && HistogramComparer.Compare(key, nextKey) == 0
                )
                {
                    queue.TryDequeue(out readerIndex, out _);
                    AddCurrentAndAdvance(readerIndex, readers, queue, ref count);
                }

                var feature = key.Feature;
                var truncated = feature.StartsWith("sha256:", StringComparison.Ordinal);
                var category = metadata.TryGetValue(key.PatternName, out var patternMetadata)
                    ? patternMetadata.Category
                    : "custom";
                await WriteTsvRowAsync(
                    writer,
                    [
                        $"n={count.ToString(CultureInfo.InvariantCulture)}",
                        count.ToString(CultureInfo.InvariantCulture),
                        key.PatternName,
                        category,
                        feature,
                        truncated ? "true" : "false",
                    ],
                    cancellationToken
                );
                rows++;
            }
            await writer.FlushAsync(cancellationToken);
            return rows;
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static void AddCurrentAndAdvance(
        int readerIndex,
        IReadOnlyList<HistogramChunkReader> readers,
        PriorityQueue<int, HistogramKey> queue,
        ref long count
    )
    {
        var reader = readers[readerIndex];
        count += reader.Current.Count;
        if (reader.MoveNext())
        {
            queue.Enqueue(readerIndex, reader.Current.Key);
        }
    }

    private static async Task WriteVisualizationAsync(
        string path,
        IEnumerable<PatternStatistics> statistics,
        long totalMatches,
        CancellationToken cancellationToken
    )
    {
        var ordered = statistics
            .OrderByDescending(value => value.MatchCount)
            .ThenBy(value => value.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var maximum = ordered.Length == 0 ? 0 : ordered.Max(value => value.MatchCount);
        var html = new StringBuilder(4096);
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<title>bstrings pattern histogram</title>");
        html.AppendLine("<style>body{font:14px system-ui,sans-serif;margin:2rem;color:#18212f}table{border-collapse:collapse;width:100%}th,td{padding:.45rem .6rem;border-bottom:1px solid #d8dee8;text-align:left}th{position:sticky;top:0;background:#fff}.bar{height:1rem;background:#2463eb;border-radius:3px;min-width:0}.track{width:100%;min-width:12rem;background:#edf2f7;border-radius:3px}.num{text-align:right;font-variant-numeric:tabular-nums}</style></head><body>");
        html.Append("<h1>Pattern histogram</h1><p>Total matches: ")
            .Append(totalMatches.ToString("N0", CultureInfo.InvariantCulture))
            .AppendLine(". Zero-count requested patterns are retained.</p>");
        html.AppendLine("<table><thead><tr><th>Pattern</th><th>Category</th><th class=\"num\">Matches</th><th>Relative volume</th><th>Description</th></tr></thead><tbody>");
        foreach (var value in ordered)
        {
            var width = maximum == 0 ? 0 : value.MatchCount * 100.0 / maximum;
            html.Append("<tr><td>")
                .Append(WebUtility.HtmlEncode(value.Metadata.Name))
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(value.Metadata.Category))
                .Append("</td><td class=\"num\">")
                .Append(value.MatchCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td><div class=\"track\"><div class=\"bar\" style=\"width:")
                .Append(width.ToString("0.####", CultureInfo.InvariantCulture))
                .Append("%\"></div></div></td><td>")
                .Append(WebUtility.HtmlEncode(value.Metadata.Description))
                .AppendLine("</td></tr>");
        }
        html.AppendLine("</tbody></table></body></html>");
        await File.WriteAllTextAsync(
            path,
            html.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken
        );
    }

    private static string FlushHistogramChunk(
        Dictionary<HistogramKey, long> counts,
        string directory
    )
    {
        var path = Path.Combine(
            directory,
            ".bstrings-feature-histogram." + Guid.NewGuid().ToString("N") + ".chunk"
        );
        try
        {
            using var writer = new BinaryWriter(
                new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.SequentialScan
                ),
                Encoding.UTF8,
                leaveOpen: false
            );
            foreach (
                var pair in counts
                    .OrderBy(pair => pair.Key, HistogramComparer)
            )
            {
                writer.Write(pair.Key.PatternName);
                writer.Write(pair.Key.Feature);
                writer.Write(pair.Value);
            }
            counts.Clear();
            return path;
        }
        catch
        {
            DeleteTemporaryFile(path);
            throw;
        }
    }

    private static StreamWriter CreateWriter(string path) =>
        new(
            new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan
            ),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            1024 * 1024
        );

    private static async Task WriteTsvRowAsync(
        StreamWriter writer,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken
    )
    {
        var line = new StringBuilder(values.Sum(value => value?.Length ?? 0) + values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                line.Append('\t');
            }
            AppendEscapedTsv(line, values[index] ?? string.Empty);
        }
        await writer.WriteLineAsync(line.ToString().AsMemory(), cancellationToken);
    }

    internal static string EscapeTsv(string value)
    {
        var result = new StringBuilder(value?.Length ?? 0);
        AppendEscapedTsv(result, value ?? string.Empty);
        return result.ToString();
    }

    private static void AppendEscapedTsv(StringBuilder output, string value)
    {
        foreach (var character in value)
        {
            switch (character)
            {
                case '\t':
                    output.Append("\\t");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                default:
                    if (character < ' ' || character == '\u007F')
                    {
                        output.Append("\\u")
                            .Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(character);
                    }
                    break;
            }
        }
    }

    private static PatternMetadata ValidateMatchRecord(
        EnrichmentRegexMatchRecord record,
        long lineNumber,
        IReadOnlyDictionary<string, PatternMetadata> selectedPatterns
    )
    {
        if (
            record.SchemaVersion != EnrichmentRegexPipelineCore.CurrentSchemaVersion
            || !string.Equals(record.RecordType, "regex-match", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(record.PatternName)
            || string.IsNullOrWhiteSpace(record.PatternDescription)
            || string.IsNullOrWhiteSpace(record.PatternSource)
            || string.IsNullOrWhiteSpace(record.PatternValidation)
            || string.IsNullOrWhiteSpace(record.SourceRecordId)
            || string.IsNullOrWhiteSpace(record.SourceFile)
            || record.Match is null
        )
        {
            throw new InvalidDataException(
                $"Regex match JSONL line {lineNumber:N0} is missing required schema or provenance fields."
            );
        }

        if (!selectedPatterns.TryGetValue(record.PatternName, out var metadata))
        {
            throw new InvalidDataException(
                $"Regex match JSONL line {lineNumber:N0} references unselected pattern '{record.PatternName}'."
            );
        }
        if (!string.Equals(record.Pattern, metadata.Pattern, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Regex match JSONL line {lineNumber:N0} expression does not match selected pattern '{record.PatternName}'."
            );
        }
        if (
            record.MatchStart < 0
            || record.MatchLength != record.Match.Length
            || record.MatchStart > int.MaxValue - record.MatchLength
        )
        {
            throw new InvalidDataException(
                $"Regex match JSONL line {lineNumber:N0} contains an invalid match range."
            );
        }
        if (
            !string.Equals(record.PatternDescription, metadata.Description, StringComparison.Ordinal)
            || !string.Equals(record.PatternSource, metadata.Source, StringComparison.Ordinal)
            || !string.Equals(record.PatternValidation, metadata.Validation, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                $"Regex match JSONL line {lineNumber:N0} pattern metadata does not match selected pattern '{record.PatternName}'."
            );
        }
        var isDecoding = string.Equals(
            record.Transform?.Kind,
            "decoding",
            StringComparison.OrdinalIgnoreCase
        );
        if (
            isDecoding
            && (
                !string.Equals(record.EvidenceClass, "derived-decoding", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(record.ParentRecordId)
                || !string.Equals(record.Transform?.Engine, "bstrings", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(record.Transform?.EngineVersion)
                || record.Transform?.Profile
                    is not ("powershell-encoded-command-v1" or "rfc4648-base64-text-v1")
                || !string.Equals(
                    record.Transform?.PolicyVersion,
                    "decoder-policy-v1",
                    StringComparison.Ordinal
                )
                || !string.Equals(record.Transform?.Outcome, "decoded-text", StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidDataException(
                $"Regex match JSONL line {lineNumber:N0} has incomplete decoding provenance."
            );
        }
        if (
            !isDecoding
            && string.Equals(record.EvidenceClass, "derived-decoding", StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                $"Regex match JSONL line {lineNumber:N0} claims derived-decoding evidence without a decoding transform."
            );
        }

        return metadata;
    }

    private static string NormalizeHistogramFeature(string value, out bool truncated)
    {
        if (value.Length <= MaximumHistogramFeatureCharacters)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var preview = value[..Math.Min(160, value.Length)];
        return $"sha256:{digest}:preview:{preview}";
    }

    private static string GetCategory(string name)
    {
        if (BuiltInPatternCatalog.Groups.TryGetValue("pii", out var pii) && Contains(pii, name))
        {
            return "pii";
        }
        if (
            BuiltInPatternCatalog.Groups.TryGetValue("credentials", out var credentials)
            && Contains(credentials, name)
        )
        {
            return "credential";
        }
        if (
            BuiltInPatternCatalog.Groups.TryGetValue("registry", out var registry)
            && Contains(registry, name)
        )
        {
            return "registry";
        }
        if (BuiltInPatternCatalog.Groups.TryGetValue("wallets", out var wallets) && Contains(wallets, name))
        {
            return "cryptocurrency";
        }
        return name switch
        {
            "ipv4" or "ipv6" or "mac" or "url3986" or "unc" or "named_pipe" or "onion_v3" => "network",
            "win_path" => "filesystem",
            "b64" => "encoded-data",
            "browser_profile_path" => "browser-artifact",
            "cve" or "cpe23" => "security-identifier",
            "sha256" or "md5_labelled" or "sha1_labelled" or "sha384_labelled" or "sha512_labelled" => "hash",
            "tlp_marking" => "handling-marking",
            "email_message_id" => "communication-identifier",
            "lei" => "legal-entity-identifier",
            "guid" or "sid" => "identifier",
            _ => "other",
        };
    }

    private static bool Contains(IReadOnlyList<string> values, string value) =>
        values.Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));

    private static string ClassifySourceArtifact(string sourceFile)
    {
        var separator = sourceFile.LastIndexOfAny(['\\', '/']);
        var fileName = separator >= 0 ? sourceFile[(separator + 1)..] : sourceFile;
        string extension;
        try
        {
            extension = Path.GetExtension(fileName);
        }
        catch (ArgumentException)
        {
            extension = string.Empty;
        }

        var normalized = sourceFile.Replace('/', '\\');
        var lower = normalized.ToLowerInvariant();
        var browser = string.Empty;
        if (lower.Contains("\\google\\chrome\\", StringComparison.Ordinal))
        {
            browser = "Google Chrome";
        }
        else if (lower.Contains("\\microsoft\\edge\\", StringComparison.Ordinal))
        {
            browser = "Microsoft Edge";
        }
        else if (lower.Contains("\\brave-browser\\", StringComparison.Ordinal))
        {
            browser = "Brave";
        }
        else if (lower.Contains("\\chromium\\", StringComparison.Ordinal))
        {
            browser = "Chromium";
        }
        else if (lower.Contains("\\mozilla\\firefox\\", StringComparison.Ordinal))
        {
            browser = "Mozilla Firefox";
        }

        return ClassifyArtifact(fileName, extension, browser);
    }

    private static string ClassifyArtifact(string fileName, string extension, string browser)
    {
        if (browser.Length > 0)
        {
            if (
                fileName.Equals("Login Data", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("logins.json", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("key4.db", StringComparison.OrdinalIgnoreCase)
            )
            {
                return "browser-credential-store";
            }
            if (
                fileName.Equals("History", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("places.sqlite", StringComparison.OrdinalIgnoreCase)
            )
            {
                return "browser-history";
            }
            if (
                fileName.Contains("Cookie", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("cookies.sqlite", StringComparison.OrdinalIgnoreCase)
            )
            {
                return "browser-cookies";
            }
            return "browser-profile";
        }

        if (
            fileName.Equals("NTUSER.DAT", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("UsrClass.dat", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Amcache.hve", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("SAM", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("SECURITY", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "windows-registry-hive";
        }
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "pdf";
        }
        return string.Empty;
    }

    private static string CreateBar(long value, long maximum, int width)
    {
        if (value <= 0 || maximum <= 0)
        {
            return string.Empty;
        }
        var characters = Math.Max(1, (int)Math.Round(value * width / (double)maximum));
        return new string('#', Math.Min(width, characters));
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PatternMetadata(
        string Name,
        string Pattern,
        string Description,
        string Source,
        string Category,
        string Validation
    )
    {
        internal static PatternMetadata CreateCustom(string name, string pattern) =>
            new(
                name,
                pattern,
                "User-supplied regular expression",
                "user-supplied",
                "custom",
                "custom-regex"
            );
    }

    private sealed class PatternStatistics(PatternMetadata metadata)
    {
        internal PatternMetadata Metadata { get; } = metadata;
        internal long MatchCount { get; private set; }
        internal long ByteNativeCount { get; private set; }
        internal long DerivedExtractorCount { get; private set; }
        internal long DerivedTranslationCount { get; private set; }
        internal long DerivedDecodingCount { get; private set; }
        internal long OtherEvidenceCount { get; private set; }
        internal HashSet<string> SourceFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal void Add(EnrichmentRegexMatchRecord record)
        {
            MatchCount++;
            SourceFiles.Add(record.SourceFile);
            switch (record.EvidenceClass)
            {
                case "byte-native":
                    ByteNativeCount++;
                    break;
                case "derived-extractor":
                    DerivedExtractorCount++;
                    break;
                case "derived-translation":
                    DerivedTranslationCount++;
                    break;
                case "derived-decoding":
                    DerivedDecodingCount++;
                    break;
                default:
                    OtherEvidenceCount++;
                    break;
            }
        }
    }

    private readonly record struct HistogramKey(string PatternName, string Feature);

    private readonly record struct HistogramEntry(HistogramKey Key, long Count);

    private sealed class HistogramKeyComparer : IComparer<HistogramKey>
    {
        public int Compare(HistogramKey left, HistogramKey right)
        {
            var pattern = StringComparer.OrdinalIgnoreCase.Compare(
                left.PatternName,
                right.PatternName
            );
            return pattern != 0
                ? pattern
                : StringComparer.Ordinal.Compare(left.Feature, right.Feature);
        }
    }

    private sealed class HistogramChunkReader : IDisposable
    {
        private readonly BinaryReader _reader;

        internal HistogramChunkReader(string path)
        {
            _reader = new BinaryReader(
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.SequentialScan
                ),
                Encoding.UTF8,
                leaveOpen: false
            );
        }

        internal HistogramEntry Current { get; private set; }

        internal bool MoveNext()
        {
            if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
            {
                return false;
            }
            Current = new HistogramEntry(
                new HistogramKey(_reader.ReadString(), _reader.ReadString()),
                _reader.ReadInt64()
            );
            return true;
        }

        public void Dispose() => _reader.Dispose();
    }
}
