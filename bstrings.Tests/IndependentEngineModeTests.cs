using Xunit;

namespace bstrings.Tests;

public sealed class IndependentEngineModeTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("on", 0)]
    [InlineData("ON", 0)]
    [InlineData("off", 1)]
    public void NativeExtractionResolverUsesBackwardCompatibleDefault(
        string? value,
        int expected
    ) => Assert.Equal((NativeExtractionMode)expected, AnalysisCli.ResolveNativeExtractionMode(value));

    [Fact]
    public void NativeExtractionResolverRejectsUnknownValues()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AnalysisCli.ResolveNativeExtractionMode("sometimes")
        );

        Assert.Contains("on or off", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeRejectsAProducerlessRunBeforeCreatingOutput()
    {
        var fixture = CreateFixture();
        try
        {
            var options = CreateOptions(fixture.Input, fixture.Output) with
            {
                NativeExtractionMode = NativeExtractionMode.Off,
            };

            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                AnalysisOrchestrator.RunAsync(options, TestContext.Current.CancellationToken)
            );

            Assert.Contains("source producer", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(fixture.Output));
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeRejectsInvalidRegexBeforeCreatingOutput()
    {
        var fixture = CreateFixture();
        var regexFile = Path.Combine(fixture.Root, "invalid-regex.txt");
        await File.WriteAllTextAsync(
            regexFile,
            "([unterminated",
            TestContext.Current.CancellationToken
        );
        try
        {
            var options = CreateOptions(fixture.Input, fixture.Output) with
            {
                PatternSelection = "email",
                RegexFilePath = regexFile,
            };

            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                AnalysisOrchestrator.RunAsync(options, TestContext.Current.CancellationToken)
            );

            Assert.False(Directory.Exists(fixture.Output));
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Theory]
    [InlineData("quantum", "dotnet", "processor")]
    [InlineData("cpu", "quantum", "cpu-engine")]
    public async Task AnalyzeRejectsInvalidNativeHardwareOptionsBeforeCreatingOutput(
        string processor,
        string cpuEngine,
        string expectedMessage
    )
    {
        var fixture = CreateFixture();
        try
        {
            var options = CreateOptions(fixture.Input, fixture.Output) with
            {
                Processor = processor,
                CpuEngine = cpuEngine,
            };

            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                AnalysisOrchestrator.RunAsync(options, TestContext.Current.CancellationToken)
            );

            Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(fixture.Output));
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public void PlannedStageCountsExcludeNativeStagesWhenDisabled()
    {
        var baseline = CreateOptions("input", "output");
        Assert.Equal(9, AnalysisOrchestrator.CountPlannedStages(baseline, false));

        var flossOnly = baseline with
        {
            NativeExtractionMode = NativeExtractionMode.Off,
            RecoveryMode = ExecutableRecoveryMode.Force,
        };
        Assert.Equal(12, AnalysisOrchestrator.CountPlannedStages(flossOnly, true));

        var ocrOnly = baseline with
        {
            NativeExtractionMode = NativeExtractionMode.Off,
            OcrMode = OcrWorkflowMode.Force,
        };
        Assert.Equal(13, AnalysisOrchestrator.CountPlannedStages(ocrOnly, true));
    }

    [Fact]
    public void SpecialistArgumentsDisableNativeAndRestoreFlossStaticCoverage()
    {
        var toolchain = CreateToolchain();
        var options = CreateOptions("input", "output") with
        {
            NativeExtractionMode = NativeExtractionMode.Off,
            RecoveryMode = ExecutableRecoveryMode.Force,
        };

        var routing = AnalysisOrchestrator.BuildContentRoutingArguments(
            options,
            toolchain,
            "inventory.txt",
            "manifest.jsonl",
            "routing.jsonl",
            3
        );
        Assert.Contains("--disable-native", routing);
        Assert.Contains("--enable-floss", routing);
        Assert.Contains("--force-floss", routing);
        Assert.DoesNotContain("--include-floss-static", routing);

        var recovery = AnalysisOrchestrator.BuildRecoveryArguments(
            options,
            toolchain,
            "floss-inputs.txt",
            "routing.jsonl",
            "recovered.jsonl",
            2
        );
        Assert.Contains("--include-floss-static", recovery);

        var nativeOn = options with { NativeExtractionMode = NativeExtractionMode.On };
        Assert.DoesNotContain(
            "--disable-native",
            AnalysisOrchestrator.BuildContentRoutingArguments(
                nativeOn,
                toolchain,
                "inventory.txt",
                "manifest.jsonl",
                "routing.jsonl",
                3
            )
        );
        Assert.DoesNotContain(
            "--include-floss-static",
            AnalysisOrchestrator.BuildRecoveryArguments(
                nativeOn,
                toolchain,
                "floss-inputs.txt",
                "routing.jsonl",
                "recovered.jsonl",
                2
            )
        );
    }

    private static AnalysisOptions CreateOptions(string input, string output) =>
        new(
            FilePath: input,
            DirectoryPath: null,
            Mask: null,
            OutputDirectory: output,
            Full: false,
            OcrMode: OcrWorkflowMode.Off,
            OcrProvider: OcrProvider.Auto,
            OcrThreads: 0,
            RecoveryMode: ExecutableRecoveryMode.Off,
            TranslationMode: TranslationWorkflowMode.Off,
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
            Airgap: false
        );

    private static (string Root, string Input, string Output) CreateFixture()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "bstrings-independent-engine-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.bin");
        File.WriteAllText(input, "synthetic test material");
        return (root, input, Path.Combine(root, "output"));
    }

    private static AnalysisToolchain CreateToolchain() =>
        new(
            BundleRoot: "bundle",
            BundleIntegrity: new BundleIntegrity(
                "bundle-manifest.json",
                new string('a', 64),
                1,
                1,
                "bstrings.exe",
                new string('b', 64)
            ),
            PythonExecutable: "python.exe",
            EnrichmentAdapter: "bstrings_enrich.py",
            OcrPythonExecutable: null,
            OcrExecutable: null,
            OcrAdapter: null,
            OcrEngine: null,
            OcrEngineVersion: null,
            OcrModelPath: null,
            OcrModelId: null,
            OcrModelRevision: null,
            OcrModelSha256: null,
            MagikaExecutable: "magika.exe",
            FlossExecutable: "floss.exe",
            LlamaServer: null,
            TranslationModelPath: null,
            TranslationModelId: null,
            TranslationModelRevision: null,
            TranslationModelSha256: null
        );
}
