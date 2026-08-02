using System.Text;
using System.Diagnostics;
using DiscUtils.Streams;
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
    public void ParseRegexPatternsWithNames_PreservesCommasInsideRegexConstructsAndDeduplicates()
    {
        var patterns = SearchCore.ParseRegexPatternsWithNames(
            @"email,\d{1,3}(?:,\d{3})*,EMAIL",
            BuiltInPatterns
        );

        Assert.Collection(
            patterns,
            pattern =>
            {
                Assert.Equal("email", pattern.name);
                Assert.Equal(BuiltInPatterns["email"], pattern.pattern);
            },
            pattern =>
            {
                Assert.Equal(@"\d{1,3}(?:,\d{3})*", pattern.name);
                Assert.Equal(@"\d{1,3}(?:,\d{3})*", pattern.pattern);
            }
        );
    }

    [Fact]
    public async Task ParseAndExecuteCustomRegexes_PreservesCaseDistinctPatterns()
    {
        var patterns = SearchCore.ParseRegexPatternsWithNames(
            "secret,SECRET,secret",
            BuiltInPatterns
        );

        Assert.Equal(["secret", "SECRET"], patterns.Select(pattern => pattern.name));
        var count = await Program.ProcessRegexPatternsConcurrentlyAsync(
            new HashSet<string> { "secret", "SECRET" },
            patterns,
            ro: false,
            off: false,
            s: true,
            sw: null!,
            q: true,
            o: string.Empty
        );

        Assert.Equal(2, count);
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
        var bytes = Encoding.ASCII.GetBytes("ABCDEFGHIJ\u0001");

        var hits = SearchCore.FindAsciiStringHits(bytes, 2, 3, 4096);
        var materialized = SearchCore.MaterializeStringHits(bytes, hits, includeOffset: false);

        var hit = Assert.Single(hits);
        Assert.Equal(0, hit.Start);
        Assert.Equal(3, hit.Length);
        Assert.Equal(["ABC"], materialized);
    }

    [Fact]
    public void FindAsciiStringHits_MatchesScalarReferenceAcrossRandomInputs()
    {
        var random = new Random(unchecked((int)0xB57A1A65));
        var ranges = new (byte Min, byte Max)[]
        {
            (0x00, 0x00),
            (0x20, 0x7E),
            (0x30, 0x39),
            (0x80, 0xFF),
            (0x00, 0xFF),
            (0xFF, 0x00),
        };

        for (var iteration = 0; iteration < 250; iteration++)
        {
            var data = new byte[random.Next(0, 4097)];
            random.NextBytes(data);
            var minLength = random.Next(1, 17);
            var maxLength = random.Next(0, 3) == 0 ? -1 : random.Next(1, 33);
            var fileOffset = random.NextInt64(0, 1L << 40);

            foreach (var (minChar, maxChar) in ranges)
            {
                var expected = FindAsciiStringHitsScalar(
                    data,
                    minLength,
                    maxLength,
                    fileOffset,
                    minChar,
                    maxChar
                );
                var actual = SearchCore.FindAsciiStringHits(
                    data,
                    minLength,
                    maxLength,
                    fileOffset,
                    minChar,
                    maxChar
                );

                Assert.Equal(
                    expected.Select(hit => (hit.Start, hit.Length, hit.FileOffset)),
                    actual.Select(hit => (hit.Start, hit.Length, hit.FileOffset))
                );
            }
        }
    }

    [Fact]
    public void GetAsciiHits_UsesRequestedCodePage()
    {
        var hits = SearchCore.GetAsciiHits(
            [0x01, 0x80, 0x80, 0x02],
            minLength: 2,
            maxLength: -1,
            currentOffset: 0,
            includeOffset: false,
            asciiRange: "[\\x80-\\x80]",
            codePage: 1252
        );

        Assert.Equal(["€€"], hits);
    }

    private static List<StringHitPosition> FindAsciiStringHitsScalar(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar,
        byte maxChar
    )
    {
        var hits = new List<StringHitPosition>();
        var stringStart = -1;

        for (var position = 0; position <= data.Length; position++)
        {
            var isValid =
                position < data.Length
                && minChar <= maxChar
                && data[position] >= minChar
                && data[position] <= maxChar;
            if (isValid)
            {
                if (stringStart < 0)
                {
                    stringStart = position;
                }

                continue;
            }

            if (stringStart < 0)
            {
                continue;
            }

            var length = position - stringStart;
            if (length >= minLength)
            {
                var actualLength = maxLength > 0 && length > maxLength ? maxLength : length;
                hits.Add(new StringHitPosition(stringStart, actualLength, fileOffset));
            }

            stringStart = -1;
        }

        return hits;
    }

    [Fact]
    public void GetUnicodeHits_UsesRequestedCharacterRange()
    {
        var hits = SearchCore.GetUnicodeHits(
            Encoding.Unicode.GetBytes("\u0001ĀĆ\u0002"),
            minLength: 2,
            maxLength: -1,
            currentOffset: 0,
            includeOffset: false,
            unicodeRange: "[\\u0100-\\u017F]"
        );

        Assert.Equal(["ĀĆ"], hits);
    }

    [Fact]
    public void OrderHits_SortsExtractedDataRatherThanOffsetPrefixes()
    {
        var hits = new[]
        {
            "0x10\tZulu",
            "0x30\talpha",
            "0x20\tBeta",
        };

        var alphabetic = Program.OrderHits(
            hits,
            alphabetically: true,
            byLength: false,
            includeOffset: true
        );
        var byLength = Program.OrderHits(
            hits,
            alphabetically: false,
            byLength: true,
            includeOffset: true
        );

        Assert.Equal(["0x30\talpha", "0x20\tBeta", "0x10\tZulu"], alphabetic);
        Assert.Equal(["0x20\tBeta", "0x10\tZulu", "0x30\talpha"], byLength);
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

        await writer.FlushAsync(TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
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

        await writer.FlushAsync(TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = (
            await reader.ReadToEndAsync(TestContext.Current.CancellationToken)
        ).Trim();

        Assert.Equal(1, count);
        Assert.Equal("Alpha\t~0x201", output);
    }

    [Fact]
    public async Task UrlUserRegexOnlyOutput_EmitsUsernameRatherThanPasswordOrUrlPrefix()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);

        var count = await Program.ProcessRegexPatternsConcurrentlyAsync(
            new HashSet<string> { "https://analyst:secret@example.com/path" },
            [("urlUser", BuiltInPatternCatalog.Patterns["urlUser"])],
            ro: true,
            off: false,
            s: true,
            sw: writer,
            q: true,
            o: "results.txt"
        );

        await writer.FlushAsync(TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = (
            await reader.ReadToEndAsync(TestContext.Current.CancellationToken)
        ).Trim();

        Assert.Equal(1, count);
        Assert.Equal("analyst", output);
    }

    [Fact]
    public async Task ProcessRegexPatternsConcurrentlyAsync_PropagatesOutputFailures()
    {
        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        await writer.DisposeAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            Program.ProcessRegexPatternsConcurrentlyAsync(
                new HashSet<string> { "Alpha123" },
                [("letters", "Alpha")],
                ro: false,
                off: false,
                s: true,
                sw: writer,
                q: true,
                o: "results.txt"
            )
        );

        var propagatedDisposedFailure =
            exception is ObjectDisposedException
            || (
                exception is AggregateException aggregate
                && aggregate
                    .Flatten()
                    .InnerExceptions.Any(inner => inner is ObjectDisposedException)
            );
        Assert.True(propagatedDisposedFailure, exception.ToString());
    }

    [Fact]
    public async Task ProcessRegexPatternsConcurrentlyAsync_StopsAfterFirstRegexTimeout()
    {
        var pathological = new string('a', 100_000) + "!";
        var laterMatch = "a";
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            Program.ProcessRegexPatternsConcurrentlyAsync(
                new HashSet<string> { pathological, laterMatch },
                [("pathological", "^(a+)+$")],
                ro: false,
                off: false,
                s: true,
                sw: writer,
                q: true,
                o: "results.txt",
                orderedHits: [pathological, laterMatch]
            )
        );
        stopwatch.Stop();

        await writer.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Contains("incomplete", exception.Message);
        Assert.Empty(stream.ToArray());
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(6),
            $"Timeout abort took {stopwatch.Elapsed}."
        );
    }

    [Fact]
    public async Task Cli_UsesNonzeroExitAndKeepsIncompleteMarkerOnProcessingFailure()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("bstrings-cli-failure-");
        try
        {
            var inputPath = Path.Combine(tempDirectory.FullName, "input.txt");
            var outputPath = Path.Combine(tempDirectory.FullName, "output.txt");
            await File.WriteAllTextAsync(
                inputPath,
                "Alpha123",
                TestContext.Current.CancellationToken
            );

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("--lr");
            startInfo.ArgumentList.Add("(");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add("-s");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(
                TestContext.Current.CancellationToken
            );
            var stderrTask = process.StandardError.ReadToEndAsync(
                TestContext.Current.CancellationToken
            );
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.NotEqual(0, process.ExitCode);
            Assert.True(
                File.Exists(outputPath + ".incomplete"),
                $"No incomplete marker. stdout={stdout}; stderr={stderr}"
            );
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
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

        await writer.FlushAsync(TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
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
        Assert.Equal(33, BuiltInPatternCatalog.Descriptions.Count);
        Assert.Equal(33, BuiltInPatternCatalog.Patterns.Count);
        Assert.Equal(
            BuiltInPatternCatalog.Descriptions.Keys.OrderBy(key => key),
            BuiltInPatternCatalog.Patterns.Keys.OrderBy(key => key)
        );
    }

    [Fact]
    public void BuiltInPatternCatalog_ContainsRepresentativeEntries()
    {
        Assert.Equal("Finds GUIDs", BuiltInPatternCatalog.Descriptions["guid"]);
        Assert.Contains("long TLDs", BuiltInPatternCatalog.Descriptions["email"]);
        Assert.Contains("{0,61}", BuiltInPatternCatalog.Patterns["email"]);
        Assert.Contains("IPv6 candidate", BuiltInPatternCatalog.Patterns["url3986"]);
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
    public void ProcessChunk_BoundaryOwnershipRejectsClippedAndNonCrossingHits()
    {
        var complete = new byte[64];
        Encoding.ASCII.GetBytes("https://example.test/item").CopyTo(complete, 20);

        var clippedAtStart = new byte[64];
        Encoding.ASCII.GetBytes("ps://example.test/item-that-crosses").CopyTo(clippedAtStart, 0);

        var clippedAtEnd = new byte[64];
        Encoding.ASCII
            .GetBytes("https://example.test/".PadRight(40, 'x'))
            .CopyTo(clippedAtEnd, 24);

        var leftOnly = new byte[64];
        Encoding.ASCII.GetBytes("https://left.test").CopyTo(leftOnly, 4);

        Assert.Equal(["  https://example.test/item"], ProcessBoundary(complete));
        Assert.Empty(ProcessBoundary(clippedAtStart));
        Assert.Empty(ProcessBoundary(clippedAtEnd));
        Assert.Empty(ProcessBoundary(leftOnly));
    }

    [Fact]
    public void ProcessChunk_PrimaryOwnershipRejectsUnfinishedEdgeFragments()
    {
        var left = Encoding.ASCII.GetBytes("\u0001https://example.test");
        var right = Encoding.ASCII.GetBytes("ps://example.test\u0001");

        var leftResults = ChunkProcessingCore.ProcessChunk(
            left,
            fileOffset: 0,
            isBoundaryChunk: false,
            minLength: 3,
            maxLength: -1,
            asciiSearch: true,
            unicodeSearch: false,
            includeOffset: false,
            asciiRange: "[\\x20-\\x7E]",
            unicodeRange: "[\\u0020-\\u007E]",
            suppressTrailingFragment: true
        );
        var rightResults = ChunkProcessingCore.ProcessChunk(
            right,
            fileOffset: 16,
            isBoundaryChunk: false,
            minLength: 3,
            maxLength: -1,
            asciiSearch: true,
            unicodeSearch: false,
            includeOffset: false,
            asciiRange: "[\\x20-\\x7E]",
            unicodeRange: "[\\u0020-\\u007E]",
            suppressLeadingFragment: true
        );

        Assert.Empty(leftResults);
        Assert.Empty(rightResults);
    }

    [Theory]
    [InlineData(16 * 1024 * 1024, 3, -1, 256 * 1024)]
    [InlineData(1024 * 1024, 3, -1, 256 * 1024)]
    [InlineData(16 * 1024 * 1024, 3, 4096, 16 * 1024)]
    [InlineData(16 * 1024 * 1024, 3, 1_000_000, 4_000_000)]
    public void BoundarySizingCore_UsesBoundedContextAndHonorsExplicitMaximum(
        int chunkSize,
        int minLength,
        int maxLength,
        int expected
    )
    {
        Assert.Equal(
            expected,
            BoundarySizingCore.CalculateWindowSize(chunkSize, minLength, maxLength)
        );
    }

    [Fact]
    public void BoundarySizingCore_CapsWindowAtMaximumArrayLength()
    {
        var expected = Array.MaxLength - (Array.MaxLength % 2);

        Assert.Equal(
            expected,
            BoundarySizingCore.CalculateWindowSize(
                1024 * 1024 * 1024,
                minLength: 3,
                maxLength: int.MaxValue
            )
        );
    }

    private static List<string> ProcessBoundary(byte[] bytes)
    {
        return ChunkProcessingCore.ProcessChunk(
            bytes,
            fileOffset: 0,
            isBoundaryChunk: true,
            minLength: 3,
            maxLength: -1,
            asciiSearch: true,
            unicodeSearch: false,
            includeOffset: false,
            asciiRange: "[\\x20-\\x7E]",
            unicodeRange: "[\\u0020-\\u007E]",
            suppressLeadingFragment: true,
            suppressTrailingFragment: true,
            boundaryCrossingOffset: bytes.Length / 2
        );
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

        await writer.FlushAsync(TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = (
            await reader.ReadToEndAsync(TestContext.Current.CancellationToken)
        )
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

        await writer.FlushAsync(TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = (
            await reader.ReadToEndAsync(TestContext.Current.CancellationToken)
        )
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
        Assert.Empty(secondChunk);
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

    [Fact]
    public async Task ChunkReadingCore_ReadChunksAsync_KeepsPartialFinalBoundaryWindow()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("ABCDEFGHI"));

        var chunks = await ChunkReadingCore.ReadChunksAsync(
            stream,
            totalBytes: 9,
            chunkSizeBytes: 4,
            startOffset: 2,
            isBoundaryMode: true,
            boundaryChunkSize: 4,
            maxReadAheadChunks: 10,
            rent: size => new byte[size]
        );

        Assert.Equal(2, chunks.Count);
        Assert.Equal("CDEF", Encoding.ASCII.GetString(chunks[0].Data, 0, chunks[0].ValidBytes));
        Assert.Equal("GHI", Encoding.ASCII.GetString(chunks[1].Data, 0, chunks[1].ValidBytes));
        Assert.Equal(2, chunks[0].BoundaryCrossingOffset);
        Assert.Equal(2, chunks[1].BoundaryCrossingOffset);
        Assert.True(chunks[1].SuppressLeadingFragment);
        Assert.False(chunks[1].SuppressTrailingFragment);
    }

    [Fact]
    public async Task ReadChunksAsyncEnumerable_ContinuesBoundaryStridePastReadAheadBatch()
    {
        var bytes = Enumerable.Range(0, 50).Select(value => (byte)value).ToArray();
        using var source = new MemoryStream(bytes);
        using var mapped = MappedStream.FromStream(source, Ownership.None);
        var chunks = new List<Program.DataChunk>();

        await foreach (
            var chunk in Program.ReadChunksAsyncEnumerable(
                mapped,
                fileSizeBytes: bytes.Length,
                chunkSizeBytes: 4,
                startOffset: 2,
                isBoundaryMode: true,
                boundaryChunkSize: 3
            )
        )
        {
            chunks.Add(chunk);
        }

        try
        {
            Assert.Equal(Enumerable.Range(0, 12).Select(index => 2L + index * 4), chunks.Select(chunk => chunk.FileOffset));
            Assert.Equal(Enumerable.Range(0, 12), chunks.Select(chunk => chunk.ChunkIndex));
        }
        finally
        {
            foreach (var chunk in chunks)
            {
                Program.ByteArrayPool.Return(chunk.Data);
            }
        }
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
