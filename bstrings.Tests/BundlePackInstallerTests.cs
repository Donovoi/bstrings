using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class BundlePackInstallerTests
{
    [Theory]
    [InlineData("\"unexpected\":true,", "unknown root property")]
    [InlineData("\"profile\":\"duplicate\",", "duplicate property")]
    public void ReadTrustManifest_RejectsUnknownOrDuplicateRootProperties(
        string injectedProperty,
        string expectedMessage
    )
    {
        using var scope = new BundlePackScope();
        File.WriteAllText(
            scope.TrustManifestPath,
            $$"""
            {"schemaVersion":1,{{injectedProperty}}"profile":"win-x64-offline-quality","bundleIdentity":"bstrings-1.9.0-win-x64-offline-quality","airgapManifestSha256":"{{new string('a', 64)}}","packs":[{"id":"core","url":"https://example.test/core.zip","bytes":1,"sha256":"{{new string('b', 64)}}"}]}
            """
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.ReadTrustManifest(scope.TrustManifestPath)
        );

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://example.test/core.zip", 1, "HTTPS")]
    [InlineData("https://example.test/core.zip", 2_000_000_000L, "1 through")]
    public void ReadTrustManifest_RequiresHttpsAndPackLengthsBelowTwoBillion(
        string url,
        long bytes,
        string expectedMessage
    )
    {
        using var scope = new BundlePackScope();
        File.WriteAllText(
            scope.TrustManifestPath,
            $$"""
            {"schemaVersion":1,"profile":"win-x64-offline-quality","bundleIdentity":"bstrings-1.9.0-win-x64-offline-quality","airgapManifestSha256":"{{new string('a', 64)}}","packs":[{"id":"core","url":"{{url}}","bytes":{{bytes}},"sha256":"{{new string('b', 64)}}"}]}
            """
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.ReadTrustManifest(scope.TrustManifestPath)
        );

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadTrustManifest_AcceptsLegacyZipAndBoundedLargeFilePacks()
    {
        using var scope = new BundlePackScope();
        File.WriteAllText(
            scope.TrustManifestPath,
            $$"""
            {"schemaVersion":1,"profile":"win-x64-offline-quality","bundleIdentity":"bstrings-1.9.0-win-x64-offline-quality","airgapManifestSha256":"{{new string('a', 64)}}","packs":[{"id":"core","url":"https://example.test/core.zip","bytes":1,"sha256":"{{new string('b', 64)}}"},{"id":"translation-q8","kind":"file","target":"models/translation-q8.gguf","url":"https://huggingface.co/example/repo/resolve/revision/translation-q8.gguf","bytes":{{BundlePackInstaller.MaximumFilePackBytes}},"sha256":"{{new string('c', 64)}}"}]}
            """
        );

        var manifest = BundlePackInstaller.ReadTrustManifest(scope.TrustManifestPath);

        Assert.Equal(BundlePackKind.Zip, manifest.Packs[0].Kind);
        Assert.Null(manifest.Packs[0].Target);
        Assert.Equal(BundlePackKind.File, manifest.Packs[1].Kind);
        Assert.Equal("models/translation-q8.gguf", manifest.Packs[1].Target);
        Assert.Equal(BundlePackInstaller.MaximumFilePackBytes, manifest.Packs[1].Bytes);
    }

    [Theory]
    [InlineData("../model.gguf")]
    [InlineData("/rooted/model.gguf")]
    [InlineData("C:/rooted/model.gguf")]
    [InlineData("models\\model.gguf")]
    [InlineData("models/model.gguf:stream")]
    [InlineData("models/NUL.gguf")]
    [InlineData("Airgap-manifest.json")]
    public void ReadTrustManifest_RejectsUnsafeFileTargets(string target)
    {
        using var scope = new BundlePackScope();
        File.WriteAllText(
            scope.TrustManifestPath,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    profile = "win-x64-offline-quality",
                    bundleIdentity = "bstrings-1.9.0-win-x64-offline-quality",
                    airgapManifestSha256 = new string('a', 64),
                    packs = new[]
                    {
                        new
                        {
                            id = "translation-q8",
                            kind = "file",
                            target,
                            url = "https://huggingface.co/example/repo/resolve/revision/model.gguf",
                            bytes = 1,
                            sha256 = new string('b', 64),
                        },
                    },
                }
            )
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.ReadTrustManifest(scope.TrustManifestPath)
        );

        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadTrustManifest_AcceptsCanonicalFinalManifestFilePack()
    {
        using var scope = new BundlePackScope();
        File.WriteAllText(
            scope.TrustManifestPath,
            $$"""
            {"schemaVersion":1,"profile":"win-x64-offline-quality","bundleIdentity":"bstrings-1.9.0-win-x64-offline-quality","airgapManifestSha256":"{{new string('a', 64)}}","packs":[{"id":"airgap-manifest","kind":"file","target":"airgap-manifest.json","url":"https://example.test/airgap-manifest.json","bytes":1,"sha256":"{{new string('b', 64)}}"}]}
            """
        );

        var manifest = BundlePackInstaller.ReadTrustManifest(scope.TrustManifestPath);

        Assert.Equal(BundlePackKind.File, manifest.Packs[0].Kind);
        Assert.Equal(BundleManifestVerifier.ManifestFileName, manifest.Packs[0].Target);
    }

    [Theory]
    [InlineData(2, "file", "models/model.gguf", 1, "schema version")]
    [InlineData(1, "directory", "models/model.gguf", 1, "zip' or 'file")]
    [InlineData(1, "file", null, 1, "target")]
    [InlineData(1, "file", "models/model.gguf", 68719476737L, "1 through")]
    public void ReadTrustManifest_RejectsUnsupportedSchemaKindOrFileSize(
        int schemaVersion,
        string kind,
        string? target,
        long bytes,
        string expectedMessage
    )
    {
        using var scope = new BundlePackScope();
        var pack = new Dictionary<string, object?>
        {
            ["id"] = "translation-q8",
            ["kind"] = kind,
            ["url"] = "https://huggingface.co/example/repo/resolve/revision/model.gguf",
            ["bytes"] = bytes,
            ["sha256"] = new string('b', 64),
        };
        if (target is not null)
        {
            pack["target"] = target;
        }
        File.WriteAllText(
            scope.TrustManifestPath,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion,
                    profile = "win-x64-offline-quality",
                    bundleIdentity = "bstrings-1.9.0-win-x64-offline-quality",
                    airgapManifestSha256 = new string('a', 64),
                    packs = new[] { pack },
                }
            )
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.ReadTrustManifest(scope.TrustManifestPath)
        );

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadTrustManifest_RejectsTargetOnZipPack()
    {
        using var scope = new BundlePackScope();
        File.WriteAllText(
            scope.TrustManifestPath,
            $$"""
            {"schemaVersion":1,"profile":"win-x64-offline-quality","bundleIdentity":"bstrings-1.9.0-win-x64-offline-quality","airgapManifestSha256":"{{new string('a', 64)}}","packs":[{"id":"core","kind":"zip","target":"models/model.gguf","url":"https://example.test/core.zip","bytes":1,"sha256":"{{new string('b', 64)}}"}]}
            """
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.ReadTrustManifest(scope.TrustManifestPath)
        );

        Assert.Contains("must not contain target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assemble_VerifiesEveryPackAndPublishesTheCompleteBundleAtomically()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack();

        var result = BundlePackInstaller.Assemble(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("win-x64-offline-quality", result.Profile);
        Assert.Equal("bstrings-1.9.0-win-x64-offline-quality", result.BundleIdentity);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(fixture.AirgapManifestSha256, result.AirgapManifestSha256);
        Assert.True(File.Exists(Path.Combine(scope.OutputDirectory, "tools", "tool.exe")));
        Assert.True(File.Exists(Path.Combine(scope.OutputDirectory, "models", "model.bin")));
        BundleManifestVerifier.Verify(scope.OutputDirectory);
        Assert.True(File.Exists(scope.CachePath(fixture.Packs[0].Id)));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public void Assemble_ReportsPackAssemblyAndFinalVerificationProgress()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack();
        var updates = new List<(string Activity, long Completed, long Total)>();

        BundlePackInstaller.Assemble(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken,
            (activity, completed, total) => updates.Add((activity, completed, total))
        );

        Assert.Contains(updates, update =>
            update.Activity.StartsWith("bundle pack verification (", StringComparison.Ordinal)
                && update.Completed == update.Total
        );
        Assert.Contains(updates, update =>
            update.Activity == "bundle assembly" && update.Completed == update.Total
        );
        Assert.Contains(updates, update =>
            update.Activity == "bundle verification" && update.Completed == update.Total
        );
    }

    [Fact]
    public void Assemble_StagesVerifiedFilePackBeforeStrictFinalManifestVerification()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSplitPack();

        var result = BundlePackInstaller.Assemble(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, result.FileCount);
        Assert.Equal(fixture.AirgapManifestSha256, result.AirgapManifestSha256);
        Assert.Equal(
            "full-quality-q8-model",
            File.ReadAllText(
                Path.Combine(scope.OutputDirectory, "models", "translation-q8.gguf")
            )
        );
        Assert.True(
            File.Exists(scope.CachePath("translation-q8", BundlePackKind.File))
        );
        BundleManifestVerifier.Verify(scope.OutputDirectory);
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public void Assemble_AcceptsTheProductionSplitPackManifestAsAFilePack()
    {
        using var scope = new BundlePackScope();
        var tool = "tool"u8.ToArray();
        var model = "full-quality-q8-model"u8.ToArray();
        var files = new Dictionary<string, byte[]>
        {
            ["tools/tool.exe"] = tool,
            ["models/translation-q8.gguf"] = model,
        };
        var airgapManifest = CreateAirgapManifest(files);
        var core = scope.CachePack(
            "base",
            CreateZip(new Dictionary<string, byte[]> { ["tools/tool.exe"] = tool })
        );
        var configuration = scope.CachePack(
            "airgap-manifest",
            airgapManifest,
            BundlePackKind.File,
            BundleManifestVerifier.ManifestFileName
        );
        var modelPack = scope.CachePack(
            "translation-model",
            model,
            BundlePackKind.File,
            "models/translation-q8.gguf"
        );
        var trust = scope.WriteTrustManifest(
            [core, configuration, modelPack],
            Sha256(airgapManifest)
        );

        var result = BundlePackInstaller.Assemble(
            trust,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, result.FileCount);
        Assert.Equal(Sha256(airgapManifest), result.AirgapManifestSha256);
        Assert.Equal(
            model,
            File.ReadAllBytes(
                Path.Combine(scope.OutputDirectory, "models", "translation-q8.gguf")
            )
        );
        BundleManifestVerifier.Verify(scope.OutputDirectory);
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Assemble_RejectsCorruptedOrTruncatedFilePackBeforeStaging(bool truncate)
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSplitPack();
        var filePack = fixture.Packs.Single(pack => pack.Kind == BundlePackKind.File);
        var path = scope.CachePath(filePack.Id, BundlePackKind.File);
        var bytes = File.ReadAllBytes(path);
        if (truncate)
        {
            File.WriteAllBytes(path, bytes[..^1]);
        }
        else
        {
            bytes[0] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
        }

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains(
            truncate ? "size mismatch" : "SHA-256 mismatch",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.False(Directory.Exists(scope.OutputDirectory));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Assemble_RejectsCorruptedOrTruncatedPackBeforeExtraction(bool truncate)
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack();
        var packPath = scope.CachePath(fixture.Packs[0].Id);
        var bytes = File.ReadAllBytes(packPath);
        if (truncate)
        {
            File.WriteAllBytes(packPath, bytes[..^1]);
        }
        else
        {
            bytes[0] ^= 0xFF;
            File.WriteAllBytes(packPath, bytes);
        }

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains(
            truncate ? "size mismatch" : "SHA-256 mismatch",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.False(Directory.Exists(scope.OutputDirectory));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("C:/rooted.txt")]
    [InlineData("safe/file.txt:alternate-stream")]
    [InlineData("tools/NUL.txt")]
    public void Assemble_RejectsUnsafeZipPathsWithoutWritingOutsideTheTemporaryRoot(
        string unsafePath
    )
    {
        using var scope = new BundlePackScope();
        var airgapManifest = CreateAirgapManifest(
            new Dictionary<string, byte[]> { ["safe.txt"] = "safe"u8.ToArray() }
        );
        var packBytes = CreateZip(
            new Dictionary<string, byte[]>
            {
                [unsafePath] = "unsafe"u8.ToArray(),
                [BundleManifestVerifier.ManifestFileName] = airgapManifest,
            }
        );
        var pack = scope.CachePack("unsafe", packBytes);
        var trust = scope.WriteTrustManifest(
            [pack],
            Sha256(airgapManifest)
        );

        Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                trust,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.False(File.Exists(Path.Combine(scope.Root, "escape.txt")));
        Assert.False(Directory.Exists(scope.OutputDirectory));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public void Assemble_RejectsZipSymlinks()
    {
        using var scope = new BundlePackScope();
        var airgapManifest = CreateAirgapManifest(
            new Dictionary<string, byte[]> { ["safe.txt"] = "safe"u8.ToArray() }
        );
        byte[] packBytes;
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                var link = archive.CreateEntry("unsafe-link");
                link.ExternalAttributes = unchecked((int)0xA1FF0000);
                using (var writer = new StreamWriter(link.Open()))
                {
                    writer.Write("outside.txt");
                }
                WriteZipEntry(
                    archive,
                    BundleManifestVerifier.ManifestFileName,
                    airgapManifest
                );
            }
            packBytes = memory.ToArray();
        }
        var pack = scope.CachePack("symlink", packBytes);
        var trust = scope.WriteTrustManifest([pack], Sha256(airgapManifest));

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                trust,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("link or special", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(scope.OutputDirectory));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public void Assemble_RejectsCaseInsensitiveCollisionsAcrossPacks()
    {
        using var scope = new BundlePackScope();
        var files = new Dictionary<string, byte[]> { ["shared.txt"] = "same"u8.ToArray() };
        var airgapManifest = CreateAirgapManifest(files);
        var first = scope.CachePack(
            "first",
            CreateZip(
                new Dictionary<string, byte[]>
                {
                    ["shared.txt"] = files["shared.txt"],
                    [BundleManifestVerifier.ManifestFileName] = airgapManifest,
                }
            )
        );
        var second = scope.CachePack(
            "second",
            CreateZip(
                new Dictionary<string, byte[]> { ["SHARED.txt"] = files["shared.txt"] }
            )
        );
        var trust = scope.WriteTrustManifest([first, second], Sha256(airgapManifest));

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                trust,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("duplicate path", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(scope.OutputDirectory));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public void Assemble_RejectsFileTargetCollisionWithZipEntry()
    {
        using var scope = new BundlePackScope();
        var model = "model"u8.ToArray();
        var airgapManifest = CreateAirgapManifest(
            new Dictionary<string, byte[]> { ["models/model.gguf"] = model }
        );
        var core = scope.CachePack(
            "core",
            CreateZip(
                new Dictionary<string, byte[]>
                {
                    ["models/MODEL.gguf"] = model,
                    [BundleManifestVerifier.ManifestFileName] = airgapManifest,
                }
            )
        );
        var file = scope.CachePack(
            "model",
            model,
            BundlePackKind.File,
            "models/model.gguf"
        );
        var trust = scope.WriteTrustManifest([core, file], Sha256(airgapManifest));

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                trust,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("duplicate path", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(scope.OutputDirectory));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public void Assemble_RejectsAnUnexpectedCompressedEntryBeforeCreatingAWorkspace()
    {
        using var scope = new BundlePackScope();
        var safe = "safe"u8.ToArray();
        var airgapManifest = CreateAirgapManifest(
            new Dictionary<string, byte[]> { ["safe.txt"] = safe }
        );
        byte[] packBytes;
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteZipEntry(archive, "safe.txt", safe);
                WriteZipEntry(
                    archive,
                    BundleManifestVerifier.ManifestFileName,
                    airgapManifest
                );
                var unexpected = archive.CreateEntry(
                    "unexpected-compressed.bin",
                    CompressionLevel.SmallestSize
                );
                using var output = unexpected.Open();
                output.Write(new byte[16 * 1024 * 1024]);
            }
            packBytes = memory.ToArray();
        }
        var pack = scope.CachePack("unexpected", packBytes);
        var trust = scope.WriteTrustManifest([pack], Sha256(airgapManifest));

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                trust,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("not declared", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(scope.OutputDirectory));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public void Assemble_RejectsAnOversizedDeclaredZipTotalBeforeCreatingAWorkspace()
    {
        using var scope = new BundlePackScope();
        var airgapManifest = CreateAirgapManifest(
            new Dictionary<string, byte[]> { ["safe.txt"] = "safe"u8.ToArray() }
        );
        var packBytes = CreateZipWithOversizedDeclaredEntries(airgapManifest, 65);
        var pack = scope.CachePack("oversized", packBytes);
        var trust = scope.WriteTrustManifest([pack], Sha256(airgapManifest));

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                trust,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains(
            "expanded assembly safety limit",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.False(Directory.Exists(scope.OutputDirectory));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public void Assemble_RejectsATamperedFinalManifestAndPreservesVerifiedPacks()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack(expectedAirgapSha256: new string('0', 64));
        var cachedPack = scope.CachePath(fixture.Packs[0].Id);
        var cachedBytes = File.ReadAllBytes(cachedPack);

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("trusted final manifest", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(cachedBytes, File.ReadAllBytes(cachedPack));
        Assert.False(Directory.Exists(scope.OutputDirectory));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public void Assemble_RefusesAPreexistingOutputWithoutChangingIt()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack();
        Directory.CreateDirectory(scope.OutputDirectory);
        var sentinel = Path.Combine(scope.OutputDirectory, "do-not-touch.txt");
        File.WriteAllText(sentinel, "original");

        var error = Assert.Throws<IOException>(() =>
            BundlePackInstaller.Assemble(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("already exists", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(sentinel));
        scope.AssertNoAssemblyTemporaryDirectories();
    }

    [Fact]
    public async Task Acquire_ResumesAPartialPackThenVerifiesAndAssemblesIt()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack(cachePack: false);
        var pack = fixture.Packs[0];
        var split = pack.Bytes.Length / 3;
        var objectPath = scope.ObjectPath(pack);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        File.WriteAllBytes(objectPath + ".partial", pack.Bytes[..split]);
        using var handler = new ResumeHandler(pack.Bytes);

        var result = await BundlePackInstaller.AcquireAndAssembleAsync(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken,
            handler
        );

        Assert.Equal(split, handler.RequestedOffset);
        Assert.Equal(pack.Bytes, File.ReadAllBytes(objectPath));
        Assert.False(File.Exists(objectPath + ".partial"));
        Assert.True(Directory.Exists(result.OutputDirectory));
        BundleManifestVerifier.Verify(scope.OutputDirectory);
    }

    [Fact]
    public async Task Acquire_ResumesLargeFilePackThenVerifiesAndAssemblesIt()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSplitPack(cacheFile: false);
        var pack = fixture.Packs.Single(item => item.Kind == BundlePackKind.File);
        var split = pack.Bytes.Length / 3;
        var objectPath = scope.ObjectPath(pack);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        File.WriteAllBytes(objectPath + ".partial", pack.Bytes[..split]);
        using var handler = new ResumeHandler(pack.Bytes);

        var result = await BundlePackInstaller.AcquireAndAssembleAsync(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken,
            handler
        );

        Assert.Equal(split, handler.RequestedOffset);
        Assert.Equal(pack.Bytes, File.ReadAllBytes(objectPath));
        Assert.False(File.Exists(objectPath + ".partial"));
        Assert.True(Directory.Exists(result.OutputDirectory));
        BundleManifestVerifier.Verify(scope.OutputDirectory);
    }

    [Fact]
    public async Task Acquire_ImportsExactLegacyPackIntoContentStoreWithoutNetwork()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack();
        var pack = fixture.Packs[0];
        using var handler = new ThrowingHandler(
            new HttpRequestException("network must not be used")
        );

        await BundlePackInstaller.AcquireAndAssembleAsync(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken,
            handler
        );

        var objectPath = scope.ObjectPath(pack);
        Assert.Equal(pack.Bytes, File.ReadAllBytes(objectPath));
        Assert.True(File.Exists(scope.CachePath(pack.Id)));
        BundleManifestVerifier.Verify(scope.OutputDirectory);
    }

    [Fact]
    public void ContentObjectPath_BindsSchemaKindLengthAndShaUnderCacheRoot()
    {
        using var scope = new BundlePackScope();
        var bytes = "object-identity"u8.ToArray();
        var sha256 = Sha256(bytes);
        var zip = new BundlePackDefinition(
            "zip-id",
            new Uri("https://example.test/pack.zip"),
            bytes.LongLength,
            sha256
        );
        var file = zip with
        {
            Id = "file-id",
            Kind = BundlePackKind.File,
            Target = "models/model.gguf",
        };

        var zipPath = BundlePackInstaller.ContentObjectPath(scope.CacheDirectory, zip);
        var filePath = BundlePackInstaller.ContentObjectPath(scope.CacheDirectory, file);

        Assert.Equal(
            Path.Combine(
                scope.CacheDirectory,
                "objects",
                "v1",
                "zip",
                bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                sha256[..2],
                sha256 + ".object"
            ),
            zipPath
        );
        Assert.Equal(
            Path.Combine(
                scope.CacheDirectory,
                "objects",
                "v1",
                "file",
                bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                sha256[..2],
                sha256 + ".object"
            ),
            filePath
        );
        Assert.NotEqual(zipPath, filePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acquire_RecoversCorruptOrTruncatedObjectThroughColdFallback(
        bool truncate
    )
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack(cachePack: false);
        var pack = fixture.Packs[0];
        var objectPath = scope.ObjectPath(pack);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        File.WriteAllBytes(
            objectPath,
            truncate
                ? pack.Bytes[..^1]
                : Enumerable.Repeat((byte)0xA5, pack.Bytes.Length).ToArray()
        );
        using var handler = new ResumeHandler(pack.Bytes);

        await BundlePackInstaller.AcquireAndAssembleAsync(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken,
            handler
        );

        Assert.Null(handler.RequestedOffset);
        Assert.Equal(pack.Bytes, File.ReadAllBytes(objectPath));
        BundleManifestVerifier.Verify(scope.OutputDirectory);
    }

    [Fact]
    public void Assemble_RejectsHardLinkedContentObjectOutsideTheCache()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The release cache hard-link contract is Windows-specific.");
        }
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack(cachePack: false);
        var pack = fixture.Packs[0];
        var outsidePath = Path.Combine(scope.Root, "outside-pack.zip");
        File.WriteAllBytes(outsidePath, pack.Bytes);
        var objectPath = scope.ObjectPath(pack);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        if (!CreateHardLink(objectPath, outsidePath, IntPtr.Zero))
        {
            Assert.Skip(
                $"Hard links are unavailable on this host: {Marshal.GetLastWin32Error()}."
            );
        }

        var error = Assert.Throws<InvalidDataException>(() =>
            BundlePackInstaller.Assemble(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("physical filesystem link", error.Message);
        Assert.False(Directory.Exists(scope.OutputDirectory));
        Assert.Equal(pack.Bytes, File.ReadAllBytes(outsidePath));
    }

    [Fact]
    public async Task Acquire_SeedsMissingFilePackFromExactPhysicalBundleWithoutNetwork()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSplitPack(cacheFile: false);
        var filePack = fixture.Packs.Single(pack => pack.Kind == BundlePackKind.File);
        var seedRoot = Path.Combine(scope.Root, "seed-bundle");
        var seedPath = Path.Combine(
            seedRoot,
            filePack.Target!.Replace('/', Path.DirectorySeparatorChar)
        );
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        File.WriteAllBytes(seedPath, filePack.Bytes);
        using var handler = new ThrowingHandler(
            new HttpRequestException("network must not be used")
        );

        await BundlePackInstaller.AcquireAndAssembleAsync(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken,
            handler,
            seedBundleDirectory: seedRoot
        );

        Assert.Equal(filePack.Bytes, File.ReadAllBytes(scope.ObjectPath(filePack)));
        Assert.Equal(
            filePack.Bytes,
            File.ReadAllBytes(
                Path.Combine(
                    scope.OutputDirectory,
                    filePack.Target.Replace('/', Path.DirectorySeparatorChar)
                )
            )
        );
    }

    [Fact]
    public async Task Acquire_RejectsMutatedSeedAndDownloadsExactFilePack()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSplitPack(cacheFile: false);
        var filePack = fixture.Packs.Single(pack => pack.Kind == BundlePackKind.File);
        var seedRoot = Path.Combine(scope.Root, "seed-bundle");
        var seedPath = Path.Combine(
            seedRoot,
            filePack.Target!.Replace('/', Path.DirectorySeparatorChar)
        );
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        var mutated = filePack.Bytes.ToArray();
        mutated[0] ^= 0xFF;
        File.WriteAllBytes(seedPath, mutated);
        using var handler = new ResumeHandler(filePack.Bytes);

        await BundlePackInstaller.AcquireAndAssembleAsync(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken,
            handler,
            seedBundleDirectory: seedRoot
        );

        Assert.Null(handler.RequestedOffset);
        Assert.Equal(filePack.Bytes, File.ReadAllBytes(scope.ObjectPath(filePack)));
        BundleManifestVerifier.Verify(scope.OutputDirectory);
    }

    [Fact]
    public async Task Acquire_RejectsSeedFileLinkBeforeNetworkOrPublication()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSplitPack(cacheFile: false);
        var filePack = fixture.Packs.Single(pack => pack.Kind == BundlePackKind.File);
        var seedRoot = Path.Combine(scope.Root, "seed-bundle");
        var seedPath = Path.Combine(
            seedRoot,
            filePack.Target!.Replace('/', Path.DirectorySeparatorChar)
        );
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        var outsidePath = Path.Combine(scope.Root, "outside-model.gguf");
        File.WriteAllBytes(outsidePath, filePack.Bytes);
        try
        {
            File.CreateSymbolicLink(seedPath, outsidePath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
        )
        {
            Assert.Skip($"File links are unavailable on this test host: {ex.Message}");
        }
        using var handler = new ThrowingHandler(
            new HttpRequestException("network must not be used")
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BundlePackInstaller.AcquireAndAssembleAsync(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken,
                handler,
                seedBundleDirectory: seedRoot
            )
        );

        Assert.Contains("link or reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(scope.ObjectPath(filePack)));
        Assert.False(Directory.Exists(scope.OutputDirectory));
    }

    [Fact]
    public async Task Acquire_InterruptedInvocationLeavesDigestPartialForNextLeaseOwner()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack(cachePack: false);
        var pack = fixture.Packs[0];
        var split = pack.Bytes.Length / 3;
        using var interrupted = new ShortBodyHandler(pack.Bytes, split);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BundlePackInstaller.AcquireAndAssembleAsync(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken,
                interrupted
            )
        );

        var objectPath = scope.ObjectPath(pack);
        Assert.Equal(split, new FileInfo(objectPath + ".partial").Length);
        using var resumed = new ResumeHandler(pack.Bytes);
        await BundlePackInstaller.AcquireAndAssembleAsync(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken,
            resumed
        );

        Assert.Equal(split, resumed.RequestedOffset);
        Assert.False(File.Exists(objectPath + ".partial"));
        Assert.Equal(pack.Bytes, File.ReadAllBytes(objectPath));
    }

    [Fact]
    public async Task Acquire_DiscardsRightSizeWrongHashParkedPartialBeforeDownload()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack(cachePack: false);
        var pack = fixture.Packs[0];
        var objectPath = scope.ObjectPath(pack);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        File.WriteAllBytes(
            objectPath + ".partial",
            Enumerable.Repeat((byte)0x5A, pack.Bytes.Length).ToArray()
        );
        using var handler = new ResumeHandler(pack.Bytes);

        await BundlePackInstaller.AcquireAndAssembleAsync(
            fixture.TrustManifestPath,
            scope.CacheDirectory,
            scope.OutputDirectory,
            TestContext.Current.CancellationToken,
            handler
        );

        Assert.Null(handler.RequestedOffset);
        Assert.False(File.Exists(objectPath + ".partial"));
        Assert.Equal(pack.Bytes, File.ReadAllBytes(objectPath));
    }

    [Fact]
    public async Task Acquire_ParallelInvocationsSerializeObjectOwnershipAndDownloadOnce()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack(cachePack: false);
        var pack = fixture.Packs[0];
        using var handler = new CountingHandler(pack.Bytes, TimeSpan.FromMilliseconds(150));
        var firstOutput = scope.OutputDirectory + "-first";
        var secondOutput = scope.OutputDirectory + "-second";

        await Task.WhenAll(
            BundlePackInstaller.AcquireAndAssembleAsync(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                firstOutput,
                TestContext.Current.CancellationToken,
                handler
            ),
            BundlePackInstaller.AcquireAndAssembleAsync(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                secondOutput,
                TestContext.Current.CancellationToken,
                handler
            )
        );

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(pack.Bytes, File.ReadAllBytes(scope.ObjectPath(pack)));
        BundleManifestVerifier.Verify(firstOutput);
        BundleManifestVerifier.Verify(secondOutput);
        Assert.Empty(
            Directory.EnumerateFiles(
                scope.CacheDirectory,
                "*.partial",
                SearchOption.AllDirectories
            )
        );
    }

    [Fact]
    public async Task Acquire_RejectsARedirectThatDowngradesToHttp()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack(cachePack: false);
        using var handler = new RedirectHandler(new Uri("http://example.test/untrusted.zip"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BundlePackInstaller.AcquireAndAssembleAsync(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken,
                handler
            )
        );

        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(scope.OutputDirectory));
    }

    [Fact]
    public async Task Acquire_DoesNotExposeUrlTokensWhenAnHttpsRequestFails()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack(cachePack: false);
        var manifestText = File.ReadAllText(fixture.TrustManifestPath)
            .Replace("quality.zip", "quality.zip?token=do-not-log", StringComparison.Ordinal);
        File.WriteAllText(fixture.TrustManifestPath, manifestText);
        using var handler = new ThrowingHandler(
            new HttpRequestException("request failed for token=do-not-log")
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BundlePackInstaller.AcquireAndAssembleAsync(
                fixture.TrustManifestPath,
                scope.CacheDirectory,
                scope.OutputDirectory,
                TestContext.Current.CancellationToken,
                handler
            )
        );

        Assert.DoesNotContain("do-not-log", error.Message, StringComparison.Ordinal);
        Assert.Contains("HTTPS request failed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BundleCli_ExposesAssembleAndAcquireThroughTheSingleExecutableSurface()
    {
        using var scope = new BundlePackScope();
        var fixture = scope.CreateValidSinglePack();

        var assembleExit = await BundleCli.RunAsync(
            [
                "assemble",
                "--manifest",
                fixture.TrustManifestPath,
                "--cache",
                scope.CacheDirectory,
                "--output",
                scope.OutputDirectory,
            ]
        );
        var acquireHelpExit = await BundleCli.RunAsync(["acquire", "--help"]);

        Assert.Equal(0, assembleExit);
        Assert.Equal(0, acquireHelpExit);
        Assert.True(Directory.Exists(scope.OutputDirectory));
    }

    private static byte[] CreateAirgapManifest(IReadOnlyDictionary<string, byte[]> files)
    {
        var entries = files
            .Select(pair => new
            {
                path = pair.Key,
                bytes = (long)pair.Value.Length,
                sha256 = Sha256(pair.Value),
            })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, files = entries });
    }

    private static byte[] CreateZip(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var pair in entries)
            {
                WriteZipEntry(archive, pair.Key, pair.Value);
            }
        }
        return memory.ToArray();
    }

    private static byte[] CreateZipWithOversizedDeclaredEntries(
        byte[] airgapManifest,
        int entryCount
    )
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < entryCount; index++)
            {
                WriteZipEntry(archive, $"oversized-{index:D3}.bin", []);
            }
            WriteZipEntry(
                archive,
                BundleManifestVerifier.ManifestFileName,
                airgapManifest
            );
        }

        var bytes = memory.ToArray();
        var cursor = 0;
        var patched = 0;
        while (cursor <= bytes.Length - 46)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)) != 0x02014B50)
            {
                cursor++;
                continue;
            }
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(cursor + 28, 2)
            );
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(cursor + 30, 2)
            );
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(cursor + 32, 2)
            );
            var name = Encoding.UTF8.GetString(bytes, cursor + 46, nameLength);
            if (name.StartsWith("oversized-", StringComparison.Ordinal))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(cursor + 24, 4),
                    0xFFFFFFFE
                );
                patched++;
            }
            cursor += 46 + nameLength + extraLength + commentLength;
        }
        Assert.Equal(entryCount, patched);
        return bytes;
    }

    private static void WriteZipEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes
    );

    private sealed record TestPack(
        string Id,
        byte[] Bytes,
        BundlePackKind Kind = BundlePackKind.Zip,
        string? Target = null
    )
    {
        internal string Sha256 => BundlePackInstallerTests.Sha256(Bytes);
    }

    private sealed record PackFixture(
        string TrustManifestPath,
        string AirgapManifestSha256,
        IReadOnlyList<TestPack> Packs
    );

    private sealed class BundlePackScope : IDisposable
    {
        internal BundlePackScope()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "bstrings-bundle-pack-tests",
                Guid.NewGuid().ToString("N")
            );
            CacheDirectory = Path.Combine(Root, "cache");
            OutputDirectory = Path.Combine(Root, "output");
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }
        internal string CacheDirectory { get; }
        internal string OutputDirectory { get; }
        internal string TrustManifestPath => Path.Combine(Root, "bundle-packs.json");

        internal PackFixture CreateValidSinglePack(
            string? expectedAirgapSha256 = null,
            bool cachePack = true
        )
        {
            var files = new Dictionary<string, byte[]>
            {
                ["tools/tool.exe"] = "tool"u8.ToArray(),
                ["models/model.bin"] = "model"u8.ToArray(),
            };
            var airgapManifest = CreateAirgapManifest(files);
            var zipEntries = new Dictionary<string, byte[]>(files)
            {
                [BundleManifestVerifier.ManifestFileName] = airgapManifest,
            };
            var packBytes = CreateZip(zipEntries);
            var pack = cachePack
                ? CachePack("quality", packBytes)
                : new TestPack("quality", packBytes);
            var airgapSha256 = Sha256(airgapManifest);
            var trust = WriteTrustManifest(
                [pack],
                expectedAirgapSha256 ?? airgapSha256
            );
            return new PackFixture(trust, airgapSha256, [pack]);
        }

        internal PackFixture CreateValidSplitPack(bool cacheFile = true)
        {
            var tool = "tool"u8.ToArray();
            var model = "full-quality-q8-model"u8.ToArray();
            var files = new Dictionary<string, byte[]>
            {
                ["tools/tool.exe"] = tool,
                ["models/translation-q8.gguf"] = model,
            };
            var airgapManifest = CreateAirgapManifest(files);
            var core = CachePack(
                "core",
                CreateZip(
                    new Dictionary<string, byte[]>
                    {
                        ["tools/tool.exe"] = tool,
                        [BundleManifestVerifier.ManifestFileName] = airgapManifest,
                    }
                )
            );
            var modelPack = cacheFile
                ? CachePack(
                    "translation-q8",
                    model,
                    BundlePackKind.File,
                    "models/translation-q8.gguf"
                )
                : new TestPack(
                    "translation-q8",
                    model,
                    BundlePackKind.File,
                    "models/translation-q8.gguf"
                );
            var airgapSha256 = Sha256(airgapManifest);
            var trust = WriteTrustManifest([core, modelPack], airgapSha256);
            return new PackFixture(trust, airgapSha256, [core, modelPack]);
        }

        internal TestPack CachePack(
            string id,
            byte[] bytes,
            BundlePackKind kind = BundlePackKind.Zip,
            string? target = null
        )
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllBytes(CachePath(id, kind), bytes);
            return new TestPack(id, bytes, kind, target);
        }

        internal string WriteTrustManifest(
            IReadOnlyList<TestPack> packs,
            string airgapManifestSha256
        )
        {
            var packRows = packs.Select(pack =>
            {
                var row = new Dictionary<string, object?>
                {
                    ["id"] = pack.Id,
                    ["url"] = pack.Kind == BundlePackKind.Zip
                        ? $"https://example.test/{pack.Id}.zip"
                        : $"https://huggingface.co/example/repo/resolve/revision/{pack.Id}.gguf",
                    ["bytes"] = (long)pack.Bytes.Length,
                    ["sha256"] = pack.Sha256,
                };
                if (pack.Kind == BundlePackKind.File)
                {
                    row["kind"] = "file";
                    row["target"] = pack.Target;
                }
                return row;
            });
            File.WriteAllText(
                TrustManifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        profile = "win-x64-offline-quality",
                        bundleIdentity = "bstrings-1.9.0-win-x64-offline-quality",
                        airgapManifestSha256,
                        packs = packRows,
                    }
                )
            );
            return TrustManifestPath;
        }

        internal string CachePath(
            string id,
            BundlePackKind kind = BundlePackKind.Zip
        ) =>
            Path.Combine(
                CacheDirectory,
                id + (kind == BundlePackKind.Zip ? ".zip" : ".file")
            );

        internal string ObjectPath(TestPack pack) =>
            BundlePackInstaller.ContentObjectPath(
                CacheDirectory,
                new BundlePackDefinition(
                    pack.Id,
                    new Uri("https://example.test/pack"),
                    pack.Bytes.LongLength,
                    pack.Sha256,
                    pack.Kind,
                    pack.Target
                )
            );

        internal void AssertNoAssemblyTemporaryDirectories()
        {
            Assert.Empty(
                Directory.EnumerateDirectories(
                    Root,
                    ".output.assemble.*.partial",
                    SearchOption.TopDirectoryOnly
                )
            );
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class ResumeHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;

        internal ResumeHandler(byte[] bytes)
        {
            _bytes = bytes;
        }

        internal long? RequestedOffset { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestedOffset = request.Headers.Range?.Ranges.Single().From;
            var offset = checked((int)(RequestedOffset ?? 0));
            var content = new ByteArrayContent(_bytes[offset..]);
            content.Headers.ContentLength = _bytes.Length - offset;
            var response = new HttpResponseMessage(
                offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent
            )
            {
                Content = content,
            };
            if (offset > 0)
            {
                content.Headers.ContentRange = new ContentRangeHeaderValue(
                    offset,
                    _bytes.Length - 1,
                    _bytes.Length
                );
            }
            return Task.FromResult(response);
        }
    }

    private sealed class ShortBodyHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        private readonly int _bodyLength;

        internal ShortBodyHandler(byte[] bytes, int bodyLength)
        {
            _bytes = bytes;
            _bodyLength = bodyLength;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var content = new ByteArrayContent(_bytes[.._bodyLength]);
            content.Headers.ContentLength = _bytes.Length;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
            );
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        private readonly TimeSpan _delay;
        private int _requestCount;

        internal CountingHandler(byte[] bytes, TimeSpan delay)
        {
            _bytes = bytes;
            _delay = delay;
        }

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _requestCount);
            await Task.Delay(_delay, cancellationToken);
            var content = new ByteArrayContent(_bytes);
            content.Headers.ContentLength = _bytes.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        private readonly Uri _location;

        internal RedirectHandler(Uri location)
        {
            _location = location;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = _location;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        internal ThrowingHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromException<HttpResponseMessage>(_exception);
    }
}
