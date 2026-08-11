#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal sealed record TranslationWorkStats(
    string File,
    string Sha256,
    long CandidateOccurrences,
    long TextDecisions,
    long ProtectedOnlyBypassTexts,
    long RunCacheHits,
    long TranslationCacheHits,
    long TranslatorRequests,
    long TranslatorInputTexts,
    long ModelResults,
    long ModelFallbacks,
    long TranslatedChildOccurrences,
    long PreservationFallbackChildOccurrences
);

internal static class TranslationWorkStatsCore
{
    internal const int SchemaVersion = 1;
    private const int MaximumBytes = 64 * 1024;
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "candidateOccurrences",
        "textDecisions",
        "protectedOnlyBypassTexts",
        "runCacheHits",
        "translationCacheHits",
        "translatorRequests",
        "translatorInputTexts",
        "modelResults",
        "modelFallbacks",
        "translatedChildOccurrences",
        "preservationFallbackChildOccurrences",
    };

    internal static void ValidateAtomicDestination(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                $"The translation work-statistics directory does not exist: '{parent}'."
            );
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(
            parent,
            "translation work-statistics directory"
        );
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return;
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(
            fullPath,
            "translation work-statistics destination"
        );
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The translation work-statistics destination is not a physical file."
            );
        }
    }

    internal static async Task<TranslationWorkStats> ValidateAsync(
        string path,
        long expectedCandidateOccurrences,
        CancellationToken cancellationToken = default
    )
    {
        if (expectedCandidateOccurrences < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedCandidateOccurrences));
        }
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The translation work-statistics file is missing.",
                fullPath
            );
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(
            fullPath,
            "translation work-statistics file"
        );
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The translation work-statistics path is not a physical file."
            );
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        if (stream.Length is <= 0 or > MaximumBytes)
        {
            throw new InvalidDataException(
                $"The translation work-statistics file must contain 1 to {MaximumBytes:N0} bytes."
            );
        }
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                }
            );
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"The translation work-statistics file is not valid JSON: {ex.Message}",
                ex
            );
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The translation work-statistics root must be an object."
                );
            }
            ValidatePropertySet(root);
            if (
                !root.TryGetProperty("schemaVersion", out var schema)
                || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != SchemaVersion
            )
            {
                throw new InvalidDataException(
                    $"The translation work-statistics schemaVersion must be {SchemaVersion}."
                );
            }

            var candidateOccurrences = RequiredCounter(root, "candidateOccurrences");
            var textDecisions = RequiredCounter(root, "textDecisions");
            var protectedOnlyBypassTexts = RequiredCounter(
                root,
                "protectedOnlyBypassTexts"
            );
            var runCacheHits = RequiredCounter(root, "runCacheHits");
            var translationCacheHits = RequiredCounter(root, "translationCacheHits");
            var translatorRequests = RequiredCounter(root, "translatorRequests");
            var translatorInputTexts = RequiredCounter(root, "translatorInputTexts");
            var modelResults = RequiredCounter(root, "modelResults");
            var modelFallbacks = RequiredCounter(root, "modelFallbacks");
            var translatedChildOccurrences = RequiredCounter(
                root,
                "translatedChildOccurrences"
            );
            var preservationFallbackChildOccurrences = RequiredCounter(
                root,
                "preservationFallbackChildOccurrences"
            );

            if (candidateOccurrences != expectedCandidateOccurrences)
            {
                throw new InvalidDataException(
                    "Translation work-statistics candidate cardinality does not match language triage."
                );
            }
            if (translatedChildOccurrences != candidateOccurrences)
            {
                throw new InvalidDataException(
                    "Translation work-statistics child cardinality does not match its candidates."
                );
            }
            long decisionBuckets;
            try
            {
                decisionBuckets = checked(
                    runCacheHits
                        + protectedOnlyBypassTexts
                        + translationCacheHits
                        + translatorInputTexts
                );
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException(
                    "Translation work-statistics decision counters overflowed.",
                    ex
                );
            }
            if (textDecisions != decisionBuckets)
            {
                throw new InvalidDataException(
                    "Translation work-statistics text decisions do not reconcile."
                );
            }
            if (modelResults != translatorInputTexts || modelFallbacks > modelResults)
            {
                throw new InvalidDataException(
                    "Translation work-statistics model result counters do not reconcile."
                );
            }
            if (
                translatorRequests > translatorInputTexts
                || textDecisions > candidateOccurrences
                || preservationFallbackChildOccurrences > translatedChildOccurrences
            )
            {
                throw new InvalidDataException(
                    "Translation work-statistics counters exceed their parent cardinality."
                );
            }

            return new TranslationWorkStats(
                Path.GetFileName(fullPath),
                sha256,
                candidateOccurrences,
                textDecisions,
                protectedOnlyBypassTexts,
                runCacheHits,
                translationCacheHits,
                translatorRequests,
                translatorInputTexts,
                modelResults,
                modelFallbacks,
                translatedChildOccurrences,
                preservationFallbackChildOccurrences
            );
        }
    }

    private static void ValidatePropertySet(JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Translation work-statistics contain duplicate property '{property.Name}'."
                );
            }
            if (!AllowedProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"Translation work-statistics contain unknown property '{property.Name}'."
                );
            }
        }
        if (seen.Count != AllowedProperties.Count)
        {
            throw new InvalidDataException(
                "Translation work-statistics are missing one or more required properties."
            );
        }
    }

    private static long RequiredCounter(JsonElement root, string name)
    {
        if (
            !root.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var value)
            || value < 0
        )
        {
            throw new InvalidDataException(
                $"Translation work-statistics property '{name}' must be a nonnegative integer."
            );
        }
        return value;
    }
}
