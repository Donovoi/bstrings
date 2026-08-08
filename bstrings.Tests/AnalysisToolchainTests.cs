using System.Text.Json;
using System.Security.Cryptography;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisToolchainTests
{
    [Fact]
    public void Locate_ResolvesOnlyFilesInsideTheConfiguredBundle()
    {
        using var scope = new TemporaryDirectory();
        WriteBundle(scope.DirectoryPath, "models/model.gguf");

        var toolchain = AnalysisToolchainLocator.Locate(
            scope.DirectoryPath,
            requireExplicitBundle: true
        );

        Assert.Equal(Path.GetFullPath(scope.DirectoryPath), toolchain.BundleRoot);
        Assert.Equal(
            Path.Combine(scope.DirectoryPath, "tools", "magika.exe"),
            toolchain.MagikaExecutable
        );
        Assert.Equal(new string('a', 64), toolchain.TranslationModelSha256);
        Assert.Equal(BundleManifestVerifier.ManifestFileName, toolchain.BundleIntegrity.Manifest);
        Assert.Equal("bstrings.exe", toolchain.BundleIntegrity.Executable);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Environment.ProcessPath!)))
                .ToLowerInvariant(),
            toolchain.BundleIntegrity.ExecutableSha256
        );
        var manifestPath = Path.Combine(
            scope.DirectoryPath,
            BundleManifestVerifier.ManifestFileName
        );
        Assert.Equal(
            Convert
                .ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)))
                .ToLowerInvariant(),
            toolchain.BundleIntegrity.ManifestSha256
        );
        Assert.Equal(8, toolchain.BundleIntegrity.FileCount);
        Assert.True(toolchain.BundleIntegrity.ByteCount > 0);
    }

    [Fact]
    public void Locate_RejectsConfigurationPathTraversal()
    {
        using var scope = new TemporaryDirectory();
        WriteBundle(scope.DirectoryPath, "../outside.gguf");

        var error = Assert.Throws<InvalidDataException>(() =>
            AnalysisToolchainLocator.Locate(scope.DirectoryPath, requireExplicitBundle: true)
        );

        Assert.Contains("escapes", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locate_TranslationOnlyDoesNotRequireRecoveryTools()
    {
        using var scope = new TemporaryDirectory();
        WriteBundle(
            scope.DirectoryPath,
            "models/model.gguf",
            includeRecoveryTools: false
        );

        var toolchain = AnalysisToolchainLocator.Locate(
            scope.DirectoryPath,
            requireExplicitBundle: true,
            requireRecovery: false,
            requireTranslation: true
        );

        Assert.Null(toolchain.MagikaExecutable);
        Assert.Null(toolchain.FlossExecutable);
        Assert.NotNull(toolchain.TranslationModelPath);
    }

    [Fact]
    public void Locate_OcrOnlyUsesItsIndependentRuntimeAdapterAndModelIdentity()
    {
        using var scope = new TemporaryDirectory();
        WriteBundle(
            scope.DirectoryPath,
            "models/model.gguf",
            includeRecoveryTools: false,
            includeOcr: true
        );

        var toolchain = AnalysisToolchainLocator.Locate(
            scope.DirectoryPath,
            requireExplicitBundle: true,
            requireRecovery: false,
            requireTranslation: false,
            requireOcr: true
        );

        Assert.Equal(
            Path.Combine(scope.DirectoryPath, "ocr", "python.exe"),
            toolchain.OcrPythonExecutable
        );
        Assert.Equal(
            Path.Combine(scope.DirectoryPath, "ocr", "ocr-adapter.py"),
            toolchain.OcrAdapter
        );
        Assert.Equal(Path.Combine(scope.DirectoryPath, "ocr", "ocr.exe"), toolchain.OcrExecutable);
        Assert.Equal("fixture-ocr", toolchain.OcrEngine);
        Assert.Equal("1.0.0", toolchain.OcrEngineVersion);
        Assert.Equal("fixture/ocr-model", toolchain.OcrModelId);
        Assert.Equal("fixture-ocr-revision", toolchain.OcrModelRevision);
        Assert.Equal(new string('b', 64), toolchain.OcrModelSha256);
        Assert.Equal(
            Path.Combine(scope.DirectoryPath, "tools", "magika.exe"),
            toolchain.MagikaExecutable
        );
        Assert.Null(toolchain.FlossExecutable);
        Assert.Null(toolchain.TranslationModelPath);
    }

    [Fact]
    public void OcrCommands_PassTheExactRequestedProviderToSelfTestAndAnalysis()
    {
        using var scope = new TemporaryDirectory();
        WriteBundle(
            scope.DirectoryPath,
            "models/model.gguf",
            includeRecoveryTools: false,
            includeOcr: true
        );
        var toolchain = AnalysisToolchainLocator.Locate(
            scope.DirectoryPath,
            requireExplicitBundle: true,
            requireRecovery: false,
            requireTranslation: false,
            requireOcr: true
        );
        var identity = new[]
        {
            "--ocr-executable",
            toolchain.OcrExecutable!,
            "--ocr-engine",
            toolchain.OcrEngine!,
            "--ocr-engine-version",
            toolchain.OcrEngineVersion!,
            "--ocr-model-path",
            toolchain.OcrModelPath!,
            "--ocr-model-id",
            toolchain.OcrModelId!,
            "--ocr-model-revision",
            toolchain.OcrModelRevision!,
            "--ocr-model-sha256",
            toolchain.OcrModelSha256!,
        };

        Assert.Equal(
            new[]
            {
                "-I",
                "-B",
                toolchain.OcrAdapter!,
                "--airgap",
                "--self-test",
            }
                .Concat(identity)
                .Concat(["--provider", "directml", "--threads", "0"]),
            AnalysisOrchestrator.BuildOcrPreflightArguments(
                toolchain,
                OcrProvider.DirectMl,
                0
            )
        );
        Assert.Equal(
            new[]
            {
                "-I",
                "-B",
                toolchain.OcrAdapter!,
                "--airgap",
                "--paths-from",
                "inventory.txt",
                "--input-manifest",
                "manifest.jsonl",
                "--routing-manifest",
                "routing.jsonl",
                "--output",
                "strings.jsonl",
                "--assessments-output",
                "assessments.jsonl",
            }
                .Concat(identity)
                .Concat(
                    [
                        "--provider",
                        "hybrid",
                        "--threads",
                        "7",
                        "--ocr-mode",
                        "force",
                        "--progress-total-files",
                        "123",
                    ]
                ),
            AnalysisOrchestrator.BuildOcrAnalysisArguments(
                toolchain,
                OcrWorkflowMode.Force,
                OcrProvider.Hybrid,
                7,
                "inventory.txt",
                "manifest.jsonl",
                "routing.jsonl",
                "strings.jsonl",
                "assessments.jsonl",
                123
            )
        );
    }

    [Fact]
    public void Locate_MissingConfigurationPointsToTheReleaseInstaller()
    {
        using var scope = new TemporaryDirectory();
        var previousBundle = Environment.GetEnvironmentVariable("BSTRINGS_AIRGAP_BUNDLE");
        try
        {
            Environment.SetEnvironmentVariable("BSTRINGS_AIRGAP_BUNDLE", null);

            var error = Assert.Throws<DirectoryNotFoundException>(() =>
                AnalysisToolchainLocator.Locate(
                    scope.DirectoryPath,
                    requireExplicitBundle: true
                )
            );

            Assert.Contains(
                "Install-BstringsQuality.ps1",
                error.Message,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.Contains(
                "github.com/Donovoi/bstrings/blob/v1.9.12/README.md#get-started",
                error.Message,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.DoesNotContain(
                "bundle acquire",
                error.Message,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.DoesNotContain(
                "bundle-packs-quality.json",
                error.Message,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.DoesNotContain("offline-cpu", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("actions/workflows", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSTRINGS_AIRGAP_BUNDLE", previousBundle);
        }
    }

    [Fact]
    public void Locate_VerifiesTheBundleBeforeUsingItsToolchain()
    {
        using var scope = new TemporaryDirectory();
        WriteBundle(scope.DirectoryPath, "models/model.gguf");
        File.AppendAllText(Path.Combine(scope.DirectoryPath, "tools", "magika.exe"), "tampered");

        var error = Assert.Throws<InvalidDataException>(() =>
            AnalysisToolchainLocator.Locate(scope.DirectoryPath, requireExplicitBundle: true)
        );

        Assert.Contains("size mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locate_RejectsAValidBundleWhenTheRunningExecutableDoesNotMatch()
    {
        using var scope = new TemporaryDirectory();
        WriteBundle(scope.DirectoryPath, "models/model.gguf");
        var outsideExecutable = Path.Combine(
            Path.GetTempPath(),
            "bstrings-mismatched-executable-" + Guid.NewGuid().ToString("N") + ".exe"
        );
        try
        {
            File.WriteAllText(outsideExecutable, "different executable bytes");

            var error = Assert.Throws<InvalidDataException>(() =>
                AnalysisToolchainLocator.Locate(
                    scope.DirectoryPath,
                    requireExplicitBundle: true,
                    executingExecutablePath: outsideExecutable
                )
            );

            Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outsideExecutable);
        }
    }

    private static void WriteBundle(
        string root,
        string modelRelativePath,
        bool includeRecoveryTools = true,
        bool includeOcr = false
    )
    {
        var files = new List<string>
        {
            "runtime/python.exe",
            "tools/bstrings_enrich.py",
            "runtime/llama-server.exe",
            "models/model.gguf",
        };
        if (includeRecoveryTools || includeOcr)
        {
            files.Add("tools/magika.exe");
        }
        if (includeRecoveryTools)
        {
            files.Add("tools/floss.exe");
        }
        if (includeOcr)
        {
            files.Add("ocr/python.exe");
            files.Add("ocr/ocr.exe");
            files.Add("ocr/ocr-adapter.py");
            files.Add("ocr/model.pack");
        }
        foreach (var relativePath in files)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
        File.Copy(Environment.ProcessPath!, Path.Combine(root, "bstrings.exe"));
        var configuration = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["bstringsExecutable"] = "bstrings.exe",
            ["pythonExecutable"] = "runtime/python.exe",
            ["enrichmentAdapter"] = "tools/bstrings_enrich.py",
            ["magikaExecutable"] = "tools/magika.exe",
            ["flossExecutable"] = "tools/floss.exe",
            ["llamaServer"] = "runtime/llama-server.exe",
            ["translationModel"] = new
            {
                path = modelRelativePath,
                id = "fixture/model",
                revision = "fixture-revision",
                sha256 = new string('a', 64),
            },
        };
        if (includeOcr)
        {
            configuration["ocr"] = new
            {
                pythonExecutable = "ocr/python.exe",
                executable = "ocr/ocr.exe",
                adapter = "ocr/ocr-adapter.py",
                engine = "fixture-ocr",
                engineVersion = "1.0.0",
                model = new
                {
                    path = "ocr/model.pack",
                    id = "fixture/ocr-model",
                    revision = "fixture-ocr-revision",
                    sha256 = new string('b', 64),
                },
            };
        }
        File.WriteAllText(
            Path.Combine(root, "airgap-config.json"),
            JsonSerializer.Serialize(configuration)
        );
        BundleManifestTestFixture.Write(root);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-toolchain-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
