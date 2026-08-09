using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisPathSafetyTests
{
    [Fact]
    public void EnsureNoReparsePoints_RejectsAnExistingLinkedAncestor()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        var path = Path.Combine(root, "safe", "linked", "results");
        var linked = Path.Combine(root, "safe", "linked");

        var error = Assert.Throws<ArgumentException>(() =>
            AnalysisOrchestrator.EnsureNoReparsePoints(
                path,
                "results path",
                _ => false,
                candidate =>
                    string.Equals(candidate, Path.Combine(root, "safe"), PathComparison)
                    || string.Equals(candidate, linked, PathComparison),
                candidate =>
                    string.Equals(candidate, linked, PathComparison)
                        ? FileAttributes.Directory | FileAttributes.ReparsePoint
                        : FileAttributes.Directory
            )
        );

        Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(linked, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureNoReparsePoints_AllowsANonexistentPhysicalSuffix()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        var existing = Path.Combine(root, "safe");
        var path = Path.Combine(existing, "new", "results");

        AnalysisOrchestrator.EnsureNoReparsePoints(
            path,
            "results path",
            _ => false,
            candidate => string.Equals(candidate, existing, PathComparison),
            _ => FileAttributes.Directory
        );
    }

    [Fact]
    public void ValidateNativeOutputCompletion_RejectsAStaleIncompleteMarker()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "bstrings-native-marker-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "native-strings.jsonl");
            File.WriteAllText(output, string.Empty);
            File.WriteAllText(output + ".incomplete", "incomplete");

            var error = Assert.Throws<InvalidDataException>(() =>
                AnalysisOrchestrator.ValidateNativeOutputCompletion(output)
            );

            Assert.Contains("incomplete marker", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidateNativeOutputCompletion_AcceptsACompletedOutput()
    {
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jsonl");
        AnalysisOrchestrator.ValidateNativeOutputCompletion(output);
    }

    [Fact]
    public async Task RunWithTranslationCacheCleanup_CancellationPreservesPriorOutputAndRemovesExactArtifacts()
    {
        var directory = CreateTemporaryDirectory("bstrings-translation-cancel-tests");
        var output = Path.Combine(directory, "translated-strings.jsonl");
        var database = Path.Combine(
            directory,
            ".bstrings-translation-cache-cancelled.sqlite3"
        );
        var stagedOutputs = new[]
        {
            output + ".partial.cancelled",
            output + ".partial.second",
        };
        var unrelated = new[]
        {
            database + ".backup",
            Path.Combine(directory, ".bstrings-translation-cache-cancelled.txt"),
            Path.Combine(directory, ".bstrings-translation-cache-.sqlite3"),
            Path.Combine(directory, ".BSTRINGS-translation-cache-uppercase.sqlite3"),
            output + ".partial",
            output + ".partial.",
            Path.Combine(directory, "other-output.jsonl.partial.cancelled"),
        };
        File.WriteAllText(output, "previous translated output");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AnalysisOrchestrator.RunWithTranslationCacheCleanupAsync(
                    output,
                    () =>
                    {
                        foreach (var artifact in new[]
                        {
                            database,
                            database + "-wal",
                            database + "-shm",
                            database + "-journal",
                        })
                        {
                            File.WriteAllText(artifact, "run-local cache");
                        }
                        foreach (var path in stagedOutputs)
                        {
                            File.WriteAllText(path, "unpublished derived text");
                        }
                        foreach (var path in unrelated)
                        {
                            File.WriteAllText(path, "unrelated");
                        }
                        throw new OperationCanceledException("translation child was cancelled");
                    }
                )
            );

            Assert.Equal("previous translated output", File.ReadAllText(output));
            Assert.False(File.Exists(database));
            Assert.False(File.Exists(database + "-wal"));
            Assert.False(File.Exists(database + "-shm"));
            Assert.False(File.Exists(database + "-journal"));
            Assert.All(stagedOutputs, path => Assert.False(File.Exists(path)));
            Assert.All(unrelated, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CleanupTranslationCacheArtifacts_RejectsReparseAmbiguityBeforeDeletingAnything()
    {
        var directory = CreateTemporaryDirectory("bstrings-translation-reparse-tests");
        var output = Path.Combine(directory, "translated-strings.jsonl");
        var database = Path.Combine(directory, ".bstrings-translation-cache-run.sqlite3");
        var stagedOutput = output + ".partial.ambiguous";
        File.WriteAllText(output, "previous translated output");
        File.WriteAllText(database, "cache");
        File.WriteAllText(stagedOutput, "unpublished derived text");
        var deleteAttempts = new List<string>();
        try
        {
            var error = Assert.Throws<IOException>(() =>
                AnalysisOrchestrator.CleanupTranslationCacheArtifacts(
                    output,
                    path =>
                        string.Equals(path, stagedOutput, PathComparison)
                            ? FileAttributes.ReparsePoint
                            : File.GetAttributes(path),
                    path => deleteAttempts.Add(path)
                )
            );

            Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(deleteAttempts);
            Assert.True(File.Exists(database));
            Assert.True(File.Exists(stagedOutput));
            Assert.Equal("previous translated output", File.ReadAllText(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CleanupTranslationCacheArtifacts_DeleteFailureIsSurfacedAndOutputIsPreserved()
    {
        var directory = CreateTemporaryDirectory("bstrings-translation-cleanup-failure-tests");
        var output = Path.Combine(directory, "translated-strings.jsonl");
        var database = Path.Combine(directory, ".bstrings-translation-cache-run.sqlite3");
        File.WriteAllText(output, "previous translated output");
        File.WriteAllText(database, "cache");
        try
        {
            var error = Assert.Throws<IOException>(() =>
                AnalysisOrchestrator.CleanupTranslationCacheArtifacts(
                    output,
                    deleteFile: _ => throw new IOException("forced cleanup failure")
                )
            );

            Assert.Contains("forced cleanup failure", error.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(database));
            Assert.Equal("previous translated output", File.ReadAllText(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_RejectsExtendedPathAliasInsideEvidenceDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Path.Combine(
            Path.GetTempPath(),
            "bstrings-output-alias-tests",
            Guid.NewGuid().ToString("N")
        );
        var evidence = Path.Combine(root, "evidence-with-a-long-name");
        var physicalOutput = Path.Combine(evidence, "results");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "input.txt"), "analyst@example.com");
        try
        {
            var extendedOutput = @"\\?\" + physicalOutput;
            var options = new AnalysisOptions(
                FilePath: null,
                DirectoryPath: evidence,
                Mask: "*",
                OutputDirectory: extendedOutput,
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

            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                AnalysisOrchestrator.RunAsync(
                    options,
                    TestContext.Current.CancellationToken
                )
            );

            Assert.Contains("evidence directory", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(physicalOutput));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string CreateTemporaryDirectory(string category)
    {
        var directory = Path.Combine(Path.GetTempPath(), category, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
