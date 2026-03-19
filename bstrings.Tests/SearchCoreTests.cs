using System.Text;
using Xunit;
using bstrings.Rapids;

namespace bstrings.Tests;

public class SearchCoreTests
{
    private static readonly IReadOnlyDictionary<string, string> BuiltInPatterns =
        new Dictionary<string, string>
        {
            ["email"] = @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,6}\b",
            ["guid"] = @"[A-F0-9]{8}-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{12}",
        };

    [Theory]
    [InlineData(null, 32, 126)]
    [InlineData("", 32, 126)]
    [InlineData("[\\x20-\\x7E]", 32, 126)]
    [InlineData("not-a-range", 32, 126)]
    [InlineData("[\\x30-\\x39]", 48, 57)]
    public void ParseCharRange_ReturnsExpectedBounds(string? input, byte expectedMin, byte expectedMax)
    {
        var (actualMin, actualMax) = SearchCore.ParseCharRange(input!);

        Assert.Equal(expectedMin, actualMin);
        Assert.Equal(expectedMax, actualMax);
    }

    [Fact]
    public void ParseRegexPatternsWithNames_ResolvesBuiltInsLiteralPatternsAndAllKeyword()
    {
        var mixedPatterns = SearchCore.ParseRegexPatternsWithNames(
            "email, custom.*, guid",
            BuiltInPatterns
        );
        var allPatterns = SearchCore.ParseRegexPatternsWithNames("all", BuiltInPatterns);
        var emptyPatterns = SearchCore.ParseRegexPatternsWithNames("   ", BuiltInPatterns);

        Assert.Collection(
            mixedPatterns,
            pattern =>
            {
                Assert.Equal("email", pattern.name);
                Assert.Equal(BuiltInPatterns["email"], pattern.pattern);
            },
            pattern =>
            {
                Assert.Equal("custom.*", pattern.name);
                Assert.Equal("custom.*", pattern.pattern);
            },
            pattern =>
            {
                Assert.Equal("guid", pattern.name);
                Assert.Equal(BuiltInPatterns["guid"], pattern.pattern);
            }
        );

        Assert.Equal(2, allPatterns.Count);
        Assert.Contains(allPatterns, pattern => pattern.name == "email");
        Assert.Contains(allPatterns, pattern => pattern.name == "guid");
        Assert.Empty(emptyPatterns);
    }

    [Fact]
    public void ParseRegexPatterns_ReturnsResolvedPatternStrings()
    {
        var patterns = SearchCore.ParseRegexPatterns("guid, raw.+", BuiltInPatterns);

        Assert.Equal(
            [BuiltInPatterns["guid"], "raw.+"],
            patterns
        );
    }

    [Fact]
    public void FindAsciiStringHits_IdentifiesAndMaterializesOffsets()
    {
        var bytes = Encoding.ASCII.GetBytes("\u0001Alpha123\u0002Beta!\u001f");
        var hits = SearchCore.FindAsciiStringHits(bytes, 4, -1, 512);
        var materialized = SearchCore.MaterializeStringHits(bytes, hits, includeOffset: true);

        Assert.Collection(
            hits,
            hit =>
            {
                Assert.Equal(1, hit.Start);
                Assert.Equal(8, hit.Length);
                Assert.Equal(512, hit.FileOffset);
            },
            hit =>
            {
                Assert.Equal(10, hit.Start);
                Assert.Equal(5, hit.Length);
                Assert.Equal(512, hit.FileOffset);
            }
        );

        Assert.Equal(["0x201\tAlpha123", "0x20A\tBeta!"], materialized);
    }

    [Fact]
    public void FindAsciiStringHits_RespectsCustomCharacterRangeAndMaxLength()
    {
        var bytes = Encoding.ASCII.GetBytes("AB12-34Z");
        var (minChar, maxChar) = SearchCore.ParseCharRange("[\\x30-\\x39]");

        var hits = SearchCore.FindAsciiStringHits(bytes, 2, 3, 100, minChar, maxChar);
        var materialized = SearchCore.MaterializeStringHits(bytes, hits, includeOffset: false);

        Assert.Collection(
            hits,
            hit =>
            {
                Assert.Equal(2, hit.Start);
                Assert.Equal(2, hit.Length);
                Assert.Equal(100, hit.FileOffset);
            },
            hit =>
            {
                Assert.Equal(5, hit.Start);
                Assert.Equal(2, hit.Length);
                Assert.Equal(100, hit.FileOffset);
            }
        );

        Assert.Equal(["12", "34"], materialized);
    }

    [Fact]
    public void FindAsciiStringHits_TruncatesLongRunsAtConfiguredMaximum()
    {
        var bytes = new byte[] { (byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'E', 0x01 };

        var hits = SearchCore.FindAsciiStringHits(bytes, 2, 3, 4096);
        var materialized = SearchCore.MaterializeStringHits(bytes, hits, includeOffset: false);

        var hit = Assert.Single(hits);
        Assert.Equal(0, hit.Start);
        Assert.Equal(3, hit.Length);
        Assert.Equal(["ABC"], materialized);
    }

    [Fact]
    public void MaterializeStringHits_SkipsOutOfRangeHits()
    {
        var bytes = Encoding.ASCII.GetBytes("ABCD");
        var hits = new List<StringHitPosition>
        {
            new(0, 2, 12),
            new(3, 5, 12),
        };

        var materialized = SearchCore.MaterializeStringHits(bytes, hits, includeOffset: false);

        Assert.Equal(["AB"], materialized);
    }

    [Fact]
    public void ParseHit_ExtractsOffsetPrefixedStrings()
    {
        var parsed = RegexOutputCore.ParseHit("0x201\tAlpha123", includeOffset: true);

        Assert.Equal("0x201\tAlpha123", parsed.RawHit);
        Assert.Equal("Alpha123", parsed.Data);
        Assert.Equal("0x201", parsed.Offset);
    }

    [Fact]
    public void BuildCsvLine_SeparatesOffsetFromData()
    {
        var line = RegexOutputCore.BuildCsvLine(
            new RegexOutputRecord("email", "user@example.com", "file.bin", "0x20", "Regex")
        );

        Assert.Equal(
            "\"email\",\"user@example.com\",\"file.bin\",\"0x20\",\"Regex\"",
            line
        );
    }

    [Fact]
    public async Task ProcessRegexPatternsConcurrentlyAsync_WritesCorrectCsvForOffsetHit()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);

        var count = await Program.ProcessRegexPatternsConcurrentlyAsync(
            new HashSet<string> { "0x201\tAlpha123" },
            [(
                "letters",
                "Alpha123"
            )],
            ro: false,
            off: true,
            s: true,
            sw: writer,
            q: true,
            o: "results.csv",
            currentFile: "sample.bin",
            isCsvOutput: true,
            csvHeaderAlreadyWritten: false
        );

        await writer.FlushAsync();
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(1, count);
        Assert.Equal(2, lines.Length);
        Assert.Equal(RegexOutputCore.CsvHeader, lines[0]);
        Assert.Equal(
            "\"letters\",\"Alpha123\",\"sample.bin\",\"0x201\",\"Regex\"",
            lines[1]
        );
    }

    [Fact]
    public async Task ProcessRegexPatternsConcurrentlyAsync_WritesRegexOnlyTextWithRealOffset()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);

        var count = await Program.ProcessRegexPatternsConcurrentlyAsync(
            new HashSet<string> { "0x201\tAlpha123" },
            [(
                "letters",
                "Alpha"
            )],
            ro: true,
            off: true,
            s: true,
            sw: writer,
            q: true,
            o: "results.txt",
            currentFile: "sample.bin",
            isCsvOutput: false,
            csvHeaderAlreadyWritten: false
        );

        await writer.FlushAsync();
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = (await reader.ReadToEndAsync()).Trim();

        Assert.Equal(1, count);
        Assert.Equal("Alpha\t~0x201", output);
    }

    [Fact]
    public async Task RapidsBridge_FallsBackToStandardCsvSchemaWhenUnavailable()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);

        var count = await RapidsProcessor.ProcessRegexPatternsBridgeAsync(
            new HashSet<string> { "0x201\tAlpha123" },
            [("letters", "Alpha123")],
            ro: false,
            off: true,
            s: true,
            sw: writer,
            q: true,
            o: "results.csv",
            currentFile: "sample.bin",
            isCsvOutput: true,
            csvHeaderAlreadyWritten: false
        );

        await writer.FlushAsync();
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(1, count);
        Assert.Equal(2, lines.Length);
        Assert.Equal(RegexOutputCore.CsvHeader, lines[0]);
        Assert.Equal(
            "\"letters\",\"Alpha123\",\"sample.bin\",\"0x201\",\"Regex\"",
            lines[1]
        );
    }

    [Fact]
    public void ProcessingBenchmark_ToStringReflectsAvailability()
    {
        var unavailable = new ProcessingBenchmark { RapidsAvailable = false };
        var available = new ProcessingBenchmark
        {
            RapidsAvailable = true,
            StandardProcessingTimeMs = 100,
            RapidsProcessingTimeMs = 25,
            StandardResultCount = 5,
            RapidsResultCount = 5,
            SpeedupFactor = 4.0,
        };

        Assert.Equal("RAPIDS not available for comparison", unavailable.ToString());
        Assert.Equal(
            "Performance: Standard=100ms, RAPIDS=25ms, Speedup=4.00x, Results=Standard:5/RAPIDS:5",
            available.ToString()
        );
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    public void GetSizeReadable_ReturnsExpectedHumanReadableValues(long value, string expected)
    {
        Assert.Equal(expected, RuntimeUtilityCore.GetSizeReadable(value));
    }

    [Theory]
    [InlineData(64, 50L * 1024 * 1024, 25)]
    [InlineData(8, 10L * 1024 * 1024, 16)]
    [InlineData(64, 500L * 1024 * 1024, 64)]
    [InlineData(256, 2L * 1024 * 1024 * 1024, 128)]
    [InlineData(32, 20L * 1024 * 1024 * 1024, 64)]
    public void AdaptChunkSizeForFile_ReturnsExpectedChunkSize(
        int baseChunkSizeMb,
        long fileSizeBytes,
        int expectedChunkSizeMb
    )
    {
        Assert.Equal(
            expectedChunkSizeMb,
            RuntimeUtilityCore.AdaptChunkSizeForFile(baseChunkSizeMb, fileSizeBytes)
        );
    }

    [Fact]
    public async Task CreateBatchedAsyncEnumerable_GroupsItemsIntoExpectedBatches()
    {
        var batches = new List<List<int>>();

        await foreach (var batch in RuntimeUtilityCore.CreateBatchedAsyncEnumerable(GetNumbers(), 2))
        {
            batches.Add(batch);
        }

        Assert.Equal(3, batches.Count);
        Assert.Equal([1, 2], batches[0]);
        Assert.Equal([3, 4], batches[1]);
        Assert.Equal([5], batches[2]);
    }

    [Fact]
    public void BuiltInPatternCatalog_ContainsExpectedInventory()
    {
        Assert.Equal(27, BuiltInPatternCatalog.Descriptions.Count);
        Assert.Equal(27, BuiltInPatternCatalog.Patterns.Count);
        Assert.Equal(
            BuiltInPatternCatalog.Descriptions.Keys.OrderBy(key => key),
            BuiltInPatternCatalog.Patterns.Keys.OrderBy(key => key)
        );
    }

    [Fact]
    public void BuiltInPatternCatalog_ContainsRepresentativeEntries()
    {
        Assert.Equal("\tFinds GUIDs", BuiltInPatternCatalog.Descriptions["guid"]);
        Assert.Equal(
            @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,6}\b",
            BuiltInPatternCatalog.Patterns["email"]
        );
        Assert.Contains("IPv6 host", BuiltInPatternCatalog.Patterns["url3986"]);
    }

    [Fact]
    public void ProcessChunk_ExtractsAsciiAndUnicodeHits()
    {
        var asciiResults = ChunkProcessingCore.ProcessChunk(
            Encoding.ASCII.GetBytes("\u0001Alpha\u0002"),
            fileOffset: 256,
            isBoundaryChunk: false,
            minLength: 4,
            maxLength: -1,
            asciiSearch: true,
            unicodeSearch: false,
            includeOffset: true,
            asciiRange: "[\\x20-\\x7E]",
            unicodeRange: "[\\u0020-\\u007E]"
        );

        var unicodeResults = ChunkProcessingCore.ProcessChunk(
            Encoding.Unicode.GetBytes("Beta"),
            fileOffset: 512,
            isBoundaryChunk: false,
            minLength: 4,
            maxLength: -1,
            asciiSearch: false,
            unicodeSearch: true,
            includeOffset: true,
            asciiRange: "[\\x20-\\x7E]",
            unicodeRange: "[\\u0020-\\u007E]"
        );

        Assert.Equal(["0x101\tAlpha"], asciiResults);
        Assert.Equal(["0x200\tBeta"], unicodeResults);
    }

    [Fact]
    public void ProcessChunk_PrefixesBoundaryHits()
    {
        var bytes = Encoding.ASCII.GetBytes("\u0001Alpha\u0002");

        var results = ChunkProcessingCore.ProcessChunk(
            bytes,
            fileOffset: 0,
            isBoundaryChunk: true,
            minLength: 4,
            maxLength: -1,
            asciiSearch: true,
            unicodeSearch: false,
            includeOffset: false,
            asciiRange: "[\\x20-\\x7E]",
            unicodeRange: "[\\u0020-\\u007E]"
        );

        Assert.Equal(["  Alpha"], results);
    }

    [Fact]
    public async Task ChunkProcessingPipeline_ProcessChunksAsync_CollectsResults()
    {
        using var pipeline = new Program.ChunkProcessingPipeline(maxConcurrency: 1);

        var results = await pipeline.ProcessChunksAsync(
            GetChunks(CreateChunk("\u0001Alpha\u0002", 0, 0, false)),
            minLength: 4,
            maxLength: -1,
            asciiSearch: true,
            unicodeSearch: false,
            off: false,
            cp: 1252,
            ar: "[\\x20-\\x7E]",
            ur: "[\\u0020-\\u007E]",
            progressTracker: new Program.ProgressTracker(totalChunks: 1, quiet: true)
        );

        Assert.Equal(["Alpha"], results);
    }

    [Fact]
    public async Task ChunkProcessingPipeline_ProcessChunksStreamingAsync_DeduplicatesAndWritesOutput()
    {
        using var pipeline = new Program.ChunkProcessingPipeline(maxConcurrency: 1);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        var resultsSet = new HashSet<string>();

        var totalCount = await pipeline.ProcessChunksStreamingAsync(
            GetChunks(
                CreateChunk("\u0001Alpha\u0002", 0, 0, false),
                CreateChunk("\u0001Alpha\u0002", 16, 1, false)
            ),
            minLength: 4,
            maxLength: -1,
            asciiSearch: true,
            unicodeSearch: false,
            off: false,
            cp: 1252,
            ar: "[\\x20-\\x7E]",
            ur: "[\\u0020-\\u007E]",
            progressTracker: new Program.ProgressTracker(totalChunks: 2, quiet: true),
            outputWriter: writer,
            resultsSet: resultsSet
        );

        await writer.FlushAsync();
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = (await reader.ReadToEndAsync())
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(1, totalCount);
        Assert.Equal(["Alpha"], output);
        Assert.Equal(["Alpha"], resultsSet.ToArray());
    }

    [Fact]
    public void ProgressTracker_TracksCompletionAndStringCounts()
    {
        var tracker = new Program.ProgressTracker(totalChunks: 2, quiet: true);

        tracker.ReportChunkComplete(3);

        Assert.True(tracker.HasCompletedChunks);
        Assert.Equal(3, tracker.TotalStrings);
        Assert.False(tracker.IsCompleted);

        tracker.ReportChunkComplete(2);

        Assert.Equal(5, tracker.TotalStrings);
        Assert.True(tracker.IsCompleted);
        Assert.True(tracker.ElapsedSeconds >= 0);
    }

    [Fact]
    public void ProgressTracker_MarkCompletedForcesCompletedState()
    {
        var tracker = new Program.ProgressTracker(totalChunks: 10, quiet: true);

        tracker.MarkCompleted();

        Assert.True(tracker.IsCompleted);
    }

    [Fact]
    public async Task ResultCollectionCore_CollectStreamingAsync_DeduplicatesAndClearsChunks()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        var resultsSet = new HashSet<string>();
        var firstChunk = new List<string> { "Alpha", "Beta" };
        var secondChunk = new List<string> { "Beta", "Gamma" };

        var total = await ResultCollectionCore.CollectStreamingAsync(
            GetStringBatches(firstChunk, secondChunk),
            writer,
            resultsSet,
            flushBatchSize: 2
        );

        await writer.FlushAsync();
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = (await reader.ReadToEndAsync())
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, total);
        Assert.Equal(["Alpha", "Beta", "Gamma"], output);
        Assert.Empty(firstChunk);
        Assert.Empty(secondChunk);
    }

    [Fact]
    public async Task ResultCollectionCore_CollectLimitedAsync_TruncatesAtLimitAndLogsWhenDebugging()
    {
        var results = new List<string>();
        var firstChunk = new List<string> { "Alpha", "Beta" };
        var secondChunk = new List<string> { "Gamma", "Delta" };
        string? debugMessage = null;

        await ResultCollectionCore.CollectLimitedAsync(
            GetStringBatches(firstChunk, secondChunk),
            results,
            debug: true,
            maxResultsInMemory: 3,
            debugLogger: message => debugMessage = message
        );

        Assert.Equal(["Alpha", "Beta", "Gamma"], results);
        Assert.Empty(firstChunk);
        Assert.Equal(["Gamma", "Delta"], secondChunk);
        Assert.Contains("truncated at 3 results", debugMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChunkReadingCore_ReadChunksAsync_ReadsMainChunksSequentially()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("ABCDEFGHIJ"));

        var chunks = await ChunkReadingCore.ReadChunksAsync(
            stream,
            totalBytes: 10,
            chunkSizeBytes: 4,
            maxReadAheadChunks: 10,
            rent: size => new byte[size]
        );

        Assert.Equal(3, chunks.Count);
        Assert.Equal("ABCD", Encoding.ASCII.GetString(chunks[0].Data, 0, chunks[0].ValidBytes));
        Assert.Equal("EFGH", Encoding.ASCII.GetString(chunks[1].Data, 0, chunks[1].ValidBytes));
        Assert.Equal("IJ", Encoding.ASCII.GetString(chunks[2].Data, 0, chunks[2].ValidBytes));
        Assert.Equal(0, chunks[0].FileOffset);
        Assert.Equal(4, chunks[1].FileOffset);
        Assert.Equal(8, chunks[2].FileOffset);
        Assert.All(chunks, chunk => Assert.False(chunk.IsBoundaryChunk));
    }

    [Fact]
    public async Task ChunkReadingCore_ReadChunksAsync_ReadsBoundaryChunksAtStride()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("ABCDEFGHIJ"));

        var chunks = await ChunkReadingCore.ReadChunksAsync(
            stream,
            totalBytes: 10,
            chunkSizeBytes: 4,
            startOffset: 2,
            isBoundaryMode: true,
            boundaryChunkSize: 3,
            maxReadAheadChunks: 10,
            rent: size => new byte[size]
        );

        Assert.Equal(2, chunks.Count);
        Assert.Equal("CDE", Encoding.ASCII.GetString(chunks[0].Data, 0, chunks[0].ValidBytes));
        Assert.Equal("GHI", Encoding.ASCII.GetString(chunks[1].Data, 0, chunks[1].ValidBytes));
        Assert.Equal(2, chunks[0].FileOffset);
        Assert.Equal(6, chunks[1].FileOffset);
        Assert.All(chunks, chunk => Assert.True(chunk.IsBoundaryChunk));
    }

    private static async IAsyncEnumerable<int> GetNumbers()
    {
        for (var i = 1; i <= 5; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    private static async IAsyncEnumerable<Program.DataChunk> GetChunks(params Program.DataChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<List<string>> GetStringBatches(params List<string>[] batches)
    {
        foreach (var batch in batches)
        {
            await Task.Yield();
            yield return batch;
        }
    }

    private static Program.DataChunk CreateChunk(
        string content,
        long fileOffset,
        int chunkIndex,
        bool isBoundaryChunk
    )
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        var rented = Program.ByteArrayPool.Rent(bytes.Length);
        Array.Copy(bytes, rented, bytes.Length);

        return new Program.DataChunk
        {
            Data = rented,
            ValidBytes = bytes.Length,
            FileOffset = fileOffset,
            ChunkIndex = chunkIndex,
            IsBoundaryChunk = isBoundaryChunk,
        };
    }
}
