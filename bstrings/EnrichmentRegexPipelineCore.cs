#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal const int MaxNativeTextCharacters = 2 * 1024 * 1024;
    internal const int MatchContextCharacters = 160;

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
        CancellationToken cancellationToken = default,
        bool trustedParentFirstInput = false,
        TranslationValidationRequirements? translationRequirements = null,
        string? translationIntegritySourcePath = null,
        Action<string>? fallbackIndexDeleteDirectory = null
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
        if (
            translationIntegritySourcePath is not null
            && (!trustedParentFirstInput || translationRequirements is null)
        )
        {
            throw new ArgumentException(
                "A translation integrity source requires trusted parent-first validation settings.",
                nameof(translationIntegritySourcePath)
            );
        }
        var translationIntegritySourceFullPath =
            trustedParentFirstInput && translationRequirements is not null
                ? Path.GetFullPath(translationIntegritySourcePath ?? inputFullPath)
                : null;
        if (
            translationIntegritySourceFullPath is not null
            && !File.Exists(translationIntegritySourceFullPath)
        )
        {
            throw new FileNotFoundException(
                "The translation integrity source was not found.",
                translationIntegritySourceFullPath
            );
        }
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
        long preservationFallbackRecords = 0;
        var seenRecordIds = trustedParentFirstInput
            ? null
            : new HashSet<string>(StringComparer.Ordinal);
        using var provenanceValidator = trustedParentFirstInput
            ? new DiskBackedProvenanceValidator(
                outputFullPath is null
                    ? Path.GetTempPath()
                    : Path.GetDirectoryName(outputFullPath)!
            )
            : null;
        using var fallbackTextValidator = translationIntegritySourceFullPath is null
            ? null
            : new DiskBackedFallbackTextValidator(
                outputFullPath is null
                    ? Path.GetTempPath()
                    : Path.GetDirectoryName(outputFullPath)!,
                fallbackIndexDeleteDirectory
            );
        var translatedSectionStarted = false;
        try
        {
            if (fallbackTextValidator is not null)
            {
                await IndexPreservationFallbacksAsync(
                    translationIntegritySourceFullPath!,
                    translationRequirements!,
                    fallbackTextValidator,
                    cancellationToken
                );
            }
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
                var isTranslation = IsTranslation(record);
                if (
                    seenRecordIds is not null
                    && isTranslation
                    && !seenRecordIds.Contains(record.ParentRecordId!)
                )
                {
                    throw new InvalidDataException(
                        $"Translated enrichment record at line {lineNumber:N0} references parentRecordId "
                            + $"'{record.ParentRecordId}', which has not appeared earlier in the stream."
                    );
                }
                if (seenRecordIds is not null && !seenRecordIds.Add(record.RecordId))
                {
                    throw new InvalidDataException(
                        $"Enrichment JSONL line {lineNumber:N0} repeats recordId '{record.RecordId}'."
                    );
                }
                if (trustedParentFirstInput)
                {
                    if (isTranslation)
                    {
                        translatedSectionStarted = true;
                        var integrity = ValidateTranslationRequirements(
                            record,
                            translationRequirements,
                            lineNumber
                        );
                        if (integrity == TranslationIntegrityStatus.PreservationFallback)
                        {
                            fallbackTextValidator!.ValidateFallbackChild(
                                record.ParentRecordId!,
                                record.RecordId,
                                record.Text!
                            );
                            preservationFallbackRecords++;
                        }
                        provenanceValidator!.AddTranslation(
                            record.RecordId,
                            record.ParentRecordId!,
                            CreateLineageIdentity(record)
                        );
                    }
                    else if (translatedSectionStarted)
                    {
                        throw new InvalidDataException(
                            $"Enrichment JSONL line {lineNumber:N0} places an original record after translated children."
                        );
                    }
                    else
                    {
                        fallbackTextValidator?.ValidateParent(record.RecordId, record.Text!);
                        provenanceValidator!.AddOriginal(
                            record.RecordId,
                            CreateLineageIdentity(record)
                        );
                    }
                }
                inputRecords++;
                if (isTranslation)
                {
                    translatedRecords++;
                }

                var parsedHit = new ParsedHit(record.Text!, record.Text!, record.Location?.Value ?? string.Empty);
                int[]? lineStarts = null;
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
                            var context = CreateContext(
                                record.Text!,
                                match.DataStart,
                                match.DataLength
                            );
                            var outputRecord = new EnrichmentRegexMatchRecord
                            {
                                PatternName = name,
                                Pattern = pattern,
                                Match = match.DataFound,
                                MatchStart = match.DataStart,
                                MatchLength = match.DataLength,
                                MatchLine = GetRecordLine(
                                    record.Text!,
                                    match.DataStart,
                                    ref lineStarts
                                ),
                                ContextStart = context.Start,
                                Context = context.Value,
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

            provenanceValidator?.Validate(cancellationToken);
            fallbackTextValidator?.ValidateComplete(requireChildReplay: true);
            fallbackTextValidator?.Cleanup();
            await writer.FlushAsync(cancellationToken);
            if (ownedWriter is not null)
            {
                await ownedWriter.DisposeAsync();
                ownedWriter = null;
                File.Move(temporaryOutputPath!, outputFullPath!, overwrite: true);
                temporaryOutputPath = null;
            }

            return new EnrichmentPipelineStats(
                inputRecords,
                translatedRecords,
                matchRecords,
                preservationFallbackRecords
            );
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

    private static async Task IndexPreservationFallbacksAsync(
        string translationSourcePath,
        TranslationValidationRequirements requirements,
        DiskBackedFallbackTextValidator fallbackTextValidator,
        CancellationToken cancellationToken
    )
    {
        await foreach (
            var item in EnrichmentJsonlReader.ReadAsync(translationSourcePath, cancellationToken)
        )
        {
            if (!IsTranslation(item.Record))
            {
                continue;
            }
            var integrity = ValidateTranslationRequirements(
                item.Record,
                requirements,
                item.LineNumber
            );
            if (integrity == TranslationIntegrityStatus.PreservationFallback)
            {
                fallbackTextValidator.AddFallback(
                    item.Record.ParentRecordId!,
                    item.Record.RecordId,
                    item.Record.Text!
                );
            }
        }
    }

    private static (int Start, string Value) CreateContext(
        string text,
        int matchStart,
        int matchLength
    )
    {
        if (matchStart < 0 || matchStart > text.Length)
        {
            return (-1, string.Empty);
        }

        var safeLength = Math.Clamp(matchLength, 0, text.Length - matchStart);
        var start = Math.Max(0, matchStart - MatchContextCharacters);
        var end = Math.Min(
            text.Length,
            matchStart + safeLength + MatchContextCharacters
        );
        return (start, text[start..end]);
    }

    private static int GetRecordLine(string text, int matchStart, ref int[]? lineStarts)
    {
        if (matchStart < 0 || matchStart > text.Length)
        {
            return 0;
        }

        if (lineStarts is null)
        {
            var starts = new List<int> { 0 };
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\n')
                {
                    starts.Add(index + 1);
                }
            }
            lineStarts = starts.ToArray();
        }

        var found = Array.BinarySearch(lineStarts, matchStart);
        return found >= 0 ? found + 1 : ~found;
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

    internal static void ValidateRecord(EnrichmentStringRecord record, long lineNumber)
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
        if (record.RecordId.Length > DiskBackedProvenanceValidator.MaxIdentifierCharacters)
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} has a recordId longer than the "
                    + $"{DiskBackedProvenanceValidator.MaxIdentifierCharacters:N0}-character safety limit."
            );
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
        var hasOriginModel = !string.IsNullOrWhiteSpace(record.Origin.Model);
        var hasOriginRevision = !string.IsNullOrWhiteSpace(record.Origin.Revision);
        var hasOriginModelSha256 = !string.IsNullOrWhiteSpace(record.Origin.ModelSha256);
        if (
            hasOriginModel != hasOriginRevision
            || hasOriginModel != hasOriginModelSha256
            || (
                hasOriginModelSha256
                && (
                    record.Origin.ModelSha256!.Length != 64
                    || record.Origin.ModelSha256.Any(character => !Uri.IsHexDigit(character))
                )
            )
        )
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} must provide a complete origin model, revision, and 64-character modelSha256."
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
            record.ParentRecordId?.Length
            > DiskBackedProvenanceValidator.MaxIdentifierCharacters
        )
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} has a parentRecordId longer than the "
                    + $"{DiskBackedProvenanceValidator.MaxIdentifierCharacters:N0}-character safety limit."
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

    internal static bool IsTranslation(EnrichmentStringRecord record) =>
        string.Equals(record.Transform?.Kind, "translation", StringComparison.OrdinalIgnoreCase);

    internal static TranslationLineageIdentity CreateLineageIdentity(
        EnrichmentStringRecord record
    ) =>
        new(
            record.SourceFile,
            record.Location!.Kind,
            record.Location.Value,
            record.Origin!.Extractor,
            record.Origin.Version,
            record.Origin.Kind,
            record.Origin.Model,
            record.Origin.Revision,
            record.Origin.ModelSha256,
            record.Origin.Provider
        );

    internal static TranslationIntegrityStatus? ValidateTranslationRequirements(
        EnrichmentStringRecord record,
        TranslationValidationRequirements? requirements,
        long lineNumber
    )
    {
        if (requirements is null || !IsTranslation(record))
        {
            return null;
        }

        var transform = record.Transform!;
        if (transform.Outcome is not ("translated" or "unchanged"))
        {
            throw new InvalidDataException(
                $"Translated enrichment record at line {lineNumber:N0} must record outcome "
                    + "'translated' or 'unchanged'."
            );
        }
        if (!string.Equals(transform.Engine, requirements.Engine, StringComparison.Ordinal))
        {
            throw TranslationSettingMismatch(
                lineNumber,
                "engine",
                requirements.Engine,
                transform.Engine
            );
        }
        if (
            !string.Equals(
                transform.TargetLanguage,
                requirements.TargetLanguage,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw TranslationSettingMismatch(
                lineNumber,
                "targetLanguage",
                requirements.TargetLanguage,
                transform.TargetLanguage
            );
        }
        if (!string.Equals(transform.Model, requirements.Model, StringComparison.Ordinal))
        {
            throw TranslationSettingMismatch(
                lineNumber,
                "model",
                requirements.Model,
                transform.Model
            );
        }
        if (!string.Equals(transform.Revision, requirements.Revision, StringComparison.Ordinal))
        {
            throw TranslationSettingMismatch(
                lineNumber,
                "revision",
                requirements.Revision,
                transform.Revision
            );
        }
        if (
            !string.Equals(
                transform.ModelSha256,
                requirements.ModelSha256,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw TranslationSettingMismatch(
                lineNumber,
                "modelSha256",
                requirements.ModelSha256,
                transform.ModelSha256
            );
        }

        if (
            record.Attributes is null
            || !record.Attributes.TryGetValue("translationIntegrity", out var integrityElement)
            || integrityElement.ValueKind != JsonValueKind.String
        )
        {
            throw new InvalidDataException(
                $"Translated enrichment record at line {lineNumber:N0} must record a string "
                    + "attributes.translationIntegrity value."
            );
        }

        var integrity = integrityElement.GetString() switch
        {
            "verified" => TranslationIntegrityStatus.Verified,
            "source-retained-ambiguous" => TranslationIntegrityStatus.SourceRetainedAmbiguous,
            "preservation-fallback" => TranslationIntegrityStatus.PreservationFallback,
            var value => throw new InvalidDataException(
                $"Translated enrichment record at line {lineNumber:N0} has unsupported "
                    + $"attributes.translationIntegrity '{value ?? "<null>"}'."
            ),
        };

        var hasReason = record.Attributes.TryGetValue(
            "translationIntegrityReason",
            out var reasonElement
        );
        if (integrity == TranslationIntegrityStatus.PreservationFallback)
        {
            if (!string.Equals(transform.Outcome, "unchanged", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Preservation-fallback translation at line {lineNumber:N0} must record "
                        + "transform.outcome 'unchanged'."
                );
            }
            if (
                !hasReason
                || reasonElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(reasonElement.GetString())
            )
            {
                throw new InvalidDataException(
                    $"Preservation-fallback translation at line {lineNumber:N0} must record a "
                        + "non-empty attributes.translationIntegrityReason."
                );
            }
        }
        else if (hasReason)
        {
            throw new InvalidDataException(
                $"Translated enrichment record at line {lineNumber:N0} records "
                    + "attributes.translationIntegrityReason without a preservation fallback."
            );
        }

        var hasAmbiguousCount = record.Attributes.TryGetValue(
            "translationAmbiguousIdentifierCount",
            out var ambiguousCountElement
        );
        if (integrity == TranslationIntegrityStatus.SourceRetainedAmbiguous)
        {
            if (
                !hasAmbiguousCount
                || (
                    ambiguousCountElement.ValueKind != JsonValueKind.Number
                    || !ambiguousCountElement.TryGetInt64(out var ambiguousCount)
                    || ambiguousCount <= 0
                )
            )
            {
                throw new InvalidDataException(
                    $"Translated enrichment record at line {lineNumber:N0} must record "
                        + "attributes.translationAmbiguousIdentifierCount as a positive integer."
                );
            }
        }
        else if (hasAmbiguousCount)
        {
            throw new InvalidDataException(
                $"Translated enrichment record at line {lineNumber:N0} records "
                    + "attributes.translationAmbiguousIdentifierCount without source-retained-ambiguous integrity."
            );
        }

        return integrity;
    }

    private static InvalidDataException TranslationSettingMismatch(
        long lineNumber,
        string field,
        string expected,
        string? actual
    ) =>
        new(
            $"Translated enrichment record at line {lineNumber:N0} has {field} "
                + $"'{actual ?? "<missing>"}', expected the verified setting '{expected}'."
        );

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
