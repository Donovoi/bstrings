using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisCliTests
{
    [Theory]
    [InlineData(new[] { "help" }, new[] { "--help" })]
    [InlineData(new[] { "help", "analyze" }, new[] { "analyze", "--help" })]
    [InlineData(
        new[] { "help", "bundle", "verify" },
        new[] { "bundle", "verify", "--help" }
    )]
    [InlineData(new[] { "analyze", "help" }, new[] { "analyze", "--help" })]
    [InlineData(new[] { "--help" }, new[] { "--help" })]
    public void NormalizeHelpArguments_AcceptsCommandStyleHelp(
        string[] arguments,
        string[] expected
    )
    {
        Assert.Equal(expected, Program.NormalizeHelpArguments(arguments));
    }

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
        Assert.Contains("bstrings.exe help analyze", footer, StringComparison.Ordinal);
        Assert.Contains("percentage completion", footer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".incomplete", footer, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 0, true)]
    [InlineData(1, 8, true)]
    [InlineData(4, 0, false)]
    public void ValidateTranslationScheduling_AcceptsSupportedCombinations(
        int parallelism,
        int threads,
        bool strictDeterminism
    )
    {
        AnalysisCli.ValidateTranslationScheduling(parallelism, threads, strictDeterminism);
    }

    [Theory]
    [InlineData(-1, 0, false)]
    [InlineData(0, -1, false)]
    public void ValidateTranslationScheduling_RejectsNegativeCounts(
        int parallelism,
        int threads,
        bool strictDeterminism
    )
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AnalysisCli.ValidateTranslationScheduling(parallelism, threads, strictDeterminism)
        );
    }

    [Fact]
    public void ValidateTranslationScheduling_RejectsParallelStrictDeterminism()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AnalysisCli.ValidateTranslationScheduling(
                parallelism: 2,
                threads: 0,
                strictDeterminism: true
            )
        );

        Assert.Contains("parallelism above 1", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
