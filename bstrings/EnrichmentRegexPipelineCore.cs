#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal static class EnrichmentRegexPipelineCore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaxJsonLineCharacters = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async Task<EnrichmentPipelineStats> ProcessAsync(
        string inputPath,
        string? outputPath,
        IReadOnlyList<(string name, string pattern)> patterns,
        TextWriter? consoleOutput = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("An enrichment JSONL input path is required.", nameof(inputPath));
        }
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Enrichment JSONL input was not found.", inputPath);
        }
        if (patterns.Count == 0)
        {
            throw new ArgumentException("At least one regex pattern is required.", nameof(patterns));
        }

        var inputFullPath = Path.GetFullPath(inputPath);
        var outputFullPath = string.IsNullOrWhiteSpace(outputPath)
            ? null
            : Path.GetFullPath(outputPath);
        if (
            outputFullPath is not null
            && string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new ArgumentException("The enrichment input and output paths must differ.");
        }

        var compiledPatterns = CompilePatterns(patterns);
        string? temporaryOutputPath = null;
        StreamWriter? ownedWriter = null;
        TextWriter writer;
        if (outputFullPath is null)
        {
            writer = consoleOutput ?? Console.Out;
        }
        else
        {
            var outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
            temporaryOutputPath = outputFullPath + ".partial." + Guid.NewGuid().ToString("N");
            ownedWriter = new StreamWriter(
                new FileStream(
                    temporaryOutputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 1024 * 1024,
                    FileOptions.SequentialScan
                ),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024 * 1024
            );
            writer = ownedWriter;
        }

        long inputRecords = 0;
        long translatedRecords = 0;
        long matchRecords = 0;
        var seenRecordIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var reader = new StreamReader(
                new FileStream(
                    inputFullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1024 * 1024,
                    FileOptions.SequentialScan
                ),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024 * 1024
            );

            long lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lineNumber++;
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (line.Length > MaxJsonLineCharacters)
                {
                    throw new InvalidDataException(
                        $"Enrichment JSONL line {lineNumber:N0} exceeds the {MaxJsonLineCharacters:N0}-character safety limit."
                    );
                }

                EnrichmentStringRecord record;
                try
                {
                    record =
                        JsonSerializer.Deserialize<EnrichmentStringRecord>(line, JsonOptions)
                        ?? throw new JsonException("The record was null.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Invalid enrichment JSONL at line {lineNumber:N0}: {ex.Message}",
                        ex
                    );
                }

                ValidateRecord(record, lineNumber);
                if (IsTranslation(record) && !seenRecordIds.Contains(record.ParentRecordId!))
                {
                    throw new InvalidDataException(
                        $"Translated enrichment record at line {lineNumber:N0} references parentRecordId "
                            + $"'{record.ParentRecordId}', which has not appeared earlier in the stream."
                    );
                }
                if (!seenRecordIds.Add(record.RecordId))
                {
                    throw new InvalidDataException(
                        $"Enrichment JSONL line {lineNumber:N0} repeats recordId '{record.RecordId}'."
                    );
                }
                inputRecords++;
                if (IsTranslation(record))
                {
                    translatedRecords++;
                }

                var parsedHit = new ParsedHit(record.Text!, record.Text!, record.Location?.Value ?? string.Empty);
                foreach (var (name, pattern, regex) in compiledPatterns)
                {
                    IEnumerable<RegexOutputRecord> matches;
                    try
                    {
                        matches = RegexOutputCore.CreateRecords(
                            parsedHit,
                            name,
                            regex,
                            regexOutput: true,
                            record.SourceFile,
                            GetEvidenceClass(record)
                        );
                        foreach (var match in matches)
                        {
                            var outputRecord = new EnrichmentRegexMatchRecord
                            {
                                PatternName = name,
                                Pattern = pattern,
                                Match = match.DataFound,
                                SourceRecordId = record.RecordId,
                                SourceFile = record.SourceFile,
                                Location = record.Location,
                                Origin = record.Origin,
                                ParentRecordId = record.ParentRecordId,
                                Transform = record.Transform,
                                EvidenceClass = GetEvidenceClass(record),
                                Attributes = record.Attributes,
                            };
                            await writer.WriteLineAsync(
                                JsonSerializer.Serialize(outputRecord, JsonOptions).AsMemory(),
                                cancellationToken
                            );
                            matchRecords++;
                        }
                    }
                    catch (RegexMatchTimeoutException ex)
                    {
                        throw new TimeoutException(
                            $"Regex '{name}' timed out at enrichment record '{record.RecordId}'; output is incomplete.",
                            ex
                        );
                    }
                }
            }

            await writer.FlushAsync(cancellationToken);
            if (ownedWriter is not null)
            {
                await ownedWriter.DisposeAsync();
                ownedWriter = null;
                File.Move(temporaryOutputPath!, outputFullPath!, overwrite: true);
                temporaryOutputPath = null;
            }

            return new EnrichmentPipelineStats(inputRecords, translatedRecords, matchRecords);
        }
        finally
        {
            if (ownedWriter is not null)
            {
                await ownedWriter.DisposeAsync();
            }
            if (temporaryOutputPath is not null)
            {
                try
                {
                    File.Delete(temporaryOutputPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static List<(string name, string pattern, Regex regex)> CompilePatterns(
        IReadOnlyList<(string name, string pattern)> patterns
    )
    {
        var compiled = new List<(string name, string pattern, Regex regex)>(patterns.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, pattern) in patterns)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pattern) || !seen.Add(name))
            {
                continue;
            }
            compiled.Add((name, pattern, RegexOutputCore.GetOrCreateRegex(name, pattern)));
        }
        if (compiled.Count == 0)
        {
            throw new ArgumentException("No valid, unique regex patterns were supplied.", nameof(patterns));
        }
        return compiled;
    }

    private static void ValidateRecord(EnrichmentStringRecord record, long lineNumber)
    {
        if (record.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} uses unsupported schema version {record.SchemaVersion}."
            );
        }
        if (!string.Equals(record.RecordType, "string", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} has recordType '{record.RecordType}', expected 'string'."
            );
        }
        if (string.IsNullOrWhiteSpace(record.RecordId))
        {
            throw new InvalidDataException($"Enrichment JSONL line {lineNumber:N0} has no recordId.");
        }
        if (string.IsNullOrEmpty(record.Text))
        {
            throw new InvalidDataException($"Enrichment JSONL line {lineNumber:N0} has no usable text.");
        }
        if (string.IsNullOrWhiteSpace(record.SourceFile))
        {
            throw new InvalidDataException($"Enrichment JSONL line {lineNumber:N0} has no sourceFile.");
        }
        if (
            record.Origin is null
            || string.IsNullOrWhiteSpace(record.Origin.Extractor)
            || string.IsNullOrWhiteSpace(record.Origin.Kind)
        )
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} has an incomplete extractor origin."
            );
        }
        if (
            record.Location is null
            || string.IsNullOrWhiteSpace(record.Location.Kind)
            || string.IsNullOrWhiteSpace(record.Location.Value)
        )
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} has an incomplete location."
            );
        }
        if (record.Transform is not null && !IsTranslation(record))
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} uses unsupported transform kind '{record.Transform.Kind}'."
            );
        }
        if (!IsTranslation(record) && !string.IsNullOrWhiteSpace(record.ParentRecordId))
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} names a parentRecordId without a supported transform."
            );
        }
        if (
            IsTranslation(record)
            && (
                string.IsNullOrWhiteSpace(record.ParentRecordId)
                || string.IsNullOrWhiteSpace(record.Transform?.Engine)
                || string.IsNullOrWhiteSpace(record.Transform?.EngineVersion)
                || string.IsNullOrWhiteSpace(record.Transform?.Model)
                || string.IsNullOrWhiteSpace(record.Transform?.Revision)
                || string.IsNullOrWhiteSpace(record.Transform?.ModelSha256)
                || string.IsNullOrWhiteSpace(record.Transform?.TargetLanguage)
            )
        )
        {
            throw new InvalidDataException(
                $"Translated enrichment record at line {lineNumber:N0} must name its parentRecordId, engine, engineVersion, model, revision, modelSha256, and targetLanguage."
            );
        }
    }

    private static bool IsTranslation(EnrichmentStringRecord record) =>
        string.Equals(record.Transform?.Kind, "translation", StringComparison.OrdinalIgnoreCase);

    private static string GetEvidenceClass(EnrichmentStringRecord record)
    {
        if (IsTranslation(record))
        {
            return "derived-translation";
        }
        if (
            string.Equals(record.Location?.Kind, "file_offset", StringComparison.OrdinalIgnoreCase)
            && (
                string.Equals(record.Origin?.Kind, "static", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Origin?.Kind, "language", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Origin?.Extractor, "bstrings", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "byte-native";
        }
        return "derived-extractor";
    }
}
