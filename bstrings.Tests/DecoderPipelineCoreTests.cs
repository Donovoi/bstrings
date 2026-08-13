using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using Xunit;

namespace bstrings.Tests;

public sealed class DecoderPipelineCoreTests
{
    [Fact]
    public void IsCandidate_TwoMiBNonPowerShellTokenStreamAllocatesNothing()
    {
        const int targetCharacters = 2 * 1024 * 1024;
        var builder = new StringBuilder(targetCharacters);
        while (builder.Length < targetCharacters)
        {
            builder.Append("ordinary-token ");
        }
        builder.Length = targetCharacters;
        var text = builder.ToString();
        Assert.False(DecoderPipelineCore.IsCandidate(text, DecoderWorkflowMode.Auto));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 8; iteration++)
        {
            Assert.False(DecoderPipelineCore.IsCandidate(text, DecoderWorkflowMode.Auto));
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task ProcessAsync_AutoPublishesCanonicalUtf8AndIgnoresAmbiguousLetters()
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync(
            "VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==",
            "ThisLooksLikeEnglishLettersOnly"
        );

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stats.InputRecords);
        Assert.Equal(1, stats.CandidateOccurrences);
        Assert.Equal(1, stats.PublishedTextChildren);
        var child = Assert.Single(await scope.ReadDecodedAsync());
        Assert.Equal("This is a useful text.", child.Text);
        Assert.Equal(DecoderPipelineCore.Base64Profile, child.Transform!.Profile);
    }

    [Fact]
    public async Task ProcessAsync_ForceAssessesNonCanonicalPaddingWithoutPublishing()
    {
        using var scope = new DecoderScope(DecoderWorkflowMode.Force);
        await scope.WriteRawAsync("ZE==ZE==");

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.CanonicalRejected);
        Assert.Equal(0, stats.PublishedTextChildren);
        Assert.Empty(await scope.ReadDecodedAsync());
    }

    [Theory]
    [InlineData("Zm9vZh==")]
    [InlineData("Zm9vYmF=")]
    public async Task ProcessAsync_RejectsNonzeroUnusedPadBits(string encoded)
    {
        using var scope = new DecoderScope(DecoderWorkflowMode.Force);
        await scope.WriteRawAsync(encoded);

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.CanonicalRejected);
    }

    [Theory]
    [InlineData("  VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==  ", true)]
    [InlineData("VGhpcyBp cyBhIHVzZWZ1bCB0ZXh0Lg==", false)]
    [InlineData("prefix-VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==", false)]
    [InlineData("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==-suffix", false)]
    [InlineData("________________________", false)]
    public async Task ProcessAsync_OnlyAcceptsWholeStandardAlphabetWithOuterWhitespace(
        string value,
        bool expectedCandidate
    )
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync(value);

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedCandidate ? 1 : 0, stats.CandidateOccurrences);
    }

    [Fact]
    public async Task ProcessAsync_PowerShellProfileDecodesContextualUtf16Le()
    {
        using var scope = new DecoderScope();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("Write-Host test"));
        await scope.WriteRawAsync($"pwsh -NoProfile -EncodedCommand {encoded}");

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.PublishedTextChildren);
        var child = Assert.Single(await scope.ReadDecodedAsync());
        Assert.Equal("Write-Host test", child.Text);
        Assert.Equal(DecoderPipelineCore.PowerShellProfile, child.Transform!.Profile);
        Assert.Equal("utf-16le-powershell", child.Attributes!["decodedCharset"].GetString());
    }

    [Theory]
    [InlineData("-Command")]
    [InlineData("-File")]
    [InlineData("--%")]
    [InlineData("-ExecutionPolicy")]
    [InlineData("-Unknown")]
    public async Task ProcessAsync_RejectsAmbiguousPowerShellPreambleTokens(string token)
    {
        using var scope = new DecoderScope();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("Write-Host test"));
        await scope.WriteRawAsync($"pwsh {token} -EncodedCommand {encoded}");

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stats.CandidateOccurrences);
        Assert.Empty(await scope.ReadDecodedAsync());
    }

    [Fact]
    public async Task ProcessAsync_RejectsDuplicateEncodedCommandSwitch()
    {
        using var scope = new DecoderScope();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("Write-Host test"));
        await scope.WriteRawAsync($"pwsh -EncodedCommand {encoded} -EncodedCommand {encoded}");

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stats.CandidateOccurrences);
    }

    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le-bom")]
    [InlineData("utf-16be-bom")]
    public async Task ProcessAsync_GenericProfileSupportsOnlyExplicitBomTextEncodings(string charset)
    {
        using var scope = new DecoderScope();
        var payload = EncodeWithBom("visible text content", charset);
        await scope.WriteRawAsync(Convert.ToBase64String(payload));

        await scope.ProcessAsync(TestContext.Current.CancellationToken);

        var child = Assert.Single(await scope.ReadDecodedAsync());
        Assert.Equal("visible text content", child.Text);
        Assert.Equal(charset, child.Attributes!["decodedCharset"].GetString());
    }

    [Fact]
    public async Task ProcessAsync_RecognizesKnownBinaryWithoutPublishingIt()
    {
        using var scope = new DecoderScope();
        var payload = new byte[128];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x3C, 4), 0x40);
        "PE\0\0"u8.CopyTo(payload.AsSpan(0x40));
        await scope.WriteRawAsync(Convert.ToBase64String(payload));

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.DecodedBinaryKnown);
        Assert.Empty(await scope.ReadDecodedAsync());
        var assessment = Assert.Single(await scope.ReadAssessmentsAsync());
        Assert.Equal("pe", assessment.BinaryClass);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotSuppressPlainTextStartingWithMz()
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("MZ this is plainly text")));

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stats.DecodedBinaryKnown);
        Assert.Equal("MZ this is plainly text", Assert.Single(await scope.ReadDecodedAsync()).Text);
    }

    [Theory]
    [MemberData(nameof(UnpublishablePayloads))]
    public async Task ProcessAsync_DoesNotPublishInvalidOrDisallowedDecodedText(byte[] payload)
    {
        using var scope = new DecoderScope(DecoderWorkflowMode.Force);
        await scope.WriteRawAsync(Convert.ToBase64String(payload));

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stats.PublishedTextChildren);
        Assert.Empty(await scope.ReadDecodedAsync());
    }

    [Fact]
    public async Task ProcessAsync_PublishesExactlyFourVisibleScalars()
    {
        using var scope = new DecoderScope(DecoderWorkflowMode.Force);
        await scope.WriteRawAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("A B C D")));

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.PublishedTextChildren);
    }

    public static TheoryData<byte[]> UnpublishablePayloads => new()
    {
        new byte[] { 0xF0, 0x28, 0x8C, 0x28, 0x20, 0x20 },
        new byte[] { 0x00, (byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'E' },
        new byte[] { 0x01, (byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'E' },
        new byte[] { 0x00, 0xD8, (byte)'A', 0x00, (byte)'B', 0x00 },
    };

    [Fact]
    public async Task ProcessAsync_CandidateLimitAggregatesLaterOccurrences()
    {
        using var scope = new DecoderScope(maxCandidates: 1);
        await scope.WriteRawAsync(
            "VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==",
            "QW5vdGhlciB1c2VmdWwgdGV4dC4="
        );

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stats.CandidateOccurrences);
        Assert.Equal(1, stats.AttemptedCandidates);
        Assert.Equal(1, stats.AssessmentRecords);
        Assert.Equal(1, stats.SkippedAfterCandidateLimit);
    }

    [Theory]
    [InlineData("candidate", "candidate-too-large")]
    [InlineData("record-bytes", "decoded-payload-too-large")]
    [InlineData("total-bytes", "total-decoded-byte-limit")]
    public async Task ProcessAsync_EnforcesEveryConfiguredByteAndRecordLimit(
        string limit,
        string expectedReason
    )
    {
        using var scope = limit switch
        {
            "candidate" => new DecoderScope(maxCharacters: 8),
            "record-bytes" => new DecoderScope(maxBytes: 8),
            "total-bytes" => new DecoderScope(maxTotalBytes: 8),
            _ => throw new ArgumentOutOfRangeException(nameof(limit)),
        };
        await scope.WriteRawAsync("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==");

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.ResourceLimited);
        Assert.Equal(0, stats.PublishedTextChildren);
        Assert.Equal(expectedReason, Assert.Single(await scope.ReadAssessmentsAsync()).Reason);
    }

    [Fact]
    public async Task ProcessAsync_UsesExactPaddedDecodedLengthForPerRecordLimit()
    {
        using var scope = new DecoderScope(DecoderWorkflowMode.Force, maxBytes: 5);
        await scope.WriteRawAsync("SGVsbG8=");

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.PublishedTextChildren);
        Assert.Equal("Hello", Assert.Single(await scope.ReadDecodedAsync()).Text);
    }

    [Fact]
    public async Task ProcessAsync_TotalLimitPreflightDoesNotCountOrPublishOverLimitBytes()
    {
        using var scope = new DecoderScope(DecoderWorkflowMode.Force, maxTotalBytes: 5);
        await scope.WriteRawAsync("SGVsbG8=", "V29ybGQ=");

        var stats = await scope.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, stats.DecodedBytesAttempted);
        Assert.Equal(1, stats.PublishedTextChildren);
        Assert.Equal(1, stats.ResourceLimited);
        Assert.Equal("total-decoded-byte-limit", (await scope.ReadAssessmentsAsync())[1].Reason);
    }

    [Fact]
    public async Task ProcessAsync_CancellationLeavesNoOutputsOrPartials()
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.ProcessAsync(cancelled.Token)
        );

        Assert.False(File.Exists(scope.DecodedPath));
        Assert.False(File.Exists(scope.AssessmentsPath));
        Assert.False(File.Exists(scope.StatsPath));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    [Fact]
    public async Task ProcessAsync_IsByteDeterministicAcrossEquivalentRuns()
    {
        using var first = new DecoderScope();
        using var second = new DecoderScope();
        const string encoded = "VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==";
        await first.WriteRawAsync(encoded);
        await second.WriteRawAsync(encoded);

        await first.ProcessAsync(TestContext.Current.CancellationToken);
        await second.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(await File.ReadAllBytesAsync(first.DecodedPath, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(second.DecodedPath, TestContext.Current.CancellationToken));
        Assert.Equal(await File.ReadAllBytesAsync(first.AssessmentsPath, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(second.AssessmentsPath, TestContext.Current.CancellationToken));
        Assert.Equal(await File.ReadAllBytesAsync(first.StatsPath, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(second.StatsPath, TestContext.Current.CancellationToken));
    }

    private static byte[] EncodeWithBom(string value, string charset) => charset switch
    {
        "utf-8-bom" => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(value)],
        "utf-16le-bom" => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(value)],
        "utf-16be-bom" => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(value)],
        _ => throw new ArgumentOutOfRangeException(nameof(charset)),
    };
}

internal sealed class DecoderScope : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly DecoderPipelineOptions _options;

    internal DecoderScope(
        DecoderWorkflowMode mode = DecoderWorkflowMode.Auto,
        int maxCharacters = 16_384,
        int maxBytes = 12_288,
        long maxCandidates = 100_000,
        long maxTotalBytes = 64L * 1024 * 1024
    )
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "bstrings-decoder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        RawPath = Path.Combine(DirectoryPath, "raw.jsonl");
        DecodedPath = Path.Combine(DirectoryPath, "decoded-strings.jsonl");
        AssessmentsPath = Path.Combine(DirectoryPath, "decoder-assessments.jsonl");
        StatsPath = Path.Combine(DirectoryPath, "decoder-work-stats.json");
        _options = new(mode, maxCharacters, maxBytes, maxCandidates, maxTotalBytes);
    }

    internal string DirectoryPath { get; }
    internal string RawPath { get; }
    internal string DecodedPath { get; }
    internal string AssessmentsPath { get; }
    internal string StatsPath { get; }
    internal DecoderPipelineOptions Options => _options;

    internal async Task WriteRawAsync(params string[] text)
    {
        var lines = text.Select((value, index) => JsonSerializer.Serialize(
            new EnrichmentStringRecord
            {
                SchemaVersion = 1,
                RecordType = "string",
                RecordId = $"raw-{index + 1}",
                Text = value,
                SourceFile = "evidence.bin",
                Location = new EnrichmentLocation { Kind = "file_offset", Value = $"0x{index:X}" },
                Origin = new EnrichmentOrigin { Extractor = "bstrings", Version = "test", Kind = "static" },
            },
            JsonOptions
        ));
        await File.WriteAllLinesAsync(RawPath, lines, TestContext.Current.CancellationToken);
    }

    internal Task<DecoderPipelineStats> ProcessAsync(CancellationToken cancellationToken) =>
        DecoderPipelineCore.ProcessAsync(RawPath, DecodedPath, AssessmentsPath, StatsPath, _options, cancellationToken);

    internal async Task<List<EnrichmentStringRecord>> ReadDecodedAsync()
    {
        var records = new List<EnrichmentStringRecord>();
        await foreach (var item in EnrichmentJsonlReader.ReadAsync(DecodedPath)) records.Add(item.Record);
        return records;
    }

    internal async Task<List<DecoderAssessmentRecord>> ReadAssessmentsAsync()
    {
        var records = new List<DecoderAssessmentRecord>();
        foreach (var line in await File.ReadAllLinesAsync(AssessmentsPath))
        {
            records.Add(JsonSerializer.Deserialize<DecoderAssessmentRecord>(line, JsonOptions)!);
        }
        return records;
    }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
