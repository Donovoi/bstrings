using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class InputEvidenceManifestTests
{
    [Fact]
    public async Task CreateAndVerifyAsync_RecordsAndVerifiesExactContentIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.bin");
        var second = scope.PathFor("second.bin");
        var inventory = scope.PathFor("input-files.txt");
        var manifest = scope.PathFor("input-manifest.jsonl");
        await File.WriteAllBytesAsync(first, [0, 1, 2, 3], cancellationToken);
        await File.WriteAllTextAsync(second, "evidence", cancellationToken);

        var info = await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            [first, second],
            cancellationToken
        );

        Assert.Equal(2, info.FileCount);
        Assert.Equal("input-files.txt", info.InventoryFile);
        Assert.Equal(Sha256(inventory), info.InventorySha256);
        Assert.Equal("input-manifest.jsonl", info.ManifestFile);
        Assert.Equal(64, info.ManifestSha256.Length);
        Assert.Equal(
            [Path.GetFullPath(first), Path.GetFullPath(second)],
            await File.ReadAllLinesAsync(inventory, cancellationToken)
        );
        var rows = await File.ReadAllLinesAsync(manifest, cancellationToken);
        Assert.Equal(2, rows.Length);
        using (var firstRow = JsonDocument.Parse(rows[0]))
        {
            Assert.Equal(Path.GetFullPath(first), firstRow.RootElement.GetProperty("path").GetString());
            Assert.Equal(4, firstRow.RootElement.GetProperty("length").GetInt64());
            Assert.Equal(64, firstRow.RootElement.GetProperty("sha256").GetString()!.Length);
        }

        await InputEvidenceManifest.VerifyInventoryAsync(inventory, info, cancellationToken);
        await InputEvidenceManifest.VerifyAsync(manifest, info, cancellationToken);
    }

    [Fact]
    public async Task VerifyInventoryAsync_RejectsInventoryByteMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.bin");
        var second = scope.PathFor("second.bin");
        var inventory = scope.PathFor("input-files.txt");
        var manifest = scope.PathFor("input-manifest.jsonl");
        await File.WriteAllTextAsync(first, "alpha", cancellationToken);
        await File.WriteAllTextAsync(second, "bravo", cancellationToken);
        var info = await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            [first],
            cancellationToken
        );
        await WriteInventoryAsync(inventory, [second], cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            InputEvidenceManifest.VerifyInventoryAsync(inventory, info, cancellationToken)
        );

        Assert.Contains("inventory SHA-256 mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyInventoryAsync_RejectsNonCanonicalRowsEvenWhenDigestMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        var inventory = scope.PathFor("input-files.txt");
        var manifest = scope.PathFor("input-manifest.jsonl");
        await File.WriteAllTextAsync(evidence, "alpha", cancellationToken);
        var info = await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            [evidence],
            cancellationToken
        );
        await WriteInventoryAsync(inventory, ["relative.bin"], cancellationToken);
        info = info with { InventorySha256 = Sha256(inventory) };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            InputEvidenceManifest.VerifyInventoryAsync(inventory, info, cancellationToken)
        );

        Assert.Contains("absolute path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyInventoryAsync_RejectsCountDriftEvenWhenDigestMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        var inventory = scope.PathFor("input-files.txt");
        var manifest = scope.PathFor("input-manifest.jsonl");
        await File.WriteAllTextAsync(evidence, "alpha", cancellationToken);
        var info = await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            [evidence],
            cancellationToken
        );
        await WriteInventoryAsync(inventory, [evidence, evidence], cancellationToken);
        info = info with { InventorySha256 = Sha256(inventory) };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            InputEvidenceManifest.VerifyInventoryAsync(inventory, info, cancellationToken)
        );

        Assert.Contains("inventory count changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifySelectionAsync_RejectsAddedOrRemovedInputsWithoutChangingTheInventory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.bin");
        var second = scope.PathFor("second.bin");
        var inventory = scope.PathFor("input-files.txt");
        var manifest = scope.PathFor("input-manifest.jsonl");
        await File.WriteAllTextAsync(first, "alpha", cancellationToken);
        await File.WriteAllTextAsync(second, "bravo", cancellationToken);
        await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            [first],
            cancellationToken
        );

        await InputEvidenceManifest.VerifySelectionAsync(
            inventory,
            [first],
            cancellationToken
        );
        var added = await Assert.ThrowsAsync<InvalidDataException>(() =>
            InputEvidenceManifest.VerifySelectionAsync(
                inventory,
                [first, second],
                cancellationToken
            )
        );
        var removed = await Assert.ThrowsAsync<InvalidDataException>(() =>
            InputEvidenceManifest.VerifySelectionAsync(
                inventory,
                Array.Empty<string>(),
                cancellationToken
            )
        );

        Assert.Contains("selection changed", added.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selection changed", removed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadInputInventory_MaterializesOneImmutableSequence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.bin");
        var second = scope.PathFor("second.bin");
        var inventory = scope.PathFor("input-files.txt");
        await File.WriteAllTextAsync(first, "alpha", cancellationToken);
        await File.WriteAllTextAsync(second, "bravo", cancellationToken);
        await WriteInventoryAsync(inventory, [first], cancellationToken);

        var snapshot = Program.ReadInputInventory(inventory);
        await WriteInventoryAsync(inventory, [second], cancellationToken);

        Assert.Equal([Path.GetFullPath(first)], snapshot);
    }

    [Fact]
    public async Task Analyze_CommittedInventoryCannotBeChangedBeforeNativeExtraction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        var replacement = scope.PathFor("replacement.bin");
        var output = scope.PathFor("results");
        await File.WriteAllTextAsync(evidence, "analyst@example.com", cancellationToken);
        await File.WriteAllTextAsync(replacement, "replacement", cancellationToken);

        await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                CreateOptions(evidence, output),
                cancellationToken,
                afterInputInventoryCreated: (inventoryPath, token) =>
                    WriteInventoryAsync(inventoryPath, [replacement], token)
            )
        );

        Assert.True(File.Exists(Path.Combine(output, ".incomplete")));
        Assert.False(File.Exists(Path.Combine(output, "summary.json")));
        using var run = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "run.json"), cancellationToken)
        );
        Assert.Equal("failed", run.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            64,
            run.RootElement
                .GetProperty("input")
                .GetProperty("inventorySha256")
                .GetString()!
                .Length
        );
    }

    [Fact]
    public async Task Analyze_EvidenceMutationAfterProcessingFailsBeforeCompletion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        var output = scope.PathFor("results");
        const string original = "analyst@example.com";
        await File.WriteAllTextAsync(evidence, original, cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisOrchestrator.RunAsync(
                CreateOptions(evidence, output),
                cancellationToken,
                executingExecutablePath: Path.Combine(AppContext.BaseDirectory, "bstrings.exe"),
                beforeFinalInputVerification: token =>
                    File.WriteAllTextAsync(evidence, new string('X', original.Length), token)
            )
        );

        Assert.Contains("content changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(output, ".incomplete")));
        Assert.False(File.Exists(Path.Combine(output, "summary.json")));
        using var run = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "run.json"), cancellationToken)
        );
        Assert.Equal("failed", run.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task VerifyAsync_RejectsSameLengthContentMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        var inventory = scope.PathFor("input-files.txt");
        var manifest = scope.PathFor("input-manifest.jsonl");
        await File.WriteAllTextAsync(evidence, "alpha", cancellationToken);
        var info = await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            [evidence],
            cancellationToken
        );
        await File.WriteAllTextAsync(evidence, "bravo", cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            InputEvidenceManifest.VerifyAsync(manifest, info, cancellationToken)
        );

        Assert.Contains("content changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsManifestTamperingBeforeTrustingEntries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        var inventory = scope.PathFor("input-files.txt");
        var manifest = scope.PathFor("input-manifest.jsonl");
        await File.WriteAllTextAsync(evidence, "alpha", cancellationToken);
        var info = await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            [evidence],
            cancellationToken
        );
        await File.AppendAllTextAsync(manifest, " ", cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            InputEvidenceManifest.VerifyAsync(manifest, info, cancellationToken)
        );

        Assert.Contains("manifest SHA-256 mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AnalysisOptions CreateOptions(string evidence, string output) =>
        new(
            FilePath: evidence,
            DirectoryPath: null,
            Mask: null,
            OutputDirectory: output,
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

    private static async Task WriteInventoryAsync(
        string path,
        IEnumerable<string> rows,
        CancellationToken cancellationToken
    ) =>
        await File.WriteAllTextAsync(
            path,
            string.Join('\n', rows) + "\n",
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true
            ),
            cancellationToken
        );

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-input-manifest-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string PathFor(string name) => Path.Combine(DirectoryPath, name);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
