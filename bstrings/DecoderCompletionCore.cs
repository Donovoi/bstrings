#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal sealed record DecoderCompletionStats(
    long RawRecords,
    long AssessmentRecords,
    long DecodedRecords,
    long CandidateOccurrences,
    long AttemptedCandidates,
    long DecodedBytesAttempted,
    string DecodedStringsSha256,
    string AssessmentsSha256,
    string WorkStatsSha256
);

internal static class DecoderCompletionCore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<DecoderCompletionStats> ValidateAsync(
        string rawInputPath,
        string decodedOutputPath,
        string assessmentsOutputPath,
        string workStatsOutputPath,
        DecoderPipelineOptions options,
        CancellationToken cancellationToken = default
    )
    {
        DecoderPipelineCore.ValidateOptions(options);
        var rawFullPath = RequireFile(rawInputPath, "decoder raw input");
        var decodedFullPath = RequireFile(decodedOutputPath, "decoded string output");
        var assessmentsFullPath = RequireFile(
            assessmentsOutputPath,
            "decoder assessment output"
        );
        var statsFullPath = RequireFile(workStatsOutputPath, "decoder work statistics");

        var stats = await ReadStatsAsync(statsFullPath, cancellationToken);
        ValidateStatsSettings(stats, options, decodedFullPath, assessmentsFullPath);

        long rawRecords = 0;
        long assessmentRecords = 0;
        long decodedRecords = 0;
        long decodedBytesFromAssessments = 0;
        long publishedText = 0;
        long decodedBinaryKnown = 0;
        long decodedBinaryOpaque = 0;
        long textRejected = 0;
        long canonicalRejected = 0;
        long resourceLimited = 0;
        long candidateOccurrences = 0;
        long skippedAfterCandidateLimit = 0;
        long skippedAfterTotalByteLimit = 0;
        var totalLimitReached = false;
        var workingDirectory = Path.GetDirectoryName(statsFullPath)
            ?? throw new InvalidDataException("Decoder statistics path has no parent directory.");
        using var provenance = new DiskBackedProvenanceValidator(workingDirectory);
        await using var assessmentEnumerator = ReadAssessmentsAsync(
                assessmentsFullPath,
                cancellationToken
            )
            .GetAsyncEnumerator(cancellationToken);
        await using var childEnumerator = EnrichmentJsonlReader
            .ReadAsync(decodedFullPath, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        var hasAssessment = await assessmentEnumerator.MoveNextAsync();
        await foreach (var raw in EnrichmentJsonlReader.ReadAsync(rawFullPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                raw.Record.Transform is not null
                || !string.IsNullOrWhiteSpace(raw.Record.ParentRecordId)
            )
            {
                throw new InvalidDataException(
                    $"Decoder raw input at line {raw.LineNumber:N0} is already transformed."
                );
            }
            var rawLineage = EnrichmentRegexPipelineCore.CreateLineageIdentity(raw.Record);
            provenance.AddOriginal(raw.Record.RecordId, rawLineage);
            rawRecords++;

            if (!DecoderPipelineCore.IsCandidate(raw.Record.Text!, options.Mode))
            {
                continue;
            }
            candidateOccurrences++;
            if (assessmentRecords >= options.MaxCandidates)
            {
                skippedAfterCandidateLimit++;
                continue;
            }
            if (totalLimitReached)
            {
                skippedAfterTotalByteLimit++;
                continue;
            }
            if (
                !hasAssessment
                || !string.Equals(
                    assessmentEnumerator.Current.Record.SourceRecordId,
                    raw.Record.RecordId,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException(
                    $"Decoder candidate at raw line {raw.LineNumber:N0} has no ordered assessment."
                );
            }

            var assessmentLine = assessmentEnumerator.Current;
            var assessment = assessmentLine.Record;
            ValidateAssessment(assessment, assessmentLine.LineNumber, options);
            if (
                assessment.SourceFile != raw.Record.SourceFile
                || assessment.Location != raw.Record.Location
            )
            {
                throw new InvalidDataException(
                    $"Decoder assessment line {assessmentLine.LineNumber:N0} does not retain its raw parent's sourceFile and location."
                );
            }
            var decodedBytes = DecoderPipelineCore.ValidateAssessmentAgainstParent(
                raw.Record,
                assessment,
                options,
                decodedBytesFromAssessments,
                assessmentLine.LineNumber
            );
            decodedBytesFromAssessments = checked(decodedBytesFromAssessments + decodedBytes);
            assessmentRecords++;
            totalLimitReached = assessment.Reason == "total-decoded-byte-limit";
            switch (assessment.Outcome)
            {
                case "published-text":
                    publishedText++;
                    if (!await childEnumerator.MoveNextAsync())
                    {
                        throw new InvalidDataException(
                            $"Published decoder assessment at line {assessmentLine.LineNumber:N0} has no ordered child."
                        );
                    }
                    ValidateChild(
                        childEnumerator.Current.Record,
                        childEnumerator.Current.LineNumber,
                        raw.Record,
                        assessment,
                        options
                    );
                    provenance.AddDecoding(
                        childEnumerator.Current.Record.RecordId,
                        childEnumerator.Current.Record.ParentRecordId!,
                        EnrichmentRegexPipelineCore.CreateLineageIdentity(
                            childEnumerator.Current.Record
                        )
                    );
                    decodedRecords++;
                    break;
                case "decoded-binary-known": decodedBinaryKnown++; break;
                case "decoded-binary-opaque": decodedBinaryOpaque++; break;
                case "text-rejected": textRejected++; break;
                case "canonical-rejected": canonicalRejected++; break;
                case "resource-limited": resourceLimited++; break;
            }
            hasAssessment = await assessmentEnumerator.MoveNextAsync();
        }

        if (hasAssessment)
        {
            throw new InvalidDataException(
                $"Decoder assessment line {assessmentEnumerator.Current.LineNumber:N0} is duplicate, out of order, or references a missing raw parent."
            );
        }
        if (await childEnumerator.MoveNextAsync())
        {
            throw new InvalidDataException(
                $"Decoded output line {childEnumerator.Current.LineNumber:N0} has no published assessment."
            );
        }
        provenance.Validate(cancellationToken);

        var decodedSha256 = await HashFileAsync(decodedFullPath, cancellationToken);
        var assessmentsSha256 = await HashFileAsync(assessmentsFullPath, cancellationToken);
        var workStatsSha256 = await HashFileAsync(statsFullPath, cancellationToken);
        ValidateStatsCardinality(
            stats,
            rawRecords,
            assessmentRecords,
            decodedRecords,
            decodedBytesFromAssessments,
            candidateOccurrences,
            skippedAfterCandidateLimit,
            skippedAfterTotalByteLimit,
            publishedText,
            decodedBinaryKnown,
            decodedBinaryOpaque,
            textRejected,
            canonicalRejected,
            resourceLimited,
            decodedSha256,
            assessmentsSha256
        );

        return new DecoderCompletionStats(
            rawRecords,
            assessmentRecords,
            decodedRecords,
            stats.CandidateOccurrences,
            stats.AttemptedCandidates,
            stats.DecodedBytesAttempted,
            decodedSha256,
            assessmentsSha256,
            workStatsSha256
        );
    }

    private static void ValidateStatsSettings(
        DecoderPipelineStats stats,
        DecoderPipelineOptions options,
        string decodedFullPath,
        string assessmentsFullPath
    )
    {
        if (
            stats.SchemaVersion != 1
            || stats.PolicyVersion != DecoderPipelineCore.PolicyVersion
            || stats.Mode != ModeName(options.Mode)
            || stats.MaxCharacters != options.MaxCharacters
            || stats.MaxBytesPerRecord != options.MaxBytesPerRecord
            || stats.MaxCandidates != options.MaxCandidates
            || stats.MaxTotalBytes != options.MaxTotalBytes
            || stats.DecodedStrings is null
            || stats.Assessments is null
            || stats.DecodedStrings.File != Path.GetFileName(decodedFullPath)
            || stats.Assessments.File != Path.GetFileName(assessmentsFullPath)
        )
        {
            throw new InvalidDataException("Decoder work statistics do not match the requested run settings.");
        }
    }

    private static void ValidateStatsCardinality(
        DecoderPipelineStats stats,
        long rawRecords,
        long assessmentRecords,
        long decodedRecords,
        long decodedBytesFromAssessments,
        long candidateOccurrences,
        long skippedAfterCandidateLimit,
        long skippedAfterTotalByteLimit,
        long publishedText,
        long decodedBinaryKnown,
        long decodedBinaryOpaque,
        long textRejected,
        long canonicalRejected,
        long resourceLimited,
        string decodedSha256,
        string assessmentsSha256
    )
    {
        if (
            stats.InputRecords < 0
            || stats.CandidateOccurrences < 0
            || stats.AttemptedCandidates < 0
            || stats.AssessmentRecords < 0
            || stats.PublishedTextChildren < 0
            || stats.DecodedBinaryKnown < 0
            || stats.DecodedBinaryOpaque < 0
            || stats.TextRejected < 0
            || stats.CanonicalRejected < 0
            || stats.ResourceLimited < 0
            || stats.SkippedAfterCandidateLimit < 0
            || stats.SkippedAfterTotalByteLimit < 0
            || stats.DecodedBytesAttempted < 0
            || stats.DecodedStrings is null
            || stats.DecodedStrings.Records < 0
            || stats.Assessments is null
            || stats.Assessments.Records < 0
        )
        {
            throw new InvalidDataException("Decoder work statistics contain negative or missing counters.");
        }
        long categorized;
        long expectedOccurrences;
        try
        {
            categorized = checked(
                stats.PublishedTextChildren
                    + stats.DecodedBinaryKnown
                    + stats.DecodedBinaryOpaque
                    + stats.TextRejected
                    + stats.CanonicalRejected
                    + stats.ResourceLimited
            );
            expectedOccurrences = checked(
                stats.AttemptedCandidates
                    + stats.SkippedAfterCandidateLimit
                    + stats.SkippedAfterTotalByteLimit
            );
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("Decoder work statistics overflow their cardinality equations.", ex);
        }
        if (
            stats.InputRecords != rawRecords
            || stats.AssessmentRecords != assessmentRecords
            || stats.AttemptedCandidates != assessmentRecords
            || stats.CandidateOccurrences != candidateOccurrences
            || stats.SkippedAfterCandidateLimit != skippedAfterCandidateLimit
            || stats.SkippedAfterTotalByteLimit != skippedAfterTotalByteLimit
            || categorized != stats.AttemptedCandidates
            || stats.DecodedBytesAttempted != decodedBytesFromAssessments
            || stats.PublishedTextChildren != publishedText
            || stats.DecodedBinaryKnown != decodedBinaryKnown
            || stats.DecodedBinaryOpaque != decodedBinaryOpaque
            || stats.TextRejected != textRejected
            || stats.CanonicalRejected != canonicalRejected
            || stats.ResourceLimited != resourceLimited
            || stats.PublishedTextChildren != decodedRecords
            || stats.DecodedStrings.Records != decodedRecords
            || stats.Assessments.Records != assessmentRecords
            || stats.CandidateOccurrences != expectedOccurrences
            || stats.AttemptedCandidates > stats.MaxCandidates
            || stats.DecodedBytesAttempted > stats.MaxTotalBytes
            || !string.Equals(stats.DecodedStrings.Sha256, decodedSha256, StringComparison.Ordinal)
            || !string.Equals(stats.Assessments.Sha256, assessmentsSha256, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException("Decoder work statistics fail cardinality or artifact-integrity validation.");
        }
    }

    private static void ValidateAssessment(
        DecoderAssessmentRecord assessment,
        long lineNumber,
        DecoderPipelineOptions options
    )
    {
        if (
            assessment.SchemaVersion != 1
            || assessment.RecordType != "decoder-assessment"
            || string.IsNullOrWhiteSpace(assessment.SourceRecordId)
            || string.IsNullOrWhiteSpace(assessment.SourceFile)
            || assessment.Location is null
            || string.IsNullOrWhiteSpace(assessment.Location.Kind)
            || string.IsNullOrWhiteSpace(assessment.Location.Value)
            || assessment.PolicyVersion != DecoderPipelineCore.PolicyVersion
            || assessment.Mode != ModeName(options.Mode)
            || assessment.Profile is not (
                DecoderPipelineCore.PowerShellProfile or DecoderPipelineCore.Base64Profile
            )
            || assessment.Outcome is not (
                "published-text"
                    or "decoded-binary-known"
                    or "decoded-binary-opaque"
                    or "text-rejected"
                    or "canonical-rejected"
                    or "resource-limited"
            )
            || string.IsNullOrWhiteSpace(assessment.Reason)
            || assessment.CandidateStart < 0
            || assessment.CandidateLength < 8
            || assessment.DecodeDepth != 1
            || assessment.MaxCandidateCharacters != options.MaxCharacters
            || assessment.MaxDecodedBytesPerRecord != options.MaxBytesPerRecord
            || assessment.MaxAttemptedCandidates != options.MaxCandidates
            || assessment.MaxTotalDecodedBytes != options.MaxTotalBytes
        )
        {
            throw new InvalidDataException(
                $"Decoder assessment line {lineNumber:N0} has invalid schema, provenance, policy, outcome, or limits."
            );
        }
        var decodedOutcome = assessment.Outcome
            is "published-text" or "decoded-binary-known" or "decoded-binary-opaque" or "text-rejected";
        if (
            decodedOutcome
            != (
                assessment.DecodedByteLength is > 0
                && IsLowerSha256(assessment.DecodedSha256)
            )
            || (assessment.Outcome == "published-text") != !string.IsNullOrWhiteSpace(assessment.DecodedCharset)
            || (assessment.Outcome == "decoded-binary-known") != !string.IsNullOrWhiteSpace(assessment.BinaryClass)
        )
        {
            throw new InvalidDataException(
                $"Decoder assessment line {lineNumber:N0} has inconsistent decoded fields."
            );
        }
    }

    private static void ValidateChild(
        EnrichmentStringRecord child,
        long lineNumber,
        EnrichmentStringRecord parent,
        DecoderAssessmentRecord assessment,
        DecoderPipelineOptions options
    )
    {
        EnrichmentRegexPipelineCore.ValidateRecord(child, lineNumber);
        if (
            !EnrichmentRegexPipelineCore.IsDecoding(child)
            || child.ParentRecordId != parent.RecordId
            || child.SourceFile != parent.SourceFile
            || child.Location != parent.Location
            || child.Origin != parent.Origin
            || child.Transform!.Profile != assessment.Profile
            || child.Attributes is null
            || GetInt32(child.Attributes, "candidateStart") != assessment.CandidateStart
            || GetInt32(child.Attributes, "candidateLength") != assessment.CandidateLength
            || GetString(child.Attributes, "outerWhitespaceTreatment") != assessment.OuterWhitespaceTreatment
            || GetInt32(child.Attributes, "leadingWhitespaceCharacters") != assessment.LeadingWhitespaceCharacters
            || GetInt32(child.Attributes, "trailingWhitespaceCharacters") != assessment.TrailingWhitespaceCharacters
            || GetInt32(child.Attributes, "decodedByteLength") != assessment.DecodedByteLength
            || GetString(child.Attributes, "decodedSha256") != assessment.DecodedSha256
            || GetString(child.Attributes, "decodedCharset") != assessment.DecodedCharset
            || GetInt32(child.Attributes, "maxCandidateCharacters") != options.MaxCharacters
            || GetInt32(child.Attributes, "maxDecodedBytesPerRecord") != options.MaxBytesPerRecord
            || GetInt64(child.Attributes, "maxAttemptedCandidates") != options.MaxCandidates
            || GetInt64(child.Attributes, "maxTotalDecodedBytes") != options.MaxTotalBytes
        )
        {
            throw new InvalidDataException(
                $"Decoded output line {lineNumber:N0} does not retain its exact ordered parent, assessment, or run settings."
            );
        }
        var expectedId = DecoderPipelineCore.CreateChildRecordId(
            parent.RecordId,
            assessment.CandidateStart,
            assessment.CandidateLength,
            assessment.Profile,
            assessment.DecodedCharset!,
            assessment.OuterWhitespaceTreatment,
            assessment.LeadingWhitespaceCharacters,
            assessment.TrailingWhitespaceCharacters,
            child.Text!
        );
        if (child.RecordId != expectedId)
        {
            throw new InvalidDataException(
                $"Decoded output line {lineNumber:N0} has a stale or arbitrary recordId."
            );
        }
        if (
            child.Transform!.Kind != "decoding"
            || child.Transform.Engine != "bstrings"
            || child.Transform.EngineVersion != DecoderPipelineCore.EngineVersion
            || child.Transform.Profile != assessment.Profile
            || child.Transform.PolicyVersion != DecoderPipelineCore.PolicyVersion
            || child.Transform.Outcome != "decoded-text"
            || child.Transform.Model is not null
            || child.Transform.Revision is not null
            || child.Transform.ModelSha256 is not null
            || child.Transform.SourceLanguage is not null
            || child.Transform.TargetLanguage is not null
            || GetString(child.Attributes!, "decoder") != "base64"
            || GetString(child.Attributes, "decoderProfile") != assessment.Profile
            || GetString(child.Attributes, "decoderPolicyVersion") != DecoderPipelineCore.PolicyVersion
            || GetInt32(child.Attributes, "decodeDepth") != 1
        )
        {
            throw new InvalidDataException(
                $"Decoded output line {lineNumber:N0} has tampered transform or copied decoder provenance."
            );
        }
    }

    private static async IAsyncEnumerable<(long LineNumber, DecoderAssessmentRecord Record)> ReadAssessmentsAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        using var reader = new StreamReader(stream, StrictUtf8, true, 64 * 1024);
        long lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > EnrichmentRegexPipelineCore.MaxJsonLineCharacters)
            {
                throw new InvalidDataException(
                    $"Decoder assessment line {lineNumber:N0} exceeds the JSONL safety limit."
                );
            }
            DecoderAssessmentRecord record;
            try
            {
                record = JsonSerializer.Deserialize<DecoderAssessmentRecord>(line, JsonOptions)
                    ?? throw new JsonException("The record was null.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Invalid decoder assessment JSONL at line {lineNumber:N0}: {ex.Message}",
                    ex
                );
            }
            yield return (lineNumber, record);
        }
    }

    private static async Task<DecoderPipelineStats> ReadStatsAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            return await JsonSerializer.DeserializeAsync<DecoderPipelineStats>(
                    stream,
                    JsonOptions,
                    cancellationToken
                )
                ?? throw new JsonException("Decoder work statistics were null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid decoder work statistics: {ex.Message}", ex);
        }
    }

    private static int GetInt32(IReadOnlyDictionary<string, JsonElement> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : int.MinValue;

    private static long GetInt64(IReadOnlyDictionary<string, JsonElement> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : long.MinValue;

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string RequireFile(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The {description} was not found.", fullPath);
        }
        return fullPath;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        return Convert
            .ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static string ModeName(DecoderWorkflowMode mode) =>
        mode == DecoderWorkflowMode.Auto ? "auto" : "force";
}
