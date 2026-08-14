using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisResumeIntegrationTests
{
    [Fact]
    public async Task AttemptSetup_DoesNotCreateLogsWhenIncompleteMarkerPublicationFails()
    {
        using var scope = new TemporaryScope();
        Directory.CreateDirectory(scope.OutputPath);
        var incompleteMarker = Path.Combine(scope.OutputPath, ".incomplete");
        await File.WriteAllTextAsync(
            incompleteMarker,
            "existing marker",
            TestContext.Current.CancellationToken
        );

        await using (
            var lease = new FileStream(
                incompleteMarker,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None
            )
        )
        {
            var error = await Record.ExceptionAsync(() =>
                AnalysisOrchestrator.PrepareAttemptOutputAsync(
                    scope.OutputPath,
                    TestContext.Current.CancellationToken
                )
            );
            Assert.True(
                error is IOException or UnauthorizedAccessException,
                $"Expected marker publication to fail, but got {error?.GetType().FullName ?? "no exception"}."
            );
        }

        Assert.False(Directory.Exists(Path.Combine(scope.OutputPath, "logs")));
        Assert.Equal(
            "existing marker",
            await File.ReadAllTextAsync(incompleteMarker, TestContext.Current.CancellationToken)
        );
        Assert.Empty(Directory.EnumerateFiles(scope.OutputPath, "*.partial.*"));
    }

    [Fact]
    public async Task Resume_ReusesValidatedWholeStagesAndCompletesWithoutChangingEvidence()
    {
        using var scope = new TemporaryScope();
        var options = CreateOptions(scope.InputPath, scope.OutputPath);
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");

        await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                options,
                TestContext.Current.CancellationToken,
                executingExecutablePath: executable,
                beforeFinalInputVerification: _ =>
                    throw new IOException("simulated interruption after committed stages")
            )
        );

        Assert.True(File.Exists(Path.Combine(scope.OutputPath, ".incomplete")));
        var checkpointDirectory = Path.Combine(
            scope.OutputPath,
            AnalysisResumeCore.ResumeDirectoryName,
            "checkpoints"
        );
        Assert.Equal(13, Directory.EnumerateFiles(checkpointDirectory, "*.json").Count());
        var evidenceNames = new[]
        {
            "native-strings.jsonl",
            "raw-strings.jsonl",
            "decoded-strings.jsonl",
            "enriched-strings.jsonl",
            "regex-matches.jsonl",
            "findings.tsv",
            "pattern-histogram.tsv",
            "feature-histogram.tsv",
            "pattern-histogram.html",
        };
        var before = evidenceNames.ToDictionary(
            name => name,
            name => Hash(Path.Combine(scope.OutputPath, name)),
            StringComparer.Ordinal
        );

        await AnalysisOrchestrator.ResumeAsync(
            scope.OutputPath,
            bundleRootOverride: null,
            TestContext.Current.CancellationToken
        );

        Assert.False(File.Exists(Path.Combine(scope.OutputPath, ".incomplete")));
        foreach (var name in evidenceNames)
        {
            Assert.Equal(before[name], Hash(Path.Combine(scope.OutputPath, name)));
        }
        using var summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(scope.OutputPath, "summary.json"),
                TestContext.Current.CancellationToken
            )
        );
        var resume = summary.RootElement.GetProperty("resume");
        Assert.Equal("resume", resume.GetProperty("attemptMode").GetString());
        Assert.Equal(13, resume.GetProperty("reusedStages").GetArrayLength());
        Assert.Equal("complete", summary.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Resume_QuarantinesUncheckpointedOwnedArtifactAndRerunsItsStage()
    {
        using var scope = new TemporaryScope();
        var options = CreateOptions(scope.InputPath, scope.OutputPath);
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");
        await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                options,
                TestContext.Current.CancellationToken,
                executingExecutablePath: executable,
                beforeFinalInputVerification: _ => throw new IOException("simulated interruption")
            )
        );

        var checkpointDirectory = Path.Combine(
            scope.OutputPath,
            AnalysisResumeCore.ResumeDirectoryName,
            "checkpoints"
        );
        foreach (
            var checkpointName in new[]
            {
                "0010-enriched-merge.json",
                "0011-pattern-matching.json",
                "0012-reports.json",
                "0013-engine-ledger.json",
            }
        )
        {
            var checkpoint = Path.Combine(checkpointDirectory, checkpointName);
            Assert.True(File.Exists(checkpoint));
            File.Delete(checkpoint);
        }

        var staleArtifact = Path.Combine(scope.OutputPath, "enriched-strings.jsonl");
        File.AppendAllText(staleArtifact, "uncommitted-tail");

        await AnalysisOrchestrator.ResumeAsync(
            scope.OutputPath,
            bundleRootOverride: null,
            TestContext.Current.CancellationToken
        );

        Assert.False(File.Exists(Path.Combine(scope.OutputPath, ".incomplete")));
        var abandoned = Directory.GetFiles(
            Path.Combine(scope.OutputPath, "logs"),
            "enriched-strings.jsonl",
            SearchOption.AllDirectories
        );
        Assert.Single(abandoned);
        Assert.EndsWith("uncommitted-tail", File.ReadAllText(abandoned[0]), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "uncommitted-tail",
            File.ReadAllText(Path.Combine(scope.OutputPath, "enriched-strings.jsonl")),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Resume_AfterFinalSummaryPublication_RevalidatesAndFinalizes()
    {
        using var scope = new TemporaryScope();
        var options = CreateOptions(scope.InputPath, scope.OutputPath);
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");
        await AnalysisOrchestrator.RunAsync(
            options,
            TestContext.Current.CancellationToken,
            executingExecutablePath: executable
        );

        await File.WriteAllTextAsync(
            Path.Combine(scope.OutputPath, ".incomplete"),
            "simulated finalization interruption\n",
            TestContext.Current.CancellationToken
        );

        await AnalysisOrchestrator.ResumeAsync(
            scope.OutputPath,
            bundleRootOverride: null,
            TestContext.Current.CancellationToken
        );

        Assert.False(File.Exists(Path.Combine(scope.OutputPath, ".incomplete")));
        var abandoned = Directory.GetFiles(
            Path.Combine(scope.OutputPath, "logs"),
            "summary.json",
            SearchOption.AllDirectories
        );
        Assert.Single(abandoned);
        using var summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(scope.OutputPath, "summary.json"),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("complete", summary.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FinalVerification_RejectsArtifactMutationAfterItsStageCommit()
    {
        using var scope = new TemporaryScope();
        var options = CreateOptions(scope.InputPath, scope.OutputPath);
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                options,
                TestContext.Current.CancellationToken,
                executingExecutablePath: executable,
                beforeFinalInputVerification: _ =>
                {
                    File.AppendAllText(
                        Path.Combine(scope.OutputPath, "pattern-histogram.html"),
                        "tampered-after-commit"
                    );
                    return Task.CompletedTask;
                }
            )
        );

        Assert.Contains("used by another process", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(scope.OutputPath, ".incomplete")));
        Assert.False(File.Exists(Path.Combine(scope.OutputPath, "summary.json")));
    }

    [Fact]
    public async Task Resume_InputDriftRefusesBeforeQuarantiningUncommittedEvidence()
    {
        using var scope = new TemporaryScope();
        var options = CreateOptions(scope.InputPath, scope.OutputPath);
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");
        await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                options,
                TestContext.Current.CancellationToken,
                executingExecutablePath: executable,
                beforeFinalInputVerification: _ => throw new IOException("simulated interruption")
            )
        );
        RemoveCheckpointTail(scope.OutputPath, firstOrdinal: 10);
        var stale = Path.Combine(scope.OutputPath, "enriched-strings.jsonl");
        File.AppendAllText(stale, "uncommitted-tail");
        var input = File.ReadAllBytes(scope.InputPath);
        input[0] ^= 1;
        File.WriteAllBytes(scope.InputPath, input);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisOrchestrator.ResumeAsync(
                scope.OutputPath,
                bundleRootOverride: null,
                TestContext.Current.CancellationToken
            )
        );

        Assert.EndsWith("uncommitted-tail", File.ReadAllText(stale), StringComparison.Ordinal);
        Assert.Empty(
            Directory.GetFiles(
                Path.Combine(scope.OutputPath, "logs"),
                "enriched-strings.jsonl",
                SearchOption.AllDirectories
            )
        );
    }

    [Fact]
    public async Task Resume_UnknownPartialNameRefusesBeforeMovingOwnedTail()
    {
        using var scope = new TemporaryScope();
        var options = CreateOptions(scope.InputPath, scope.OutputPath);
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");
        await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                options,
                TestContext.Current.CancellationToken,
                executingExecutablePath: executable,
                beforeFinalInputVerification: _ => throw new IOException("simulated interruption")
            )
        );
        RemoveCheckpointTail(scope.OutputPath, firstOrdinal: 10);
        var stale = Path.Combine(scope.OutputPath, "enriched-strings.jsonl");
        File.AppendAllText(stale, "uncommitted-tail");
        var unknown = Path.Combine(scope.OutputPath, "notes.partial.txt");
        File.WriteAllText(unknown, "not owned by bstrings");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisOrchestrator.ResumeAsync(
                scope.OutputPath,
                bundleRootOverride: null,
                TestContext.Current.CancellationToken
            )
        );

        Assert.True(File.Exists(unknown));
        Assert.EndsWith("uncommitted-tail", File.ReadAllText(stale), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_RejectsReparseIncompleteMarkerWithoutChangingItsTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The result-path reparse contract is Windows-specific.");
        }
        using var scope = new TemporaryScope();
        var options = CreateOptions(scope.InputPath, scope.OutputPath);
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");
        await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                options,
                TestContext.Current.CancellationToken,
                executingExecutablePath: executable,
                beforeFinalInputVerification: _ => throw new IOException("simulated interruption")
            )
        );
        var marker = Path.Combine(scope.OutputPath, ".incomplete");
        File.Delete(marker);
        var sentinel = scope.PathFor("sentinel.txt");
        File.WriteAllText(sentinel, "unchanged");
        try
        {
            File.CreateSymbolicLink(marker, sentinel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Symbolic links are unavailable on this host: {ex.Message}");
        }
        var attemptCount = Directory.GetFiles(
            Path.Combine(
                scope.OutputPath,
                AnalysisResumeCore.ResumeDirectoryName,
                "attempts"
            )
        ).Length;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            AnalysisOrchestrator.ResumeAsync(
                scope.OutputPath,
                bundleRootOverride: null,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("unchanged", File.ReadAllText(sentinel));
        Assert.Equal(
            attemptCount,
            Directory.GetFiles(
                Path.Combine(
                    scope.OutputPath,
                    AnalysisResumeCore.ResumeDirectoryName,
                    "attempts"
                )
            ).Length
        );
    }

    public static IEnumerable<object[]> ResumeStages =>
        AnalysisResumeStage.All.Select(stage => new object[] { stage.Ordinal, stage.Id });

    [Theory]
    [MemberData(nameof(ResumeStages))]
    public async Task Resume_AfterEveryCommittedStageMatchesACleanRun(
        int stageOrdinal,
        string stageId
    )
    {
        var interruptedStage = AnalysisResumeStage.FromOrdinalAndId(stageOrdinal, stageId);
        using var scope = new TemporaryScope();
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");
        var interruptedOptions = CreateOptions(scope.InputPath, scope.OutputPath);
        var interruption = await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                interruptedOptions,
                TestContext.Current.CancellationToken,
                executingExecutablePath: executable,
                afterResumeStageCommitted: (stage, _) =>
                    stage == interruptedStage
                        ? throw new IOException(
                            $"simulated interruption after {stage.Id}"
                        )
                        : Task.CompletedTask
            )
        );
        Assert.Contains(interruptedStage.Id, interruption.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(scope.OutputPath, ".incomplete")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    scope.OutputPath,
                    AnalysisResumeCore.ResumeDirectoryName,
                    "checkpoints",
                    $"{interruptedStage.Ordinal:D4}-{interruptedStage.Id}.json"
                )
            )
        );

        await AnalysisOrchestrator.ResumeAsync(
            scope.OutputPath,
            bundleRootOverride: null,
            TestContext.Current.CancellationToken
        );
        var freshOutput = scope.PathFor("fresh-output");
        await AnalysisOrchestrator.RunAsync(
            CreateOptions(scope.InputPath, freshOutput),
            TestContext.Current.CancellationToken,
            executingExecutablePath: executable
        );

        AssertEvidenceParity(freshOutput, scope.OutputPath);
        using var summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(scope.OutputPath, "summary.json"),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            interruptedStage.Ordinal,
            summary.RootElement
                .GetProperty("resume")
                .GetProperty("reusedStages")
                .GetArrayLength()
        );
    }

    private static void AssertEvidenceParity(string expectedDirectory, string actualDirectory)
    {
        foreach (
            var name in new[]
            {
                "input-files.txt",
                "input-manifest.jsonl",
                "native-strings.jsonl",
                "recovered-strings.jsonl",
                "ocr-strings.jsonl",
                "ocr-assessments.jsonl",
                "raw-strings.jsonl",
                "language-assessments.jsonl",
                "translation-candidates.jsonl",
                "translated-strings.jsonl",
                "decoded-strings.jsonl",
                "decoder-assessments.jsonl",
                "decoder-work-stats.json",
                "enriched-strings.jsonl",
                "regex-matches.jsonl",
                "findings.tsv",
                "pattern-histogram.tsv",
                "feature-histogram.tsv",
                "pattern-histogram.html",
            }
        )
        {
            var expected = Path.Combine(expectedDirectory, name);
            var actual = Path.Combine(actualDirectory, name);
            Assert.True(File.Exists(expected), $"Fresh evidence is missing '{name}'.");
            Assert.True(File.Exists(actual), $"Resumed evidence is missing '{name}'.");
            Assert.Equal(Hash(expected), Hash(actual));
        }
    }

    private static void RemoveCheckpointTail(string outputPath, int firstOrdinal)
    {
        var checkpointDirectory = Path.Combine(
            outputPath,
            AnalysisResumeCore.ResumeDirectoryName,
            "checkpoints"
        );
        foreach (var checkpoint in Directory.EnumerateFiles(checkpointDirectory, "*.json"))
        {
            if (int.Parse(Path.GetFileName(checkpoint)[..4]) >= firstOrdinal)
            {
                File.Delete(checkpoint);
            }
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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
                "bstrings-analysis-resume-tests",
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
        internal string PathFor(string name) => Path.Combine(RootPath, name);

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
