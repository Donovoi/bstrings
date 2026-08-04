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
        await foreach (
            var item in EnrichmentJsonlReader.ReadAsync(candidatesPath, cancellationToken)
        )
        {
            if (
                item.Record.Transform is not null
                || !string.IsNullOrWhiteSpace(item.Record.ParentRecordId)
            )
            {
                throw new InvalidDataException(
                    $"Translation candidate at line {item.LineNumber:N0} is not an original record."
                );
            }
            validator.AddOriginal(
                item.Record.RecordId,
                EnrichmentRegexPipelineCore.CreateLineageIdentity(item.Record)
            );
            candidateRecords++;
        }

        if (candidateRecords != expectedCandidateCount)
        {
            throw new InvalidDataException(
                $"Translation candidate count changed: expected {expectedCandidateCount:N0}, "
                    + $"found {candidateRecords:N0}."
            );
        }

        long translationRecords = 0;
        await foreach (
            var item in EnrichmentJsonlReader.ReadAsync(translationsPath, cancellationToken)
        )
        {
            if (!EnrichmentRegexPipelineCore.IsTranslation(item.Record))
            {
                throw new InvalidDataException(
                    $"Translation output at line {item.LineNumber:N0} is not a translated child record."
                );
            }
            EnrichmentRegexPipelineCore.ValidateTranslationRequirements(
                item.Record,
                requirements,
                item.LineNumber
            );
            validator.AddTranslation(
                item.Record.RecordId,
                item.Record.ParentRecordId!,
                EnrichmentRegexPipelineCore.CreateLineageIdentity(item.Record)
            );
            translationRecords++;
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
