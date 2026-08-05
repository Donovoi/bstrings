using System.Text.Json;
using System.Security.Cryptography;
using Xunit;

namespace bstrings.Tests;

public sealed class BundleManifestVerifierTests
{
    [Fact]
    public void Verify_AcceptsAnExactPhysicalBundle()
    {
        using var scope = new TemporaryBundle();
        scope.WriteFile("tools/tool.exe", "tool");
        scope.WriteFile("models/model.gguf", "model");
        BundleManifestTestFixture.Write(scope.Root);

        var result = BundleManifestVerifier.Verify(scope.Root);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(9, result.TotalBytes);
        Assert.Equal(
            Convert
                .ToHexString(
                    SHA256.HashData(
                        File.ReadAllBytes(
                            Path.Combine(scope.Root, BundleManifestVerifier.ManifestFileName)
                        )
                    )
                )
                .ToLowerInvariant(),
            result.ManifestSha256
        );
    }

    [Fact]
    public void Verify_RejectsSameLengthContentTampering()
    {
        using var scope = new TemporaryBundle();
        var path = scope.WriteFile("tools/tool.exe", "good");
        BundleManifestTestFixture.Write(scope.Root);
        File.WriteAllText(path, "evil");

        var error = Assert.Throws<InvalidDataException>(() =>
            BundleManifestVerifier.Verify(scope.Root)
        );

        Assert.Contains("SHA-256 mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectsMissingAndUnexpectedFilesBeforeHashing()
    {
        using var scope = new TemporaryBundle();
        var expected = scope.WriteFile("expected.bin", "expected");
        BundleManifestTestFixture.Write(scope.Root);
        File.Delete(expected);
        scope.WriteFile("unexpected.bin", "unexpected");

        var error = Assert.Throws<InvalidDataException>(() =>
            BundleManifestVerifier.Verify(scope.Root)
        );

        Assert.Contains("missing=[expected.bin]", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "unexpected=[unexpected.bin]",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public void Verify_DefaultRejectsAReservedIncompleteMarker()
    {
        using var scope = new TemporaryBundle();
        scope.WriteFile("tools/tool.exe", "tool");
        BundleManifestTestFixture.Write(scope.Root);
        scope.WriteFile(BundleManifestVerifier.IncompleteMarkerFileName, "building");

        var error = Assert.Throws<InvalidDataException>(() =>
            BundleManifestVerifier.Verify(scope.Root)
        );

        Assert.Contains("lingering root .incomplete", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_BuilderOnlyModeRequiresAndExcludesThePhysicalMarker()
    {
        using var scope = new TemporaryBundle();
        scope.WriteFile("tools/tool.exe", "tool");
        BundleManifestTestFixture.Write(scope.Root);

        var missing = Assert.Throws<InvalidDataException>(() =>
            BundleManifestVerifier.Verify(scope.Root, allowIncompleteMarker: true)
        );
        Assert.Contains("requires a root .incomplete", missing.Message, StringComparison.Ordinal);

        scope.WriteFile(BundleManifestVerifier.IncompleteMarkerFileName, "building");
        var result = BundleManifestVerifier.Verify(
            scope.Root,
            allowIncompleteMarker: true
        );

        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void Verify_RejectsAnIncompleteMarkerDirectoryInEveryMode()
    {
        using var scope = new TemporaryBundle();
        scope.WriteFile("tools/tool.exe", "tool");
        BundleManifestTestFixture.Write(scope.Root);
        Directory.CreateDirectory(
            Path.Combine(scope.Root, BundleManifestVerifier.IncompleteMarkerFileName)
        );

        var strict = Assert.Throws<InvalidDataException>(() =>
            BundleManifestVerifier.Verify(scope.Root)
        );
        var builder = Assert.Throws<InvalidDataException>(() =>
            BundleManifestVerifier.Verify(scope.Root, allowIncompleteMarker: true)
        );

        Assert.Contains("physical regular file", strict.Message, StringComparison.Ordinal);
        Assert.Contains("physical regular file", builder.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsCaseInsensitiveManifestDuplicates()
    {
        using var scope = new TemporaryBundle();
        scope.WriteFile("tools/tool.exe", "tool");
        var digest = new string('a', 64);
        File.WriteAllText(
            Path.Combine(scope.Root, BundleManifestVerifier.ManifestFileName),
            $$"""
            {"schemaVersion":1,"files":[
              {"path":"tools/tool.exe","bytes":4,"sha256":"{{digest}}"},
              {"path":"TOOLS/TOOL.EXE","bytes":4,"sha256":"{{digest}}"}
            ]}
            """
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            BundleManifestVerifier.Verify(scope.Root)
        );

        Assert.Contains("duplicate path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("tools\\tool.exe")]
    [InlineData("tools//tool.exe")]
    [InlineData("tools/./tool.exe")]
    [InlineData("C:drive-relative.bin")]
    [InlineData("tools/NUL.txt")]
    [InlineData("tools/trailing. ")]
    [InlineData("airgap-manifest.json")]
    [InlineData(".incomplete")]
    public void ValidateRelativePath_RejectsUnsafeOrAmbiguousWindowsPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() =>
            BundleManifestVerifier.ValidateRelativePath(path)
        );
    }

    [Fact]
    public void Verify_RejectsAReparsePointBeforeReadingItsContent()
    {
        using var scope = new TemporaryBundle();
        var file = scope.WriteFile("tools/tool.exe", "tool");
        BundleManifestTestFixture.Write(scope.Root);

        var error = Assert.Throws<InvalidDataException>(() =>
            BundleManifestVerifier.VerifyForTesting(
                scope.Root,
                path =>
                    string.Equals(path, file, StringComparison.OrdinalIgnoreCase)
                        ? File.GetAttributes(path) | FileAttributes.ReparsePoint
                        : File.GetAttributes(path)
            )
        );

        Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BundleCli_VerifiesThroughTheSameExecutableCommandSurface()
    {
        using var scope = new TemporaryBundle();
        scope.WriteFile("tools/tool.exe", "tool");
        BundleManifestTestFixture.Write(scope.Root);

        var exitCode = await BundleCli.RunAsync(["verify", "--bundle-root", scope.Root]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task BundleCli_BuilderOnlyMarkerOptionIsExplicitAndFailClosed()
    {
        using var scope = new TemporaryBundle();
        scope.WriteFile("tools/tool.exe", "tool");
        BundleManifestTestFixture.Write(scope.Root);

        var missingMarker = await BundleCli.RunAsync(
            ["verify", "--bundle-root", scope.Root, "--allow-incomplete-marker"]
        );
        scope.WriteFile(BundleManifestVerifier.IncompleteMarkerFileName, "building");
        var strict = await BundleCli.RunAsync(["verify", "--bundle-root", scope.Root]);
        var builder = await BundleCli.RunAsync(
            ["verify", "--bundle-root", scope.Root, "--allow-incomplete-marker"]
        );

        Assert.Equal(2, missingMarker);
        Assert.Equal(2, strict);
        Assert.Equal(0, builder);
    }

    [Fact]
    public async Task BundleCli_ReturnsFailureForAnInvalidBundle()
    {
        using var scope = new TemporaryBundle();
        scope.WriteFile("tools/tool.exe", "tool");

        var exitCode = await BundleCli.RunAsync(["verify", "--bundle-root", scope.Root]);

        Assert.Equal(2, exitCode);
    }

    private sealed class TemporaryBundle : IDisposable
    {
        internal TemporaryBundle()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "bstrings-bundle-verifier-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string WriteFile(string relativePath, string content)
        {
            var path = Path.Combine(
                Root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            );
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
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
