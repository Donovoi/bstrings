using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisResumeEngineModeMatrixTests
{
    [Theory]
    [InlineData("native-on", "on", "off", "off", 3)]
    [InlineData("decoder-auto", "on", "off", "auto", 9)]
    [InlineData("translation-detect-only", "on", "detect-only", "off", 7)]
    public async Task Resume_FromSelectedEngineBoundary_MatchesFreshCanonicalEvidence(
        string modeName,
        string nativeValue,
        string translationValue,
        string decoderValue,
        int interruptedStageOrdinal
    )
    {
        using var scope = new TemporaryScope();
        var nativeMode = Enum.Parse<NativeExtractionMode>(nativeValue, ignoreCase: true);
        var translationMode = translationValue == "detect-only"
            ? TranslationWorkflowMode.DetectOnly
            : Enum.Parse<TranslationWorkflowMode>(translationValue, ignoreCase: true);
        var decoderMode = Enum.Parse<DecoderWorkflowMode>(decoderValue, ignoreCase: true);
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");
        var freshOptions = CreateOptions(
            scope.InputPath,
            scope.FreshOutputPath,
            nativeMode,
            translationMode,
            decoderMode
        );
        var resumedOptions = freshOptions with { OutputDirectory = scope.ResumedOutputPath };

        await AnalysisOrchestrator.RunAsync(
            freshOptions,
            TestContext.Current.CancellationToken,
            executingExecutablePath: executable
        );

        await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                resumedOptions,
                TestContext.Current.CancellationToken,
                executingExecutablePath: executable,
                afterResumeStageCommitted: (stage, _) =>
                    stage.Ordinal == interruptedStageOrdinal
                        ? throw new IOException(
                            $"simulated {modeName} interruption after {stage.Id}"
                        )
                        : Task.CompletedTask
            )
        );
        AssertCheckpointPrefix(scope.ResumedOutputPath, interruptedStageOrdinal);

        await AnalysisOrchestrator.ResumeAsync(
            scope.ResumedOutputPath,
            bundleRootOverride: null,
            TestContext.Current.CancellationToken
        );

        Assert.False(File.Exists(Path.Combine(scope.ResumedOutputPath, ".incomplete")));
        AssertCanonicalArtifactParity(scope.FreshOutputPath, scope.ResumedOutputPath);
        await AssertCompleteProvenanceAsync(scope.ResumedOutputPath);
        AssertModeProducedExpectedEvidence(
            scope.ResumedOutputPath,
            nativeMode,
            translationMode,
            decoderMode
        );

        using var summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(scope.ResumedOutputPath, "summary.json"),
                TestContext.Current.CancellationToken
            )
        );
        var resume = summary.RootElement.GetProperty("resume");
        Assert.Equal("resume", resume.GetProperty("attemptMode").GetString());
        Assert.Equal(interruptedStageOrdinal, resume.GetProperty("reusedStages").GetArrayLength());
        Assert.Equal("complete", summary.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task NativeOffWithoutFlossOrOcr_IsRejectedBeforeCreatingResumeState()
    {
        using var scope = new TemporaryScope();
        var options = CreateOptions(
            scope.InputPath,
            scope.ResumedOutputPath,
            NativeExtractionMode.Off,
            TranslationWorkflowMode.Off,
            DecoderWorkflowMode.Off
        );

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            AnalysisOrchestrator.RunAsync(options, TestContext.Current.CancellationToken)
        );

        Assert.Contains("at least one source producer", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(scope.ResumedOutputPath));
    }

    private static void AssertCheckpointPrefix(string outputPath, int expectedLastOrdinal)
    {
        var checkpointDirectory = Path.Combine(
            outputPath,
            AnalysisResumeCore.ResumeDirectoryName,
            "checkpoints"
        );
        var checkpoints = Directory.EnumerateFiles(checkpointDirectory, "*.json").ToArray();
        Assert.Equal(expectedLastOrdinal, checkpoints.Length);
        Assert.Equal(
            Enumerable.Range(1, expectedLastOrdinal),
            checkpoints.Select(path => int.Parse(Path.GetFileName(path)[..4])).Order()
        );
    }

    private static void AssertCanonicalArtifactParity(string freshOutput, string resumedOutput)
    {
        var fresh = SnapshotCommittedArtifacts(freshOutput);
        var resumed = SnapshotCommittedArtifacts(resumedOutput);
        Assert.Equal(fresh.Keys.Order(), resumed.Keys.Order());
        foreach (var artifact in fresh)
        {
            Assert.Equal(artifact.Value, resumed[artifact.Key]);
        }
    }

    private static Dictionary<string, string> SnapshotCommittedArtifacts(string outputPath)
    {
        var checkpointDirectory = Path.Combine(
            outputPath,
            AnalysisResumeCore.ResumeDirectoryName,
            "checkpoints"
        );
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var checkpoint in Directory.EnumerateFiles(checkpointDirectory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(checkpoint));
            foreach (var artifact in document.RootElement.GetProperty("artifacts").EnumerateArray())
            {
                var relativePath = artifact.GetProperty("path").GetString()!;
                var fullPath = Path.Combine(outputPath, relativePath);
                Assert.True(File.Exists(fullPath), $"Missing committed artifact '{relativePath}'.");
                artifacts[relativePath] = Hash(fullPath);
            }
        }
        Assert.NotEmpty(artifacts);
        return artifacts;
    }

    private static async Task AssertCompleteProvenanceAsync(string outputPath)
    {
        var raw = await ReadRecordsAsync(Path.Combine(outputPath, "raw-strings.jsonl"));
        var enriched = await ReadRecordsAsync(Path.Combine(outputPath, "enriched-strings.jsonl"));
        var rawIdentifiers = raw.Select(record => record.RecordId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(raw.Count, rawIdentifiers.Count);
        Assert.Equal(enriched.Count, enriched.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count());

        foreach (var record in raw)
        {
            Assert.False(string.IsNullOrWhiteSpace(record.SourceFile));
            Assert.NotNull(record.Location);
            Assert.NotNull(record.Origin);
            Assert.NotNull(record.Attributes);
            Assert.Null(record.Transform);
            Assert.True(string.IsNullOrWhiteSpace(record.ParentRecordId));
        }

        var enrichedOriginals = enriched
            .Where(record => record.Transform is null)
            .Select(record => record.RecordId)
            .ToArray();
        Assert.Equal(rawIdentifiers.Order(), enrichedOriginals.Order());
        foreach (var child in enriched.Where(record => record.Transform is not null))
        {
            Assert.False(string.IsNullOrWhiteSpace(child.ParentRecordId));
            Assert.Contains(child.ParentRecordId!, rawIdentifiers);
            Assert.False(string.IsNullOrWhiteSpace(child.SourceFile));
            Assert.NotNull(child.Location);
            Assert.NotNull(child.Origin);
            Assert.NotNull(child.Attributes);
        }
    }

    private static async Task<List<EnrichmentStringRecord>> ReadRecordsAsync(string path)
    {
        var records = new List<EnrichmentStringRecord>();
        await foreach (
            var line in EnrichmentJsonlReader.ReadAsync(path, TestContext.Current.CancellationToken)
        )
        {
            EnrichmentRegexPipelineCore.ValidateRecord(line.Record, line.LineNumber);
            records.Add(line.Record);
        }
        return records;
    }

    private static void AssertModeProducedExpectedEvidence(
        string outputPath,
        NativeExtractionMode nativeMode,
        TranslationWorkflowMode translationMode,
        DecoderWorkflowMode decoderMode
    )
    {
        var nativeLength = new FileInfo(Path.Combine(outputPath, "native-strings.jsonl")).Length;
        if (nativeMode == NativeExtractionMode.On)
        {
            Assert.True(nativeLength > 0);
        }
        else
        {
            Assert.Equal(0, nativeLength);
            Assert.Equal(0, new FileInfo(Path.Combine(outputPath, "raw-strings.jsonl")).Length);
        }

        if (translationMode == TranslationWorkflowMode.DetectOnly)
        {
            Assert.True(new FileInfo(Path.Combine(outputPath, "language-assessments.jsonl")).Length > 0);
            Assert.Equal(0, new FileInfo(Path.Combine(outputPath, "translated-strings.jsonl")).Length);
        }
        if (decoderMode != DecoderWorkflowMode.Off)
        {
            Assert.True(new FileInfo(Path.Combine(outputPath, "decoder-assessments.jsonl")).Length > 0);
            Assert.True(new FileInfo(Path.Combine(outputPath, "decoded-strings.jsonl")).Length > 0);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static AnalysisOptions CreateOptions(
        string inputPath,
        string outputPath,
        NativeExtractionMode nativeMode,
        TranslationWorkflowMode translationMode,
        DecoderWorkflowMode decoderMode
    ) =>
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
            TranslationMode: translationMode,
            LanguageDetectionMode: LanguageDetectionMode.Accurate,
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
            NativeExtractionMode: nativeMode,
            DecoderMode: decoderMode
        );

    private sealed class TemporaryScope : IDisposable
    {
        internal TemporaryScope()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-analysis-resume-mode-matrix",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(RootPath);
            InputPath = Path.Combine(RootPath, "input.bin");
            File.WriteAllText(
                InputPath,
                "Cette phrase contient des informations importantes pour une analyse forensique complete.\n"
                    + "analyst@example.com\n"
                    + "YW5hbHlzdEBleGFtcGxlLmNvbQ==\n"
            );
            FreshOutputPath = Path.Combine(RootPath, "fresh");
            ResumedOutputPath = Path.Combine(RootPath, "resumed");
        }

        private string RootPath { get; }
        internal string InputPath { get; }
        internal string FreshOutputPath { get; }
        internal string ResumedOutputPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
