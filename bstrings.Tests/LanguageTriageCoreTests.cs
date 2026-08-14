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

        Assert.Equal(2, stats.InputRecords);
        Assert.Equal(1, stats.TargetLanguageRecords);
        Assert.Equal(1, stats.TranslationCandidates);
        Assert.Equal(LanguageDetectionMode.Accurate, stats.EffectiveMode);
        Assert.Equal(TranslationWorthinessRouter.PolicyVersion, stats.TranslationRoutingPolicyVersion);
        Assert.Equal(2, stats.TranslationRoutingRetained);
        Assert.Equal(0, stats.TranslationRoutingProspectiveBypasses);
        Assert.Equal(0, stats.TranslationRoutingUnknown);
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
    public void NormalizeAssessmentMetric_UsesDeclaredMidpointPolicyAndCanonicalZero()
    {
        Assert.Null(LanguageTriageCore.NormalizeAssessmentMetric(null));
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(LanguageTriageCore.NormalizeAssessmentMetric(-0d)!.Value));
        Assert.Equal(0.123456789012, LanguageTriageCore.NormalizeAssessmentMetric(0.1234567890125));
        Assert.Equal(0.123456789014, LanguageTriageCore.NormalizeAssessmentMetric(0.1234567890135));
    }

    [Fact]
    public async Task ProcessAsync_ReusesSuccessfulDetectionOncePerBatchLocalOrdinalText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        const string duplicateText = "This duplicate linguistic evidence is already English.";
        await File.WriteAllLinesAsync(
            inputPath,
            Enumerable
                .Range(0, 3)
                .Select(index => CreateRecord($"duplicate-{index}", duplicateText)),
            cancellationToken
        );
        var detectorCalls = 0;

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            Interlocked.Increment(ref detectorCalls);
            result = new LanguageDetectionResult("en", 0.99, 0.99, 0.01, false);
            error = null;
            return true;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            CreateOptions(batchSize: 3),
            cancellationToken,
            detector,
            reuseSuccessfulDetections: true
        );

        Assert.Equal(1, detectorCalls);
        Assert.Equal(3, stats.TargetLanguageRecords);
        Assert.Equal(1, stats.TranslationRoutingEvaluations);
        Assert.Equal(3, stats.DetectorEligibleRecords);
        Assert.Equal(1, stats.DetectorExecutions);
        Assert.Equal(2, stats.DetectorReuseHits);
        Assert.Equal(
            ["target-language", "target-language", "target-language"],
            await ReadDecisionsAsync(assessmentsPath, cancellationToken)
        );
    }

    [Fact]
    public async Task ProcessAsync_RetriesFailedDuplicateDetectionsInInputOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        const string duplicateText = "This duplicate linguistic evidence always fails detection.";
        await File.WriteAllLinesAsync(
            inputPath,
            Enumerable
                .Range(0, 3)
                .Select(index => CreateRecord($"failure-{index}", duplicateText)),
            cancellationToken
        );
        var detectorCalls = 0;

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            var call = ++detectorCalls;
            result = default;
            error = $"synthetic failure {call}";
            return false;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            scope.PathFor("candidates.jsonl"),
            assessmentsPath,
            CreateOptions(batchSize: 3),
            cancellationToken,
            detector,
            reuseSuccessfulDetections: true
        );

        Assert.Equal(3, detectorCalls);
        Assert.Equal(3, stats.DetectorFailures);
        Assert.Equal(1, stats.TranslationRoutingEvaluations);
        Assert.Equal(3, stats.DetectorEligibleRecords);
        Assert.Equal(3, stats.DetectorExecutions);
        Assert.Equal(0, stats.DetectorReuseHits);
        Assert.Equal(
            ["synthetic failure 1", "synthetic failure 2", "synthetic failure 3"],
            await ReadErrorsAsync(assessmentsPath, cancellationToken)
        );
    }

    [Fact]
    public async Task ProcessAsync_InjectedDetectorDoesNotReuseByDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        const string duplicateText = "This injected detector evidence remains independently assessed.";
        await File.WriteAllLinesAsync(
            inputPath,
            Enumerable
                .Range(0, 3)
                .Select(index => CreateRecord($"injected-{index}", duplicateText)),
            cancellationToken
        );
        var detectorCalls = 0;

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            Interlocked.Increment(ref detectorCalls);
            result = new LanguageDetectionResult("en", 0.99, 0.99, 0.01, false);
            error = null;
            return true;
        };

        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            scope.PathFor("candidates.jsonl"),
            scope.PathFor("assessments.jsonl"),
            CreateOptions(batchSize: 3),
            cancellationToken,
            detector
        );

        Assert.Equal(3, detectorCalls);
        Assert.Equal(1, stats.TranslationRoutingEvaluations);
        Assert.Equal(3, stats.DetectorEligibleRecords);
        Assert.Equal(3, stats.DetectorExecutions);
        Assert.Equal(0, stats.DetectorReuseHits);
    }

    [Fact]
    public async Task ProcessAsync_ReuseProducesByteIdenticalOutputsAndStats()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("spanish-1", "Esta evidencia duplicada necesita traduccion."),
                CreateRecord("english-1", "This duplicate evidence is already in English."),
                CreateRecord("spanish-2", "Esta evidencia duplicada necesita traduccion."),
                CreateRecord("english-2", "This duplicate evidence is already in English."),
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
            var language = text.StartsWith("Esta", StringComparison.Ordinal) ? "es" : "en";
            result = new LanguageDetectionResult(
                language,
                0.99,
                language == targetLanguage ? 0.99 : 0.01,
                0.01,
                false
            );
            error = null;
            return true;
        };
        var candidatesWithoutReuse = scope.PathFor("candidates-without-reuse.jsonl");
        var assessmentsWithoutReuse = scope.PathFor("assessments-without-reuse.jsonl");
        var candidatesWithReuse = scope.PathFor("candidates-with-reuse.jsonl");
        var assessmentsWithReuse = scope.PathFor("assessments-with-reuse.jsonl");

        var statsWithoutReuse = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesWithoutReuse,
            assessmentsWithoutReuse,
            CreateOptions(batchSize: 4),
            cancellationToken,
            detector,
            reuseSuccessfulDetections: false
        );
        var statsWithReuse = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesWithReuse,
            assessmentsWithReuse,
            CreateOptions(batchSize: 4),
            cancellationToken,
            detector,
            reuseSuccessfulDetections: true
        );

        Assert.Equal(statsWithoutReuse.InputRecords, statsWithReuse.InputRecords);
        Assert.Equal(
            statsWithoutReuse.TargetLanguageRecords,
            statsWithReuse.TargetLanguageRecords
        );
        Assert.Equal(
            statsWithoutReuse.TranslationCandidates,
            statsWithReuse.TranslationCandidates
        );
        Assert.Equal(statsWithoutReuse.AmbiguousRecords, statsWithReuse.AmbiguousRecords);
        Assert.Equal(
            statsWithoutReuse.NonLinguisticRecords,
            statsWithReuse.NonLinguisticRecords
        );
        Assert.Equal(statsWithoutReuse.DetectorFailures, statsWithReuse.DetectorFailures);
        Assert.Equal(statsWithoutReuse.EffectiveMode, statsWithReuse.EffectiveMode);
        Assert.Equal(
            statsWithoutReuse.TranslationRoutingEvaluations,
            statsWithReuse.TranslationRoutingEvaluations
        );
        Assert.Equal(2, statsWithReuse.TranslationRoutingEvaluations);
        Assert.Equal(4, statsWithoutReuse.DetectorExecutions);
        Assert.Equal(0, statsWithoutReuse.DetectorReuseHits);
        Assert.Equal(2, statsWithReuse.DetectorExecutions);
        Assert.Equal(2, statsWithReuse.DetectorReuseHits);
        Assert.Equal(
            await File.ReadAllBytesAsync(candidatesWithoutReuse, cancellationToken),
            await File.ReadAllBytesAsync(candidatesWithReuse, cancellationToken)
        );
        Assert.Equal(
            await ReadAssessmentsWithoutRoutingAsync(assessmentsWithoutReuse, cancellationToken),
            await ReadAssessmentsWithoutRoutingAsync(assessmentsWithReuse, cancellationToken)
        );
        Assert.Equal(
            await ReadRoutingCodesAsync(assessmentsWithoutReuse, cancellationToken),
            await ReadRoutingCodesAsync(assessmentsWithReuse, cancellationToken)
        );
    }

    [Fact]
    public async Task ProcessAsync_CanonicalScoresRemoveDetectorTailJitter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        const string duplicateText =
            "This repeated linguistic evidence exercises floating point stability.";
        await File.WriteAllLinesAsync(
            inputPath,
            Enumerable
                .Range(0, 2)
                .Select(index => CreateRecord($"jitter-{index}", duplicateText)),
            cancellationToken
        );

        static LanguageDetectionHandler CreateJitteringDetector()
        {
            var calls = 0;
            return (
                string text,
                LanguageDetectionMode mode,
                string targetLanguage,
                out LanguageDetectionResult result,
                out string? error
            ) =>
            {
                var confidence =
                    Interlocked.Increment(ref calls) == 1
                        ? 0.9999996642380474
                        : 0.9999996642380476;
                result = new LanguageDetectionResult("en", confidence, confidence, 0.10, false);
                error = null;
                return true;
            };
        }

        var baselineCandidates = scope.PathFor("baseline-candidates.jsonl");
        var baselineAssessments = scope.PathFor("baseline-assessments.jsonl");
        var reuseCandidates = scope.PathFor("reuse-candidates.jsonl");
        var reuseAssessments = scope.PathFor("reuse-assessments.jsonl");
        var options = CreateOptions(batchSize: 2) with { MaxDegreeOfParallelism = 1 };

        var baselineStats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            baselineCandidates,
            baselineAssessments,
            options,
            cancellationToken,
            CreateJitteringDetector(),
            reuseSuccessfulDetections: false
        );
        var reuseStats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            reuseCandidates,
            reuseAssessments,
            options,
            cancellationToken,
            CreateJitteringDetector(),
            reuseSuccessfulDetections: true
        );

        Assert.Equal(
            baselineStats with
            {
                DetectorExecutions = reuseStats.DetectorExecutions,
                DetectorReuseHits = reuseStats.DetectorReuseHits,
            },
            reuseStats
        );
        Assert.Equal(2, baselineStats.DetectorExecutions);
        Assert.Equal(0, baselineStats.DetectorReuseHits);
        Assert.Equal(1, reuseStats.DetectorExecutions);
        Assert.Equal(1, reuseStats.DetectorReuseHits);
        Assert.Equal(
            await File.ReadAllBytesAsync(baselineCandidates, cancellationToken),
            await File.ReadAllBytesAsync(reuseCandidates, cancellationToken)
        );
        Assert.Equal(
            await ReadAssessmentsWithoutRoutingAsync(baselineAssessments, cancellationToken),
            await ReadAssessmentsWithoutRoutingAsync(reuseAssessments, cancellationToken)
        );
        Assert.Equal(
            await ReadRoutingCodesAsync(baselineAssessments, cancellationToken),
            await ReadRoutingCodesAsync(reuseAssessments, cancellationToken)
        );
    }

    [Fact]
    public async Task ProcessAsync_RawScoresDriveGatesWhenDisplayedScoresMatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("confidence-below", "Confidence below the policy gate in Spanish evidence."),
                CreateRecord("confidence-above", "Confidence above the policy gate in Spanish evidence."),
                CreateRecord("margin-below", "Margin below the policy gate in Spanish evidence."),
                CreateRecord("margin-above", "Margin above the policy gate in Spanish evidence."),
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
            result = text switch
            {
                var value when value.StartsWith("Confidence below", StringComparison.Ordinal) =>
                    new LanguageDetectionResult("es", 0.7999999999998, 0.10, 0.10, false),
                var value when value.StartsWith("Confidence above", StringComparison.Ordinal) =>
                    new LanguageDetectionResult("es", 0.8000000000002, 0.10, 0.10, false),
                var value when value.StartsWith("Margin below", StringComparison.Ordinal) =>
                    new LanguageDetectionResult("es", 0.90, 0.7000000000002, 0.10, false),
                _ => new LanguageDetectionResult("es", 0.90, 0.6999999999998, 0.10, false),
            };
            error = null;
            return true;
        };

        await LanguageTriageCore.ProcessAsync(
            inputPath,
            scope.PathFor("candidates.jsonl"),
            assessmentsPath,
            CreateOptions(batchSize: 4),
            cancellationToken,
            detector,
            reuseSuccessfulDetections: true
        );

        var lines = await File.ReadAllLinesAsync(assessmentsPath, cancellationToken);
        Assert.Equal(4, lines.Length);
        var decisions = new string[lines.Length];
        var confidenceGates = new bool[lines.Length];
        var marginGates = new bool[lines.Length];
        var confidences = new double[lines.Length];
        var margins = new double[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            using var row = JsonDocument.Parse(lines[index]);
            var root = row.RootElement;
            decisions[index] = root.GetProperty("decision").GetString()!;
            confidenceGates[index] = root.GetProperty("confidenceGatePassed").GetBoolean();
            marginGates[index] = root.GetProperty("marginGatePassed").GetBoolean();
            confidences[index] = root.GetProperty("confidence").GetDouble();
            margins[index] = root.GetProperty("targetMargin").GetDouble();
            Assert.Equal(
                LanguageTriageCore.AssessmentScoreDecimalPlaces,
                root.GetProperty("scoreDecimalPlaces").GetInt32()
            );
        }

        Assert.Equal(["ambiguous", "translate", "ambiguous", "translate"], decisions);
        Assert.Equal([false, true, true, true], confidenceGates);
        Assert.Equal([true, true, false, true], marginGates);
        Assert.Equal(confidences[0], confidences[1]);
        Assert.Equal(margins[2], margins[3]);
    }

    [Fact]
    public async Task ProcessAsync_HighPrecisionAppliesFloorsUsingRawScores()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("confidence-below", "Confidence is just below the precision floor."),
                CreateRecord("confidence-above", "Confidence is just above the precision floor."),
                CreateRecord("margin-below", "Target margin is just below the precision floor."),
                CreateRecord("margin-above", "Target margin is just above the precision floor."),
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
            result = text switch
            {
                var value when value.StartsWith("Confidence is just below", StringComparison.Ordinal) =>
                    new LanguageDetectionResult("es", 0.6499999999998, 0.10, 0.10, false),
                var value when value.StartsWith("Confidence is just above", StringComparison.Ordinal) =>
                    new LanguageDetectionResult("es", 0.6500000000002, 0.10, 0.10, false),
                var value when value.StartsWith("Target margin is just below", StringComparison.Ordinal) =>
                    new LanguageDetectionResult("es", 0.90, 0.7500000000002, 0.10, false),
                _ => new LanguageDetectionResult("es", 0.90, 0.7499999999998, 0.10, false),
            };
            error = null;
            return true;
        };

        var options = CreateOptions(policy: LanguageTriagePolicy.HighPrecision, batchSize: 4) with
        {
            MinimumConfidence = 0.55,
            MinimumTargetMargin = 0.10,
        };
        await LanguageTriageCore.ProcessAsync(
            inputPath,
            scope.PathFor("candidates.jsonl"),
            assessmentsPath,
            options,
            cancellationToken,
            detector,
            reuseSuccessfulDetections: true
        );

        var lines = await File.ReadAllLinesAsync(assessmentsPath, cancellationToken);
        Assert.Equal(4, lines.Length);
        var decisions = new string[lines.Length];
        var confidenceGates = new bool[lines.Length];
        var marginGates = new bool[lines.Length];
        var displayedConfidences = new double[lines.Length];
        var displayedMargins = new double[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            using var row = JsonDocument.Parse(lines[index]);
            var root = row.RootElement;
            decisions[index] = root.GetProperty("decision").GetString()!;
            confidenceGates[index] = root.GetProperty("confidenceGatePassed").GetBoolean();
            marginGates[index] = root.GetProperty("marginGatePassed").GetBoolean();
            displayedConfidences[index] = root.GetProperty("confidence").GetDouble();
            displayedMargins[index] = root.GetProperty("targetMargin").GetDouble();
            Assert.Equal(0.55, root.GetProperty("configuredMinimumConfidence").GetDouble());
            Assert.Equal(0.10, root.GetProperty("configuredMinimumTargetMargin").GetDouble());
            Assert.Equal(0.65, root.GetProperty("effectiveMinimumConfidence").GetDouble());
            Assert.Equal(0.15, root.GetProperty("effectiveMinimumTargetMargin").GetDouble());
            Assert.Equal(0.65, root.GetProperty("minimumConfidence").GetDouble());
            Assert.Equal(0.15, root.GetProperty("minimumTargetMargin").GetDouble());
            Assert.Equal("high-precision", root.GetProperty("policy").GetString());
        }

        Assert.Equal(["ambiguous", "translate", "ambiguous", "translate"], decisions);
        Assert.Equal([false, true, true, true], confidenceGates);
        Assert.Equal([true, true, false, true], marginGates);
        Assert.Equal(displayedConfidences[0], displayedConfidences[1]);
        Assert.Equal(displayedMargins[2], displayedMargins[3]);
    }

    [Fact]
    public async Task ProcessAsync_HighPrecisionKeepsStricterConfiguredThresholds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllTextAsync(
            inputPath,
            CreateRecord("configured-gate", "Configured precision threshold remains authoritative.")
                + Environment.NewLine,
            cancellationToken
        );

        await LanguageTriageCore.ProcessAsync(
            inputPath,
            scope.PathFor("candidates.jsonl"),
            assessmentsPath,
            CreateOptions(policy: LanguageTriagePolicy.HighPrecision),
            cancellationToken,
            (
                string text,
                LanguageDetectionMode mode,
                string targetLanguage,
                out LanguageDetectionResult result,
                out string? error
            ) =>
            {
                result = new LanguageDetectionResult("es", 0.79, 0.10, 0.10, false);
                error = null;
                return true;
            }
        );

        using var row = JsonDocument.Parse(
            (await File.ReadAllLinesAsync(assessmentsPath, cancellationToken)).Single()
        );
        var root = row.RootElement;
        Assert.Equal("ambiguous", root.GetProperty("decision").GetString());
        Assert.False(root.GetProperty("confidenceGatePassed").GetBoolean());
        Assert.Equal(0.80, root.GetProperty("configuredMinimumConfidence").GetDouble());
        Assert.Equal(0.20, root.GetProperty("configuredMinimumTargetMargin").GetDouble());
        Assert.Equal(0.80, root.GetProperty("effectiveMinimumConfidence").GetDouble());
        Assert.Equal(0.20, root.GetProperty("effectiveMinimumTargetMargin").GetDouble());
    }

    [Fact]
    public async Task ProcessAsync_ReuseDoesNotDetectDerivedOrNonLinguisticRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        const string linguisticText = "This duplicate evidence has already been translated.";
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateDerivedRecord("derived-1", linguisticText),
                CreateRecord("original", linguisticText),
                CreateDerivedRecord("derived-2", linguisticText),
                CreateRecord("identifier-1", "$SYNTH_TOKEN001"),
                CreateRecord("identifier-2", "$SYNTH_TOKEN001"),
            ],
            cancellationToken
        );
        var detectorCalls = 0;

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            detectorCalls++;
            result = new LanguageDetectionResult("en", 0.99, 0.99, 0.01, false);
            error = null;
            return true;
        };

        var assessmentsPath = scope.PathFor("assessments.jsonl");
        var stats = await LanguageTriageCore.ProcessAsync(
            inputPath,
            scope.PathFor("candidates.jsonl"),
            assessmentsPath,
            CreateOptions(batchSize: 5),
            cancellationToken,
            detector,
            reuseSuccessfulDetections: true
        );

        Assert.Equal(1, detectorCalls);
        Assert.Equal(4, stats.NonLinguisticRecords);
        Assert.Equal(
            [
                "already-derived",
                "target-language",
                "already-derived",
                "non-linguistic",
                "non-linguistic",
            ],
            await ReadDecisionsAsync(assessmentsPath, cancellationToken)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_CancellationPreservesExistingOutputsWithOrWithoutReuse(
        bool reuseSuccessfulDetections
    )
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            testCancellationToken
        );
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        const string duplicateText = "This duplicate evidence exercises cancellation behavior.";
        await File.WriteAllLinesAsync(
            inputPath,
            Enumerable
                .Range(0, 3)
                .Select(index => CreateRecord($"cancellation-{index}", duplicateText)),
            testCancellationToken
        );
        await File.WriteAllTextAsync(candidatesPath, "previous-candidates", testCancellationToken);
        await File.WriteAllTextAsync(
            assessmentsPath,
            "previous-assessments",
            testCancellationToken
        );
        var detectorCalls = 0;

        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            detectorCalls++;
            cancellationSource.Cancel();
            result = new LanguageDetectionResult("en", 0.99, 0.99, 0.01, false);
            error = null;
            return true;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LanguageTriageCore.ProcessAsync(
                inputPath,
                candidatesPath,
                assessmentsPath,
                CreateOptions(batchSize: 3) with { MaxDegreeOfParallelism = 1 },
                cancellationSource.Token,
                detector,
                reuseSuccessfulDetections: reuseSuccessfulDetections
            )
        );

        Assert.Equal(1, detectorCalls);
        Assert.Equal(
            "previous-candidates",
            await File.ReadAllTextAsync(candidatesPath, testCancellationToken)
        );
        Assert.Equal(
            "previous-assessments",
            await File.ReadAllTextAsync(assessmentsPath, testCancellationToken)
        );
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    [Fact]
    public async Task ProcessAsync_RejectsIdentifierOnlyTextBeforeLanguageDetection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllTextAsync(
            inputPath,
            CreateRecord("synthetic-token", "$SYNTH_TOKEN001"),
            cancellationToken
        );
        var detectorCalled = false;
        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            detectorCalled = true;
            result = new LanguageDetectionResult("es", 0.99, 0.01, 0.01, false);
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

        Assert.False(detectorCalled);
        Assert.Equal(1, stats.NonLinguisticRecords);
        Assert.Equal(0, stats.TranslationCandidates);
        Assert.Empty(await File.ReadAllLinesAsync(candidatesPath, cancellationToken));
        using var assessment = JsonDocument.Parse(
            (await File.ReadAllLinesAsync(assessmentsPath, cancellationToken)).Single()
        );
        Assert.Equal("non-linguistic", assessment.RootElement.GetProperty("decision").GetString());
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

    [Fact]
    public async Task ValidateCompletedOutputsAsync_ReconstructsExactStatsWithoutCallingDetector()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        var duplicateText = "Esta evidencia duplicada necesita traduccion.";
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("record-one", duplicateText),
                CreateRecord("record-two", "Otra evidencia necesita traduccion."),
                CreateRecord("record-three", duplicateText),
            ],
            cancellationToken
        );
        var detectorCalls = 0;
        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            Interlocked.Increment(ref detectorCalls);
            return AlwaysSpanish(text, mode, targetLanguage, out result, out error);
        };
        var options = CreateOptions(batchSize: 4);
        var expected = await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            options,
            cancellationToken,
            detector,
            reuseSuccessfulDetections: true
        );
        var callsAfterProcessing = detectorCalls;

        var actual = await LanguageTriageCore.ValidateCompletedOutputsAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            options,
            cancellationToken,
            reuseSuccessfulDetections: true
        );

        Assert.Equal(expected, actual);
        Assert.Equal(2, callsAfterProcessing);
        Assert.Equal(callsAfterProcessing, detectorCalls);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("reordered")]
    [InlineData("duplicate-property")]
    [InlineData("same-length-tamper")]
    [InlineData("unterminated")]
    public async Task ValidateCompletedOutputsAsync_RejectsInvalidAssessments(string mutation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCompletedOutputsAsync(scope, cancellationToken);
        var lines = await File.ReadAllLinesAsync(completed.AssessmentsPath, cancellationToken);

        switch (mutation)
        {
            case "truncated":
                lines = lines[..^1];
                break;
            case "reordered":
                Array.Reverse(lines);
                break;
            case "duplicate-property":
                lines[0] = lines[0].Replace(
                    "\"decision\":\"translate\"",
                    "\"decision\":\"translate\",\"decision\":\"translate\"",
                    StringComparison.Ordinal
                );
                break;
            case "same-length-tamper":
                lines[0] = lines[0].Replace("record-one", "record-xxx", StringComparison.Ordinal);
                break;
            case "unterminated":
                await File.WriteAllTextAsync(
                    completed.AssessmentsPath,
                    string.Join(Environment.NewLine, lines),
                    cancellationToken
                );
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation '{mutation}'.");
        }
        if (mutation != "unterminated")
        {
            await File.WriteAllLinesAsync(completed.AssessmentsPath, lines, cancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanguageTriageCore.ValidateCompletedOutputsAsync(
                completed.InputPath,
                completed.CandidatesPath,
                completed.AssessmentsPath,
                completed.Options,
                cancellationToken,
                reuseSuccessfulDetections: true
            )
        );
    }

    [Fact]
    public async Task ValidateCompletedOutputsAsync_RejectsMismatchedCandidateProjection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCompletedOutputsAsync(scope, cancellationToken);
        var candidates = await File.ReadAllLinesAsync(completed.CandidatesPath, cancellationToken);
        (candidates[0], candidates[1]) = (candidates[1], candidates[0]);
        await File.WriteAllLinesAsync(completed.CandidatesPath, candidates, cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanguageTriageCore.ValidateCompletedOutputsAsync(
                completed.InputPath,
                completed.CandidatesPath,
                completed.AssessmentsPath,
                completed.Options,
                cancellationToken,
                reuseSuccessfulDetections: true
            )
        );
    }

    [Fact]
    public async Task ValidateCompletedOutputsAsync_HonorsCancellation()
    {
        using var scope = new TemporaryDirectory();
        var completed = await CreateCompletedOutputsAsync(
            scope,
            TestContext.Current.CancellationToken
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LanguageTriageCore.ValidateCompletedOutputsAsync(
                completed.InputPath,
                completed.CandidatesPath,
                completed.AssessmentsPath,
                completed.Options,
                cancellation.Token,
                reuseSuccessfulDetections: true
            )
        );
    }

    [Theory]
    [InlineData("bstrings", "static", false, (int)TranslationRoutingProvenance.NativeStatic)]
    [InlineData(
        "floss",
        "decoded",
        false,
        (int)(TranslationRoutingProvenance.Floss | TranslationRoutingProvenance.FlossDecoded)
    )]
    [InlineData("floss", "stack", false, (int)TranslationRoutingProvenance.Floss)]
    [InlineData("rapidocr", "ocr", false, (int)TranslationRoutingProvenance.Ocr)]
    [InlineData("rapidocr", "pdf-text", false, (int)TranslationRoutingProvenance.PdfText)]
    [InlineData("future", "ocr", false, (int)TranslationRoutingProvenance.Unknown)]
    [InlineData("future", "future-kind", false, (int)TranslationRoutingProvenance.Unknown)]
    public void ClassifyRoutingProvenance_UsesBoundedFailOpenFamilies(
        string extractor,
        string kind,
        bool isDerived,
        int expectedValue
    )
    {
        Assert.Equal(
            (TranslationRoutingProvenance)expectedValue,
            LanguageTriageCore.ClassifyRoutingProvenance(extractor, kind, isDerived)
        );
    }

    [Fact]
    public void ClassifyRoutingProvenance_RetainsDerivedIdentityAsAnIndependentBit()
    {
        var provenance = LanguageTriageCore.ClassifyRoutingProvenance(
            "bstrings",
            "static",
            isDerived: true
        );

        Assert.True(provenance.HasFlag(TranslationRoutingProvenance.NativeStatic));
        Assert.True(provenance.HasFlag(TranslationRoutingProvenance.DerivedTranslation));
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

    private static string CreateDerivedRecord(string recordId, string text) =>
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
                parentRecordId = "parent",
                transform = new
                {
                    kind = "translation",
                    engine = "test",
                    engineVersion = "1",
                    model = "test-model",
                    revision = "test-revision",
                    modelSha256 = new string('a', 64),
                    targetLanguage = "en",
                },
            }
        );

    private static async Task<(
        string InputPath,
        string CandidatesPath,
        string AssessmentsPath,
        LanguageTriageOptions Options
    )> CreateCompletedOutputsAsync(
        TemporaryDirectory scope,
        CancellationToken cancellationToken
    )
    {
        var inputPath = scope.PathFor("input.jsonl");
        var candidatesPath = scope.PathFor("candidates.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        var options = CreateOptions(batchSize: 4);
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("record-one", "Esta evidencia necesita traduccion."),
                CreateRecord("record-two", "Otra evidencia necesita traduccion."),
                CreateRecord("record-three", "Una tercera evidencia necesita traduccion."),
            ],
            cancellationToken
        );
        await LanguageTriageCore.ProcessAsync(
            inputPath,
            candidatesPath,
            assessmentsPath,
            options,
            cancellationToken,
            AlwaysSpanish,
            reuseSuccessfulDetections: true
        );
        return (inputPath, candidatesPath, assessmentsPath, options);
    }

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

    private static async Task<string[]> ReadErrorsAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var errors = new string[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            using var row = JsonDocument.Parse(lines[index]);
            errors[index] = row.RootElement.GetProperty("error").GetString()!;
        }
        return errors;
    }

    [Fact]
    public async Task ProcessAsync_RoutingDisabledPreservesSuccessfulAndReusedLegacyJsonBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        const string duplicate = "Esta evidencia duplicada necesita traduccion.";
        await File.WriteAllLinesAsync(
            inputPath,
            [CreateRecord("golden-1", duplicate), CreateRecord("golden-2", duplicate)],
            cancellationToken
        );
        var detectorCalls = 0;
        LanguageDetectionHandler detector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            detectorCalls++;
            result = new LanguageDetectionResult("es", 0.99, 0.01, 0.01, false);
            error = null;
            return true;
        };

        await LanguageTriageCore.ProcessAsync(
            inputPath,
            scope.PathFor("candidates.jsonl"),
            assessmentsPath,
            CreateOptions(batchSize: 2),
            cancellationToken,
            detector,
            reuseSuccessfulDetections: true,
            includeTranslationRouting: false
        );

        const string expectedTemplate = "{\"schemaVersion\":1,\"recordType\":\"language-assessment\",\"sourceRecordId\":\"RECORD\",\"sourceFile\":\"sample.bin\",\"location\":{\"kind\":\"file_offset\",\"value\":\"0x10\"},\"detector\":\"lingua-rs\",\"detectorVersion\":\"1.8.0\",\"profile\":\"accurate\",\"targetLanguage\":\"en\",\"detectorTargetLanguage\":\"en\",\"language\":\"es\",\"confidence\":0.99,\"targetConfidence\":0.01,\"secondConfidence\":0.01,\"topLanguageMargin\":0.98,\"targetMargin\":0.98,\"scoreDecimalPlaces\":12,\"configuredMinimumConfidence\":0.8,\"configuredMinimumTargetMargin\":0.2,\"effectiveMinimumConfidence\":0.8,\"effectiveMinimumTargetMargin\":0.2,\"minimumConfidence\":0.8,\"minimumTargetMargin\":0.2,\"confidenceGatePassed\":true,\"marginGatePassed\":true,\"policy\":\"balanced\",\"decision\":\"translate\",\"translationCandidate\":true,\"error\":null}";
        Assert.Equal(1, detectorCalls);
        Assert.Equal(
            [
                expectedTemplate.Replace("RECORD", "golden-1", StringComparison.Ordinal),
                expectedTemplate.Replace("RECORD", "golden-2", StringComparison.Ordinal),
            ],
            await File.ReadAllLinesAsync(assessmentsPath, cancellationToken)
        );
    }

    [Fact]
    public async Task ProcessAsync_RoutingDisabledPreservesDetectorFailureLegacyJsonBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var assessmentsPath = scope.PathFor("assessments.jsonl");
        await File.WriteAllTextAsync(
            inputPath,
            CreateRecord("golden-failure", "Detector failure evidence remains auditable."),
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
            result = default;
            error = "golden failure";
            return false;
        };

        await LanguageTriageCore.ProcessAsync(
            inputPath,
            scope.PathFor("candidates.jsonl"),
            assessmentsPath,
            CreateOptions(),
            cancellationToken,
            detector,
            includeTranslationRouting: false
        );

        const string expected = "{\"schemaVersion\":1,\"recordType\":\"language-assessment\",\"sourceRecordId\":\"golden-failure\",\"sourceFile\":\"sample.bin\",\"location\":{\"kind\":\"file_offset\",\"value\":\"0x10\"},\"detector\":\"lingua-rs\",\"detectorVersion\":\"1.8.0\",\"profile\":\"accurate\",\"targetLanguage\":\"en\",\"detectorTargetLanguage\":\"en\",\"language\":null,\"confidence\":null,\"targetConfidence\":null,\"secondConfidence\":null,\"topLanguageMargin\":null,\"targetMargin\":null,\"scoreDecimalPlaces\":12,\"configuredMinimumConfidence\":0.8,\"configuredMinimumTargetMargin\":0.2,\"effectiveMinimumConfidence\":0.8,\"effectiveMinimumTargetMargin\":0.2,\"minimumConfidence\":0.8,\"minimumTargetMargin\":0.2,\"confidenceGatePassed\":null,\"marginGatePassed\":null,\"policy\":\"balanced\",\"decision\":\"detector-failed\",\"translationCandidate\":false,\"error\":\"golden failure\"}";
        Assert.Equal(
            expected,
            Assert.Single(await File.ReadAllLinesAsync(assessmentsPath, cancellationToken))
        );
    }

    [Fact]
    public async Task ProcessAsync_ShadowRoutingPreservesDetectorCandidatesAndParentBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("input.jsonl");
        var baselineCandidates = scope.PathFor("baseline-candidates.jsonl");
        var baselineAssessments = scope.PathFor("baseline-assessments.jsonl");
        var shadowCandidates = scope.PathFor("shadow-candidates.jsonl");
        var shadowAssessments = scope.PathFor("shadow-assessments.jsonl");
        var records = new[]
        {
            CreateRecord("natural", "Esta evidencia necesita traduccion."),
            CreateRecord("guid", "6F9619FF-8B86-D011-B42D-00C04FC964FF"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    recordType = "string",
                    recordId = "digest",
                    text = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    sourceFile = "sample.bin",
                    location = new { kind = "file_offset", value = "0x20" },
                    origin = new { extractor = "ocr", version = "test", kind = "page-text" },
                    attributes = new { bounded = true, marker = "must-not-be-duplicated" },
                }
            ),
        };
        await File.WriteAllLinesAsync(inputPath, records, cancellationToken);
        var baselineDetectorCalls = 0;
        var shadowDetectorCalls = 0;
        LanguageDetectionHandler baselineDetector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            baselineDetectorCalls++;
            result = new LanguageDetectionResult("es", 0.99, 0.01, 0.01, false);
            error = null;
            return true;
        };
        LanguageDetectionHandler shadowDetector = (
            string text,
            LanguageDetectionMode mode,
            string targetLanguage,
            out LanguageDetectionResult result,
            out string? error
        ) =>
        {
            shadowDetectorCalls++;
            result = new LanguageDetectionResult("es", 0.99, 0.01, 0.01, false);
            error = null;
            return true;
        };
        var options = CreateOptions(policy: LanguageTriagePolicy.HighRecall, batchSize: 3);

        var baseline = await LanguageTriageCore.ProcessAsync(
            inputPath,
            baselineCandidates,
            baselineAssessments,
            options,
            cancellationToken,
            baselineDetector,
            reuseSuccessfulDetections: false,
            includeTranslationRouting: false
        );
        var shadow = await LanguageTriageCore.ProcessAsync(
            inputPath,
            shadowCandidates,
            shadowAssessments,
            options,
            cancellationToken,
            shadowDetector,
            reuseSuccessfulDetections: false,
            includeTranslationRouting: true
        );

        Assert.Equal(baselineDetectorCalls, shadowDetectorCalls);
        Assert.Equal(2, shadowDetectorCalls);
        Assert.Equal(baseline.InputRecords, shadow.InputRecords);
        Assert.Equal(baseline.TargetLanguageRecords, shadow.TargetLanguageRecords);
        Assert.Equal(baseline.TranslationCandidates, shadow.TranslationCandidates);
        Assert.Equal(baseline.NonLinguisticRecords, shadow.NonLinguisticRecords);
        Assert.Equal(
            await File.ReadAllBytesAsync(baselineCandidates, cancellationToken),
            await File.ReadAllBytesAsync(shadowCandidates, cancellationToken)
        );
        Assert.Equal(records[..2], await File.ReadAllLinesAsync(shadowCandidates, cancellationToken));
        Assert.Equal("disabled", baseline.TranslationRoutingPolicyVersion);
        Assert.Equal(TranslationWorthinessRouter.PolicyVersion, shadow.TranslationRoutingPolicyVersion);
        Assert.Equal(1, shadow.TranslationRoutingRetained);
        Assert.Equal(2, shadow.TranslationRoutingProspectiveBypasses);
        Assert.Equal(0, shadow.TranslationRoutingUnknown);

        var shadowLines = await File.ReadAllLinesAsync(shadowAssessments, cancellationToken);
        Assert.Equal(["retain", "prospective-guid", "prospective-digest"], shadowLines.Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement
                .GetProperty("translationRouting")
                .GetProperty("code")
                .GetString()!;
        }));
        Assert.DoesNotContain("must-not-be-duplicated", string.Join('\n', shadowLines));
        Assert.Equal(
            await ReadAssessmentsWithoutRoutingAsync(baselineAssessments, cancellationToken),
            await ReadAssessmentsWithoutRoutingAsync(shadowAssessments, cancellationToken)
        );
    }

    private static async Task<string[]> ReadAssessmentsWithoutRoutingAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        for (var index = 0; index < lines.Length; index++)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(lines[index])!;
            values.Remove("translationRouting");
            lines[index] = JsonSerializer.Serialize(values);
        }
        return lines;
    }

    private static async Task<string[]> ReadRoutingCodesAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        return lines
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement
                    .GetProperty("translationRouting")
                    .GetProperty("code")
                    .GetString()!;
            })
            .ToArray();
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
