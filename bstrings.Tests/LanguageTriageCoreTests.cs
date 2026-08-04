using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class LanguageTriageCoreTests
{
    [Fact]
    public void ShouldFlushBeforeAdding_BoundsBothRecordCountAndUtf8Bytes()
    {
        Assert.True(
            LanguageTriageCore.ShouldFlushBeforeAdding(
                pendingCount: 2,
                pendingUtf8Bytes: 100,
                nextRecordUtf8Bytes: 10,
                maximumRecords: 2,
                maximumUtf8Bytes: 1_000
            )
        );
        Assert.True(
            LanguageTriageCore.ShouldFlushBeforeAdding(
                pendingCount: 1,
                pendingUtf8Bytes: 900,
                nextRecordUtf8Bytes: 101,
                maximumRecords: 2_048,
                maximumUtf8Bytes: 1_000
            )
        );
        Assert.False(
            LanguageTriageCore.ShouldFlushBeforeAdding(
                pendingCount: 1,
                pendingUtf8Bytes: 900,
                nextRecordUtf8Bytes: 100,
                maximumRecords: 2_048,
                maximumUtf8Bytes: 1_000
            )
        );
        Assert.False(
            LanguageTriageCore.ShouldFlushBeforeAdding(
                pendingCount: 0,
                pendingUtf8Bytes: 0,
                nextRecordUtf8Bytes: 10_000,
                maximumRecords: 2_048,
                maximumUtf8Bytes: 1_000
            )
        );
    }

    [Fact]
    public async Task ProcessAsync_SeparatesTargetAndNonTargetLanguages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("english", "This sentence is already written in English."),
                CreateRecord("spanish", "Esta frase contiene evidencia importante en espanol."),
            ],
            cancellationToken
        );

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            var language = text.StartsWith("This", StringComparison.Ordinal) ? "en" : "es";
            var targetConfidence = language == targetLanguage ? 0.97 : 0.02;
            result = new LanguageDetectionResult(language, 0.97, targetConfidence, 0.02, false);
            error = null;
            return true;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            CreateOptions(),
            cancellationToken,
            detector
        );

        Assert.Equal(
            new LanguageTriageStats(2, 1, 1, 0, 0, 0, LanguageDetectionMode.Accurate),
            stats
        );
        var candidate = Assert.Single(
            await File.ReadAllLinesAsync(candidatesPath, cancellationToken)
        );
        using (var candidateJson = JsonDocument.Parse(candidate))
        {
            Assert.Equal("spanish", candidateJson.RootElement.GetProperty("recordId").GetString());
        }
        Assert.Equal(
            ["target-language", "translate"],
            await ReadDecisionsAsync(assessmentsPath, cancellationToken)
        );
    }

    [Fact]
    public async Task ProcessAsync_UsesUnicodeScalarLengthsForEligibility()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        var fourDeseretLetters = "\U00010400\U00010401\U00010402\U00010403";
        await File.WriteAllTextAsync(
            inputPath,
            CreateRecord("astral-letters", fourDeseretLetters) + Environment.NewLine,
            cancellationToken
        );

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            CreateOptions() with { MaximumCharacters = 4 },
            cancellationToken,
            AlwaysSpanish
        );

        Assert.Equal(1, stats.TranslationCandidates);
        Assert.Single(await File.ReadAllLinesAsync(candidatesPath, cancellationToken));
    }

    [Fact]
    public async Task ProcessAsync_HighRecallKeepsAmbiguousAndFailedDetections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("ambiguous", "Esta frase breve puede ser ambigua para el detector."),
                CreateRecord("failed", "Detector failure material must not be silently discarded."),
            ],
            cancellationToken
        );

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            if (text.StartsWith("Detector", StringComparison.Ordinal))
            {
                result = default;
                error = "synthetic detector failure";
                return false;
            }
            result = new LanguageDetectionResult("es", 0.45, 0.40, 0.40, false);
            error = null;
            return true;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            CreateOptions(policy: LanguageTriagePolicy.HighRecall),
            cancellationToken,
            detector
        );

        Assert.Equal(2, stats.TranslationCandidates);
        Assert.Equal(0, stats.AmbiguousRecords);
        Assert.Equal(1, stats.DetectorFailures);
        Assert.Equal(2, (await File.ReadAllLinesAsync(candidatesPath, cancellationToken)).Length);
        Assert.Equal(
            ["translate", "detector-failed"],
            await ReadDecisionsAsync(assessmentsPath, cancellationToken)
        );
    }

    [Fact]
    public async Task ProcessAsync_HighRecallKeepsUncertainTargetDetectionsForTranslation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("low-confidence", "This target-language result has low confidence."),
                CreateRecord("low-margin", "This target-language result has a weak runner-up margin."),
            ],
            cancellationToken
        );

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            result = text.Contains("low confidence", StringComparison.Ordinal)
                ? new LanguageDetectionResult("en", 0.50, 0.50, 0.20, false)
                : new LanguageDetectionResult("en", 0.90, 0.90, 0.85, false);
            error = null;
            return true;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            CreateOptions(policy: LanguageTriagePolicy.HighRecall),
            cancellationToken,
            detector
        );

        Assert.Equal(0, stats.TargetLanguageRecords);
        Assert.Equal(2, stats.AmbiguousRecords);
        Assert.Equal(2, stats.TranslationCandidates);
        Assert.Equal(2, (await File.ReadAllLinesAsync(candidatesPath, cancellationToken)).Length);
        Assert.Equal(
            ["ambiguous", "ambiguous"],
            await ReadDecisionsAsync(assessmentsPath, cancellationToken)
        );
    }

    [Fact]
    public async Task ProcessAsync_BalancedPolicyReportsAmbiguousAndFailedDetections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("ambiguous", "Esta frase breve puede ser ambigua para el detector."),
                CreateRecord("failed", "Detector failure material should be recorded explicitly."),
            ],
            cancellationToken
        );

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            if (text.StartsWith("Detector", StringComparison.Ordinal))
            {
                result = default;
                error = "synthetic detector failure";
                return false;
            }
            result = new LanguageDetectionResult("es", 0.45, 0.40, 0.40, false);
            error = null;
            return true;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            CreateOptions(),
            cancellationToken,
            detector
        );

        Assert.Equal(0, stats.TranslationCandidates);
        Assert.Equal(1, stats.AmbiguousRecords);
        Assert.Equal(1, stats.DetectorFailures);
        Assert.Empty(await File.ReadAllLinesAsync(candidatesPath, cancellationToken));
        Assert.Equal(
            ["ambiguous", "detector-failed"],
            await ReadDecisionsAsync(assessmentsPath, cancellationToken)
        );
    }

    [Fact]
    public async Task ProcessAsync_UsesTheConfiguredTargetLanguage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("french", "Cette phrase contient deja du texte francais important."),
                CreateRecord("english", "This sentence still needs translation into French."),
            ],
            cancellationToken
        );
        var observedTargets = new ConcurrentBag<string>();

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            observedTargets.Add(targetLanguage);
            var language = text.StartsWith("Cette", StringComparison.Ordinal) ? "fr" : "en";
            result = new LanguageDetectionResult(
                language,
                0.98,
                language == targetLanguage ? 0.98 : 0.01,
                0.01,
                false
            );
            error = null;
            return true;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            CreateOptions(targetLanguage: "fr"),
            cancellationToken,
            detector
        );

        Assert.Equal(1, stats.TargetLanguageRecords);
        Assert.Equal(1, stats.TranslationCandidates);
        Assert.Equal(2, observedTargets.Count);
        Assert.All(observedTargets, target => Assert.Equal("fr", target));
    }

    [Fact]
    public async Task ProcessAsync_NormalizesBcp47ForDetectionAndPreservesTranslationTarget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("chinese", "这段文字包含需要保留的重要取证信息。"),
                CreateRecord("english", "This evidence needs translation into Traditional Chinese."),
            ],
            cancellationToken
        );
        var observedTargets = new ConcurrentBag<string>();

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            observedTargets.Add(targetLanguage);
            var language = text.StartsWith("这", StringComparison.Ordinal) ? "zh" : "en";
            result = new LanguageDetectionResult(
                language,
                0.98,
                language == targetLanguage ? 0.98 : 0.01,
                0.01,
                false
            );
            error = null;
            return true;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            CreateOptions(targetLanguage: "zh-Hant"),
            cancellationToken,
            detector
        );

        Assert.Equal(1, stats.TargetLanguageRecords);
        Assert.Equal(1, stats.TranslationCandidates);
        Assert.All(observedTargets, target => Assert.Equal("zh", target));
        var assessments = await File.ReadAllLinesAsync(assessmentsPath, cancellationToken);
        using var assessment = JsonDocument.Parse(assessments[0]);
        Assert.Equal(
            "zh-Hant",
            assessment.RootElement.GetProperty("targetLanguage").GetString()
        );
        Assert.Equal(
            "zh",
            assessment.RootElement.GetProperty("detectorTargetLanguage").GetString()
        );
    }

    [Fact]
    public async Task ProcessAsync_AdaptiveModeUsesTheFullAvailableSample()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var mixedModes = await RunAdaptiveCaseAsync(
            [
                new string('A', 130),
                new string('B', 130),
                "short language sample three",
                "short language sample four",
                "short language sample five",
                "short language sample six",
                "short language sample seven",
                "short language sample eight",
                "short language sample nine",
                "short language sample ten",
            ],
            cancellationToken
        );
        Assert.Equal(LanguageDetectionMode.Accurate, mixedModes.Stats.EffectiveMode);
        Assert.All(mixedModes.ObservedModes, mode => Assert.Equal(LanguageDetectionMode.Accurate, mode));

        var longModes = await RunAdaptiveCaseAsync(
            Enumerable.Range(0, 5).Select(index => new string((char)('A' + index), 130)),
            cancellationToken
        );
        Assert.Equal(LanguageDetectionMode.Fast, longModes.Stats.EffectiveMode);
        Assert.All(longModes.ObservedModes, mode => Assert.Equal(LanguageDetectionMode.Fast, mode));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"x\",\"text\":\"linguistic evidence\",\"sourceFile\":\"sample.bin\",\"location\":null,\"origin\":{\"extractor\":\"bstrings\",\"kind\":\"static\"}}")]
    [InlineData("{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"x\",\"text\":\"linguistic evidence\",\"sourceFile\":\"sample.bin\",\"location\":{\"kind\":\"file_offset\",\"value\":\"0x0\"}}")]
    [InlineData("{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"x\",\"text\":\"linguistic evidence\",\"sourceFile\":\"sample.bin\",\"location\":{\"kind\":\"file_offset\",\"value\":\"\"},\"origin\":{\"extractor\":\"bstrings\",\"kind\":\"static\"}}")]
    [InlineData("{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"x\",\"text\":\"linguistic evidence\",\"sourceFile\":\"sample.bin\",\"location\":{\"kind\":\"file_offset\",\"value\":\"0x0\"},\"origin\":{\"extractor\":\"bstrings\",\"kind\":\"static\"},\"parentRecordId\":\"raw\",\"transform\":{}}")]
    public async Task ProcessAsync_RejectsMalformedProvenance(string invalidRecord)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("invalid.jsonl");
        await File.WriteAllTextAsync(inputPath, invalidRecord, cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanguageTriageCore.ProcessAsync(
                inputPath,
                scope.PathFor("candidates.jsonl"),
                scope.PathFor("assessments.jsonl"),
                CreateOptions(),
                cancellationToken,
                AlwaysEnglish
            )
        );

        Assert.Contains("provenance", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    [Fact]
    public async Task ProcessAsync_PreservesBothOutputsWhenALaterRecordIsInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("valid", "Esta evidencia requiere traduccion antes de buscarla."),
                "{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"broken\",\"text\":\"broken provenance\",\"sourceFile\":\"sample.bin\",\"location\":null}",
            ],
            cancellationToken
        );
        await File.WriteAllTextAsync(candidatesPath, "previous-candidates", cancellationToken);
        await File.WriteAllTextAsync(assessmentsPath, "previous-assessments", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanguageTriageCore.ProcessAsync(
                inputPath,
                candidatesPath,
                assessmentsPath,
                CreateOptions(batchSize: 1),
                cancellationToken,
                AlwaysSpanish
            )
        );

        Assert.Equal(
            "previous-candidates",
            await File.ReadAllTextAsync(candidatesPath, cancellationToken)
        );
        Assert.Equal(
            "previous-assessments",
            await File.ReadAllTextAsync(assessmentsPath, cancellationToken)
        );
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    [Fact]
    public async Task ProcessAsync_RestoresTheFirstOutputWhenTheSecondCommitFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var blockedAssessmentsPath = scope.PathFor("blocked-assessments");
        await File.WriteAllTextAsync(
            inputPath,
            CreateRecord("spanish", "Esta evidencia importante necesita traduccion."),
            cancellationToken
        );
        await File.WriteAllTextAsync(candidatesPath, "previous-candidates", cancellationToken);
        Directory.CreateDirectory(blockedAssessmentsPath);

        await Assert.ThrowsAsync<IOException>(() =>
            LanguageTriageCore.ProcessAsync(
                inputPath,
                candidatesPath,
                blockedAssessmentsPath,
                CreateOptions(),
                cancellationToken,
                AlwaysSpanish
            )
        );

        Assert.Equal(
            "previous-candidates",
            await File.ReadAllTextAsync(candidatesPath, cancellationToken)
        );
        Assert.True(Directory.Exists(blockedAssessmentsPath));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.backup.*"));
    }

    private static LanguageTriageOptions CreateOptions(
        string targetLanguage = "en",
        LanguageDetectionMode mode = LanguageDetectionMode.Accurate,
        LanguageTriagePolicy policy = LanguageTriagePolicy.Balanced,
        int batchSize = 2
    ) =>
        new(
            targetLanguage,
            mode,
            policy,
            MinimumConfidence: 0.80,
            MinimumTargetMargin: 0.20,
            MinimumCharacters: 4,
            MaximumCharacters: 4096,
            batchSize,
            MaxDegreeOfParallelism: 2
        );

    private static string CreateRecord(string recordId, string text) =>
        JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId,
                text,
                sourceFile = "sample.bin",
                location = new { kind = "file_offset", value = "0x10" },
                origin = new { extractor = "bstrings", version = "test", kind = "static" },
            }
        );

    private static async Task<string[]> ReadDecisionsAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var decisions = new string[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            using var row = JsonDocument.Parse(lines[index]);
            decisions[index] = row.RootElement.GetProperty("decision").GetString()!;
        }
        return decisions;
    }

    private static async Task<(
        LanguageTriageStats Stats,
        LanguageDetectionMode[] ObservedModes
    )> RunAdaptiveCaseAsync(IEnumerable<string> texts, CancellationToken cancellationToken)
    {
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            texts.Select((text, index) => CreateRecord($"record-{index}", text)),
            cancellationToken
        );
        var observedModes = new ConcurrentBag<LanguageDetectionMode>();
        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            observedModes.Add(mode);
            result = new LanguageDetectionResult("en", 0.99, 0.99, 0.01, mode == LanguageDetectionMode.Fast);
            error = null;
            return true;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            scope.PathFor("candidates.jsonl"),
            scope.PathFor("assessments.jsonl"),
            CreateOptions(mode: LanguageDetectionMode.Adaptive),
            cancellationToken,
            detector
        );
        return (stats, observedModes.ToArray());
    }

    private static bool AlwaysEnglish(
        string text,
        LanguageDetectionMode mode,
        string targetLanguage,
        out LanguageDetectionResult result,
        out string? error
    )
    {
        result = new LanguageDetectionResult("en", 0.99, 0.99, 0.01, false);
        error = null;
        return true;
    }

    private static bool AlwaysSpanish(
        string text,
        LanguageDetectionMode mode,
        string targetLanguage,
        out LanguageDetectionResult result,
        out string? error
    )
    {
        result = new LanguageDetectionResult("es", 0.99, 0.01, 0.01, false);
        error = null;
        return true;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-language-triage-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string PathFor(string name) => Path.Combine(DirectoryPath, name);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
