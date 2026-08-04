using Xunit;

namespace bstrings.Tests;

public sealed class LanguageDetectionCoreTests
{
    [Fact]
    public void NativeDetector_IsAvailableThroughTheManagedAbi()
    {
        Assert.True(LanguageDetectionCore.TryVerifyAvailability(out var error), error);
    }

    [Fact]
    public void TryDetect_ReturnsTargetAwareConfidence()
    {
        const string french =
            "Cette phrase contient des informations importantes pour une analyse forensique complete.";

        var detected = LanguageDetectionCore.TryDetect(
            french,
            LanguageDetectionMode.Accurate,
            "en",
            out var result,
            out var error
        );

        Assert.True(detected, error);
        Assert.Equal("fr", result.Language);
        Assert.True(result.Confidence > result.TargetConfidence);
        Assert.True(result.TopLanguageMargin > 0);
    }

    [Fact]
    public void TryNormalizeTargetLanguage_PreservesThePrimaryBcp47Subtag()
    {
        Assert.True(
            LanguageDetectionCore.TryNormalizeTargetLanguage(
                "zh-Hant",
                out var primary,
                out var error
            ),
            error
        );
        Assert.Equal("zh", primary);
    }
}
