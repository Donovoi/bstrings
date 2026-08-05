using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisCliTests
{
    [Theory]
    [InlineData(null, false, 0)]
    [InlineData(null, true, 1)]
    [InlineData("off", true, 0)]
    [InlineData("auto", false, 1)]
    [InlineData("force", false, 2)]
    public void ResolveOcrMode_UsesExplicitValueOrFullDefault(
        string? value,
        bool full,
        int expected
    )
    {
        Assert.Equal((OcrWorkflowMode)expected, AnalysisCli.ResolveOcrMode(value, full));
    }

    [Fact]
    public void ResolveOcrMode_RejectsUnknownModes()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AnalysisCli.ResolveOcrMode("sometimes", full: false)
        );

        Assert.Contains("off, auto, or force", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, false, 0)]
    [InlineData(null, true, 0)]
    [InlineData("auto", false, 0)]
    [InlineData("cpu", false, 1)]
    [InlineData("CUDA", false, 2)]
    [InlineData("directml", false, 3)]
    [InlineData("hybrid", false, 4)]
    public void ResolveOcrProvider_UsesCanonicalDefaultAndExplicitValue(
        string? value,
        bool full,
        int expected
    )
    {
        Assert.Equal((OcrProvider)expected, AnalysisCli.ResolveOcrProvider(value, full));
    }

    [Fact]
    public void ResolveOcrProvider_RejectsUnknownProviders()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AnalysisCli.ResolveOcrProvider("metal", full: false)
        );

        Assert.Contains(
            "auto, cpu, cuda, directml, or hybrid",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public void OcrProvider_SerializesDirectMlUsingTheCliContractSpelling()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        };

        Assert.Equal("\"directml\"", JsonSerializer.Serialize(OcrProvider.DirectMl, options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    public void ValidateRequestedThreads_AcceptsWorkerContractBounds(int threads)
    {
        OcrCompletionCore.ValidateRequestedThreads(threads);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(257)]
    public void ValidateRequestedThreads_RejectsValuesOutsideWorkerContract(int threads)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OcrCompletionCore.ValidateRequestedThreads(threads)
        );
    }

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
