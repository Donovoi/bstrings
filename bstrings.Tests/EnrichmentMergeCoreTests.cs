using Xunit;

namespace bstrings.Tests;

public sealed class EnrichmentMergeCoreTests
{
    [Fact]
    public async Task ConcatenateAsync_PreservesOrderAddsFinalNewlineAndCountsRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.jsonl");
        var second = scope.PathFor("second.jsonl");
        var output = scope.PathFor("output.jsonl");
        await File.WriteAllTextAsync(first, "one\ntwo", cancellationToken);
        await File.WriteAllTextAsync(second, "three\n", cancellationToken);

        var stats = await EnrichmentMergeCore.ConcatenateAsync(
            [first, second],
            output,
            cancellationToken
        );

        Assert.Equal(3, stats.OutputRecords);
        Assert.Equal([2L, 1L], stats.InputRecords);
        Assert.Equal("one\ntwo\nthree\n", await File.ReadAllTextAsync(output, cancellationToken));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    [Fact]
    public async Task ConcatenateAsync_ValidatesEveryInputBeforeReplacingOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.jsonl");
        var output = scope.PathFor("output.jsonl");
        await File.WriteAllTextAsync(first, "one\n", cancellationToken);
        await File.WriteAllTextAsync(output, "previous", cancellationToken);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            EnrichmentMergeCore.ConcatenateAsync(
                [first, scope.PathFor("missing.jsonl")],
                output,
                cancellationToken
            )
        );

        Assert.Equal("previous", await File.ReadAllTextAsync(output, cancellationToken));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-merge-tests",
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
