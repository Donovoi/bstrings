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

    [Fact]
    public async Task ValidateConcatenationAsync_AcceptsExactOrderedNormalizedBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.jsonl");
        var second = scope.PathFor("second.jsonl");
        var empty = scope.PathFor("empty.jsonl");
        var output = scope.PathFor("output.jsonl");
        await File.WriteAllTextAsync(first, "one\r\ntwo", cancellationToken);
        await File.WriteAllTextAsync(second, "three\n", cancellationToken);
        await File.WriteAllTextAsync(empty, string.Empty, cancellationToken);
        await File.WriteAllTextAsync(output, "one\r\ntwo\nthree\n", cancellationToken);

        var stats = await EnrichmentMergeCore.ValidateConcatenationAsync(
            [first, second, empty],
            output,
            cancellationToken
        );

        Assert.Equal(3, stats.OutputRecords);
        Assert.Equal([2L, 1L, 0L], stats.InputRecords);
    }

    [Theory]
    [InlineData("one\ntwo\nfour!\n")]
    [InlineData("one\ntwo\nthree")]
    [InlineData("one\ntwo\nthree\nextra")]
    public async Task ValidateConcatenationAsync_RejectsAnyByteDifference(string outputBytes)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.jsonl");
        var second = scope.PathFor("second.jsonl");
        var output = scope.PathFor("output.jsonl");
        await File.WriteAllTextAsync(first, "one\ntwo", cancellationToken);
        await File.WriteAllTextAsync(second, "three\n", cancellationToken);
        await File.WriteAllTextAsync(output, outputBytes, cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentMergeCore.ValidateConcatenationAsync(
                [first, second],
                output,
                cancellationToken
            )
        );
    }

    [Fact]
    public async Task ValidateConcatenationAsync_RejectsReorderedInputs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.jsonl");
        var second = scope.PathFor("second.jsonl");
        var output = scope.PathFor("output.jsonl");
        await File.WriteAllTextAsync(first, "one\ntwo", cancellationToken);
        await File.WriteAllTextAsync(second, "three\n", cancellationToken);
        await File.WriteAllTextAsync(output, "one\ntwo\nthree\n", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentMergeCore.ValidateConcatenationAsync(
                [second, first],
                output,
                cancellationToken
            )
        );
    }

    [Fact]
    public async Task ValidateConcatenationAsync_HonorsCancellation()
    {
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.jsonl");
        var output = scope.PathFor("output.jsonl");
        await File.WriteAllTextAsync(first, "one\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output, "one\n", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EnrichmentMergeCore.ValidateConcatenationAsync(
                [first],
                output,
                cancellation.Token
            )
        );
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
