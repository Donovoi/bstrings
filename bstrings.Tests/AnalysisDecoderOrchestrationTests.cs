using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisDecoderOrchestrationTests
{
    [Fact]
    public async Task Analyze_DecodeAutoPublishesValidatesMergesAndReportsChild()
    {
        using var scope = new TemporaryScope();
        var options = CreateOptions(scope.InputPath, scope.OutputPath);

        await AnalysisOrchestrator.RunAsync(
            options,
            TestContext.Current.CancellationToken,
            executingExecutablePath: Path.Combine(AppContext.BaseDirectory, "bstrings.exe")
        );

        Assert.False(File.Exists(Path.Combine(scope.OutputPath, ".incomplete")));
        foreach (var name in new[]
                 {
                     "decoded-strings.jsonl",
                     "decoder-assessments.jsonl",
                     "decoder-work-stats.json",
                 })
        {
            Assert.True(File.Exists(Path.Combine(scope.OutputPath, name)), name);
        }

        var decodedLines = await File.ReadAllLinesAsync(
            Path.Combine(scope.OutputPath, "decoded-strings.jsonl"),
            TestContext.Current.CancellationToken
        );
        var enrichedLines = await File.ReadAllLinesAsync(
            Path.Combine(scope.OutputPath, "enriched-strings.jsonl"),
            TestContext.Current.CancellationToken
        );
        Assert.Single(decodedLines);
        Assert.EndsWith(decodedLines[0], enrichedLines[^1], StringComparison.Ordinal);

        using var child = JsonDocument.Parse(decodedLines[0]);
        Assert.Equal("analyst@example.com", child.RootElement.GetProperty("text").GetString());
        Assert.Equal(
            "decoding",
            child.RootElement.GetProperty("transform").GetProperty("kind").GetString()
        );
        Assert.False(string.IsNullOrWhiteSpace(child.RootElement.GetProperty("parentRecordId").GetString()));

        using var summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(scope.OutputPath, "summary.json"),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("complete", summary.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, summary.RootElement.GetProperty("decodedStrings").GetInt64());
        var decoder = summary.RootElement.GetProperty("decoder");
        Assert.Equal("auto", decoder.GetProperty("mode").GetString());
        Assert.Equal(1, decoder.GetProperty("depth").GetInt32());
        Assert.Equal(
            1,
            decoder.GetProperty("validation").GetProperty("decodedRecords").GetInt64()
        );

        var matches = await File.ReadAllTextAsync(
            Path.Combine(scope.OutputPath, "regex-matches.jsonl"),
            TestContext.Current.CancellationToken
        );
        Assert.Contains("derived-decoding", matches, StringComparison.Ordinal);
        var histogramHeader = (
            await File.ReadAllLinesAsync(
                Path.Combine(scope.OutputPath, "pattern-histogram.tsv"),
                TestContext.Current.CancellationToken
            )
        )[0];
        Assert.Contains("DerivedDecodingCount", histogramHeader, StringComparison.Ordinal);
        var findings = await File.ReadAllTextAsync(
            Path.Combine(scope.OutputPath, "findings.tsv"),
            TestContext.Current.CancellationToken
        );
        Assert.Contains("rfc4648-base64-text-v1", findings, StringComparison.Ordinal);
    }

    private static AnalysisOptions CreateOptions(string inputPath, string outputPath) =>
        new(
            FilePath: inputPath,
            DirectoryPath: null,
            Mask: null,
            OutputDirectory: outputPath,
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
            PatternSelection: @"analyst@example\.com",
            RegexFilePath: null,
            Processor: "cpu",
            CpuEngine: "dotnet",
            MinimumStringLength: 3,
            MaximumStringLength: 4096,
            TranslationMinimumCharacters: 8,
            TranslationMaximumCharacters: 512,
            BundleRoot: null,
            Airgap: false,
            DecoderMode: DecoderWorkflowMode.Auto
        );

    private sealed class TemporaryScope : IDisposable
    {
        internal TemporaryScope()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-analysis-decoder-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(RootPath);
            InputPath = Path.Combine(RootPath, "input.bin");
            File.WriteAllText(InputPath, "YW5hbHlzdEBleGFtcGxlLmNvbQ==");
            OutputPath = Path.Combine(RootPath, "output");
        }

        private string RootPath { get; }
        internal string InputPath { get; }
        internal string OutputPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
