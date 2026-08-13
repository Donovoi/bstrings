using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisBundleIntegrityTests
{
    [Fact]
    public async Task Analyze_WritesTheExactVerifiedBundleIdentityToBothReports()
    {
        using var scope = new TemporaryAnalysis();
        var verification = scope.CreateBundle();
        var evidence = scope.WriteFile("evidence.txt", "Contact analyst@example.com for review.");
        var output = Path.Combine(scope.Root, "results");

        var result = await RunAnalysisAsync(scope.BundleRoot, evidence, output);

        Assert.Equal(0, result.ExitCode);
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "run.json")));
        using var summary = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(output, "summary.json"))
        );
        var runIntegrity = run.RootElement.GetProperty("bundleIntegrity");
        var summaryIntegrity = summary.RootElement.GetProperty("bundleIntegrity");
        var runInput = run.RootElement.GetProperty("input");
        var summaryInput = summary.RootElement.GetProperty("inputIdentity");
        Assert.Equal(runIntegrity.GetRawText(), summaryIntegrity.GetRawText());
        Assert.Equal(0, run.RootElement.GetProperty("preservationFallbacks").GetInt64());
        Assert.Equal(0, summary.RootElement.GetProperty("preservationFallbacks").GetInt64());
        Assert.Equal(
            runInput.GetProperty("inventorySha256").GetString(),
            summaryInput.GetProperty("inventorySha256").GetString()
        );
        Assert.Equal(
            Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "input-files.txt")))
                )
                .ToLowerInvariant(),
            runInput.GetProperty("inventorySha256").GetString()
        );
        Assert.Equal(
            BundleManifestVerifier.ManifestFileName,
            runIntegrity.GetProperty("manifest").GetString()
        );
        Assert.Equal(
            verification.ManifestSha256,
            runIntegrity.GetProperty("manifestSha256").GetString()
        );
        Assert.Equal(verification.FileCount, runIntegrity.GetProperty("fileCount").GetInt64());
        Assert.Equal(verification.TotalBytes, runIntegrity.GetProperty("byteCount").GetInt64());
        Assert.Equal("bstrings.exe", runIntegrity.GetProperty("executable").GetString());
        Assert.Equal(
            Convert.ToHexString(
                    SHA256.HashData(
                        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "bstrings.exe"))
                    )
                )
                .ToLowerInvariant(),
            runIntegrity.GetProperty("executableSha256").GetString()
        );
        Assert.Equal(
            verification.ManifestSha256.ToLowerInvariant(),
            verification.ManifestSha256
        );
    }

    [Fact]
    public async Task Analyze_RejectsTamperingBeforeCreatingAResultsDirectory()
    {
        using var scope = new TemporaryAnalysis();
        scope.CreateBundle();
        File.AppendAllText(
            Path.Combine(scope.BundleRoot, "runtime", "python.exe"),
            "tampered"
        );
        var evidence = scope.WriteFile("evidence.txt", "analyst@example.com");
        var output = Path.Combine(scope.Root, "tampered-results");

        var result = await RunAnalysisAsync(scope.BundleRoot, evidence, output);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("size mismatch", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void OcrPreflight_RejectsAnUnverifiedModelWhenOcrCandidatesExist()
    {
        using var scope = new TemporaryAnalysis();
        scope.CreateBundle(ocrModelSha256: new string('0', 64));
        var toolchain = AnalysisToolchainLocator.Locate(
            scope.BundleRoot,
            requireExplicitBundle: true,
            requireRecovery: false,
            requireTranslation: false,
            requireOcr: true,
            executingExecutablePath: scope.BstringsExecutable
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            OcrCompletionCore.CreateValidationRequirements(
                toolchain,
                OcrWorkflowMode.Auto,
                OcrProvider.Auto,
                0
            )
        );

        Assert.Contains("OCR model SHA-256 mismatch", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("detect-only")]
    public async Task Analyze_ImplicitCompleteBundleRecordsIntegrityForEveryMode(
        string translationMode
    )
    {
        using var scope = new TemporaryAnalysis();
        var verification = scope.CreateBundle();
        var evidence = scope.WriteFile(
            "implicit-evidence.txt",
            "La cuenta analyst@example.com necesita una revision detallada."
        );
        var output = Path.Combine(scope.Root, $"implicit-{translationMode}-results");

        var result = await RunAnalysisAsync(
            scope.BundleRoot,
            evidence,
            output,
            translationMode,
            useBundleEnvironment: true
        );

        Assert.Equal(0, result.ExitCode);
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "run.json")));
        using var summary = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(output, "summary.json"))
        );
        AssertRecordedIntegrity(run, summary, verification);
    }

    [Fact]
    public async Task Analyze_CoreWithoutBundleConfigurationDoesNotInventBundleIntegrity()
    {
        using var scope = new TemporaryAnalysis();
        var evidence = scope.WriteFile("core-evidence.txt", "Contact analyst@example.com.");
        var output = Path.Combine(scope.Root, "core-results");

        var result = await RunAnalysisAsync(null, evidence, output);

        Assert.Equal(0, result.ExitCode);
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "run.json")));
        using var summary = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(output, "summary.json"))
        );
        Assert.False(run.RootElement.TryGetProperty("bundleIntegrity", out _));
        Assert.False(summary.RootElement.TryGetProperty("bundleIntegrity", out _));
    }

    [Fact]
    public void ImplicitBundleDetection_TreatsEitherAdjacentMarkerAsACompleteBundle()
    {
        using var scope = new TemporaryAnalysis();

        Assert.False(
            AnalysisToolchainLocator.HasImplicitBundleConfiguration(scope.Root, null)
        );
        scope.CreateBundle();
        Assert.True(
            AnalysisToolchainLocator.HasImplicitBundleConfiguration(scope.BundleRoot, null)
        );
        File.Delete(Path.Combine(scope.BundleRoot, "airgap-config.json"));
        Assert.True(
            AnalysisToolchainLocator.HasImplicitBundleConfiguration(scope.BundleRoot, null)
        );
        File.Delete(Path.Combine(scope.BundleRoot, BundleManifestVerifier.ManifestFileName));
        Directory.CreateDirectory(
            Path.Combine(scope.BundleRoot, BundleManifestVerifier.ManifestFileName)
        );
        Assert.True(
            AnalysisToolchainLocator.HasImplicitBundleConfiguration(scope.BundleRoot, null)
        );
    }

    [Fact]
    public async Task Analyze_VerifiesBundleBeforeCallingNativeLanguageDetector()
    {
        using var scope = new TemporaryAnalysis();
        scope.CreateBundle();
        File.AppendAllText(
            Path.Combine(scope.BundleRoot, "runtime", "python.exe"),
            "tampered"
        );
        var evidence = scope.WriteFile("ordered-evidence.txt", "analyst@example.com");
        var output = Path.Combine(scope.Root, "ordered-results");
        var detectorCalled = false;
        bool VerifyDetector(out string? error)
        {
            detectorCalled = true;
            error = null;
            return true;
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () =>
                AnalysisOrchestrator.RunAsync(
                    CreateOptions(scope.BundleRoot, evidence, output),
                    TestContext.Current.CancellationToken,
                    VerifyDetector
                )
        );

        Assert.Contains("size mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(detectorCalled);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Analyze_RejectsOutputInsideVerifiedBundleBeforeCallingLanguageDetector()
    {
        using var scope = new TemporaryAnalysis();
        scope.CreateBundle();
        var evidence = scope.WriteFile("outside-evidence.txt", "analyst@example.com");
        var output = Path.Combine(scope.BundleRoot, "empty-results");
        Directory.CreateDirectory(output);
        var detectorCalled = false;
        bool VerifyDetector(out string? error)
        {
            detectorCalled = true;
            error = null;
            return true;
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () =>
                AnalysisOrchestrator.RunAsync(
                    CreateOptions(scope.BundleRoot, evidence, output),
                    TestContext.Current.CancellationToken,
                    VerifyDetector,
                    scope.BstringsExecutable
                )
        );

        Assert.Contains("verified bundle", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(detectorCalled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    private static async Task<ProcessResult> RunAnalysisAsync(
        string? bundleRoot,
        string evidence,
        string output,
        string translationMode = "off",
        bool useBundleEnvironment = false
    )
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
        if (useBundleEnvironment)
        {
            Assert.False(string.IsNullOrWhiteSpace(bundleRoot));
            startInfo.Environment["BSTRINGS_AIRGAP_BUNDLE"] = bundleRoot;
        }

        var arguments = new List<string>
        {
            "analyze",
            "-f",
            evidence,
            "-o",
            output,
        };
        if (!useBundleEnvironment && bundleRoot is not null)
        {
            arguments.Add("--bundle-root");
            arguments.Add(bundleRoot);
        }
        arguments.AddRange(
            [
                "--recover-executable-strings",
                "off",
                "--translation",
                translationMode,
                "--processor",
                "cpu",
                "--cpu-engine",
                "dotnet",
                "--maximum-length",
                "4096",
                "--lr",
                "email",
            ]
        );
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the bstrings test process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError
        );
    }

    private static AnalysisOptions CreateOptions(
        string bundleRoot,
        string evidence,
        string output
    ) =>
        new(
            evidence,
            null,
            null,
            output,
            Full: false,
            OcrWorkflowMode.Off,
            OcrProvider.Auto,
            0,
            ExecutableRecoveryMode.Off,
            TranslationWorkflowMode.DetectOnly,
            LanguageDetectionMode.Adaptive,
            LanguageTriagePolicy.HighRecall,
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
            BundleRoot: bundleRoot,
            Airgap: false
        );

    private static void AssertRecordedIntegrity(
        JsonDocument run,
        JsonDocument summary,
        BundleVerificationResult verification
    )
    {
        var runIntegrity = run.RootElement.GetProperty("bundleIntegrity");
        var summaryIntegrity = summary.RootElement.GetProperty("bundleIntegrity");
        Assert.Equal(runIntegrity.GetRawText(), summaryIntegrity.GetRawText());
        Assert.Equal(
            verification.ManifestSha256,
            runIntegrity.GetProperty("manifestSha256").GetString()
        );
        Assert.Equal(verification.FileCount, runIntegrity.GetProperty("fileCount").GetInt64());
        Assert.Equal(verification.TotalBytes, runIntegrity.GetProperty("byteCount").GetInt64());
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError
    );

    private sealed class TemporaryAnalysis : IDisposable
    {
        internal TemporaryAnalysis()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "bstrings-bundle-provenance-tests",
                Guid.NewGuid().ToString("N")
            );
            BundleRoot = Path.Combine(Root, "bundle");
            Directory.CreateDirectory(BundleRoot);
        }

        internal string Root { get; }
        internal string BundleRoot { get; }
        internal string BstringsExecutable => Path.Combine(BundleRoot, "bstrings.exe");

        internal BundleVerificationResult CreateBundle(string? ocrModelSha256 = null)
        {
            WriteBundleFile("runtime/python.exe", "private-python");
            WriteBundleFile("tools/bstrings_enrich.py", "private-adapter");
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "bstrings.exe"),
                BstringsExecutable
            );
            var configuration = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 2,
                ["bundleProfile"] = "windows-x64-offline-v3",
                ["bstringsExecutable"] = "bstrings.exe",
                ["pythonExecutable"] = "runtime/python.exe",
                ["enrichmentAdapter"] = "tools/bstrings_enrich.py",
            };
            if (ocrModelSha256 is not null)
            {
                WriteBundleFile("tools/magika.exe", "private-magika");
                WriteBundleFile("ocr/python.exe", "private-ocr-python");
                WriteBundleFile("ocr/ocr.exe", "private-ocr-engine");
                WriteBundleFile("ocr/ocr-adapter.py", "private-ocr-adapter");
                WriteBundleFile("ocr/model-pack.json", "private-ocr-model-pack");
                configuration["ocr"] = new
                {
                    pythonExecutable = "ocr/python.exe",
                    executable = "ocr/ocr.exe",
                    adapter = "ocr/ocr-adapter.py",
                    engine = "fixture-ocr",
                    engineVersion = "1.0.0",
                    model = new
                    {
                        path = "ocr/model-pack.json",
                        id = "fixture/ocr-model",
                        revision = "revision-1",
                        sha256 = ocrModelSha256,
                    },
                };
                configuration["magikaExecutable"] = "tools/magika.exe";
            }
            File.WriteAllText(
                Path.Combine(BundleRoot, "airgap-config.json"),
                JsonSerializer.Serialize(configuration)
            );
            BundleManifestTestFixture.Write(BundleRoot);
            return BundleManifestVerifier.Verify(BundleRoot);
        }

        internal string WriteFile(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private void WriteBundleFile(string relativePath, string content)
        {
            var path = Path.Combine(
                BundleRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            );
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
