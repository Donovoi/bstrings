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
    public void Locate_MissingConfigurationPointsToTheCompleteOfflineCpuArtifact()
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
                "bstrings-win-x64-offline-cpu",
                error.Message,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.Contains(
                "github.com/Donovoi/bstrings/actions/workflows/dotnet-desktop.yml",
                error.Message,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.DoesNotContain("core-only", error.Message, StringComparison.OrdinalIgnoreCase);
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
        bool includeRecoveryTools = true
    )
    {
        var files = new List<string>
        {
            "runtime/python.exe",
            "tools/bstrings_enrich.py",
            "runtime/llama-server.exe",
            "models/model.gguf",
        };
        if (includeRecoveryTools)
        {
            files.Add("tools/magika.exe");
            files.Add("tools/floss.exe");
        }
        foreach (var relativePath in files)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
        File.Copy(Environment.ProcessPath!, Path.Combine(root, "bstrings.exe"));
        var configuration = new
        {
            schemaVersion = 1,
            bstringsExecutable = "bstrings.exe",
            pythonExecutable = "runtime/python.exe",
            enrichmentAdapter = "tools/bstrings_enrich.py",
            magikaExecutable = "tools/magika.exe",
            flossExecutable = "tools/floss.exe",
            llamaServer = "runtime/llama-server.exe",
            translationModel = new
            {
                path = modelRelativePath,
                id = "fixture/model",
                revision = "fixture-revision",
                sha256 = new string('a', 64),
            },
        };
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
