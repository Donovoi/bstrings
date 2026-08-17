#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    internal const int MaxDecoderCandidateCharacters = 2 * 1024 * 1024;
    internal const int MaxDecoderBytesPerRecord = 1_572_864;
    internal const int MaxDecoderCandidates = 1_000_000;
    internal const long MaxDecoderTotalBytes = int.MaxValue;

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
        Action<string>? fallbackIndexDeleteDirectory = null,
        MatchResultCacheOptions? matchCacheOptions = null,
        IEqualityComparer<string>? matchCacheComparer = null
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
        var effectiveCacheOptions = matchCacheOptions ?? MatchResultCacheOptions.Default;
        effectiveCacheOptions.Validate();
        var matchCache = new ExactTextMatchCache(effectiveCacheOptions, matchCacheComparer);
        var hasCacheablePatterns = compiledPatterns.Any(pattern => pattern.Cacheable);
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
        long decodedRecords = 0;
        long matchRecords = 0;
        long preservationFallbackRecords = 0;
        long matchCacheHits = 0;
        long matchCacheMisses = 0;
        long matchCacheProbationObservations = 0;
        long matchCacheProbationBypasses = 0;
        long matchCachePreLookupBypasses = 0;
        long matchCachePostComputationBypasses = 0;
        long matchCacheStores = 0;
        long matchCacheEvictions = 0;
        long reusedPatternEvaluations = 0;
        long matchRowsServedFromCache = 0;
        long matchRowsComputed = 0;
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
        var trustedSection = TrustedRecordSection.Originals;
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
                var isDecoding = IsDecoding(record);
                if (
                    seenRecordIds is not null
                    && (isTranslation || isDecoding)
                    && !seenRecordIds.Contains(record.ParentRecordId!)
                )
                {
                    throw new InvalidDataException(
                        $"Derived enrichment record at line {lineNumber:N0} references parentRecordId "
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
                        if (trustedSection == TrustedRecordSection.Decoding)
                        {
                            throw new InvalidDataException(
                                $"Enrichment JSONL line {lineNumber:N0} places a translated record after decoding children."
                            );
                        }
                        trustedSection = TrustedRecordSection.Translations;
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
                    else if (isDecoding)
                    {
                        trustedSection = TrustedRecordSection.Decoding;
                        provenanceValidator!.AddDecoding(
                            record.RecordId,
                            record.ParentRecordId!,
                            CreateLineageIdentity(record)
                        );
                    }
                    else if (trustedSection != TrustedRecordSection.Originals)
                    {
                        throw new InvalidDataException(
                            $"Enrichment JSONL line {lineNumber:N0} places an original record after derived children."
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
                else if (isDecoding)
                {
                    decodedRecords++;
                }

                var text = record.Text!;
                var parsedHit = new ParsedHit(
                    text,
                    text,
                    record.Location?.Value ?? string.Empty
                );
                var lineMap = new RecordLineMap(text);
                if (
                    !effectiveCacheOptions.Enabled
                    || !hasCacheablePatterns
                    || text.Length > effectiveCacheOptions.MaximumTextCharacters
                )
                {
                    matchCachePreLookupBypasses++;
                    for (var patternIndex = 0; patternIndex < compiledPatterns.Count; patternIndex++)
                    {
                        var evaluation = await EvaluatePatternAsync(
                            compiledPatterns[patternIndex],
                            patternIndex,
                            record,
                            parsedHit,
                            writer,
                            lineMap,
                            descriptors: null,
                            captureDescriptors: false,
                            effectiveCacheOptions.MaximumDescriptorsPerEntry,
                            cancellationToken
                        );
                        matchRecords += evaluation.Rows;
                        matchRowsComputed += evaluation.Rows;
                    }
                    continue;
                }

                var cacheProbe = matchCache.Probe(text, out var cachedMatches);
                if (cacheProbe == MatchCacheProbeResult.Hit)
                {
                    matchCacheHits++;
                    var descriptorIndex = 0;
                    for (var patternIndex = 0; patternIndex < compiledPatterns.Count; patternIndex++)
                    {
                        var compiledPattern = compiledPatterns[patternIndex];
                        if (compiledPattern.Cacheable)
                        {
                            var replay = await ReplayCachedPatternAsync(
                                cachedMatches,
                                descriptorIndex,
                                patternIndex,
                                compiledPattern,
                                record,
                                writer,
                                lineMap,
                                cancellationToken
                            );
                            descriptorIndex = replay.NextDescriptorIndex;
                            matchRecords += replay.Rows;
                            matchRowsServedFromCache += replay.Rows;
                            reusedPatternEvaluations++;
                        }
                        else
                        {
                            var evaluation = await EvaluatePatternAsync(
                                compiledPattern,
                                patternIndex,
                                record,
                                parsedHit,
                                writer,
                                lineMap,
                                descriptors: null,
                                captureDescriptors: false,
                                effectiveCacheOptions.MaximumDescriptorsPerEntry,
                                cancellationToken
                            );
                            matchRecords += evaluation.Rows;
                            matchRowsComputed += evaluation.Rows;
                        }
                    }
                    if (descriptorIndex != cachedMatches.Count)
                    {
                        throw new InvalidDataException(
                            "The run-local match cache contains an invalid pattern ordering."
                        );
                    }
                    continue;
                }

                matchCacheMisses++;
                var cacheStoreEligible = cacheProbe == MatchCacheProbeResult.MissEligible;
                if (!cacheStoreEligible)
                {
                    if (cacheProbe == MatchCacheProbeResult.MissObserved)
                    {
                        matchCacheProbationObservations++;
                    }
                    else
                    {
                        matchCacheProbationBypasses++;
                    }
                }
                var descriptors = new List<CachedRegexMatch>(
                    Math.Min(64, effectiveCacheOptions.MaximumDescriptorsPerEntry)
                );
                var descriptorOverflow = false;
                for (var patternIndex = 0; patternIndex < compiledPatterns.Count; patternIndex++)
                {
                    var compiledPattern = compiledPatterns[patternIndex];
                    var evaluation = await EvaluatePatternAsync(
                        compiledPattern,
                        patternIndex,
                        record,
                        parsedHit,
                        writer,
                        lineMap,
                        descriptors,
                        compiledPattern.Cacheable && !descriptorOverflow,
                        effectiveCacheOptions.MaximumDescriptorsPerEntry,
                        cancellationToken
                    );
                    matchRecords += evaluation.Rows;
                    matchRowsComputed += evaluation.Rows;
                    descriptorOverflow |= evaluation.DescriptorOverflow;
                }

                if (descriptorOverflow || !cacheStoreEligible)
                {
                    matchCachePostComputationBypasses++;
                    continue;
                }

                var store = matchCache.TryAdd(text, descriptors.ToArray());
                matchCacheEvictions += store.Evictions;
                if (store.Stored)
                {
                    matchCacheStores++;
                }
                else
                {
                    matchCachePostComputationBypasses++;
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

            var stats = new EnrichmentPipelineStats(
                inputRecords,
                translatedRecords,
                matchRecords,
                preservationFallbackRecords,
                decodedRecords,
                matchCacheHits,
                matchCacheMisses,
                matchCacheProbationObservations,
                matchCacheProbationBypasses,
                matchCachePreLookupBypasses,
                matchCachePostComputationBypasses,
                matchCacheStores,
                matchCacheEvictions,
                reusedPatternEvaluations,
                matchRowsServedFromCache,
                matchRowsComputed,
                matchCache.CurrentLogicalBytes,
                matchCache.Count,
                matchCache.PeakLogicalBytes,
                matchCache.PeakEntries
            );
            ValidateMatchReuseStats(
                stats,
                compiledPatterns.Count(pattern => pattern.Cacheable),
                effectiveCacheOptions
            );
            return stats;
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

    private static void ValidateMatchReuseStats(
        EnrichmentPipelineStats stats,
        int cacheablePatternCount,
        MatchResultCacheOptions options
    )
    {
        if (
            checked(
                stats.MatchCacheHits
                    + stats.MatchCacheMisses
                    + stats.MatchCachePreLookupBypasses
            ) != stats.InputRecords
            || checked(stats.MatchCacheStores + stats.MatchCachePostComputationBypasses)
                != stats.MatchCacheMisses
            || stats.MatchCacheProbationObservations
                > stats.MatchCachePostComputationBypasses
            || checked(
                stats.MatchCacheProbationObservations
                    + stats.MatchCacheProbationBypasses
            ) > stats.MatchCachePostComputationBypasses
            || checked(stats.MatchRowsComputed + stats.MatchRowsServedFromCache)
                != stats.MatchRecords
            || checked(stats.MatchCacheHits * cacheablePatternCount)
                != stats.ReusedPatternEvaluations
            || stats.MatchCacheStores > options.MaximumEntries + stats.MatchCacheEvictions
            || stats.MatchCacheCurrentEntries > options.MaximumEntries
            || stats.MatchCacheCurrentLogicalBytes > options.MaximumLogicalBytes
            || stats.MatchCachePeakEntries > options.MaximumEntries
            || stats.MatchCachePeakLogicalBytes > options.MaximumLogicalBytes
        )
        {
            throw new InvalidDataException(
                "The run-local match-cache counters or limits do not reconcile."
            );
        }
    }

    private static async Task<PatternEvaluationResult> EvaluatePatternAsync(
        CompiledPattern compiledPattern,
        int patternIndex,
        EnrichmentStringRecord record,
        ParsedHit parsedHit,
        TextWriter writer,
        RecordLineMap lineMap,
        List<CachedRegexMatch>? descriptors,
        bool captureDescriptors,
        int maximumDescriptors,
        CancellationToken cancellationToken
    )
    {
        long rows = 0;
        var descriptorOverflow = false;
        try
        {
            var matches = RegexOutputCore.CreateRecords(
                parsedHit,
                compiledPattern.Name,
                compiledPattern.Regex,
                regexOutput: true,
                record.SourceFile,
                GetEvidenceClass(record)
            );
            foreach (var match in matches)
            {
                if (captureDescriptors && !descriptorOverflow)
                {
                    if (descriptors!.Count >= maximumDescriptors)
                    {
                        descriptorOverflow = true;
                    }
                    else
                    {
                        descriptors.Add(
                            new CachedRegexMatch(
                                patternIndex,
                                match.DataStart,
                                match.DataLength
                            )
                        );
                    }
                }
                await WriteMatchAsync(
                    writer,
                    compiledPattern,
                    record,
                    match.DataFound,
                    match.DataStart,
                    match.DataLength,
                    lineMap,
                    cancellationToken
                );
                rows++;
            }
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new TimeoutException(
                $"Regex '{compiledPattern.Name}' timed out at enrichment record '{record.RecordId}'; output is incomplete.",
                ex
            );
        }
        return new PatternEvaluationResult(rows, descriptorOverflow);
    }

    private static async Task<CachedReplayResult> ReplayCachedPatternAsync(
        IReadOnlyList<CachedRegexMatch> descriptors,
        int descriptorIndex,
        int patternIndex,
        CompiledPattern compiledPattern,
        EnrichmentStringRecord record,
        TextWriter writer,
        RecordLineMap lineMap,
        CancellationToken cancellationToken
    )
    {
        long rows = 0;
        var text = record.Text!;
        while (
            descriptorIndex < descriptors.Count
            && descriptors[descriptorIndex].PatternIndex == patternIndex
        )
        {
            var descriptor = descriptors[descriptorIndex];
            if (
                descriptor.Start < 0
                || descriptor.Length < 0
                || descriptor.Start > text.Length - descriptor.Length
            )
            {
                throw new InvalidDataException(
                    "The run-local match cache contains an invalid match range."
                );
            }
            await WriteMatchAsync(
                writer,
                compiledPattern,
                record,
                text.Substring(descriptor.Start, descriptor.Length),
                descriptor.Start,
                descriptor.Length,
                lineMap,
                cancellationToken
            );
            descriptorIndex++;
            rows++;
        }
        return new CachedReplayResult(rows, descriptorIndex);
    }

    private static async Task WriteMatchAsync(
        TextWriter writer,
        CompiledPattern compiledPattern,
        EnrichmentStringRecord record,
        string match,
        int matchStart,
        int matchLength,
        RecordLineMap lineMap,
        CancellationToken cancellationToken
    )
    {
        var context = CreateContext(record.Text!, matchStart, matchLength);
        var outputRecord = new EnrichmentRegexMatchRecord
        {
            PatternName = compiledPattern.Name,
            Pattern = compiledPattern.Pattern,
            PatternDescription = compiledPattern.Description,
            PatternSource = compiledPattern.Source,
            PatternValidation = compiledPattern.Validation,
            Match = match,
            MatchStart = matchStart,
            MatchLength = matchLength,
            MatchLine = lineMap.GetLine(matchStart),
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

    private static List<CompiledPattern> CompilePatterns(
        IReadOnlyList<(string name, string pattern)> patterns
    )
    {
        var compiled = new List<CompiledPattern>(patterns.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, pattern) in patterns)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pattern) || !seen.Add(name))
            {
                continue;
            }
            if (BuiltInPatternCatalog.TryGetDefinition(name, pattern, out var definition))
            {
                compiled.Add(
                    new CompiledPattern(
                        name,
                        pattern,
                        RegexOutputCore.GetOrCreateRegex(name, pattern),
                        definition.Description,
                        definition.Source,
                        BuiltInPatternCatalog.GetValidationLabel(definition),
                        definition.Validation != BuiltInValidationKind.DateOfBirth
                    )
                );
            }
            else
            {
                compiled.Add(
                    new CompiledPattern(
                        name,
                        pattern,
                        RegexOutputCore.GetOrCreateRegex(name, pattern),
                        "User-supplied regular expression",
                        "user-supplied",
                        "custom-regex",
                        true
                    )
                );
            }
        }
        if (compiled.Count == 0)
        {
            throw new ArgumentException("No valid, unique regex patterns were supplied.", nameof(patterns));
        }
        return compiled;
    }

    internal static void ValidatePatterns(
        IReadOnlyList<(string name, string pattern)> patterns
    ) => _ = CompilePatterns(patterns);

    private sealed record CompiledPattern(
        string Name,
        string Pattern,
        Regex Regex,
        string Description,
        string Source,
        string Validation,
        bool Cacheable
    );

    private readonly record struct PatternEvaluationResult(
        long Rows,
        bool DescriptorOverflow
    );

    private readonly record struct CachedReplayResult(
        long Rows,
        int NextDescriptorIndex
    );

    private sealed class RecordLineMap(string text)
    {
        private int[]? _starts;

        internal int GetLine(int matchStart)
        {
            if (matchStart < 0 || matchStart > text.Length)
            {
                return 0;
            }
            if (_starts is null)
            {
                var starts = new List<int> { 0 };
                for (var index = 0; index < text.Length; index++)
                {
                    if (text[index] == '\n')
                    {
                        starts.Add(index + 1);
                    }
                }
                _starts = starts.ToArray();
            }
            var found = Array.BinarySearch(_starts, matchStart);
            return found >= 0 ? found + 1 : ~found;
        }
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
        if (record.Transform is not null && !IsTranslation(record) && !IsDecoding(record))
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} uses unsupported transform kind '{record.Transform.Kind}'."
            );
        }
        if (
            !IsTranslation(record)
            && !IsDecoding(record)
            && !string.IsNullOrWhiteSpace(record.ParentRecordId)
        )
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
        if (
            IsDecoding(record)
            && (
                string.IsNullOrWhiteSpace(record.ParentRecordId)
                || string.IsNullOrWhiteSpace(record.Transform?.Engine)
                || string.IsNullOrWhiteSpace(record.Transform?.EngineVersion)
                || string.IsNullOrWhiteSpace(record.Transform?.Profile)
                || string.IsNullOrWhiteSpace(record.Transform?.PolicyVersion)
                || !string.Equals(record.Transform?.Outcome, "decoded-text", StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidDataException(
                $"Decoding enrichment record at line {lineNumber:N0} must name its parentRecordId, engine, engineVersion, profile, policyVersion, and decoded-text outcome."
            );
        }
        if (IsDecoding(record))
        {
            ValidateDecodingRequirements(record, lineNumber);
        }
    }

    internal static bool IsTranslation(EnrichmentStringRecord record) =>
        string.Equals(record.Transform?.Kind, "translation", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDecoding(EnrichmentStringRecord record) =>
        string.Equals(record.Transform?.Kind, "decoding", StringComparison.Ordinal);

    private static void ValidateDecodingRequirements(EnrichmentStringRecord record, long lineNumber)
    {
        var transform = record.Transform!;
        if (!string.Equals(transform.Engine, "bstrings", StringComparison.Ordinal))
        {
            throw InvalidDecodingRecord(lineNumber, "transform.engine must be 'bstrings'");
        }
        if (
            !Version.TryParse(transform.EngineVersion, out var engineVersion)
            || engineVersion.Build < 0
            || engineVersion.Revision >= 0
            || !string.Equals(
                transform.EngineVersion,
                $"{engineVersion.Major}.{engineVersion.Minor}.{engineVersion.Build}",
                StringComparison.Ordinal
            )
        )
        {
            throw InvalidDecodingRecord(
                lineNumber,
                "transform.engineVersion must be a canonical major.minor.patch version"
            );
        }
        if (
            transform.Profile is not ("powershell-encoded-command-v1" or "rfc4648-base64-text-v1")
        )
        {
            throw InvalidDecodingRecord(lineNumber, "transform.profile is not supported");
        }
        if (!string.Equals(transform.PolicyVersion, "decoder-policy-v1", StringComparison.Ordinal))
        {
            throw InvalidDecodingRecord(
                lineNumber,
                "transform.policyVersion must be 'decoder-policy-v1'"
            );
        }
        if (
            !string.IsNullOrWhiteSpace(transform.Model)
            || !string.IsNullOrWhiteSpace(transform.Revision)
            || !string.IsNullOrWhiteSpace(transform.ModelSha256)
            || !string.IsNullOrWhiteSpace(transform.SourceLanguage)
            || !string.IsNullOrWhiteSpace(transform.TargetLanguage)
        )
        {
            throw InvalidDecodingRecord(
                lineNumber,
                "decoding transforms cannot claim model or language provenance"
            );
        }

        var attributes = record.Attributes
            ?? throw InvalidDecodingRecord(lineNumber, "attributes are required");
        RequireDecodingString(attributes, "decoder", lineNumber, value => value == "base64");
        RequireDecodingString(
            attributes,
            "decoderProfile",
            lineNumber,
            value => string.Equals(value, transform.Profile, StringComparison.Ordinal)
        );
        RequireDecodingString(
            attributes,
            "decoderPolicyVersion",
            lineNumber,
            value => string.Equals(value, transform.PolicyVersion, StringComparison.Ordinal)
        );
        var candidateStart = RequireDecodingInt32(attributes, "candidateStart", lineNumber, 0, int.MaxValue);
        var candidateLength = RequireDecodingInt32(
            attributes,
            "candidateLength",
            lineNumber,
            1,
            MaxDecoderCandidateCharacters
        );
        var outerWhitespaceTreatment = RequireDecodingString(
            attributes,
            "outerWhitespaceTreatment",
            lineNumber,
            value => value is "none" or "ascii-trim"
        );
        var leadingWhitespace = RequireDecodingInt32(
            attributes,
            "leadingWhitespaceCharacters",
            lineNumber,
            0,
            MaxDecoderCandidateCharacters
        );
        var trailingWhitespace = RequireDecodingInt32(
            attributes,
            "trailingWhitespaceCharacters",
            lineNumber,
            0,
            MaxDecoderCandidateCharacters
        );
        if (
            transform.Profile == "rfc4648-base64-text-v1"
            && candidateStart != leadingWhitespace
        )
        {
            throw InvalidDecodingRecord(
                lineNumber,
                "the whole-record profile requires candidateStart to equal leadingWhitespaceCharacters"
            );
        }
        if (outerWhitespaceTreatment == "none" && (leadingWhitespace != 0 || trailingWhitespace != 0))
        {
            throw InvalidDecodingRecord(
                lineNumber,
                "attributes.outerWhitespaceTreatment 'none' requires zero outer whitespace"
            );
        }

        var decodedByteLength = RequireDecodingInt32(
            attributes,
            "decodedByteLength",
            lineNumber,
            1,
            MaxDecoderBytesPerRecord
        );
        var decodedSha256 = RequireDecodingString(
            attributes,
            "decodedSha256",
            lineNumber,
            IsLowercaseSha256
        );
        var charset = RequireDecodingString(
            attributes,
            "decodedCharset",
            lineNumber,
            value =>
                value
                    is "utf-8"
                        or "utf-8-bom"
                        or "utf-16le-bom"
                        or "utf-16be-bom"
                        or "utf-16le-powershell"
        );
        if (
            transform.Profile == "powershell-encoded-command-v1"
            && charset != "utf-16le-powershell"
        )
        {
            throw InvalidDecodingRecord(
                lineNumber,
                "the PowerShell profile requires decodedCharset 'utf-16le-powershell'"
            );
        }
        if (
            transform.Profile == "rfc4648-base64-text-v1"
            && charset == "utf-16le-powershell"
        )
        {
            throw InvalidDecodingRecord(
                lineNumber,
                "the generic Base64 profile cannot claim the PowerShell-only charset"
            );
        }
        _ = RequireDecodingInt32(attributes, "decodeDepth", lineNumber, 1, 1);
        var maxCandidateCharacters = RequireDecodingInt32(
            attributes,
            "maxCandidateCharacters",
            lineNumber,
            1,
            MaxDecoderCandidateCharacters
        );
        var maxDecodedBytesPerRecord = RequireDecodingInt32(
            attributes,
            "maxDecodedBytesPerRecord",
            lineNumber,
            1,
            MaxDecoderBytesPerRecord
        );
        _ = RequireDecodingInt32(
            attributes,
            "maxAttemptedCandidates",
            lineNumber,
            1,
            MaxDecoderCandidates
        );
        var maxTotalDecodedBytes = RequireDecodingInt64(
            attributes,
            "maxTotalDecodedBytes",
            lineNumber,
            1,
            MaxDecoderTotalBytes
        );
        if (candidateLength > maxCandidateCharacters)
        {
            throw InvalidDecodingRecord(lineNumber, "candidateLength exceeds its recorded limit");
        }
        if (decodedByteLength > maxDecodedBytesPerRecord || decodedByteLength > maxTotalDecodedBytes)
        {
            throw InvalidDecodingRecord(lineNumber, "decodedByteLength exceeds its recorded limit");
        }

        var reconstructedBytes = EncodeDecodedText(record.Text!, charset);
        if (reconstructedBytes.Length != decodedByteLength)
        {
            throw InvalidDecodingRecord(
                lineNumber,
                "decodedByteLength does not match the decoded child text"
            );
        }
        var actualSha256 = Convert.ToHexString(SHA256.HashData(reconstructedBytes)).ToLowerInvariant();
        if (!string.Equals(actualSha256, decodedSha256, StringComparison.Ordinal))
        {
            throw InvalidDecodingRecord(lineNumber, "decodedSha256 does not match the decoded child text");
        }
    }

    private static byte[] EncodeDecodedText(string text, string charset)
    {
        Encoding encoding = charset switch
        {
            "utf-8" or "utf-8-bom" => new UTF8Encoding(false, true),
            "utf-16le-bom" or "utf-16le-powershell" => new UnicodeEncoding(false, false, true),
            "utf-16be-bom" => new UnicodeEncoding(true, false, true),
            _ => throw new InvalidOperationException("The decoded charset was not validated."),
        };
        var content = encoding.GetBytes(text);
        byte[] preamble = charset switch
        {
            "utf-8-bom" => new byte[] { 0xEF, 0xBB, 0xBF },
            "utf-16le-bom" => new byte[] { 0xFF, 0xFE },
            "utf-16be-bom" => new byte[] { 0xFE, 0xFF },
            _ => [],
        };
        if (preamble.Length == 0)
        {
            return content;
        }
        var result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0);
        content.CopyTo(result, preamble.Length);
        return result;
    }

    private static string RequireDecodingString(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        long lineNumber,
        Func<string, bool> predicate
    )
    {
        if (
            !attributes.TryGetValue(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } value
            || !predicate(value)
        )
        {
            throw InvalidDecodingRecord(lineNumber, $"attributes.{name} is missing or invalid");
        }
        return value;
    }

    private static int RequireDecodingInt32(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        long lineNumber,
        int minimum,
        int maximum
    )
    {
        if (
            !attributes.TryGetValue(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value < minimum
            || value > maximum
        )
        {
            throw InvalidDecodingRecord(lineNumber, $"attributes.{name} is missing or out of range");
        }
        return value;
    }

    private static long RequireDecodingInt64(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        long lineNumber,
        long minimum,
        long maximum
    )
    {
        if (
            !attributes.TryGetValue(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var value)
            || value < minimum
            || value > maximum
        )
        {
            throw InvalidDecodingRecord(lineNumber, $"attributes.{name} is missing or out of range");
        }
        return value;
    }

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static InvalidDataException InvalidDecodingRecord(long lineNumber, string reason) =>
        new($"Decoding enrichment record at line {lineNumber:N0} is invalid: {reason}.");

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
        if (IsDecoding(record))
        {
            return "derived-decoding";
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

    private enum TrustedRecordSection
    {
        Originals,
        Translations,
        Decoding,
    }
}
