#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal readonly record struct TranslationCompletionStats(
    long CandidateRecords,
    long TranslationRecords
);

internal static class TranslationCompletionCore
{
    internal static async Task<TranslationCompletionStats> ValidateAsync(
        string candidatesPath,
        string translationsPath,
        string workingDirectory,
        long expectedCandidateCount,
        TranslationValidationRequirements requirements,
        CancellationToken cancellationToken = default
    )
    {
        if (expectedCandidateCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedCandidateCount));
        }

        using var validator = new DiskBackedProvenanceValidator(
            workingDirectory,
            requireEveryOriginalReferenced: true
        );
        long candidateRecords = 0;
        long translationRecords = 0;
        await using var candidateEnumerator = EnrichmentJsonlReader
            .ReadAsync(candidatesPath, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await using var translationEnumerator = EnrichmentJsonlReader
            .ReadAsync(translationsPath, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            var hasCandidate = await candidateEnumerator.MoveNextAsync();
            var hasTranslation = await translationEnumerator.MoveNextAsync();
            if (!hasCandidate || !hasTranslation)
            {
                if (hasCandidate)
                {
                    throw new InvalidDataException(
                        $"Translation candidate '{candidateEnumerator.Current.Record.RecordId}' does not have exactly one translated child in candidate order."
                    );
                }
                if (hasTranslation)
                {
                    throw new InvalidDataException(
                        $"Translation output line {translationEnumerator.Current.LineNumber:N0} has no ordered candidate parent."
                    );
                }
                break;
            }

            var candidate = candidateEnumerator.Current;
            var translation = translationEnumerator.Current;
            if (
                candidate.Record.Transform is not null
                || !string.IsNullOrWhiteSpace(candidate.Record.ParentRecordId)
            )
            {
                throw new InvalidDataException(
                    $"Translation candidate at line {candidate.LineNumber:N0} is not an original record."
                );
            }
            if (!EnrichmentRegexPipelineCore.IsTranslation(translation.Record))
            {
                throw new InvalidDataException(
                    $"Translation output at line {translation.LineNumber:N0} is not a translated child record."
                );
            }

            var integrity = EnrichmentRegexPipelineCore.ValidateTranslationRequirements(
                translation.Record,
                requirements,
                translation.LineNumber
            );
            if (
                !string.Equals(
                    candidate.Record.RecordId,
                    translation.Record.ParentRecordId,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException(
                    $"Translation output at line {translation.LineNumber:N0} is not the ordered child of candidate line {candidate.LineNumber:N0}."
                );
            }

            var candidateLineage = EnrichmentRegexPipelineCore.CreateLineageIdentity(
                candidate.Record
            );
            var translationLineage = EnrichmentRegexPipelineCore.CreateLineageIdentity(
                translation.Record
            );
            if (!candidateLineage.Equals(translationLineage))
            {
                throw new InvalidDataException(
                    $"Translation output at line {translation.LineNumber:N0} does not retain the exact sourceFile, location, and origin of its ordered candidate."
                );
            }
            if (
                integrity == TranslationIntegrityStatus.PreservationFallback
                && !string.Equals(
                    candidate.Record.Text,
                    translation.Record.Text,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException(
                    $"Preservation-fallback child for candidate line {candidate.LineNumber:N0} does not contain the exact parent text."
                );
            }

            validator.AddOriginal(
                candidate.Record.RecordId,
                candidateLineage
            );
            validator.AddTranslation(
                translation.Record.RecordId,
                translation.Record.ParentRecordId!,
                translationLineage
            );
            candidateRecords++;
            translationRecords++;
        }

        if (candidateRecords != expectedCandidateCount)
        {
            throw new InvalidDataException(
                $"Translation candidate count changed: expected {expectedCandidateCount:N0}, "
                    + $"found {candidateRecords:N0}."
            );
        }

        validator.Validate(cancellationToken);
        if (translationRecords != candidateRecords)
        {
            throw new InvalidDataException(
                $"Translation output cardinality mismatch: expected {candidateRecords:N0} child "
                    + $"records, found {translationRecords:N0}."
            );
        }

        return new TranslationCompletionStats(candidateRecords, translationRecords);
    }
}
