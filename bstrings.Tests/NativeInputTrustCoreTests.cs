using System.Diagnostics;
using System.Security.Cryptography;
using bstrings;
using Xunit;

namespace bstrings.Tests;

public sealed class NativeInputTrustCoreTests
{
    [Fact]
    public async Task OpenVerifiedSourceAsync_RejectsSameSizeReplacement()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(path, "original"u8.ToArray(), TestContext.Current.CancellationToken);
        var expected = await EntryAsync(path);
        await File.WriteAllBytesAsync(path, "replaced"u8.ToArray(), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeInputTrustCore.OpenVerifiedSourceAsync(
                expected,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("identity mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CalibrationSampler_UsesLeasedVerifiedHandleAndBlocksMutateRestore()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "source.bin");
        var original = "original"u8.ToArray();
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        var expected = await EntryAsync(path);
        await using var verified = await NativeInputTrustCore.OpenVerifiedSourceAsync(
            expected,
            TestContext.Current.CancellationToken
        );

        Assert.Throws<IOException>(() => File.WriteAllBytes(path, "mutated!"u8.ToArray()));
        verified.Position = 2;
        var calibrationSamples = ProcessingBackendSession.ReadCalibrationSamples(
            verified,
            expected.Length
        );
        Assert.Equal(2, verified.Position);
        Assert.Single(calibrationSamples);
        Assert.Equal(original, calibrationSamples[0].Data);
        verified.Position = 0;
        var observed = new byte[original.Length];
        await verified.ReadExactlyAsync(observed, TestContext.Current.CancellationToken);
        Assert.Equal(original, observed);
        await NativeInputTrustCore.VerifyIdentityAsync(
            verified,
            expected,
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task OpenVerifiedSourceAsync_RejectsLengthIdentityMismatch()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(path, "original"u8.ToArray(), TestContext.Current.CancellationToken);
        var expected = (await EntryAsync(path)) with { Length = 7 };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeInputTrustCore.OpenVerifiedSourceAsync(
                expected,
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task ReadAndBindManifest_RejectsPathOrderMismatch()
    {
        using var temp = new TemporaryDirectory();
        var first = Path.Combine(temp.Path, "first.bin");
        var second = Path.Combine(temp.Path, "second.bin");
        await File.WriteAllTextAsync(first, "first", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "second", TestContext.Current.CancellationToken);
        var inventoryPath = Path.Combine(temp.Path, "inputs.txt");
        var manifestPath = Path.Combine(temp.Path, "manifest.jsonl");
        await InputEvidenceManifest.CreateAsync(
            inventoryPath,
            manifestPath,
            [first, second],
            TestContext.Current.CancellationToken
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            NativeInputTrustCore.ReadAndBindManifest(manifestPath, [second, first])
        );
        Assert.Contains("path and order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ManifestIdentityFailureLeavesOnlyIncompleteAtomicOutput()
    {
        using var temp = new TemporaryDirectory();
        var first = Path.Combine(temp.Path, "first.bin");
        var second = Path.Combine(temp.Path, "second.bin");
        await File.WriteAllTextAsync(first, "Alpha123", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "Bravo456", TestContext.Current.CancellationToken);
        var inventoryPath = Path.Combine(temp.Path, "inputs.txt");
        var manifestPath = Path.Combine(temp.Path, "manifest.jsonl");
        await InputEvidenceManifest.CreateAsync(
            inventoryPath,
            manifestPath,
            [first, second],
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(second, "Changed!", TestContext.Current.CancellationToken);
        var outputPath = Path.Combine(temp.Path, "native.jsonl");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            var argument in new[]
            {
                typeof(Program).Assembly.Location,
                "--paths-from",
                inventoryPath,
                "--input-manifest",
                manifestPath,
                "--emit-enrichment-jsonl",
                "-o",
                outputPath,
                "-q",
                "-s",
            }
        )
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.NotEqual(0, process.ExitCode);
        Assert.False(File.Exists(outputPath), $"Atomic output was published. stdout={stdout}; stderr={stderr}");
        Assert.True(File.Exists(outputPath + ".incomplete"));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "native.jsonl.partial.*"));
    }

    private static async Task<InputManifestEntry> EntryAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        return new InputManifestEntry
        {
            Path = Path.GetFullPath(path),
            Length = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("bstrings-native-trust-").FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
