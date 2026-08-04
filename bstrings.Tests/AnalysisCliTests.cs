using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisCliTests
{
    [Fact]
    public void RootHelp_FullExampleUsesCarvedFilesInsteadOfImplyingRawImageCarving()
    {
        var footerField = typeof(Program).GetField(
            "Footer",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        );
        var footer = Assert.IsType<string>(footerField?.GetValue(null));

        Assert.Contains(
            @"bstrings.exe analyze -d ""C:\evidence\carved-files"" --full",
            footer,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            @"bstrings.exe analyze -f ""C:\evidence\image.bin"" --full",
            footer,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ValidateStringLengthBounds_AcceptsTheJsonlSafeMaximum()
    {
        AnalysisCli.ValidateStringLengthBounds(
            minimumLength: 3,
            maximumLength: EnrichmentRegexPipelineCore.MaxNativeTextCharacters
        );
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void ValidateStringLengthBounds_RejectsUnlimitedOrBelowMinimum(int maximumLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AnalysisCli.ValidateStringLengthBounds(minimumLength: 3, maximumLength)
        );
    }

    [Fact]
    public void ValidateStringLengthBounds_RejectsValuesAboveTheJsonlSafeMaximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AnalysisCli.ValidateStringLengthBounds(
                minimumLength: 3,
                maximumLength: EnrichmentRegexPipelineCore.MaxNativeTextCharacters + 1
            )
        );
    }
}
