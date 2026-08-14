using System.Text.Json;
using System.Text;
using Xunit;

namespace bstrings.Tests;

public sealed class NativeEnrichmentCompletionCoreTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsBoundRecordsAndReturnsPerSourceCounts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var source = scope.PathFor("evidence.bin");
        var manifest = scope.PathFor("input-manifest.jsonl");
        var output = scope.PathFor("native.jsonl");
        var sourceBytes = new byte[256];
        Encoding.Latin1.GetBytes("first text").CopyTo(sourceBytes, 16);
        Encoding.Unicode.GetBytes("second text").CopyTo(sourceBytes, 64);
        await File.WriteAllBytesAsync(source, sourceBytes, cancellationToken);
        await WriteManifestAsync(manifest, source, 256, cancellationToken);
        var transform = new NativeEnrichmentRecordTransform(source);
        await File.WriteAllLinesAsync(
            output,
            transform.TransformBatch(
                [
                    new ExtractedStringHit("first text", 16, 10, "code-page-1252"),
                    new ExtractedStringHit("second text", 64, 22, "utf-16le"),
                ]
            ),
            cancellationToken
        );

        var stats = await NativeEnrichmentCompletionCore.ValidateAsync(
            output,
            manifest,
            scope.DirectoryPath,
            cancellationToken
        );

        Assert.Equal(2, stats.OutputRecords);
        Assert.Equal(2, stats.SourceRecords[source]);
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(scope.DirectoryPath),
            path => Path.GetFileName(path).StartsWith(".bstrings-provenance.", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsDuplicateRecordIdentifiers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var (manifest, output, line) = await CreateValidCaseAsync(scope, cancellationToken);
        await File.WriteAllLinesAsync(output, [line, line], cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeEnrichmentCompletionCore.ValidateAsync(
                output,
                manifest,
                scope.DirectoryPath,
                cancellationToken
            )
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsDuplicateJsonProperties()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var (manifest, output, line) = await CreateValidCaseAsync(scope, cancellationToken);
        var duplicate = line.Replace(
            "\"recordType\":\"string\"",
            "\"recordType\":\"string\",\"recordType\":\"string\"",
            StringComparison.Ordinal
        );
        await File.WriteAllLinesAsync(output, [duplicate], cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeEnrichmentCompletionCore.ValidateAsync(
                output,
                manifest,
                scope.DirectoryPath,
                cancellationToken
            )
        );
        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsSameLengthRecordIdTamper()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var (manifest, output, line) = await CreateValidCaseAsync(scope, cancellationToken);
        using var document = JsonDocument.Parse(line);
        var recordId = document.RootElement.GetProperty("recordId").GetString()!;
        var replacement = recordId[..^1] + (recordId[^1] == '0' ? "1" : "0");
        var tampered = line.Replace(recordId, replacement, StringComparison.Ordinal);
        Assert.Equal(line.Length, tampered.Length);
        await File.WriteAllLinesAsync(output, [tampered], cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeEnrichmentCompletionCore.ValidateAsync(
                output,
                manifest,
                scope.DirectoryPath,
                cancellationToken
            )
        );
        Assert.Contains("record-ID", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sourceFile", "other.bin")]
    [InlineData("extractor", "not-bstrings")]
    [InlineData("version", "other-version")]
    [InlineData("kind", "dynamic")]
    public async Task ValidateAsync_RejectsSourceOrProvenanceTamper(
        string property,
        string replacement
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var (manifest, output, line) = await CreateValidCaseAsync(scope, cancellationToken);
        using var document = JsonDocument.Parse(line);
        string original = property switch
        {
            "sourceFile" => document.RootElement.GetProperty(property).GetString()!,
            "extractor" => document.RootElement.GetProperty("origin").GetProperty(property).GetString()!,
            _ => document.RootElement.GetProperty("origin").GetProperty(property).GetString()!,
        };
        var tampered = line.Replace(
            $"\"{property}\":{JsonSerializer.Serialize(original)}",
            $"\"{property}\":{JsonSerializer.Serialize(replacement)}",
            StringComparison.Ordinal
        );
        await File.WriteAllLinesAsync(output, [tampered], cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeEnrichmentCompletionCore.ValidateAsync(
                output,
                manifest,
                scope.DirectoryPath,
                cancellationToken
            )
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsTextThatDoesNotMatchTheClaimedSourceBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var (manifest, output, line) = await CreateValidCaseAsync(scope, cancellationToken);
        using var document = JsonDocument.Parse(line);
        var source = document.RootElement.GetProperty("sourceFile").GetString()!;
        await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 32;
            await stream.WriteAsync("X"u8.ToArray(), cancellationToken);
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeEnrichmentCompletionCore.ValidateAsync(
                output,
                manifest,
                scope.DirectoryPath,
                cancellationToken
            )
        );
        Assert.Contains("source byte range", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsUnterminatedOutputAndHonorsCancellation()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var (manifest, output, line) = await CreateValidCaseAsync(scope, testCancellationToken);
        await File.WriteAllTextAsync(output, line, testCancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeEnrichmentCompletionCore.ValidateAsync(
                output,
                manifest,
                scope.DirectoryPath,
                testCancellationToken
            )
        );

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeEnrichmentCompletionCore.ValidateAsync(
                output,
                manifest,
                scope.DirectoryPath,
                cancelled.Token
            )
        );
    }

    private static async Task<(string Manifest, string Output, string Line)> CreateValidCaseAsync(
        TemporaryDirectory scope,
        CancellationToken cancellationToken
    )
    {
        var source = scope.PathFor("evidence.bin");
        var manifest = scope.PathFor("input-manifest.jsonl");
        var output = scope.PathFor("native.jsonl");
        var sourceBytes = new byte[256];
        Encoding.Latin1.GetBytes("forensic text").CopyTo(sourceBytes, 32);
        await File.WriteAllBytesAsync(source, sourceBytes, cancellationToken);
        await WriteManifestAsync(manifest, source, 256, cancellationToken);
        var line = Assert.Single(
            new NativeEnrichmentRecordTransform(source).TransformBatch(
                [new ExtractedStringHit("forensic text", 32, 13, "code-page-1252")]
            )
        );
        await File.WriteAllLinesAsync(output, [line], cancellationToken);
        return (manifest, output, line);
    }

    private static Task WriteManifestAsync(
        string path,
        string source,
        long length,
        CancellationToken cancellationToken
    ) =>
        File.WriteAllLinesAsync(
            path,
            [
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        path = source,
                        length,
                        sha256 = new string('a', 64),
                    }
                ),
            ],
            cancellationToken
        );

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-native-completion-tests",
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
