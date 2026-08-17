#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace bstrings;

internal sealed record EnrichmentLocation
{
    public string Kind { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

internal sealed record EnrichmentOrigin
{
    public string Extractor { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string? Revision { get; init; }
    public string? ModelSha256 { get; init; }
    public string? Provider { get; init; }
}

internal sealed record EnrichmentTransform
{
    public string Kind { get; init; } = string.Empty;
    public string Engine { get; init; } = string.Empty;
    public string? EngineVersion { get; init; }
    public string? Model { get; init; }
    public string? Revision { get; init; }
    public string? ModelSha256 { get; init; }
    public string? SourceLanguage { get; init; }
    public string? TargetLanguage { get; init; }
    public string? Profile { get; init; }
    public string? PolicyVersion { get; init; }
    public string? Outcome { get; init; }
}

internal sealed record TranslationValidationRequirements(
    string Engine,
    string TargetLanguage,
    string Model,
    string Revision,
    string ModelSha256
);

internal enum TranslationIntegrityStatus
{
    Verified,
    SourceRetainedAmbiguous,
    PreservationFallback,
}

internal sealed record OcrValidationRequirements(
    string Engine,
    string EngineVersion,
    string Model,
    string Revision,
    string ModelSha256,
    string RuntimeSha256,
    string DetectorSha256,
    string RecognizerSha256,
    string ClassifierSha256,
    string DictionarySha256,
    OcrWorkflowMode RequestedMode,
    OcrProvider RequestedProvider,
    int RequestedThreads
);

internal sealed record TranslationLineageIdentity(
    string SourceFile,
    string LocationKind,
    string LocationValue,
    string OriginExtractor,
    string? OriginVersion,
    string OriginKind,
    string? OriginModel = null,
    string? OriginRevision = null,
    string? OriginModelSha256 = null,
    string? OriginProvider = null
);

internal sealed record EnrichmentStringRecord
{
    public int SchemaVersion { get; init; }
    public string RecordType { get; init; } = string.Empty;
    public string RecordId { get; init; } = string.Empty;
    public string? Text { get; init; }
    public string SourceFile { get; init; } = string.Empty;
    public EnrichmentLocation? Location { get; init; }
    public EnrichmentOrigin? Origin { get; init; }
    public string? ParentRecordId { get; init; }
    public EnrichmentTransform? Transform { get; init; }
    public Dictionary<string, JsonElement>? Attributes { get; init; }
}

internal sealed record EnrichmentRegexMatchRecord
{
    public int SchemaVersion { get; init; } = EnrichmentRegexPipelineCore.CurrentSchemaVersion;
    public string RecordType { get; init; } = "regex-match";
    public string PatternName { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public string PatternDescription { get; init; } = string.Empty;
    public string PatternSource { get; init; } = string.Empty;
    public string PatternValidation { get; init; } = string.Empty;
    public string Match { get; init; } = string.Empty;
    public int MatchStart { get; init; } = -1;
    public int MatchLength { get; init; }
    public int MatchLine { get; init; }
    public int ContextStart { get; init; } = -1;
    public string? Context { get; init; }
    public string SourceRecordId { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
    public EnrichmentLocation? Location { get; init; }
    public EnrichmentOrigin? Origin { get; init; }
    public string? ParentRecordId { get; init; }
    public EnrichmentTransform? Transform { get; init; }
    public string EvidenceClass { get; init; } = string.Empty;
    public Dictionary<string, JsonElement>? Attributes { get; init; }
}

internal readonly record struct EnrichmentPipelineStats(
    long InputRecords,
    long TranslatedRecords,
    long MatchRecords,
    long PreservationFallbackRecords = 0,
    long DecodedRecords = 0,
    long MatchCacheHits = 0,
    long MatchCacheMisses = 0,
    long MatchCacheProbationObservations = 0,
    long MatchCacheProbationBypasses = 0,
    long MatchCachePreLookupBypasses = 0,
    long MatchCachePostComputationBypasses = 0,
    long MatchCacheStores = 0,
    long MatchCacheEvictions = 0,
    long ReusedPatternEvaluations = 0,
    long MatchRowsServedFromCache = 0,
    long MatchRowsComputed = 0,
    long MatchCacheCurrentLogicalBytes = 0,
    int MatchCacheCurrentEntries = 0,
    long MatchCachePeakLogicalBytes = 0,
    int MatchCachePeakEntries = 0
);
