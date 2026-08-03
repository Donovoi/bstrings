using Xunit;

namespace bstrings.Tests;

public class RustAsciiEngineTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("DOTNET", 0)]
    [InlineData("rust", 1)]
    [InlineData(" auto ", 2)]
    public void TryParseMode_AcceptsSupportedNames(string? value, int expected)
    {
        var parsed = RustAsciiEngine.TryParseMode(value, out var actual, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal((CpuStringEngineMode)expected, actual);
    }

    [Fact]
    public void TryParseMode_RejectsUnknownName()
    {
        var parsed = RustAsciiEngine.TryParseMode("cuda", out var actual, out var error);

        Assert.False(parsed);
        Assert.Equal(CpuStringEngineMode.DotNet, actual);
        Assert.Contains("dotnet, rust, or auto", error);
    }

    [Fact]
    public void NativeEngine_PreservesKnownOffsetsRangesAndTruncation()
    {
        byte[] data = [0, .. "Alpha123"u8.ToArray(), 1, .. "Beta!"u8.ToArray(), 0];

        if (
            !RustAsciiEngine.TryFindNativeForTesting(
                data,
                4,
                5,
                0x1234,
                0x20,
                0x7E,
                out var hits,
                out var error
            )
        )
        {
            Assert.Skip($"The Rust test library is unavailable: {error}");
            return;
        }

        Assert.Equal([(1, 5, 0x1234L), (10, 5, 0x1234L)], Project(hits));
    }

    [Fact]
    public void NativeEngine_MatchesManagedEngineAcrossRandomBoundaryInputs()
    {
        var random = new Random(0x52555354);
        int[] lengths =
        [
            0,
            1,
            2,
            15,
            16,
            17,
            31,
            32,
            33,
            63,
            64,
            65,
            255,
            256,
            257,
            4095,
            4096,
            4097,
            65_537,
        ];
        (byte Minimum, byte Maximum)[] ranges =
        [
            (0x00, 0x00),
            (0x20, 0x7E),
            (0x30, 0x39),
            (0x80, 0xFF),
            (0x00, 0xFF),
            (0xFF, 0x00),
        ];

        foreach (var length in lengths)
        {
            var data = new byte[length];
            random.NextBytes(data);
            foreach (var range in ranges)
            {
                var minLength = random.Next(-1, 17);
                var maxLength = random.Next(0, 3) == 0 ? -1 : random.Next(1, 65);
                var fileOffset = random.NextInt64(0, long.MaxValue / 2);
                var expected = SearchCore.FindAsciiStringHitsManaged(
                    data,
                    minLength,
                    maxLength,
                    fileOffset,
                    range.Minimum,
                    range.Maximum
                );

                if (
                    !RustAsciiEngine.TryFindNativeForTesting(
                        data,
                        minLength,
                        maxLength,
                        fileOffset,
                        range.Minimum,
                        range.Maximum,
                        out var actual,
                        out var error
                    )
                )
                {
                    Assert.Skip($"The Rust test library is unavailable: {error}");
                    return;
                }

                Assert.Equal(Project(expected), Project(actual));
            }
        }
    }

    [Fact]
    public void NativeEngine_RetriesWithExactCapacityForHighlyFragmentedInput()
    {
        var data = new byte[65_537];
        for (var offset = 0; offset + 3 <= data.Length; offset += 4)
        {
            data[offset] = (byte)'A';
            data[offset + 1] = (byte)'B';
            data[offset + 2] = (byte)'C';
        }

        var expected = SearchCore.FindAsciiStringHitsManaged(data, 3, -1, 0x4000, 0x20, 0x7E);
        Assert.True(expected.Count > 8_000);
        if (
            !RustAsciiEngine.TryFindNativeForTesting(
                data,
                3,
                -1,
                0x4000,
                0x20,
                0x7E,
                out var actual,
                out var error
            )
        )
        {
            Assert.Skip($"The Rust test library is unavailable: {error}");
            return;
        }

        Assert.Equal(Project(expected), Project(actual));
    }

    [Fact]
    public void NativeHitBatch_MaterializesOwnedHitsAndReturnsItsPooledBuffer()
    {
        byte[] data =
        [
            .. "Leading"u8.ToArray(),
            0,
            .. "Crossing"u8.ToArray(),
            0,
            .. "Trailing"u8.ToArray(),
        ];
        const long fileOffset = 0x2000;
        var ownership = new ChunkHitOwnership(
            SuppressLeadingFragment: true,
            SuppressTrailingFragment: true,
            RequiredCrossingOffset: 13
        );

        if (
            !RustAsciiEngine.TryRentNativeForTesting(
                data,
                3,
                -1,
                fileOffset,
                0x20,
                0x7E,
                out var batch,
                out var error
            )
        )
        {
            Assert.Skip($"The Rust test library is unavailable: {error}");
            return;
        }

        var expectedHits = SearchCore.FindAsciiStringHitsManaged(
            data,
            3,
            -1,
            fileOffset,
            0x20,
            0x7E
        );
        expectedHits.RemoveAll(hit => !ownership.Accepts(hit.Start, hit.Length, data.Length));
        var expected = SearchCore.MaterializeStringHits(
            data,
            expectedHits,
            includeOffset: true
        );
        var actual = SearchCore.MaterializeStringHits(
            data,
            batch,
            includeOffset: true,
            ownership: ownership
        );

        Assert.Equal(expected, actual);
        Assert.Equal(["0x2008\tCrossing"], actual);

        batch.Dispose();
        batch.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ReadBatchLength(batch));
    }

    private static List<(int Start, int Length, long FileOffset)> Project(
        IEnumerable<StringHitPosition> hits
    )
    {
        return hits.Select(hit => (hit.Start, hit.Length, hit.FileOffset)).ToList();
    }

    private static int ReadBatchLength(RustAsciiEngine.HitBatch batch)
    {
        return batch.Hits.Length;
    }
}
