#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal sealed record DecoderPipelineOptions(
    DecoderWorkflowMode Mode,
    int MaxCharacters,
    int MaxBytesPerRecord,
    long MaxCandidates,
    long MaxTotalBytes
);

internal sealed record DecoderArtifactInfo(string File, string Sha256, long Records);

internal sealed record DecoderPipelineStats(
    int SchemaVersion,
    string PolicyVersion,
    string Mode,
    int MaxCharacters,
    int MaxBytesPerRecord,
    long MaxCandidates,
    long MaxTotalBytes,
    long InputRecords,
    long CandidateOccurrences,
    long AttemptedCandidates,
    long AssessmentRecords,
    long PublishedTextChildren,
    long DecodedBinaryKnown,
    long DecodedBinaryOpaque,
    long TextRejected,
    long CanonicalRejected,
    long ResourceLimited,
    long SkippedAfterCandidateLimit,
    long SkippedAfterTotalByteLimit,
    long DecodedBytesAttempted,
    DecoderArtifactInfo DecodedStrings,
    DecoderArtifactInfo Assessments
);

internal sealed record DecoderAssessmentRecord
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [JsonRequired]
    public string RecordType { get; init; } = "decoder-assessment";
    [JsonRequired]
    public string SourceRecordId { get; init; } = string.Empty;
    [JsonRequired]
    public string SourceFile { get; init; } = string.Empty;
    [JsonRequired]
    public EnrichmentLocation? Location { get; init; }
    [JsonRequired]
    public string PolicyVersion { get; init; } = DecoderPipelineCore.PolicyVersion;
    [JsonRequired]
    public string Mode { get; init; } = string.Empty;
    [JsonRequired]
    public string Profile { get; init; } = string.Empty;
    [JsonRequired]
    public string Outcome { get; init; } = string.Empty;
    [JsonRequired]
    public string Reason { get; init; } = string.Empty;
    [JsonRequired]
    public int CandidateStart { get; init; }
    [JsonRequired]
    public int CandidateLength { get; init; }
    [JsonRequired]
    public string OuterWhitespaceTreatment { get; init; } = "none";
    [JsonRequired]
    public int LeadingWhitespaceCharacters { get; init; }
    [JsonRequired]
    public int TrailingWhitespaceCharacters { get; init; }
    public int? DecodedByteLength { get; init; }
    public string? DecodedSha256 { get; init; }
    public string? DecodedCharset { get; init; }
    public string? BinaryClass { get; init; }
    [JsonRequired]
    public int DecodeDepth { get; init; } = 1;
    [JsonRequired]
    public int MaxCandidateCharacters { get; init; }
    [JsonRequired]
    public int MaxDecodedBytesPerRecord { get; init; }
    [JsonRequired]
    public long MaxAttemptedCandidates { get; init; }
    [JsonRequired]
    public long MaxTotalDecodedBytes { get; init; }
}

internal static class DecoderPipelineCore
{
    internal const string PolicyVersion = "decoder-policy-v1";
    internal const string PowerShellProfile = "powershell-encoded-command-v1";
    internal const string Base64Profile = "rfc4648-base64-text-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    internal static readonly string EngineVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    internal static async Task<DecoderPipelineStats> ProcessAsync(
        string rawInputPath,
        string decodedOutputPath,
        string assessmentsOutputPath,
        string workStatsOutputPath,
        DecoderPipelineOptions options,
        CancellationToken cancellationToken = default,
        Action<long, long>? progress = null
    )
    {
        ValidateOptions(options);
        if (options.Mode == DecoderWorkflowMode.Off)
        {
            throw new ArgumentException("Decoder processing requires auto or force mode.", nameof(options));
        }

        var rawFullPath = RequireInput(rawInputPath);
        var decodedFullPath = PrepareOutputPath(decodedOutputPath, rawFullPath);
        var assessmentsFullPath = PrepareOutputPath(assessmentsOutputPath, rawFullPath);
        var workStatsFullPath = PrepareOutputPath(workStatsOutputPath, rawFullPath);
        RequireDistinct([decodedFullPath, assessmentsFullPath, workStatsFullPath]);

        var decodedTemporaryPath = NewTemporaryPath(decodedFullPath);
        var assessmentsTemporaryPath = NewTemporaryPath(assessmentsFullPath);
        var statsTemporaryPath = NewTemporaryPath(workStatsFullPath);
        var published = new List<string>(capacity: 3);
        var inputBytes = new FileInfo(rawFullPath).Length;
        progress?.Invoke(0, inputBytes);

        long inputRecords = 0;
        long candidateOccurrences = 0;
        long attemptedCandidates = 0;
        long assessmentRecords = 0;
        long publishedTextChildren = 0;
        long decodedBinaryKnown = 0;
        long decodedBinaryOpaque = 0;
        long textRejected = 0;
        long canonicalRejected = 0;
        long resourceLimited = 0;
        long skippedAfterCandidateLimit = 0;
        long skippedAfterTotalByteLimit = 0;
        long decodedBytesAttempted = 0;
        var totalLimitReached = false;

        try
        {
            await using (
                var decodedWriter = CreateWriter(decodedTemporaryPath)
            )
            await using (
                var assessmentWriter = CreateWriter(assessmentsTemporaryPath)
            )
            {
                await foreach (
                    var item in EnrichmentJsonlReader.ReadAsync(rawFullPath, cancellationToken)
                )
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    inputRecords++;
                    if (
                        item.Record.Transform is not null
                        || !string.IsNullOrWhiteSpace(item.Record.ParentRecordId)
                    )
                    {
                        throw new InvalidDataException(
                            $"Decoder raw input at line {item.LineNumber:N0} is already transformed."
                        );
                    }

                    if (!TrySelectCandidate(item.Record.Text!, options.Mode, out var candidate))
                    {
                        progress?.Invoke(Math.Min(inputBytes, item.StreamPosition), inputBytes);
                        continue;
                    }
                    candidateOccurrences++;

                    if (attemptedCandidates >= options.MaxCandidates)
                    {
                        skippedAfterCandidateLimit++;
                        progress?.Invoke(Math.Min(inputBytes, item.StreamPosition), inputBytes);
                        continue;
                    }
                    if (totalLimitReached)
                    {
                        skippedAfterTotalByteLimit++;
                        progress?.Invoke(Math.Min(inputBytes, item.StreamPosition), inputBytes);
                        continue;
                    }

                    attemptedCandidates++;
                    DecoderAssessmentRecord assessment;
                    EnrichmentStringRecord? child = null;
                    if (candidate.Length > options.MaxCharacters)
                    {
                        assessment = CreateAssessment(
                            item.Record,
                            options,
                            candidate,
                            "resource-limited",
                            "candidate-too-large"
                        );
                        resourceLimited++;
                    }
                    else
                    {
                        var hasExactDecodedLength = Base64ContentCore.TryGetExactDecodedLength(candidate.Value, out var expectedDecodedLength);
                        var bufferLength = hasExactDecodedLength
                            ? expectedDecodedLength
                            : GetMaximumDecodedLength(candidate.Length);
                        if (hasExactDecodedLength && expectedDecodedLength > options.MaxBytesPerRecord)
                        {
                            assessment = CreateAssessment(
                                item.Record,
                                options,
                                candidate,
                                "resource-limited",
                                "decoded-payload-too-large"
                            );
                            resourceLimited++;
                        }
                        else if (
                            hasExactDecodedLength
                            && decodedBytesAttempted > options.MaxTotalBytes - expectedDecodedLength
                        )
                        {
                            assessment = CreateAssessment(
                                item.Record,
                                options,
                                candidate,
                                "resource-limited",
                                "total-decoded-byte-limit"
                            );
                            resourceLimited++;
                            totalLimitReached = true;
                        }
                        else
                        {
                            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, bufferLength));
                            try
                            {
                                if (
                                    !Base64ContentCore.TryDecodeCanonical(
                                        candidate.Value,
                                        buffer,
                                        out var decodedLength
                                    )
                                )
                                {
                                    assessment = CreateAssessment(
                                        item.Record,
                                        options,
                                        candidate,
                                        "canonical-rejected",
                                        "noncanonical-base64"
                                    );
                                    canonicalRejected++;
                                }
                                else
                                {
                                    decodedBytesAttempted += decodedLength;
                                    var decoded = buffer.AsSpan(0, decodedLength);
                                    var decodedSha256 = Convert
                                        .ToHexString(SHA256.HashData(decoded))
                                        .ToLowerInvariant();
                                    var binaryClass = Base64ContentCore.ClassifyKnownBinary(decoded);
                                    if (binaryClass is not null)
                                    {
                                        assessment = CreateAssessment(
                                            item.Record,
                                            options,
                                            candidate,
                                            "decoded-binary-known",
                                            "recognized-binary-signature",
                                            decodedLength,
                                            decodedSha256,
                                            binaryClass: binaryClass
                                        );
                                        decodedBinaryKnown++;
                                    }
                                    else if (
                                        Base64ContentCore.TryDecodeText(
                                            decoded,
                                            candidate.Profile == PowerShellProfile,
                                            out var text,
                                            out var charset
                                        )
                                        && Base64ContentCore.IsPublishableText(text)
                                    )
                                    {
                                        assessment = CreateAssessment(
                                            item.Record,
                                            options,
                                            candidate,
                                            "published-text",
                                            "decoded-text",
                                            decodedLength,
                                            decodedSha256,
                                            charset
                                        );
                                        child = CreateChild(
                                            item.Record,
                                            options,
                                            candidate,
                                            text,
                                            charset,
                                            decodedLength,
                                            decodedSha256
                                        );
                                        publishedTextChildren++;
                                    }
                                    else if (Base64ContentCore.LooksLikeText(decoded))
                                    {
                                        assessment = CreateAssessment(
                                            item.Record,
                                            options,
                                            candidate,
                                            "text-rejected",
                                            "unsupported-or-disallowed-text",
                                            decodedLength,
                                            decodedSha256
                                        );
                                        textRejected++;
                                    }
                                    else
                                    {
                                        assessment = CreateAssessment(
                                            item.Record,
                                            options,
                                            candidate,
                                            "decoded-binary-opaque",
                                            "opaque-binary",
                                            decodedLength,
                                            decodedSha256
                                        );
                                        decodedBinaryOpaque++;
                                    }
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                            }
                        }
                    }

                    await assessmentWriter.WriteLineAsync(
                        JsonSerializer.Serialize(assessment, JsonOptions).AsMemory(),
                        cancellationToken
                    );
                    assessmentRecords++;
                    if (child is not null)
                    {
                        await decodedWriter.WriteLineAsync(
                            JsonSerializer.Serialize(child, JsonOptions).AsMemory(),
                            cancellationToken
                        );
                    }
                    progress?.Invoke(Math.Min(inputBytes, item.StreamPosition), inputBytes);
                }
                await decodedWriter.FlushAsync(cancellationToken);
                await assessmentWriter.FlushAsync(cancellationToken);
            }

            var decodedArtifactSha256 = await HashFileAsync(decodedTemporaryPath, cancellationToken);
            var assessmentsSha256 = await HashFileAsync(
                assessmentsTemporaryPath,
                cancellationToken
            );
            var stats = new DecoderPipelineStats(
                SchemaVersion: 1,
                PolicyVersion,
                ModeName(options.Mode),
                options.MaxCharacters,
                options.MaxBytesPerRecord,
                options.MaxCandidates,
                options.MaxTotalBytes,
                inputRecords,
                candidateOccurrences,
                attemptedCandidates,
                assessmentRecords,
                publishedTextChildren,
                decodedBinaryKnown,
                decodedBinaryOpaque,
                textRejected,
                canonicalRejected,
                resourceLimited,
                skippedAfterCandidateLimit,
                skippedAfterTotalByteLimit,
                decodedBytesAttempted,
                new DecoderArtifactInfo(
                    Path.GetFileName(decodedFullPath),
                    decodedArtifactSha256,
                    publishedTextChildren
                ),
                new DecoderArtifactInfo(
                    Path.GetFileName(assessmentsFullPath),
                    assessmentsSha256,
                    assessmentRecords
                )
            );
            await File.WriteAllTextAsync(
                statsTemporaryPath,
                JsonSerializer.Serialize(stats, JsonOptions) + "\n",
                new UTF8Encoding(false),
                cancellationToken
            );

            Publish(decodedTemporaryPath, decodedFullPath, published);
            decodedTemporaryPath = string.Empty;
            Publish(assessmentsTemporaryPath, assessmentsFullPath, published);
            assessmentsTemporaryPath = string.Empty;
            Publish(statsTemporaryPath, workStatsFullPath, published);
            statsTemporaryPath = string.Empty;
            progress?.Invoke(inputBytes, inputBytes);
            return stats;
        }
        catch
        {
            foreach (var path in published)
            {
                DeleteBestEffort(path);
            }
            throw;
        }
        finally
        {
            DeleteBestEffort(decodedTemporaryPath);
            DeleteBestEffort(assessmentsTemporaryPath);
            DeleteBestEffort(statsTemporaryPath);
        }
    }

    internal static string CreateChildRecordId(
        string parentRecordId,
        int candidateStart,
        int candidateLength,
        string profile,
        string charset,
        string outerWhitespaceTreatment,
        int leadingWhitespace,
        int trailingWhitespace,
        string text
    )
    {
        var material = string.Join(
            "\0",
            parentRecordId,
            candidateStart.ToString(CultureInfo.InvariantCulture),
            candidateLength.ToString(CultureInfo.InvariantCulture),
            "base64",
            profile,
            PolicyVersion,
            charset,
            outerWhitespaceTreatment,
            leadingWhitespace.ToString(CultureInfo.InvariantCulture),
            trailingWhitespace.ToString(CultureInfo.InvariantCulture),
            text
        );
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Base64ContentCore.StrictUtf8.GetBytes(material))).ToLowerInvariant()}";
    }

    internal static void ValidateOptions(DecoderPipelineOptions options)
    {
        if (!Enum.IsDefined(options.Mode))
        {
            throw new ArgumentException("Decoder mode is invalid.", nameof(options));
        }
        if (
            options.MaxCharacters < 8
            || options.MaxCharacters > EnrichmentRegexPipelineCore.MaxDecoderCandidateCharacters
            || options.MaxBytesPerRecord < 1
            || options.MaxBytesPerRecord > EnrichmentRegexPipelineCore.MaxDecoderBytesPerRecord
            || options.MaxCandidates < 1
            || options.MaxCandidates > EnrichmentRegexPipelineCore.MaxDecoderCandidates
            || options.MaxTotalBytes < 1
            || options.MaxTotalBytes > EnrichmentRegexPipelineCore.MaxDecoderTotalBytes
        )
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Decoder limits are outside the accepted bounds.");
        }
    }

    internal static bool IsCandidate(string text, DecoderWorkflowMode mode) =>
        TrySelectCandidate(text, mode, out _);

    internal static int ValidateAssessmentAgainstParent(
        EnrichmentStringRecord parent,
        DecoderAssessmentRecord assessment,
        DecoderPipelineOptions options,
        long decodedBytesBefore,
        long assessmentLineNumber
    )
    {
        if (!TrySelectCandidate(parent.Text!, options.Mode, out var candidate))
        {
            throw InvalidAssessment(assessmentLineNumber, "the raw parent is not a decoder candidate");
        }
        if (
            assessment.Profile != candidate.Profile
            || assessment.CandidateStart != candidate.Start
            || assessment.CandidateLength != candidate.Length
            || assessment.OuterWhitespaceTreatment != candidate.OuterWhitespaceTreatment
            || assessment.LeadingWhitespaceCharacters != candidate.LeadingWhitespace
            || assessment.TrailingWhitespaceCharacters != candidate.TrailingWhitespace
        )
        {
            throw InvalidAssessment(assessmentLineNumber, "candidate span, profile, or whitespace provenance changed");
        }

        if (candidate.Length > options.MaxCharacters)
        {
            RequireAssessmentDecision(assessment, "resource-limited", "candidate-too-large", null, null, null, null, assessmentLineNumber);
            return 0;
        }
        var hasExactDecodedLength = Base64ContentCore.TryGetExactDecodedLength(candidate.Value, out var expectedDecodedLength);
        var bufferLength = hasExactDecodedLength
            ? expectedDecodedLength
            : GetMaximumDecodedLength(candidate.Length);
        if (hasExactDecodedLength && expectedDecodedLength > options.MaxBytesPerRecord)
        {
            RequireAssessmentDecision(assessment, "resource-limited", "decoded-payload-too-large", null, null, null, null, assessmentLineNumber);
            return 0;
        }
        if (
            hasExactDecodedLength
            && decodedBytesBefore > options.MaxTotalBytes - expectedDecodedLength
        )
        {
            RequireAssessmentDecision(assessment, "resource-limited", "total-decoded-byte-limit", null, null, null, null, assessmentLineNumber);
            return 0;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, bufferLength));
        try
        {
            if (!Base64ContentCore.TryDecodeCanonical(candidate.Value, buffer, out var decodedLength))
            {
                RequireAssessmentDecision(assessment, "canonical-rejected", "noncanonical-base64", null, null, null, null, assessmentLineNumber);
                return 0;
            }
            var decoded = buffer.AsSpan(0, decodedLength);
            var sha256 = Convert.ToHexString(SHA256.HashData(decoded)).ToLowerInvariant();
            var binaryClass = Base64ContentCore.ClassifyKnownBinary(decoded);
            if (binaryClass is not null)
            {
                RequireAssessmentDecision(assessment, "decoded-binary-known", "recognized-binary-signature", decodedLength, sha256, null, binaryClass, assessmentLineNumber);
            }
            else if (
                Base64ContentCore.TryDecodeText(
                    decoded,
                    candidate.Profile == PowerShellProfile,
                    out var text,
                    out var charset
                )
                && Base64ContentCore.IsPublishableText(text)
            )
            {
                RequireAssessmentDecision(assessment, "published-text", "decoded-text", decodedLength, sha256, charset, null, assessmentLineNumber);
            }
            else if (Base64ContentCore.LooksLikeText(decoded))
            {
                RequireAssessmentDecision(assessment, "text-rejected", "unsupported-or-disallowed-text", decodedLength, sha256, null, null, assessmentLineNumber);
            }
            else
            {
                RequireAssessmentDecision(assessment, "decoded-binary-opaque", "opaque-binary", decodedLength, sha256, null, null, assessmentLineNumber);
            }
            return decodedLength;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void RequireAssessmentDecision(
        DecoderAssessmentRecord assessment,
        string outcome,
        string reason,
        int? decodedLength,
        string? decodedSha256,
        string? charset,
        string? binaryClass,
        long lineNumber
    )
    {
        if (
            assessment.Outcome != outcome
            || assessment.Reason != reason
            || assessment.DecodedByteLength != decodedLength
            || assessment.DecodedSha256 != decodedSha256
            || assessment.DecodedCharset != charset
            || assessment.BinaryClass != binaryClass
        )
        {
            throw InvalidAssessment(lineNumber, "outcome or decoded evidence does not reproduce from the raw candidate");
        }
    }

    private static InvalidDataException InvalidAssessment(long lineNumber, string reason) =>
        new($"Decoder assessment line {lineNumber:N0} is invalid: {reason}.");

    private static EnrichmentStringRecord CreateChild(
        EnrichmentStringRecord parent,
        DecoderPipelineOptions options,
        Candidate candidate,
        string text,
        string charset,
        int decodedLength,
        string decodedSha256
    )
    {
        var attributes = parent.Attributes is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(parent.Attributes, StringComparer.Ordinal);
        attributes["decoder"] = JsonSerializer.SerializeToElement("base64");
        attributes["decoderProfile"] = JsonSerializer.SerializeToElement(candidate.Profile);
        attributes["decoderPolicyVersion"] = JsonSerializer.SerializeToElement(PolicyVersion);
        attributes["candidateStart"] = JsonSerializer.SerializeToElement(candidate.Start);
        attributes["candidateLength"] = JsonSerializer.SerializeToElement(candidate.Length);
        attributes["outerWhitespaceTreatment"] = JsonSerializer.SerializeToElement(
            candidate.OuterWhitespaceTreatment
        );
        attributes["leadingWhitespaceCharacters"] = JsonSerializer.SerializeToElement(
            candidate.LeadingWhitespace
        );
        attributes["trailingWhitespaceCharacters"] = JsonSerializer.SerializeToElement(
            candidate.TrailingWhitespace
        );
        attributes["decodedByteLength"] = JsonSerializer.SerializeToElement(decodedLength);
        attributes["decodedSha256"] = JsonSerializer.SerializeToElement(decodedSha256);
        attributes["decodedCharset"] = JsonSerializer.SerializeToElement(charset);
        attributes["decodeDepth"] = JsonSerializer.SerializeToElement(1);
        attributes["maxCandidateCharacters"] = JsonSerializer.SerializeToElement(
            options.MaxCharacters
        );
        attributes["maxDecodedBytesPerRecord"] = JsonSerializer.SerializeToElement(
            options.MaxBytesPerRecord
        );
        attributes["maxAttemptedCandidates"] = JsonSerializer.SerializeToElement(
            options.MaxCandidates
        );
        attributes["maxTotalDecodedBytes"] = JsonSerializer.SerializeToElement(
            options.MaxTotalBytes
        );

        return new EnrichmentStringRecord
        {
            SchemaVersion = EnrichmentRegexPipelineCore.CurrentSchemaVersion,
            RecordType = "string",
            RecordId = CreateChildRecordId(
                parent.RecordId,
                candidate.Start,
                candidate.Length,
                candidate.Profile,
                charset,
                candidate.OuterWhitespaceTreatment,
                candidate.LeadingWhitespace,
                candidate.TrailingWhitespace,
                text
            ),
            Text = text,
            SourceFile = parent.SourceFile,
            Location = parent.Location,
            Origin = parent.Origin,
            ParentRecordId = parent.RecordId,
            Transform = new EnrichmentTransform
            {
                Kind = "decoding",
                Engine = "bstrings",
                EngineVersion = EngineVersion,
                Profile = candidate.Profile,
                PolicyVersion = PolicyVersion,
                Outcome = "decoded-text",
            },
            Attributes = attributes,
        };
    }

    private static DecoderAssessmentRecord CreateAssessment(
        EnrichmentStringRecord parent,
        DecoderPipelineOptions options,
        Candidate candidate,
        string outcome,
        string reason,
        int? decodedLength = null,
        string? decodedSha256 = null,
        string? charset = null,
        string? binaryClass = null
    ) =>
        new()
        {
            SourceRecordId = parent.RecordId,
            SourceFile = parent.SourceFile,
            Location = parent.Location,
            Mode = ModeName(options.Mode),
            Profile = candidate.Profile,
            Outcome = outcome,
            Reason = reason,
            CandidateStart = candidate.Start,
            CandidateLength = candidate.Length,
            OuterWhitespaceTreatment = candidate.OuterWhitespaceTreatment,
            LeadingWhitespaceCharacters = candidate.LeadingWhitespace,
            TrailingWhitespaceCharacters = candidate.TrailingWhitespace,
            DecodedByteLength = decodedLength,
            DecodedSha256 = decodedSha256,
            DecodedCharset = charset,
            BinaryClass = binaryClass,
            MaxCandidateCharacters = options.MaxCharacters,
            MaxDecodedBytesPerRecord = options.MaxBytesPerRecord,
            MaxAttemptedCandidates = options.MaxCandidates,
            MaxTotalDecodedBytes = options.MaxTotalBytes,
        };

    private static bool TrySelectCandidate(
        string text,
        DecoderWorkflowMode mode,
        out Candidate candidate
    )
    {
        var textSpan = text.AsSpan();
        if (
            HasPowerShellExecutablePrefix(textSpan)
            && TryGetPowerShellCandidate(text, out candidate)
        )
        {
            return true;
        }

        var leading = 0;
        while (leading < text.Length && IsOuterAsciiWhitespace(text[leading]))
        {
            leading++;
        }
        var end = text.Length;
        while (end > leading && IsOuterAsciiWhitespace(text[end - 1]))
        {
            end--;
        }
        var length = end - leading;
        var minimum = mode == DecoderWorkflowMode.Force ? 8 : 24;
        if (length < minimum)
        {
            candidate = default;
            return false;
        }
        var valueSpan = textSpan.Slice(leading, length);
        if (!IsBase64Shape(valueSpan, out var hasDistinctiveCharacter))
        {
            candidate = default;
            return false;
        }
        if (
            mode == DecoderWorkflowMode.Auto
            && !hasDistinctiveCharacter
        )
        {
            candidate = default;
            return false;
        }
        candidate = new Candidate(
            valueSpan.ToString(),
            leading,
            length,
            Base64Profile,
            leading == 0 && end == text.Length ? "none" : "ascii-trim",
            leading,
            text.Length - end
        );
        return true;
    }

    private static bool TryGetPowerShellCandidate(string text, out Candidate candidate)
    {
        candidate = default;
        if (!TryTokenizeCommandLine(text, out var tokens) || tokens.Count < 3)
        {
            return false;
        }
        if (!IsPowerShellExecutable(tokens[0].Value))
        {
            return false;
        }
        var switchIndex = -1;
        for (var index = 1; index < tokens.Count; index++)
        {
            if (tokens[index].Value.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase))
            {
                if (switchIndex >= 0)
                {
                    return false;
                }
                switchIndex = index;
            }
        }
        if (switchIndex < 1 || switchIndex + 2 != tokens.Count)
        {
            return false;
        }
        for (var index = 1; index < switchIndex; index++)
        {
            if (!IsAllowedPowerShellPreambleSwitch(tokens[index].Value))
            {
                return false;
            }
        }
        var token = tokens[switchIndex + 1];
        if (token.Value.Length < 8 || !token.Value.All(IsBase64AlphabetOrPadding))
        {
            return false;
        }
        candidate = new Candidate(
            token.Value,
            token.Start,
            token.Length,
            PowerShellProfile,
            "none",
            0,
            0
        );
        return true;
    }

    private static bool HasPowerShellExecutablePrefix(ReadOnlySpan<char> text)
    {
        var index = 0;
        while (index < text.Length && IsOuterAsciiWhitespace(text[index]))
        {
            index++;
        }
        if (index == text.Length || text[index] is '"' or '\'')
        {
            return false;
        }
        var start = index;
        while (index < text.Length && !IsOuterAsciiWhitespace(text[index]))
        {
            if (text[index] is '"' or '\'')
            {
                return false;
            }
            index++;
        }
        return IsPowerShellExecutable(text[start..index]);
    }

    private static bool IsBase64Shape(
        ReadOnlySpan<char> value,
        out bool hasDistinctiveCharacter
    )
    {
        hasDistinctiveCharacter = false;
        foreach (var character in value)
        {
            if (!IsBase64AlphabetOrPadding(character))
            {
                return false;
            }
            hasDistinctiveCharacter |= character is >= '0' and <= '9' or '+' or '/' or '=';
        }
        return true;
    }

    private static bool TryTokenizeCommandLine(string text, out List<CommandToken> tokens)
    {
        tokens = [];
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && IsOuterAsciiWhitespace(text[index]))
            {
                index++;
            }
            if (index == text.Length)
            {
                break;
            }
            var start = index;
            if (text[index] is '"' or '\'')
            {
                return false;
            }
            while (index < text.Length && !IsOuterAsciiWhitespace(text[index]))
            {
                if (text[index] is '"' or '\'')
                {
                    return false;
                }
                index++;
            }
            tokens.Add(new CommandToken(text[start..index], start, index - start));
        }
        return tokens.Count > 0;
    }

    private static int GetMaximumDecodedLength(int encodedLength) =>
        checked((encodedLength / 4) * 3);

    private static bool IsAllowedPowerShellPreambleSwitch(string value) =>
        value.Equals("-NoProfile", StringComparison.OrdinalIgnoreCase)
        || value.Equals("-NonInteractive", StringComparison.OrdinalIgnoreCase)
        || value.Equals("-NoLogo", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerShellExecutable(string value) =>
        IsPowerShellExecutable(value.AsSpan());

    private static bool IsPowerShellExecutable(ReadOnlySpan<char> value) =>
        value.Equals("powershell", StringComparison.OrdinalIgnoreCase)
        || value.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
        || value.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
        || value.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsBase64AlphabetOrPadding(char value) =>
        value is >= 'A' and <= 'Z'
        || value is >= 'a' and <= 'z'
        || value is >= '0' and <= '9'
        || value is '+' or '/' or '=';

    private static bool IsOuterAsciiWhitespace(char value) =>
        value is ' ' or '\t' or '\r' or '\n' or '\v' or '\f';

    private static StreamWriter CreateWriter(string path) =>
        new(
            new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ),
            new UTF8Encoding(false),
            1024 * 1024
        )
        {
            NewLine = "\n",
        };

    private static string RequireInput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Decoder raw JSONL input was not found.", fullPath);
        }
        return fullPath;
    }

    private static string PrepareOutputPath(string path, string inputFullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(fullPath, inputFullPath, PathComparison))
        {
            throw new ArgumentException("Decoder input and output paths must differ.");
        }
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new IOException($"Decoder output already exists: '{fullPath}'.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private static void RequireDistinct(IReadOnlyList<string> paths)
    {
        for (var left = 0; left < paths.Count; left++)
        {
            for (var right = left + 1; right < paths.Count; right++)
            {
                if (string.Equals(paths[left], paths[right], PathComparison))
                {
                    throw new ArgumentException("Decoder output paths must be distinct.");
                }
            }
        }
    }

    private static string NewTemporaryPath(string destination) =>
        destination + ".partial." + Guid.NewGuid().ToString("N");

    private static void Publish(string temporaryPath, string destination, List<string> published)
    {
        File.Move(temporaryPath, destination, overwrite: false);
        published.Add(destination);
    }

    private static void DeleteBestEffort(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly record struct Candidate(
        string Value,
        int Start,
        int Length,
        string Profile,
        string OuterWhitespaceTreatment,
        int LeadingWhitespace,
        int TrailingWhitespace
    );

    private readonly record struct CommandToken(string Value, int Start, int Length);
}
