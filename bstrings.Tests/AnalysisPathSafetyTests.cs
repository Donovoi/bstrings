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
}
