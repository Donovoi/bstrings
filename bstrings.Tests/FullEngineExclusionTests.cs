using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class FullEngineExclusionTests
{
    private const int NativeBit = 1;
    private const int FlossBit = 2;
    private const int OcrBit = 4;
    private const int TranslationBit = 8;
    private const int DecodeBit = 16;

    public static IEnumerable<object[]> NonEmptyExclusionSubsets()
    {
        var names = new[] { "native", "floss", "ocr", "translation", "decode" };
        for (var mask = 1; mask < 32; mask++)
        {
            var excluded = names.Where((_, index) => (mask & (1 << index)) != 0);
            yield return new object[] { mask, string.Join(',', excluded) };
        }
    }

    public static IEnumerable<object[]> DirectSelectorConflicts()
    {
        yield return new object[] { "native", "--native-extraction", "off" };
        yield return new object[] { "floss", "--recover-executable-strings", "off" };
        yield return new object[] { "ocr", "--ocr", "off" };
        yield return new object[] { "translation", "--translation", "off" };
        yield return new object[] { "decode", "--decode", "off" };
    }

    public static IEnumerable<object[]> AcceptedCliForms()
    {
        yield return new object[]
        {
            new[]
            {
                "-e",
                "ocr,translation",
                "--native-extraction",
                "off",
                "--recover-executable-strings",
                "off",
            },
        };
        yield return new object[]
        {
            new[]
            {
                "--exclude-engine",
                "ocr,translation",
                "--native-extraction",
                "off",
                "--recover-executable-strings",
                "off",
            },
        };
        yield return new object[]
        {
            new[]
            {
                "-e",
                "native,ocr",
                "--exclude-engine",
                "floss,translation",
            },
        };
        yield return new object[]
        {
            new[]
            {
                "-e",
                " native , OCR ",
                "--exclude-engine",
                " floss , translation ",
            },
        };
    }

    [Fact]
    public void FullWithoutExclusionsRetainsEstablishedEffectiveModes()
    {
        var expected = new AnalysisEngineModes(
            NativeExtractionMode.On,
            ExecutableRecoveryMode.Auto,
            OcrWorkflowMode.Auto,
            TranslationWorkflowMode.Auto,
            DecoderWorkflowMode.Off
        );

        Assert.Equal(expected, ResolveFull());
        Assert.Equal(expected, ResolveFull([]));
        Assert.Equal(
            expected,
            AnalysisCli.ResolveEngineModes(
                full: true,
                nativeValue: "on",
                flossValue: "auto",
                ocrValue: "auto",
                translationValue: "auto",
                rawExclusions: null
            )
        );
    }

    [Theory]
    [MemberData(nameof(NonEmptyExclusionSubsets))]
    public void EveryExclusionSubsetMapsOnlyNamedFullDefaultsOff(int mask, string value)
    {
        var modes = ResolveFull([value]);

        Assert.Equal(
            (mask & NativeBit) == 0 ? NativeExtractionMode.On : NativeExtractionMode.Off,
            modes.Native
        );
        Assert.Equal(
            (mask & FlossBit) == 0
                ? ExecutableRecoveryMode.Auto
                : ExecutableRecoveryMode.Off,
            modes.Floss
        );
        Assert.Equal(
            (mask & OcrBit) == 0 ? OcrWorkflowMode.Auto : OcrWorkflowMode.Off,
            modes.Ocr
        );
        Assert.Equal(
            (mask & TranslationBit) == 0
                ? TranslationWorkflowMode.Auto
                : TranslationWorkflowMode.Off,
            modes.Translation
        );
        Assert.Equal(DecoderWorkflowMode.Off, modes.Decode);
    }

    [Fact]
    public void CommaRepeatCaseAndSingleTokenWhitespaceFormsCanonicalize()
    {
        var exclusions = AnalysisCli.ParseEngineExclusions(
            [" NATIVE , FLOSS ", "oCr,decode", "TRANSLATION"]
        );

        Assert.True(exclusions.Native);
        Assert.True(exclusions.Floss);
        Assert.True(exclusions.Ocr);
        Assert.True(exclusions.Decode);
        Assert.True(exclusions.Translation);
        Assert.True(exclusions.Any);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",ocr")]
    [InlineData("ocr,")]
    [InlineData("ocr,,translation")]
    [InlineData("gpu")]
    [InlineData("trans")]
    [InlineData("native;ocr")]
    public void EmptyMalformedAndUnknownNamesFailClosed(string value)
    {
        Assert.Throws<ArgumentException>(() => AnalysisCli.ParseEngineExclusions([value]));
    }

    [Theory]
    [InlineData("ocr", "ocr")]
    [InlineData("OCR", "ocr")]
    [InlineData("native,ocr", "OCR")]
    [InlineData("translation,NATIVE", "native")]
    public void DuplicatesAcrossCaseCommaAndRepeatFormsFailClosed(
        string first,
        string second
    )
    {
        Assert.Throws<ArgumentException>(() =>
            AnalysisCli.ParseEngineExclusions([first, second])
        );
    }

    [Fact]
    public void ExclusionsRequireFull()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AnalysisCli.ResolveEngineModes(
                full: false,
                nativeValue: null,
                flossValue: null,
                ocrValue: null,
                translationValue: null,
                rawExclusions: ["translation"]
            )
        );

        Assert.Contains("requires --full", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("native", "off", null, null, null, null, "--native-extraction")]
    [InlineData("floss", null, "off", null, null, null, "--recover-executable-strings")]
    [InlineData("ocr", null, null, "off", null, null, "--ocr")]
    [InlineData("translation", null, null, null, "off", null, "--translation")]
    [InlineData("decode", null, null, null, null, "off", "--decode")]
    public void MatchingDirectSelectorsConflictEvenWhenTheyAlsoSayOff(
        string excluded,
        string? native,
        string? floss,
        string? ocr,
        string? translation,
        string? decode,
        string selector
    )
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AnalysisCli.ResolveEngineModes(
                full: true,
                native,
                floss,
                ocr,
                translation,
                [excluded],
                decode
            )
        );

        Assert.Contains(selector, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DirectSelectorConflicts))]
    public async Task CliRejectsMatchingDirectSelectorInEitherArgumentOrder(
        string engine,
        string selector,
        string selectorValue
    )
    {
        using var scope = new TemporaryScope();
        foreach (var exclusionFirst in new[] { true, false })
        {
            var selection = exclusionFirst
                ? new[] { "-e", engine, selector, selectorValue }
                : new[] { selector, selectorValue, "-e", engine };
            var output = scope.Output($"{engine}-{exclusionFirst}");
            var result = await RunAnalyzeAsync(scope.Input, output, selection);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                $"--exclude-engine {engine} cannot be combined with {selector}",
                result.StandardError,
                StringComparison.Ordinal
            );
            Assert.False(Directory.Exists(output));
        }
    }

    [Theory]
    [MemberData(nameof(AcceptedCliForms))]
    public async Task AliasCommaRepeatAndSpacedTokenFormsReachResolvedModeValidation(
        string[] selection
    )
    {
        using var scope = new TemporaryScope();
        var output = scope.Output(Guid.NewGuid().ToString("N"));

        var result = await RunAnalyzeAsync(scope.Input, output, selection);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("source producer", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BareAndWhitespaceSeparatedMultiArgumentFormsAreRejectedByTheCli()
    {
        using var scope = new TemporaryScope();
        var bareOutput = scope.Output("bare");
        var bare = await RunProcessAsync(
            ["analyze", "-e", "--full", "-f", scope.Input, "-o", bareOutput]
        );
        Assert.NotEqual(0, bare.ExitCode);
        Assert.DoesNotContain("source producer", bare.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(bareOutput));

        var multiOutput = scope.Output("multi");
        var multi = await RunProcessAsync(
            [
                "analyze",
                "--full",
                "-e",
                "ocr",
                "translation",
                "-f",
                scope.Input,
                "-o",
                multiOutput,
            ]
        );
        Assert.NotEqual(0, multi.ExitCode);
        Assert.Contains(
            "Unrecognized command or argument 'translation'",
            multi.StandardError,
            StringComparison.Ordinal
        );
        Assert.False(Directory.Exists(multiOutput));
    }

    [Fact]
    public async Task ExclusionAliasIsAnalyzeScopedAndLegacyXRemainsNumeric()
    {
        using var scope = new TemporaryScope();
        var analyzeHelp = await RunProcessAsync(["analyze", "--help"]);
        Assert.Equal(0, analyzeHelp.ExitCode);
        Assert.Contains("-e, --exclude-engine", analyzeHelp.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("  -x <x>", analyzeHelp.StandardOutput, StringComparison.Ordinal);

        var rootHelp = await RunProcessAsync(["--help"]);
        Assert.Equal(0, rootHelp.ExitCode);
        Assert.Contains("  -x <x>", rootHelp.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-e, --exclude-engine",
            rootHelp.StandardOutput,
            StringComparison.Ordinal
        );

        var legacy = await RunProcessAsync(
            [
                "-f",
                scope.Input,
                "-x",
                "7",
                "-q",
                "-s",
                "--processor",
                "cpu",
                "--cpu-engine",
                "dotnet",
            ]
        );
        Assert.Equal(0, legacy.ExitCode);

        var analyzeOutput = scope.Output("analyze-x");
        var analyzeX = await RunProcessAsync(
            [
                "analyze",
                "--full",
                "-x",
                "7",
                "-f",
                scope.Input,
                "-o",
                analyzeOutput,
            ]
        );
        Assert.NotEqual(0, analyzeX.ExitCode);
        Assert.Contains(
            "Unrecognized command or argument '-x'",
            analyzeX.StandardError,
            StringComparison.Ordinal
        );
        Assert.False(Directory.Exists(analyzeOutput));
    }

    [Theory]
    [InlineData("native,floss,ocr")]
    [InlineData("native,floss,ocr,translation")]
    public async Task ProducerlessResolvedModesFailBeforeOutputCreation(string exclusions)
    {
        using var scope = new TemporaryScope();
        var modes = ResolveFull([exclusions]);
        var output = scope.Output(exclusions.Length.ToString());
        var options = CreateOptions(scope.Input, output, modes);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            AnalysisOrchestrator.RunAsync(options, TestContext.Current.CancellationToken)
        );

        Assert.Contains("source producer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task PackagedNativeOnlyExclusionMatchesEquivalentExplicitOffRun()
    {
        using var scope = new TemporaryScope();
        var excludedOutput = scope.Output("native-only-excluded");
        var explicitOutput = scope.Output("native-only-explicit");
        var common = new[]
        {
            "--processor",
            "cpu",
            "--cpu-engine",
            "dotnet",
            "--ocr-provider",
            "directml",
            "--translation-device",
            "cuda",
            "--lr",
            "email",
        };

        var excluded = await RunAnalyzeAsync(
            scope.Input,
            excludedOutput,
            new[] { "-e", "floss,ocr,decode,translation" }.Concat(common)
        );
        var explicitOff = await RunAnalyzeAsync(
            scope.Input,
            explicitOutput,
            new[]
            {
                "--recover-executable-strings",
                "off",
                "--ocr",
                "off",
                "--translation",
                "off",
                "--decode",
                "off",
            }.Concat(common)
        );

        Assert.Equal(0, excluded.ExitCode);
        Assert.Equal(0, explicitOff.ExitCode);
        foreach (var name in new[]
                 {
                     "native-strings.jsonl",
                     "raw-strings.jsonl",
                     "enriched-strings.jsonl",
                     "regex-matches.jsonl",
                     "findings.tsv",
                     "pattern-histogram.tsv",
                     "feature-histogram.tsv",
                 })
        {
            Assert.Equal(
                await File.ReadAllBytesAsync(
                    Path.Combine(explicitOutput, name),
                    TestContext.Current.CancellationToken
                ),
                await File.ReadAllBytesAsync(
                    Path.Combine(excludedOutput, name),
                    TestContext.Current.CancellationToken
                )
            );
        }
        using var excludedRun = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(excludedOutput, "run.json"),
                TestContext.Current.CancellationToken
            )
        );
        using var explicitRun = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(explicitOutput, "run.json"),
                TestContext.Current.CancellationToken
            )
        );
        foreach (var property in new[]
                 {
                     "nativeExtractionMode",
                     "recoveryMode",
                     "ocrMode",
                     "translationMode",
                 })
        {
            Assert.Equal(
                explicitRun.RootElement.GetProperty("options").GetProperty(property).GetRawText(),
                excludedRun.RootElement.GetProperty("options").GetProperty(property).GetRawText()
            );
        }
        Assert.False(File.Exists(Path.Combine(excludedOutput, "decoded-strings.jsonl")));
        Assert.False(File.Exists(Path.Combine(explicitOutput, "decoded-strings.jsonl")));
        Assert.DoesNotContain("content routing", excluded.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offline translation", excluded.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offline OCR", excluded.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("routed FLOSS", excluded.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--ocr-provider", "metal", "OCR provider")]
    [InlineData("--translation-device", "quantum", "Translation device")]
    public async Task InvalidExcludedEngineTuningStillFailsBeforeOutput(
        string option,
        string value,
        string expectedError
    )
    {
        using var scope = new TemporaryScope();
        var output = scope.Output(option.TrimStart('-'));

        var result = await RunAnalyzeAsync(
            scope.Input,
            output,
            ["-e", "floss,ocr,translation", option, value]
        );

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [MemberData(nameof(NonEmptyExclusionSubsets))]
    public void PlannedStageCountsFollowOnlyTheResolvedEffectiveModes(int mask, string value)
    {
        var modes = ResolveFull([value]);
        var needsExternalToolchain =
            modes.Floss != ExecutableRecoveryMode.Off
            || modes.Ocr != OcrWorkflowMode.Off
            || modes.Translation is TranslationWorkflowMode.Auto or TranslationWorkflowMode.All;
        var expected = modes.Native == NativeExtractionMode.On ? 9 : 6;
        if (needsExternalToolchain)
        {
            expected++;
        }
        if (
            modes.Floss != ExecutableRecoveryMode.Off
            || modes.Ocr != OcrWorkflowMode.Off
        )
        {
            expected += 2;
        }
        if (modes.Floss != ExecutableRecoveryMode.Off)
        {
            expected += 3;
        }
        if (modes.Ocr != OcrWorkflowMode.Off)
        {
            expected += 4;
        }
        if (modes.Decode != DecoderWorkflowMode.Off)
        {
            expected += 2;
        }
        if (modes.Translation is TranslationWorkflowMode.Auto or TranslationWorkflowMode.All)
        {
            expected += 3;
        }

        Assert.Equal(
            expected,
            AnalysisOrchestrator.CountPlannedStages(
                CreateOptions("input", "output", modes),
                needsExternalToolchain
            )
        );
        Assert.Equal((mask & NativeBit) == 0, modes.Native == NativeExtractionMode.On);
    }

    private static AnalysisEngineModes ResolveFull(IReadOnlyList<string>? exclusions = null) =>
        AnalysisCli.ResolveEngineModes(
            full: true,
            nativeValue: null,
            flossValue: null,
            ocrValue: null,
            translationValue: null,
            rawExclusions: exclusions
        );

    private static AnalysisOptions CreateOptions(
        string input,
        string output,
        AnalysisEngineModes modes
    ) =>
        new(
            FilePath: input,
            DirectoryPath: null,
            Mask: null,
            OutputDirectory: output,
            Full: true,
            OcrMode: modes.Ocr,
            OcrProvider: OcrProvider.Auto,
            OcrThreads: 0,
            RecoveryMode: modes.Floss,
            DecoderMode: modes.Decode,
            TranslationMode: modes.Translation,
            LanguageDetectionMode: LanguageDetectionMode.Adaptive,
            TranslationPolicy: LanguageTriagePolicy.HighRecall,
            LanguageConfidence: 0.55,
            LanguageMargin: 0.10,
            TranslationTarget: "en",
            TranslationDevice: "cpu",
            TranslationParallelism: 0,
            TranslationThreads: 0,
            TranslationGpuLayers: -1,
            TranslationStrictDeterminism: false,
            PatternSelection: "email",
            RegexFilePath: null,
            Processor: "cpu",
            CpuEngine: "dotnet",
            MinimumStringLength: 3,
            MaximumStringLength: 4096,
            TranslationMinimumCharacters: 8,
            TranslationMaximumCharacters: 512,
            BundleRoot: null,
            Airgap: false,
            NativeExtractionMode: modes.Native
        );

    private static async Task<ProcessResult> RunAnalyzeAsync(
        string input,
        string output,
        IEnumerable<string> selection
    )
    {
        var arguments = new List<string> { "analyze", "--full" };
        arguments.AddRange(selection);
        arguments.AddRange(["-f", input, "-o", output]);
        return await RunProcessAsync(arguments);
    }

    private static async Task<ProcessResult> RunProcessAsync(IEnumerable<string> arguments)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");
        Assert.True(File.Exists(executable), $"Test bstrings executable was not found: {executable}");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment.Remove("BSTRINGS_AIRGAP_BUNDLE");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the bstrings test process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError
        );
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError
    );

    private sealed class TemporaryScope : IDisposable
    {
        internal TemporaryScope()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "bstrings-full-engine-exclusion-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Root);
            Input = Path.Combine(Root, "input.bin");
            File.WriteAllText(Input, "synthetic analyst@example.test test material");
        }

        internal string Root { get; }
        internal string Input { get; }
        internal string Output(string name) => Path.Combine(Root, $"output-{name}");

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
