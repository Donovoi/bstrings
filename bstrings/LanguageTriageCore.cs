#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal enum LanguageTriagePolicy
{
    HighRecall,
    Balanced,
    HighPrecision,
}

internal sealed record LanguageTriageOptions(
    string TargetLanguage,
    LanguageDetectionMode DetectionMode,
    LanguageTriagePolicy Policy,
    double MinimumConfidence,
    double MinimumTargetMargin,
    int MinimumCharacters,
    int MaximumCharacters,
    int BatchSize,
    int MaxDegreeOfParallelism,
    int MaximumBatchUtf8Bytes = LanguageTriageCore.DefaultMaximumBatchUtf8Bytes
);

internal readonly record struct LanguageTriageStats(
    long InputRecords,
    long TargetLanguageRecords,
    long TranslationCandidates,
    long AmbiguousRecords,
    long NonLinguisticRecords,
    long DetectorFailures,
    LanguageDetectionMode EffectiveMode
);

internal delegate bool LanguageDetectionHandler(
    string text,
    LanguageDetectionMode mode,
    string targetLanguage,
    out LanguageDetectionResult result,
    out string? error
);

internal static class LanguageTriageCore
{
    internal const int DefaultMaximumBatchUtf8Bytes = 8 * 1024 * 1024;
    private const string DetectorName = "lingua-rs";
    private const string DetectorVersion = "1.8.0";
    private const int AdaptiveSampleSize = 512;
    private const int FastModeMinimumCharacters = 120;
    private static readonly object OutputCommitLock = new();

    private sealed record PendingRecord(
        string OriginalLine,
        string RecordId,
        string Text,
        string SourceFile,
        JsonElement Location,
        bool IsDerived
    );

    private sealed record AssessedRecord(
        string AssessmentJson,
        bool IsCandidate,
        string Decision
    );

    internal static bool TryParsePolicy(
        string? value,
        out LanguageTriagePolicy policy,
        out string? error
    )
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "high-recall":
            case "recall":
                policy = LanguageTriagePolicy.HighRecall;
                error = null;
                return true;
            case "balanced":
                policy = LanguageTriagePolicy.Balanced;
                error = null;
                return true;
            case "high-precision":
            case "precision":
                policy = LanguageTriagePolicy.HighPrecision;
                error = null;
                return true;
            default:
                policy = LanguageTriagePolicy.HighRecall;
                error =
                    "Invalid translation policy. Use high-recall, balanced, or high-precision.";
                return false;
        }
    }

    internal static async Task<LanguageTriageStats> ProcessAsync(
        string inputPath,
        string candidatesPath,
        string assessmentsPath,
        LanguageTriageOptions options,
        CancellationToken cancellationToken = default,
        LanguageDetectionHandler? detector = null
    )
    {
        ValidateOptions(options);
        if (
            !LanguageDetectionCore.TryNormalizeTargetLanguage(
                options.TargetLanguage,
                out var detectorTargetLanguage,
                out var targetError
            )
        )
        {
            throw new ArgumentException(targetError, nameof(options));
        }
        options = options with { TargetLanguage = options.TargetLanguage.Trim() };
        detector ??= LanguageDetectionCore.TryDetect;
        var inputFullPath = Path.GetFullPath(inputPath);
        var candidatesFullPath = Path.GetFullPath(candidatesPath);
        var assessmentsFullPath = Path.GetFullPath(assessmentsPath);
        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException("Language-triage input was not found.", inputFullPath);
        }
        if (
            string.Equals(inputFullPath, candidatesFullPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(inputFullPath, assessmentsFullPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidatesFullPath, assessmentsFullPath, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new ArgumentException("Language-triage input and output paths must be distinct.");
        }

        CreateParentDirectory(candidatesFullPath);
        CreateParentDirectory(assessmentsFullPath);
        var candidateTemporaryPath = candidatesFullPath + ".partial." + Guid.NewGuid().ToString("N");
        var assessmentTemporaryPath =
            assessmentsFullPath + ".partial." + Guid.NewGuid().ToString("N");

        var effectiveMode =
            options.DetectionMode == LanguageDetectionMode.Adaptive
                ? await ResolveAdaptiveModeAsync(inputFullPath, options, cancellationToken)
                : options.DetectionMode;
        long inputRecords = 0;
        long targetRecords = 0;
        long candidates = 0;
        long ambiguous = 0;
        long nonLinguistic = 0;
        long failures = 0;
        var pending = new List<PendingRecord>(options.BatchSize);
        long pendingUtf8Bytes = 0;

        try
        {
            await using var candidateWriter = CreateWriter(candidateTemporaryPath);
            await using var assessmentWriter = CreateWriter(assessmentTemporaryPath);
            using var reader = new StreamReader(
                new FileStream(
                    inputFullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.SequentialScan
                ),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                1024 * 1024
            );

            async Task FlushAsync()
            {
                if (pending.Count == 0)
                {
                    return;
                }
                var assessed = new AssessedRecord[pending.Count];
                Parallel.For(
                    0,
                    pending.Count,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                        CancellationToken = cancellationToken,
                    },
                    index =>
                        assessed[index] = Assess(
                            pending[index],
                            options,
                            effectiveMode,
                            detectorTargetLanguage,
                            detector
                        )
                );

                for (var index = 0; index < pending.Count; index++)
                {
                    var result = assessed[index];
                    await assessmentWriter.WriteLineAsync(
                        result.AssessmentJson.AsMemory(),
                        cancellationToken
                    );
                    switch (result.Decision)
                    {
                        case "target-language":
                            targetRecords++;
                            break;
                        case "translate":
                            break;
                        case "ambiguous":
                            ambiguous++;
                            break;
                        case "non-linguistic":
                        case "already-derived":
                            nonLinguistic++;
                            break;
                        case "detector-failed":
                            failures++;
                            break;
                    }
                    if (result.IsCandidate)
                    {
                        await candidateWriter.WriteLineAsync(
                            pending[index].OriginalLine.AsMemory(),
                            cancellationToken
                        );
                        candidates++;
                    }
                }
                pending.Clear();
                pendingUtf8Bytes = 0;
            }

            string? line;
            long lineNumber = 0;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (line.Length > EnrichmentRegexPipelineCore.MaxJsonLineCharacters)
                {
                    throw new InvalidDataException(
                        $"Language-triage JSONL line {lineNumber:N0} exceeds the "
                            + $"{EnrichmentRegexPipelineCore.MaxJsonLineCharacters:N0}-character safety limit."
                    );
                }
                var lineUtf8Bytes = Encoding.UTF8.GetByteCount(line);
                if (
                    ShouldFlushBeforeAdding(
                        pending.Count,
                        pendingUtf8Bytes,
                        lineUtf8Bytes,
                        options.BatchSize,
                        options.MaximumBatchUtf8Bytes
                    )
                )
                {
                    await FlushAsync();
                }
                var record = ParseRecord(line, lineNumber);
                inputRecords++;
                pending.Add(record);
                pendingUtf8Bytes += lineUtf8Bytes;
                if (
                    pending.Count >= options.BatchSize
                    || pendingUtf8Bytes >= options.MaximumBatchUtf8Bytes
                )
                {
                    await FlushAsync();
                }
            }
            await FlushAsync();
            await candidateWriter.FlushAsync(cancellationToken);
            await assessmentWriter.FlushAsync(cancellationToken);
            await candidateWriter.DisposeAsync();
            await assessmentWriter.DisposeAsync();
            CommitOutputsAtomically(
                candidateTemporaryPath,
                candidatesFullPath,
                assessmentTemporaryPath,
                assessmentsFullPath
            );
            candidateTemporaryPath = string.Empty;
            assessmentTemporaryPath = string.Empty;
        }
        finally
        {
            DeleteTemporary(candidateTemporaryPath);
            DeleteTemporary(assessmentTemporaryPath);
        }

        return new LanguageTriageStats(
            inputRecords,
            targetRecords,
            candidates,
            ambiguous,
            nonLinguistic,
            failures,
            effectiveMode
        );
    }

    private static AssessedRecord Assess(
        PendingRecord record,
        LanguageTriageOptions options,
        LanguageDetectionMode effectiveMode,
        string detectorTargetLanguage,
        LanguageDetectionHandler detector
    )
    {
        if (record.IsDerived)
        {
            return Assessment(
                record,
                options,
                effectiveMode,
                "already-derived",
                false,
                null,
                null,
                detectorTargetLanguage
            );
        }
        if (
            !IsLinguisticCandidate(
                record.Text,
                options.MinimumCharacters,
                options.MaximumCharacters
            )
        )
        {
            return Assessment(
                record,
                options,
                effectiveMode,
                "non-linguistic",
                false,
                null,
                null,
                detectorTargetLanguage
            );
        }
        if (
            !detector(
                record.Text,
                effectiveMode,
                detectorTargetLanguage,
                out var detection,
                out var detectorError
            )
        )
        {
            var translate = options.Policy == LanguageTriagePolicy.HighRecall;
            return Assessment(
                record,
                options,
                effectiveMode,
                "detector-failed",
                translate,
                null,
                detectorError,
                detectorTargetLanguage
            );
        }

        var isTarget = string.Equals(
            detection.Language,
            detectorTargetLanguage,
            StringComparison.OrdinalIgnoreCase
        );
        if (isTarget)
        {
            var confidentTarget =
                detection.Confidence >= options.MinimumConfidence
                && detection.TopLanguageMargin >= options.MinimumTargetMargin;
            return Assessment(
                record,
                options,
                effectiveMode,
                confidentTarget ? "target-language" : "ambiguous",
                !confidentTarget && options.Policy == LanguageTriagePolicy.HighRecall,
                detection,
                null,
                detectorTargetLanguage
            );
        }

        var clearsGate =
            detection.Confidence >= options.MinimumConfidence
            && detection.TargetMargin >= options.MinimumTargetMargin;
        if (clearsGate || options.Policy == LanguageTriagePolicy.HighRecall)
        {
            return Assessment(
                record,
                options,
                effectiveMode,
                "translate",
                true,
                detection,
                null,
                detectorTargetLanguage
            );
        }
        return Assessment(
            record,
            options,
            effectiveMode,
            "ambiguous",
            false,
            detection,
            null,
            detectorTargetLanguage
        );
    }

    private static AssessedRecord Assessment(
        PendingRecord record,
        LanguageTriageOptions options,
        LanguageDetectionMode mode,
        string decision,
        bool candidate,
        LanguageDetectionResult? detection,
        string? error,
        string? detectorTargetLanguage = null
    )
    {
        var assessment = new
        {
            schemaVersion = 1,
            recordType = "language-assessment",
            sourceRecordId = record.RecordId,
            sourceFile = record.SourceFile,
            location = record.Location,
            detector = DetectorName,
            detectorVersion = DetectorVersion,
            profile = mode.ToString().ToLowerInvariant(),
            targetLanguage = options.TargetLanguage,
            detectorTargetLanguage,
            language = detection?.Language,
            confidence = detection?.Confidence,
            targetConfidence = detection?.TargetConfidence,
            secondConfidence = detection?.SecondConfidence,
            topLanguageMargin = detection?.TopLanguageMargin,
            targetMargin = detection?.TargetMargin,
            minimumConfidence = options.MinimumConfidence,
            minimumTargetMargin = options.MinimumTargetMargin,
            policy = PolicyName(options.Policy),
            decision,
            translationCandidate = candidate,
            error,
        };
        return new AssessedRecord(JsonSerializer.Serialize(assessment), candidate, decision);
    }

    private static PendingRecord ParseRecord(string line, long lineNumber)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || !schemaVersion.TryGetInt32(out var version)
                || version != 1
                || !TryGetRequiredString(root, "recordType", out var recordType)
                || recordType != "string"
            )
            {
                throw new InvalidDataException(
                    $"Language-triage line {lineNumber:N0} is not a schema-1 string record."
                );
            }
            if (
                !TryGetRequiredString(root, "recordId", out var recordId)
                || !TryGetRequiredString(root, "text", out var text, allowWhitespace: true)
                || !TryGetRequiredString(root, "sourceFile", out var sourceFile)
                || !root.TryGetProperty("location", out var location)
                || location.ValueKind != JsonValueKind.Object
                || !TryGetRequiredString(location, "kind", out _)
                || !TryGetRequiredString(location, "value", out _)
                || !root.TryGetProperty("origin", out var origin)
                || origin.ValueKind != JsonValueKind.Object
                || !TryGetRequiredString(origin, "extractor", out _)
                || !TryGetRequiredString(origin, "kind", out _)
            )
            {
                throw new InvalidDataException(
                    $"Language-triage line {lineNumber:N0} is missing required provenance."
                );
            }
            var isDerived = false;
            if (
                root.TryGetProperty("transform", out var transform)
                && transform.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            )
            {
                if (
                    transform.ValueKind != JsonValueKind.Object
                    || !TryGetRequiredString(transform, "kind", out var transformKind)
                    || !string.Equals(
                        transformKind,
                        "translation",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || !TryGetRequiredString(root, "parentRecordId", out _)
                    || !TryGetRequiredString(transform, "engine", out _)
                    || !TryGetRequiredString(transform, "engineVersion", out _)
                    || !TryGetRequiredString(transform, "model", out _)
                    || !TryGetRequiredString(transform, "revision", out _)
                    || !TryGetRequiredString(transform, "modelSha256", out _)
                    || !TryGetRequiredString(transform, "targetLanguage", out _)
                )
                {
                    throw new InvalidDataException(
                        $"Language-triage line {lineNumber:N0} has malformed transform provenance."
                    );
                }
                isDerived = true;
            }
            else if (
                root.TryGetProperty("parentRecordId", out var parentRecordId)
                && parentRecordId.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            )
            {
                throw new InvalidDataException(
                    $"Language-triage line {lineNumber:N0} has parent provenance without a supported transform."
                );
            }
            return new PendingRecord(
                line,
                recordId,
                text,
                sourceFile,
                location.Clone(),
                isDerived
            );
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Invalid language-triage JSONL at line {lineNumber:N0}: {ex.Message}",
                ex
            );
        }
    }

    private static bool TryGetRequiredString(
        JsonElement parent,
        string propertyName,
        out string value,
        bool allowWhitespace = false
    )
    {
        value = string.Empty;
        if (
            !parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return allowWhitespace ? value.Length != 0 : !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryMeasureLinguisticCandidate(
        string text,
        int minimum,
        int maximum,
        out int characterCount
    )
    {
        characterCount = 0;
        if (text.Length < minimum)
        {
            return false;
        }
        var letters = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            characterCount++;
            if (characterCount > maximum)
            {
                return false;
            }
            if (Rune.IsLetter(rune))
            {
                letters++;
            }
        }
        return characterCount >= minimum && letters >= 4;
    }

    private static bool IsLinguisticCandidate(string text, int minimum, int maximum)
    {
        return TryMeasureLinguisticCandidate(text, minimum, maximum, out _);
    }

    internal static bool ShouldFlushBeforeAdding(
        int pendingCount,
        long pendingUtf8Bytes,
        int nextRecordUtf8Bytes,
        int maximumRecords,
        int maximumUtf8Bytes
    )
    {
        if (pendingCount <= 0)
        {
            return false;
        }
        return pendingCount >= maximumRecords
            || pendingUtf8Bytes > maximumUtf8Bytes - (long)nextRecordUtf8Bytes;
    }

    private static LanguageDetectionMode ResolveAdaptiveMode(IReadOnlyCollection<int> lengths)
    {
        if (lengths.Count == 0)
        {
            return LanguageDetectionMode.Accurate;
        }
        var longRecords = lengths.Count(length => length >= FastModeMinimumCharacters);
        return longRecords >= Math.Ceiling(lengths.Count * 0.8)
            ? LanguageDetectionMode.Fast
            : LanguageDetectionMode.Accurate;
    }

    private static async Task<LanguageDetectionMode> ResolveAdaptiveModeAsync(
        string inputPath,
        LanguageTriageOptions options,
        CancellationToken cancellationToken
    )
    {
        var lengths = new List<int>(AdaptiveSampleSize);
        using var reader = new StreamReader(
            new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan
            ),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            1024 * 1024
        );

        long lineNumber = 0;
        while (
            lengths.Count < AdaptiveSampleSize
            && await reader.ReadLineAsync(cancellationToken) is { } line
        )
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            if (line.Length > EnrichmentRegexPipelineCore.MaxJsonLineCharacters)
            {
                throw new InvalidDataException(
                    $"Language-triage JSONL line {lineNumber:N0} exceeds the "
                        + $"{EnrichmentRegexPipelineCore.MaxJsonLineCharacters:N0}-character safety limit."
                );
            }
            var record = ParseRecord(line, lineNumber);
            if (
                !record.IsDerived
                && TryMeasureLinguisticCandidate(
                    record.Text,
                    options.MinimumCharacters,
                    options.MaximumCharacters,
                    out var characterCount
                )
            )
            {
                lengths.Add(characterCount);
            }
        }
        return ResolveAdaptiveMode(lengths);
    }

    private static string PolicyName(LanguageTriagePolicy policy) =>
        policy switch
        {
            LanguageTriagePolicy.HighRecall => "high-recall",
            LanguageTriagePolicy.HighPrecision => "high-precision",
            _ => "balanced",
        };

    private static void ValidateOptions(LanguageTriageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TargetLanguage))
        {
            throw new ArgumentException("A target language is required.");
        }
        if (
            options.MinimumConfidence is < 0 or > 1
            || options.MinimumTargetMargin is < 0 or > 1
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Language confidence and margin must be between 0 and 1."
            );
        }
        if (
            options.MinimumCharacters < 1
            || options.MaximumCharacters < options.MinimumCharacters
            || options.BatchSize < 1
            || options.MaxDegreeOfParallelism < 1
            || options.MaximumBatchUtf8Bytes < 1
        )
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid language-triage bounds.");
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
            new UTF8Encoding(false),
            1024 * 1024
        );

    private static void CommitOutputsAtomically(
        string candidateTemporaryPath,
        string candidatesPath,
        string assessmentTemporaryPath,
        string assessmentsPath
    )
    {
        lock (OutputCommitLock)
        {
            var generation = Guid.NewGuid().ToString("N");
            var candidateBackupPath = candidatesPath + ".backup." + generation;
            var assessmentBackupPath = assessmentsPath + ".backup." + generation;
            var candidateBackedUp = false;
            var assessmentBackedUp = false;
            var candidateInstalled = false;
            var assessmentInstalled = false;
            var preserveCandidateBackup = false;
            var preserveAssessmentBackup = false;

            try
            {
                if (File.Exists(candidatesPath))
                {
                    File.Move(candidatesPath, candidateBackupPath);
                    candidateBackedUp = true;
                }
                if (File.Exists(assessmentsPath))
                {
                    File.Move(assessmentsPath, assessmentBackupPath);
                    assessmentBackedUp = true;
                }

                File.Move(candidateTemporaryPath, candidatesPath);
                candidateInstalled = true;
                File.Move(assessmentTemporaryPath, assessmentsPath);
                assessmentInstalled = true;
            }
            catch (Exception commitError)
            {
                var rollbackErrors = new List<Exception>();
                if (assessmentInstalled)
                {
                    TryRollback(() => File.Delete(assessmentsPath), rollbackErrors);
                }
                if (candidateInstalled)
                {
                    TryRollback(() => File.Delete(candidatesPath), rollbackErrors);
                }
                if (assessmentBackedUp)
                {
                    var before = rollbackErrors.Count;
                    TryRollback(
                        () => File.Move(assessmentBackupPath, assessmentsPath),
                        rollbackErrors
                    );
                    preserveAssessmentBackup = rollbackErrors.Count != before;
                }
                if (candidateBackedUp)
                {
                    var before = rollbackErrors.Count;
                    TryRollback(
                        () => File.Move(candidateBackupPath, candidatesPath),
                        rollbackErrors
                    );
                    preserveCandidateBackup = rollbackErrors.Count != before;
                }

                if (rollbackErrors.Count != 0)
                {
                    rollbackErrors.Insert(0, commitError);
                    throw new IOException(
                        "Language-triage output commit failed and rollback was incomplete. Backup files were preserved for recovery.",
                        new AggregateException(rollbackErrors)
                    );
                }
                throw;
            }
            finally
            {
                if (!preserveCandidateBackup)
                {
                    DeleteTemporary(candidateBackupPath);
                }
                if (!preserveAssessmentBackup)
                {
                    DeleteTemporary(assessmentBackupPath);
                }
            }
        }
    }

    private static void TryRollback(Action action, ICollection<Exception> errors)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(ex);
        }
    }

    private static void CreateParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void DeleteTemporary(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
