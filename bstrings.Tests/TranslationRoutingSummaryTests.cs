using Xunit;

namespace bstrings.Tests;

public sealed class TranslationRoutingSummaryTests
{
    [Theory]
    [InlineData((int)TranslationWorkflowMode.Auto)]
    [InlineData((int)TranslationWorkflowMode.DetectOnly)]
    public void CreateTranslationRoutingSummary_BindsCompleteCountsAndCodebook(
        int modeValue
    )
    {
        var mode = (TranslationWorkflowMode)modeValue;
        var stats = new LanguageTriageStats(
            InputRecords: 6,
            TargetLanguageRecords: 0,
            TranslationCandidates: 0,
            AmbiguousRecords: 0,
            NonLinguisticRecords: 0,
            DetectorFailures: 0,
            EffectiveMode: LanguageDetectionMode.Accurate,
            TranslationRoutingPolicyVersion: TranslationWorthinessRouter.PolicyVersion,
            TranslationRoutingRetained: 2,
            TranslationRoutingProspectiveBypasses: 3,
            TranslationRoutingUnknown: 1,
            TranslationRoutingEvaluations: 4,
            DetectorEligibleRecords: 5,
            DetectorExecutions: 3,
            DetectorReuseHits: 2
        );

        var summary = Assert.IsType<TranslationRoutingSummary>(
            AnalysisOrchestrator.CreateTranslationRoutingSummary(mode, stats)
        );

        Assert.Equal("shadow", summary.Mode);
        Assert.Equal(TranslationWorthinessRouter.PolicyVersion, summary.PolicyVersion);
        Assert.Equal(TranslationWorthinessRouter.CodebookVersion, summary.CodebookVersion);
        Assert.Equal("language-assessments.jsonl", summary.Assessments);
        Assert.Equal(1, summary.AssessmentSchemaVersion);
        Assert.Equal(2, summary.Retained);
        Assert.Equal(3, summary.ProspectiveBypasses);
        Assert.Equal(1, summary.Unknown);
        Assert.Equal(4, summary.RoutingEvaluations);
        Assert.Equal(5, summary.DetectorEligibleRecords);
        Assert.Equal(3, summary.DetectorExecutions);
        Assert.Equal(2, summary.DetectorReuseHits);
        Assert.Equal(TranslationWorthinessRouter.Codebook, summary.Codebook);
        Assert.Equal(
            summary.Codebook.Keys.Order(StringComparer.Ordinal),
            summary.Codebook.Keys
        );
        Assert.All(summary.Codebook.Values, category =>
            Assert.Contains(category, new[] { "retain", "prospective", "unknown" })
        );
    }

    [Fact]
    public void CreateTranslationRoutingSummary_RejectsAggregateCardinalityMismatch()
    {
        var invalid = new LanguageTriageStats(
            InputRecords: 2,
            TargetLanguageRecords: 0,
            TranslationCandidates: 0,
            AmbiguousRecords: 0,
            NonLinguisticRecords: 0,
            DetectorFailures: 0,
            EffectiveMode: LanguageDetectionMode.Accurate,
            TranslationRoutingPolicyVersion: TranslationWorthinessRouter.PolicyVersion,
            TranslationRoutingRetained: 1,
            TranslationRoutingProspectiveBypasses: 0,
            TranslationRoutingUnknown: 0
        );

        Assert.Throws<InvalidDataException>(() =>
            AnalysisOrchestrator.CreateTranslationRoutingSummary(
                TranslationWorkflowMode.Auto,
                invalid
            )
        );
    }

    [Fact]
    public void CreateTranslationRoutingSummary_RejectsWorkCountersThatDoNotReconcile()
    {
        var invalid = new LanguageTriageStats(
            InputRecords: 2,
            TargetLanguageRecords: 0,
            TranslationCandidates: 2,
            AmbiguousRecords: 0,
            NonLinguisticRecords: 0,
            DetectorFailures: 0,
            EffectiveMode: LanguageDetectionMode.Accurate,
            TranslationRoutingPolicyVersion: TranslationWorthinessRouter.PolicyVersion,
            TranslationRoutingRetained: 2,
            TranslationRoutingProspectiveBypasses: 0,
            TranslationRoutingUnknown: 0,
            TranslationRoutingEvaluations: 1,
            DetectorEligibleRecords: 2,
            DetectorExecutions: 1,
            DetectorReuseHits: 0
        );

        var exception = Assert.Throws<InvalidDataException>(() =>
            AnalysisOrchestrator.CreateTranslationRoutingSummary(
                TranslationWorkflowMode.Auto,
                invalid
            )
        );
        Assert.Contains("work counters", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)TranslationWorkflowMode.Off)]
    [InlineData((int)TranslationWorkflowMode.All)]
    public void CreateTranslationRoutingSummary_IsAbsentWhenLanguageTriageDoesNotRun(
        int modeValue
    )
    {
        var mode = (TranslationWorkflowMode)modeValue;
        Assert.Null(AnalysisOrchestrator.CreateTranslationRoutingSummary(mode, triage: null));
    }

    [Theory]
    [InlineData((int)TranslationWorkflowMode.Auto)]
    [InlineData((int)TranslationWorkflowMode.DetectOnly)]
    public void CreateTranslationRoutingSummary_PreservesIdentityBeforeOrAfterFailedTriage(
        int modeValue
    )
    {
        var mode = (TranslationWorkflowMode)modeValue;
        var summary = Assert.IsType<TranslationRoutingSummary>(
            AnalysisOrchestrator.CreateTranslationRoutingSummary(mode, triage: null)
        );

        Assert.Equal("shadow", summary.Mode);
        Assert.Equal(TranslationWorthinessRouter.PolicyVersion, summary.PolicyVersion);
        Assert.Equal(TranslationWorthinessRouter.CodebookVersion, summary.CodebookVersion);
        Assert.Equal(TranslationWorthinessRouter.Codebook, summary.Codebook);
        Assert.Null(summary.Retained);
        Assert.Null(summary.ProspectiveBypasses);
        Assert.Null(summary.Unknown);
        Assert.Null(summary.RoutingEvaluations);
        Assert.Null(summary.DetectorEligibleRecords);
        Assert.Null(summary.DetectorExecutions);
        Assert.Null(summary.DetectorReuseHits);
    }
}
