using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisCliTests
{
    [Fact]
    public void TranslationPolicyHelpAndDefaultMatchTheRuntimeContract()
    {
        Assert.True(
            LanguageTriageCore.TryParsePolicy(
                AnalysisCli.DefaultTranslationPolicy,
                out var policy,
                out var error
            ),
            error
        );
        Assert.Equal(LanguageTriagePolicy.HighRecall, policy);
        Assert.Contains("high-recall", AnalysisCli.TranslationPolicyHelp, StringComparison.Ordinal);
        Assert.Contains("balanced", AnalysisCli.TranslationPolicyHelp, StringComparison.Ordinal);
        Assert.Contains("high-precision", AnalysisCli.TranslationPolicyHelp, StringComparison.Ordinal);
        Assert.Contains("0.65", AnalysisCli.TranslationPolicyHelp, StringComparison.Ordinal);
        Assert.Contains("0.15", AnalysisCli.TranslationPolicyHelp, StringComparison.Ordinal);
    }

    [Fact]
    public void FullAndTranslationDeviceHelpMatchTheAcceptedQ4CudaContract()
    {
        Assert.Contains("Q4_K_M", AnalysisCli.FullProfileHelp, StringComparison.Ordinal);
        Assert.DoesNotContain("Q8", AnalysisCli.FullProfileHelp, StringComparison.Ordinal);
        Assert.Contains(
            "shadow translation-worthiness routing",
            AnalysisCli.FullProfileHelp,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "does not yet remove candidates",
            AnalysisCli.FullProfileHelp,
            StringComparison.Ordinal
        );
        Assert.Contains("8.9", AnalysisCli.TranslationDeviceHelp, StringComparison.Ordinal);
        Assert.Contains("full-offload CUDA p2", AnalysisCli.TranslationDeviceHelp, StringComparison.Ordinal);
        Assert.Contains("before evidence inference", AnalysisCli.TranslationDeviceHelp, StringComparison.Ordinal);
        Assert.Contains("fails closed", AnalysisCli.TranslationDeviceHelp, StringComparison.Ordinal);
        Assert.Contains("--exclude-engine", AnalysisCli.FullProfileHelp, StringComparison.Ordinal);
        Assert.Contains("-e", AnalysisCli.FullProfileHelp, StringComparison.Ordinal);
        Assert.Contains("Base64", AnalysisCli.FullProfileHelp, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEngineExclusions_AcceptsRepeatedCommaSeparatedAndAsciiCaseInsensitiveNames()
    {
        var exclusions = AnalysisCli.ParseEngineExclusions(
            ["Native,FLOSS", "OCR,decode", "translation"]
        );

        Assert.True(exclusions.Native);
        Assert.True(exclusions.Floss);
        Assert.True(exclusions.Ocr);
        Assert.True(exclusions.Decode);
        Assert.True(exclusions.Translation);
    }

    [Theory]
    [InlineData("native,")]
    [InlineData(",native")]
    [InlineData("native,,ocr")]
    [InlineData("native, NATIVE")]
    [InlineData("recovery")]
    [InlineData("floss-recovery")]
    [InlineData("ＯＣＲ")]
    public void ParseEngineExclusions_RejectsEmptyDuplicateUnknownAndAliasNames(string value)
    {
        Assert.Throws<ArgumentException>(() => AnalysisCli.ParseEngineExclusions([value]));
    }

    [Theory]
    [InlineData("native", 1, 1, 1, 1)]
    [InlineData("floss", 0, 0, 1, 1)]
    [InlineData("ocr", 0, 1, 0, 1)]
    [InlineData("translation", 0, 1, 1, 0)]
    [InlineData("decode", 0, 1, 1, 1)]
    public void ResolveEngineModes_SubtractsOneEngineFromFullDefaults(
        string excluded,
        int native,
        int floss,
        int ocr,
        int translation
    )
    {
        var modes = AnalysisCli.ResolveEngineModes(
            full: true,
            nativeValue: null,
            flossValue: null,
            ocrValue: null,
            translationValue: null,
            rawExclusions: [excluded]
        );

        Assert.Equal((NativeExtractionMode)native, modes.Native);
        Assert.Equal((ExecutableRecoveryMode)floss, modes.Floss);
        Assert.Equal((OcrWorkflowMode)ocr, modes.Ocr);
        Assert.Equal((TranslationWorkflowMode)translation, modes.Translation);
        Assert.Equal(DecoderWorkflowMode.Off, modes.Decode);
    }

    [Fact]
    public void ResolveEngineModes_PreservesCanonicalFullDefaultsWithoutExclusions()
    {
        var modes = AnalysisCli.ResolveEngineModes(
            full: true,
            nativeValue: null,
            flossValue: null,
            ocrValue: null,
            translationValue: null,
            rawExclusions: []
        );

        Assert.Equal(NativeExtractionMode.On, modes.Native);
        Assert.Equal(ExecutableRecoveryMode.Auto, modes.Floss);
        Assert.Equal(OcrWorkflowMode.Auto, modes.Ocr);
        Assert.Equal(DecoderWorkflowMode.Off, modes.Decode);
        Assert.Equal(TranslationWorkflowMode.Auto, modes.Translation);
    }

    [Fact]
    public void ResolveEngineModes_PreservesExplicitSelectorsForNonExcludedEngines()
    {
        var modes = AnalysisCli.ResolveEngineModes(
            full: true,
            nativeValue: "off",
            flossValue: "force",
            ocrValue: "force",
            translationValue: null,
            rawExclusions: ["translation"]
        );

        Assert.Equal(NativeExtractionMode.Off, modes.Native);
        Assert.Equal(ExecutableRecoveryMode.Force, modes.Floss);
        Assert.Equal(OcrWorkflowMode.Force, modes.Ocr);
        Assert.Equal(DecoderWorkflowMode.Off, modes.Decode);
        Assert.Equal(TranslationWorkflowMode.Off, modes.Translation);
    }

    [Fact]
    public async Task ExcludeEngineParser_AcceptsShortLongRepeatedCommaAndCaseFormsBeforeProducerValidation()
    {
        using var scope = new AnalyzeCliFixture();

        var exitCode = await AnalysisCli.RunAsync(
            [
                "-f",
                scope.InputPath,
                "-o",
                scope.OutputPath,
                "--full",
                "-e",
                "Native,FLOSS",
                "--exclude-engine",
                "OCR",
            ]
        );

        Assert.Equal(2, exitCode);
        Assert.False(Directory.Exists(scope.OutputPath));
    }

    public static TheoryData<string[]> InvalidExcludeEngineArguments =>
        new()
        {
            new[] { "-e", "ocr" },
            new[] { "--full", "-e" },
            new[] { "--full", "-e", "ocr", "translation" },
            new[] { "--full", "-e", "recovery" },
            new[] { "--full", "-e", "ocr," },
            new[] { "--full", "-e", ",ocr" },
            new[] { "--full", "-e", "ocr,,translation" },
            new[] { "--full", "-e", "ocr", "-e", "OCR" },
            new[] { "--full", "-e", "native", "--native-extraction", "off" },
            new[] { "--full", "-e", "floss", "--recover-executable-strings", "off" },
            new[] { "--full", "-e", "ocr", "--ocr", "off" },
            new[] { "--full", "-e", "translation", "--translation", "off" },
            new[] { "--full", "-e", "decode", "--decode", "off" },
        };

    [Theory]
    [MemberData(nameof(InvalidExcludeEngineArguments))]
    public async Task ExcludeEngineParser_RejectsInvalidGrammarAndSameEngineSelectorsBeforeOutput(
        string[] exclusionArguments
    )
    {
        using var scope = new AnalyzeCliFixture();
        var arguments = new[] { "-f", scope.InputPath, "-o", scope.OutputPath }
            .Concat(exclusionArguments)
            .ToArray();

        var exitCode = await AnalysisCli.RunAsync(arguments);

        Assert.NotEqual(0, exitCode);
        Assert.False(Directory.Exists(scope.OutputPath));
    }

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
    [InlineData(8, 1, 1, 1)]
    [InlineData(
        AnalysisCli.DefaultDecoderMaximumCandidateCharacters,
        AnalysisCli.DefaultDecoderMaximumBytesPerRecord,
        AnalysisCli.DefaultDecoderMaximumCandidates,
        AnalysisCli.DefaultDecoderMaximumTotalBytes
    )]
    [InlineData(
        AnalysisCli.MaximumDecoderCandidateCharacters,
        AnalysisCli.MaximumDecoderBytesPerRecord,
        AnalysisCli.MaximumDecoderCandidates,
        AnalysisCli.MaximumDecoderTotalBytes
    )]
    public void ValidateDecoderLimits_AcceptsDocumentedBounds(
        int characters,
        int bytesPerRecord,
        long candidates,
        long totalBytes
    )
    {
        AnalysisCli.ValidateDecoderLimits(
            characters,
            bytesPerRecord,
            candidates,
            totalBytes
        );
    }

    [Theory]
    [InlineData(7, 1, 1, 1)]
    [InlineData(8, 0, 1, 1)]
    [InlineData(8, 1, 0, 1)]
    [InlineData(8, 1, 1, 0)]
    public void ValidateDecoderLimits_RejectsValuesBelowSafeBounds(
        int characters,
        int bytesPerRecord,
        long candidates,
        long totalBytes
    )
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AnalysisCli.ValidateDecoderLimits(
                characters,
                bytesPerRecord,
                candidates,
                totalBytes
            )
        );
    }

    [Theory]
    [InlineData(
        AnalysisCli.MaximumDecoderCandidateCharacters + 1,
        1,
        1,
        1
    )]
    [InlineData(8, AnalysisCli.MaximumDecoderBytesPerRecord + 1, 1, 1)]
    [InlineData(8, 1, AnalysisCli.MaximumDecoderCandidates + 1, 1)]
    [InlineData(8, 1, 1, AnalysisCli.MaximumDecoderTotalBytes + 1)]
    public void ValidateDecoderLimits_RejectsValuesAboveSafeBounds(
        int characters,
        int bytesPerRecord,
        long candidates,
        long totalBytes
    )
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AnalysisCli.ValidateDecoderLimits(
                characters,
                bytesPerRecord,
                candidates,
                totalBytes
            )
        );
    }

    [Theory]
    [InlineData("--decode", "sometimes")]
    [InlineData("--decode-max-characters", "7")]
    [InlineData("--decode-max-bytes-per-record", "0")]
    [InlineData("--decode-max-candidates", "0")]
    [InlineData("--decode-max-total-bytes", "0")]
    public async Task DecodeCli_RejectsUnknownModeAndUnsafeLimitsBeforeOutput(
        string option,
        string value
    )
    {
        using var scope = new AnalyzeCliFixture();

        var exitCode = await AnalysisCli.RunAsync(
            ["-f", scope.InputPath, "-o", scope.OutputPath, option, value]
        );

        Assert.NotEqual(0, exitCode);
        Assert.False(Directory.Exists(scope.OutputPath));
    }

    public static TheoryData<string[]> InvalidResumeArguments =>
        new()
        {
            new[] { "-f", "input.bin" },
            new[] { "-d", "evidence" },
            new[] { "--full" },
            new[] { "--translation", "off" },
            new[] { "--decode", "off" },
            new[] { "--lr", "all" },
        };

    [Theory]
    [MemberData(nameof(InvalidResumeArguments))]
    public async Task ResumeParser_RejectsNewRunSelectorsBeforeTouchingTheOutput(
        string[] incompatibleArguments
    )
    {
        using var scope = new AnalyzeCliFixture();
        var arguments = new[] { "-r", "-o", scope.OutputPath }
            .Concat(incompatibleArguments)
            .ToArray();

        var exitCode = await AnalysisCli.RunAsync(arguments);

        Assert.NotEqual(0, exitCode);
        Assert.False(Directory.Exists(scope.OutputPath));
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

    private sealed class AnalyzeCliFixture : IDisposable
    {
        internal AnalyzeCliFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-analysis-cli-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(RootPath);
            InputPath = Path.Combine(RootPath, "input.bin");
            File.WriteAllText(InputPath, "synthetic test material");
            OutputPath = Path.Combine(RootPath, "output");
        }

        private string RootPath { get; }
        internal string InputPath { get; }
        internal string OutputPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
