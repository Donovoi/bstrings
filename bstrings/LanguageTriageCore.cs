#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    LanguageDetectionMode EffectiveMode,
    string TranslationRoutingPolicyVersion,
    long TranslationRoutingRetained,
    long TranslationRoutingProspectiveBypasses,
    long TranslationRoutingUnknown,
    long TranslationRoutingEvaluations = 0,
    long DetectorEligibleRecords = 0,
    long DetectorExecutions = 0,
    long DetectorReuseHits = 0
);

[Flags]
internal enum TranslationRoutingProvenance
{
    None = 0,
    NativeStatic = 1 << 0,
    Floss = 1 << 1,
    FlossDecoded = 1 << 2,
    Ocr = 1 << 3,
    PdfText = 1 << 4,
    DerivedTranslation = 1 << 5,
    Unknown = 1 << 6,
}

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
    internal const int AssessmentScoreDecimalPlaces = 12;
    internal const double HighPrecisionMinimumConfidence = 0.65;
    internal const double HighPrecisionMinimumTargetMargin = 0.15;
    private const string DetectorName = "lingua-rs";
    private const string DetectorVersion = "1.8.0";
    private const int AdaptiveSampleSize = 512;
    private const int FastModeMinimumCharacters = 120;
    private static readonly object OutputCommitLock = new();
    private static readonly JsonSerializerOptions AssessmentJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record PendingRecord(
        string OriginalLine,
        string RecordId,
        string Text,
        string SourceFile,
        JsonElement Location,
        bool IsDerived,
        TranslationRoutingProvenance RoutingProvenance
    );

    private sealed record AssessedRecord(
        string AssessmentJson,
        bool IsCandidate,
        string Decision,
        TranslationRoutingAssessment? Routing,
        bool DetectorExecuted,
        bool DetectorReused
    );

    private sealed class LanguageAssessmentPayload
    {
        public int SchemaVersion { get; init; }
        public string RecordType { get; init; } = string.Empty;
        public string SourceRecordId { get; init; } = string.Empty;
        public string SourceFile { get; init; } = string.Empty;
        public JsonElement Location { get; init; }
        public string Detector { get; init; } = string.Empty;
        public string DetectorVersion { get; init; } = string.Empty;
        public string Profile { get; init; } = string.Empty;
        public string TargetLanguage { get; init; } = string.Empty;
        public string? DetectorTargetLanguage { get; init; }
        public string? Language { get; init; }
        public double? Confidence { get; init; }
        public double? TargetConfidence { get; init; }
        public double? SecondConfidence { get; init; }
        public double? TopLanguageMargin { get; init; }
        public double? TargetMargin { get; init; }
        public int ScoreDecimalPlaces { get; init; }
        public double ConfiguredMinimumConfidence { get; init; }
        public double ConfiguredMinimumTargetMargin { get; init; }
        public double EffectiveMinimumConfidence { get; init; }
        public double EffectiveMinimumTargetMargin { get; init; }
        public double MinimumConfidence { get; init; }
        public double MinimumTargetMargin { get; init; }
        public bool? ConfidenceGatePassed { get; init; }
        public bool? MarginGatePassed { get; init; }
        public string Policy { get; init; } = string.Empty;
        public string Decision { get; init; } = string.Empty;
        public bool TranslationCandidate { get; init; }
        public string? Error { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TranslationRoutingProjection? TranslationRouting { get; init; }
    }

    private readonly record struct TranslationRoutingProjection(string Code);

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
        LanguageDetectionHandler? detector = null,
        Action<long, long>? progress = null,
        bool? reuseSuccessfulDetections = null,
        bool includeTranslationRouting = true
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
        var reuseDetections = reuseSuccessfulDetections ?? detector is null;
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
        var inputBytes = new FileInfo(inputFullPath).Length;
        progress?.Invoke(0, inputBytes);
        long inputRecords = 0;
        long targetRecords = 0;
        long candidates = 0;
        long ambiguous = 0;
        long nonLinguistic = 0;
        long failures = 0;
        long routingRetained = 0;
        long routingProspectiveBypasses = 0;
        long routingUnknown = 0;
        long routingEvaluations = 0;
        long detectorEligibleRecords = 0;
        long detectorExecutions = 0;
        long detectorReuseHits = 0;
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
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken,
                };
                IGrouping<string, int>[]? textGroups = null;
                if (includeTranslationRouting || reuseDetections)
                {
                    textGroups = Enumerable
                        .Range(0, pending.Count)
                        .GroupBy(index => pending[index].Text, StringComparer.Ordinal)
                        .ToArray();
                }
                TranslationRoutingAssessment?[]? routingByIndex = null;
                if (includeTranslationRouting)
                {
                    routingByIndex = new TranslationRoutingAssessment?[pending.Count];
                    Parallel.ForEach(
                        textGroups!,
                        parallelOptions,
                        group =>
                        {
                            var firstIndex = group.First();
                            var routing = TranslationWorthinessRouter.Assess(
                                pending[firstIndex].Text
                            );
                            foreach (var index in group)
                            {
                                routingByIndex[index] = routing;
                            }
                        }
                    );
                    routingEvaluations += textGroups!.Length;
                }
                if (reuseDetections)
                {
                    Parallel.ForEach(
                        textGroups!,
                        parallelOptions,
                        group =>
                        {
                            LanguageDetectionResult? successfulDetection = null;
                            foreach (var index in group)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                assessed[index] = Assess(
                                    pending[index],
                                    options,
                                    effectiveMode,
                                    detectorTargetLanguage,
                                    detector,
                                    successfulDetection,
                                    out var currentSuccessfulDetection,
                                    routingByIndex?[index]
                                );
                                successfulDetection ??= currentSuccessfulDetection;
                            }
                        }
                    );
                }
                else
                {
                    Parallel.For(
                        0,
                        pending.Count,
                        parallelOptions,
                        index =>
                            assessed[index] = Assess(
                                pending[index],
                                options,
                                effectiveMode,
                                detectorTargetLanguage,
                                detector,
                                routingByIndex?[index]
                            )
                    );
                }

                for (var index = 0; index < pending.Count; index++)
                {
                    var result = assessed[index];
                    if (result.DetectorExecuted || result.DetectorReused)
                    {
                        detectorEligibleRecords++;
                    }
                    if (result.DetectorExecuted)
                    {
                        detectorExecutions++;
                    }
                    if (result.DetectorReused)
                    {
                        detectorReuseHits++;
                    }
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
                    if (result.Routing is { } routing)
                    {
                        var routingCode = RoutingCode(routing);
                        if (!TranslationWorthinessRouter.Codebook.TryGetValue(routingCode, out var category))
                        {
                            throw new InvalidDataException(
                                $"Translation routing emitted unregistered code '{routingCode}'."
                            );
                        }
                        if (category == "unknown")
                        {
                            routingUnknown++;
                        }
                        else if (category == "prospective")
                        {
                            routingProspectiveBypasses++;
                        }
                        else
                        {
                            routingRetained++;
                        }
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
                    progress?.Invoke(Math.Min(inputBytes, reader.BaseStream.Position), inputBytes);
                }
            }
            await FlushAsync();
            progress?.Invoke(inputBytes, inputBytes);
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
            effectiveMode,
            includeTranslationRouting ? TranslationWorthinessRouter.PolicyVersion : "disabled",
            routingRetained,
            routingProspectiveBypasses,
            routingUnknown,
            routingEvaluations,
            detectorEligibleRecords,
            detectorExecutions,
            detectorReuseHits
        );
    }

    private static AssessedRecord Assess(
        PendingRecord record,
        LanguageTriageOptions options,
        LanguageDetectionMode effectiveMode,
        string detectorTargetLanguage,
        LanguageDetectionHandler detector,
        TranslationRoutingAssessment? routing
    ) =>
        Assess(
            record,
            options,
            effectiveMode,
            detectorTargetLanguage,
            detector,
            reusedDetection: null,
            out _,
            routing
        );

    private static AssessedRecord Assess(
        PendingRecord record,
        LanguageTriageOptions options,
        LanguageDetectionMode effectiveMode,
        string detectorTargetLanguage,
        LanguageDetectionHandler detector,
        LanguageDetectionResult? reusedDetection,
        out LanguageDetectionResult? successfulDetection,
        TranslationRoutingAssessment? routing
    )
    {
        successfulDetection = null;
        var (effectiveMinimumConfidence, effectiveMinimumTargetMargin) =
            ResolveEffectiveThresholds(options);
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
                detectorTargetLanguage,
                routing
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
                detectorTargetLanguage,
                routing
            );
        }
        LanguageDetectionResult detection;
        string? detectorError = null;
        if (reusedDetection is { } cachedDetection)
        {
            detection = cachedDetection;
        }
        else
        {
            if (!detector(
                record.Text,
                effectiveMode,
                detectorTargetLanguage,
                out detection,
                out detectorError
            ))
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
                    detectorTargetLanguage,
                    routing,
                    detectorExecuted: true
                );
            }
        }
        successfulDetection = detection;

        var isTarget = string.Equals(
            detection.Language,
            detectorTargetLanguage,
            StringComparison.OrdinalIgnoreCase
        );
        if (isTarget)
        {
            var confidentTarget =
                detection.Confidence >= effectiveMinimumConfidence
                && detection.TopLanguageMargin >= effectiveMinimumTargetMargin;
            return Assessment(
                record,
                options,
                effectiveMode,
                confidentTarget ? "target-language" : "ambiguous",
                !confidentTarget && options.Policy == LanguageTriagePolicy.HighRecall,
                detection,
                null,
                detectorTargetLanguage,
                routing,
                detectorExecuted: reusedDetection is null,
                detectorReused: reusedDetection is not null
            );
        }

        var clearsGate =
            detection.Confidence >= effectiveMinimumConfidence
            && detection.TargetMargin >= effectiveMinimumTargetMargin;
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
                detectorTargetLanguage,
                routing,
                detectorExecuted: reusedDetection is null,
                detectorReused: reusedDetection is not null
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
            detectorTargetLanguage,
            routing,
            detectorExecuted: reusedDetection is null,
            detectorReused: reusedDetection is not null
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
        string? detectorTargetLanguage,
        TranslationRoutingAssessment? routing,
        bool detectorExecuted = false,
        bool detectorReused = false
    )
    {
        var (effectiveMinimumConfidence, effectiveMinimumTargetMargin) =
            ResolveEffectiveThresholds(options);
        bool? confidenceGatePassed =
            detection is null
                ? null
                : detection.Value.Confidence >= effectiveMinimumConfidence;
        bool? marginGatePassed =
            detection is null
                ? null
                : string.Equals(
                    detection.Value.Language,
                    detectorTargetLanguage,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? detection.Value.TopLanguageMargin >= effectiveMinimumTargetMargin
                    : detection.Value.TargetMargin >= effectiveMinimumTargetMargin;
        TranslationRoutingProjection? routingProjection = routing is not { } routingValue
            ? null
            : new TranslationRoutingProjection(RoutingCode(routingValue));
        var assessment = new LanguageAssessmentPayload
        {
            SchemaVersion = 1,
            RecordType = "language-assessment",
            SourceRecordId = record.RecordId,
            SourceFile = record.SourceFile,
            Location = record.Location,
            Detector = DetectorName,
            DetectorVersion = DetectorVersion,
            Profile = mode.ToString().ToLowerInvariant(),
            TargetLanguage = options.TargetLanguage,
            DetectorTargetLanguage = detectorTargetLanguage,
            Language = detection?.Language,
            Confidence = NormalizeAssessmentMetric(detection?.Confidence),
            TargetConfidence = NormalizeAssessmentMetric(detection?.TargetConfidence),
            SecondConfidence = NormalizeAssessmentMetric(detection?.SecondConfidence),
            TopLanguageMargin = NormalizeAssessmentMetric(detection?.TopLanguageMargin),
            TargetMargin = NormalizeAssessmentMetric(detection?.TargetMargin),
            ScoreDecimalPlaces = AssessmentScoreDecimalPlaces,
            ConfiguredMinimumConfidence = options.MinimumConfidence,
            ConfiguredMinimumTargetMargin = options.MinimumTargetMargin,
            EffectiveMinimumConfidence = effectiveMinimumConfidence,
            EffectiveMinimumTargetMargin = effectiveMinimumTargetMargin,
            // Retain the schema-1 field names as aliases for the thresholds that
            // actually drove the recorded gate booleans and decision.
            MinimumConfidence = effectiveMinimumConfidence,
            MinimumTargetMargin = effectiveMinimumTargetMargin,
            ConfidenceGatePassed = confidenceGatePassed,
            MarginGatePassed = marginGatePassed,
            Policy = PolicyName(options.Policy),
            Decision = decision,
            TranslationCandidate = candidate,
            Error = error,
            TranslationRouting = routingProjection,
        };
        return new AssessedRecord(
            JsonSerializer.Serialize(assessment, AssessmentJsonOptions),
            candidate,
            decision,
            routing,
            detectorExecuted,
            detectorReused
        );
    }

    private static string RoutingCode(TranslationRoutingAssessment routing)
    {
        if (routing.IsUnknown)
        {
            return routing.Reason switch
            {
                "unsupported-length" => "unknown-length",
                "unsupported-control-character" => "unknown-control",
                _ => "unknown-validation",
            };
        }
        if (
            routing.Outcome == TranslationRoutingOutcome.StructuredOnlyProspectiveBypass
            && routing.StructuredClasses is [var validator]
        )
        {
            return validator switch
            {
                "guid" => "prospective-guid",
                "cryptographic-hash" => "prospective-digest",
                "ip-address" => "prospective-ip",
                "ip-network" => "prospective-network",
                "network-endpoint" => "prospective-endpoint",
                _ => "unknown-validation",
            };
        }
        if (routing.Reason == "machine-like-signal-not-bypass-eligible")
        {
            return routing.StructuredClasses is [var signal]
                ? signal switch
                {
                    "jwt" => "shadow-jwt",
                    "hex-blob" => "shadow-hex",
                    "absolute-uri" => "shadow-uri",
                    "registry-path" => "shadow-registry",
                    "absolute-file-path" => "shadow-path",
                    "base64-blob" => "shadow-base64",
                    _ => "retain",
                }
                : "retain";
        }
        return "retain";
    }

    private static (double MinimumConfidence, double MinimumTargetMargin)
        ResolveEffectiveThresholds(LanguageTriageOptions options) =>
        options.Policy == LanguageTriagePolicy.HighPrecision
            ? (
                Math.Max(options.MinimumConfidence, HighPrecisionMinimumConfidence),
                Math.Max(options.MinimumTargetMargin, HighPrecisionMinimumTargetMargin)
            )
            : (options.MinimumConfidence, options.MinimumTargetMargin);

    internal static double? NormalizeAssessmentMetric(double? value)
    {
        if (value is null)
        {
            return null;
        }
        var rounded = Math.Round(
            value.Value,
            AssessmentScoreDecimalPlaces,
            MidpointRounding.ToEven
        );
        return rounded == 0d ? 0d : rounded;
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
                || !TryGetRequiredString(origin, "extractor", out var originExtractor)
                || !TryGetRequiredString(origin, "kind", out var originKind)
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
                isDerived,
                ClassifyRoutingProvenance(originExtractor, originKind, isDerived)
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

    internal static TranslationRoutingProvenance ClassifyRoutingProvenance(
        string extractor,
        string kind,
        bool isDerived
    )
    {
        var provenance = isDerived
            ? TranslationRoutingProvenance.DerivedTranslation
            : TranslationRoutingProvenance.None;
        if (
            string.Equals(extractor, "bstrings", StringComparison.OrdinalIgnoreCase)
            && string.Equals(kind, "static", StringComparison.OrdinalIgnoreCase)
        )
        {
            return provenance | TranslationRoutingProvenance.NativeStatic;
        }
        if (string.Equals(extractor, "floss", StringComparison.OrdinalIgnoreCase))
        {
            provenance |= TranslationRoutingProvenance.Floss;
            if (string.Equals(kind, "decoded", StringComparison.OrdinalIgnoreCase))
            {
                provenance |= TranslationRoutingProvenance.FlossDecoded;
            }
            return provenance;
        }
        if (
            string.Equals(extractor, "rapidocr", StringComparison.OrdinalIgnoreCase)
            && string.Equals(kind, "ocr", StringComparison.OrdinalIgnoreCase)
        )
        {
            return provenance | TranslationRoutingProvenance.Ocr;
        }
        if (
            string.Equals(extractor, "rapidocr", StringComparison.OrdinalIgnoreCase)
            && string.Equals(kind, "pdf-text", StringComparison.OrdinalIgnoreCase)
        )
        {
            return provenance | TranslationRoutingProvenance.PdfText;
        }
        return provenance | TranslationRoutingProvenance.Unknown;
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
        characterCount = text.EnumerateRunes().Count();
        return TranslationTextEligibility.ShouldTranslate(
            text,
            minimum,
            maximum,
            minimumLetters: 4
        );
    }

    private static bool IsLinguisticCandidate(string text, int minimum, int maximum)
    {
        return TranslationTextEligibility.ShouldTranslate(
            text,
            minimum,
            maximum,
            minimumLetters: 4
        );
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
