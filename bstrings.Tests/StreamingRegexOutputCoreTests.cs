using System.Collections.Generic;
using Xunit;

namespace bstrings.Tests;

public class StreamingRegexOutputCoreTests
{
    [Fact]
    public void TransformMainBatch_WritesCsvRowsAndReleasesRawHits()
    {
        var processor = new StreamingRegexOutputCore(
            [("letters", "Alpha\\d+")],
            regexOnly: true,
            includeOffset: true,
            isCsvOutput: true,
            sourceFile: "sample.bin"
        );
        var hits = new List<string> { "0x201\tAlpha123", "0x220\tNo match" };

        var rows = processor.TransformMainBatch(hits);

        Assert.Empty(hits);
        Assert.Equal(
            ["\"letters\",\"Alpha123\",\"sample.bin\",\"0x201\",\"Regex\""],
            rows
        );
        Assert.Equal(1, processor.OutputRowCount);
    }

    [Fact]
    public void TransformBoundaryBatch_RemovesDisplayPrefixBeforeParsingOffset()
    {
        var processor = new StreamingRegexOutputCore(
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            regexOnly: true,
            includeOffset: true,
            isCsvOutput: true,
            sourceFile: "input.bin"
        );
        var hits = new List<string> { "  0xABC\tuser@example.com" };

        var rows = processor.TransformBoundaryBatch(hits);

        Assert.Equal(
            ["\"email\",\"user@example.com\",\"input.bin\",\"0xABC\",\"Regex\""],
            rows
        );
    }

    [Fact]
    public void TransformMainBatch_PromotesMeasuredPatternExactlyOnceAtCrossover()
    {
        var processor = new StreamingRegexOutputCore(
            [("usPhone", BuiltInPatternCatalog.Patterns["usPhone"])],
            regexOnly: true,
            includeOffset: false,
            isCsvOutput: false,
            sourceFile: "input.bin"
        );
        var first = Enumerable.Repeat("this is not a phone number", 6_000).ToList();
        var second = Enumerable.Repeat("this is also not a phone number", 6_000).ToList();

        Parallel.Invoke(
            () => Assert.Empty(processor.TransformMainBatch(first)),
            () => Assert.Empty(processor.TransformMainBatch(second))
        );

        Assert.Equal(1, processor.CompiledPromotionCount);
    }

    [Fact]
    public void TransformMainBatch_UsesProjectedCandidateDensityForEarlyPromotion()
    {
        var processor = new StreamingRegexOutputCore(
            [("usPhone", BuiltInPatternCatalog.Patterns["usPhone"])],
            regexOnly: true,
            includeOffset: false,
            isCsvOutput: false,
            sourceFile: "input.bin",
            estimatedBatchCount: 100
        );
        var hits = Enumerable.Repeat("this is not a phone number", 100).ToList();

        Assert.Empty(processor.TransformMainBatch(hits));
        Assert.Equal(1, processor.CompiledPromotionCount);
    }

    [Fact]
    public void TransformMainBatch_PreservesRepeatedMatchesWithinOneExtractedString()
    {
        var processor = new StreamingRegexOutputCore(
            [("b64", BuiltInPatternCatalog.Patterns["b64"])],
            regexOnly: true,
            includeOffset: true,
            isCsvOutput: true,
            sourceFile: "README.md"
        );
        var hits = new List<string> { "0x1D03\tdotnet publish bstrings\\bstrings.csproj `" };

        var rows = processor.TransformMainBatch(hits);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Contains("\"b64\",\"bstrings\"", row));
    }

    [Fact]
    public void BoundedIpv6Retry_FindsBoundarySpanningMatchesWithoutDuplicates()
    {
        var ipv6 = RegexOutputCore.GetOrCreateRegex(
            "ipv6",
            BuiltInPatternCatalog.Patterns["ipv6"]
        );
        const string address = "2001:db8:85a3::8a2e:370:7334";
        var data = new string('x', 25) + address + " " + address;
        var parsedHit = new ParsedHit("0x500\t" + data, data, "0x500");

        var records = StreamingRegexOutputCore
            .CreateBoundedIpv6Records(
                parsedHit,
                "ipv6",
                ipv6,
                "input.bin",
                primaryWindowLength: 32
            )
            .ToList();

        Assert.Equal(2, records.Count);
        Assert.All(records, record =>
        {
            Assert.Equal(address, record.DataFound);
            Assert.Equal("0x500", record.Offset);
        });
    }

    [Fact]
    public void TransformMainBatch_HandlesIpv6AfterVeryLargeHexCandidate()
    {
        const string address = "2001:db8:85a3::8a2e:370:7334";
        var processor = new StreamingRegexOutputCore(
            [("ipv6", BuiltInPatternCatalog.Patterns["ipv6"])],
            regexOnly: true,
            includeOffset: true,
            isCsvOutput: true,
            sourceFile: "input.bin"
        );
        var hits = new List<string>
        {
            "0x1000\t" + new string('a', 20 * 1024 * 1024) + " " + address,
        };

        var rows = processor.TransformMainBatch(hits);

        Assert.Contains(
            "\"ipv6\",\"2001:db8:85a3::8a2e:370:7334\",\"input.bin\",\"0x1000\",\"Regex\"",
            rows
        );
    }

    [Fact]
    public void TransformMainBatch_HandlesEmailAfterVeryLargeCandidate()
    {
        var processor = new StreamingRegexOutputCore(
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            regexOnly: true,
            includeOffset: true,
            isCsvOutput: true,
            sourceFile: "input.bin"
        );
        var hits = new List<string>
        {
            "0x1800\t"
                + new string('a', 20 * 1024 * 1024)
                + " user.name+tag@example.technology",
        };

        var rows = processor.TransformMainBatch(hits);

        Assert.Equal(
            [
                "\"email\",\"user.name+tag@example.technology\",\"input.bin\",\"0x1800\",\"Regex\"",
            ],
            rows
        );
    }

    [Fact]
    public void BoundedEmailRetry_FindsBoundarySpanningMatchesWithoutDuplicates()
    {
        var email = RegexOutputCore.GetOrCreateRegex(
            "email",
            BuiltInPatternCatalog.Patterns["email"]
        );
        const string address = "user.name+tag@example.technology";
        var data = new string('x', 25) + " " + address + " " + address;
        var parsedHit = new ParsedHit("0x500\t" + data, data, "0x500");

        var records = StreamingRegexOutputCore
            .CreateBoundedRecords(
                parsedHit,
                "email",
                email,
                "input.bin",
                overlap: 48,
                primaryWindowLength: 32
            )
            .ToList();

        Assert.Equal(2, records.Count);
        Assert.All(records, record =>
        {
            Assert.Equal(address, record.DataFound);
            Assert.Equal("0x500", record.Offset);
        });
    }

    [Fact]
    public void TransformMainBatch_HandlesUrlUserAfterVeryLargeNonUrlCandidate()
    {
        var definition = BuiltInPatternCatalog.ByName["urlUser"];
        Assert.True(definition.UseNonBacktracking);

        var processor = new StreamingRegexOutputCore(
            [("urlUser", definition.Pattern)],
            regexOnly: true,
            includeOffset: true,
            isCsvOutput: true,
            sourceFile: "input.bin"
        );
        var hits = new List<string>
        {
            "0x2000\t"
                + new string('a', 20 * 1024 * 1024)
                + " https://analyst:secret@example.com/path",
        };

        var rows = processor.TransformMainBatch(hits);

        Assert.Equal(
            ["\"urlUser\",\"analyst\",\"input.bin\",\"0x2000\",\"Regex\""],
            rows
        );
    }

    [Fact]
    public void TransformMainBatch_HandlesUrl3986AfterVeryLargeNonUrlCandidate()
    {
        var definition = BuiltInPatternCatalog.ByName["url3986"];
        Assert.True(definition.UseNonBacktracking);

        var processor = new StreamingRegexOutputCore(
            [("url3986", definition.Pattern)],
            regexOnly: true,
            includeOffset: true,
            isCsvOutput: true,
            sourceFile: "input.bin"
        );
        var hits = new List<string>
        {
            "0x2400\t"
                + new string('a', 20 * 1024 * 1024)
                + " https://user@example.com:8443/a//b?x=1#fragment",
        };

        var rows = processor.TransformMainBatch(hits);

        Assert.Equal(
            [
                "\"url3986\",\"https://user@example.com:8443/a//b?x=1#fragment\",\"input.bin\",\"0x2400\",\"Regex\"",
            ],
            rows
        );
    }

    [Fact]
    public async Task StreamingPipeline_TransformsChunksBeforeWritingAndKeepsSetEmpty()
    {
        using var pipeline = new Program.ChunkProcessingPipeline(maxConcurrency: 2);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        var resultsSet = new HashSet<string>();
        var processor = new StreamingRegexOutputCore(
            [("letters", "Alpha\\d+")],
            regexOnly: true,
            includeOffset: true,
            isCsvOutput: false,
            sourceFile: "sample.bin"
        );

        var totalCount = await pipeline.ProcessChunksStreamingAsync(
            GetChunks(
                CreateChunk("\u0001Alpha123\u0002", 0x200, 0),
                CreateChunk("\u0001Beta456\u0002", 0x300, 1)
            ),
            minLength: 4,
            maxLength: -1,
            asciiSearch: true,
            unicodeSearch: false,
            off: true,
            cp: 1252,
            ar: "[\\x20-\\x7E]",
            ur: "[\\u0020-\\u007E]",
            progressTracker: new Program.ProgressTracker(totalChunks: 2, quiet: true),
            outputWriter: writer,
            resultsSet: null,
            resultTransform: processor.TransformMainBatch
        );

        await writer.FlushAsync(TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = (
            await reader.ReadToEndAsync(TestContext.Current.CancellationToken)
        )
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(1, totalCount);
        Assert.Equal(["Alpha123\t~0x201"], output);
        Assert.Empty(resultsSet);
    }

    [Fact]
    public async Task StreamingPipeline_PropagatesWorkerFailureInsteadOfCancellation()
    {
        using var pipeline = new Program.ChunkProcessingPipeline(maxConcurrency: 1);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ProcessChunksStreamingAsync(
                GetChunks(CreateChunk("\u0001Alpha123\u0002", 0, 0)),
                minLength: 4,
                maxLength: -1,
                asciiSearch: true,
                unicodeSearch: false,
                off: true,
                cp: 1252,
                ar: "[\\x20-\\x7E]",
                ur: "[\\u0020-\\u007E]",
                progressTracker: new Program.ProgressTracker(totalChunks: 1, quiet: true),
                outputWriter: writer,
                resultsSet: null,
                resultTransform: _ => throw new InvalidOperationException("worker failure")
            )
        );

        Assert.Equal("worker failure", exception.Message);
    }

    private static async IAsyncEnumerable<Program.DataChunk> GetChunks(
        params Program.DataChunk[] chunks
    )
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
            await Task.Yield();
        }
    }

    private static Program.DataChunk CreateChunk(string text, long fileOffset, int chunkIndex)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        var rented = Program.ByteArrayPool.Rent(bytes.Length);
        Array.Copy(bytes, rented, bytes.Length);
        return new Program.DataChunk
        {
            Data = rented,
            ValidBytes = bytes.Length,
            FileOffset = fileOffset,
            ChunkIndex = chunkIndex,
            IsBoundaryChunk = false,
        };
    }
}
