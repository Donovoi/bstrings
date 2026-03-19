using System.Text;
using Xunit;

namespace bstrings.Tests;

public class InfrastructureCoreTests
{
    [Fact]
    public void SimpleObjectPool_ReusesAndResetsObjectsAcrossMultipleCycles()
    {
        var pool = new Program.SimpleObjectPool<PooledItem>(
            objectGenerator: () => new PooledItem { Value = 5 },
            resetAction: item => item.Value = 0,
            maxSize: 1
        );

        var first = pool.Get();
        first.Value = 42;
        pool.Return(first);

        var second = pool.Get();
        second.Value = 99;
        pool.Return(second);

        var third = pool.Get();

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(0, third.Value);
    }

    [Theory]
    [InlineData(512 * 1024L, 64)]
    [InlineData(50L * 1024 * 1024, 256)]
    [InlineData(500L * 1024 * 1024, 1024)]
    [InlineData(2L * 1024 * 1024 * 1024, 1024)]
    public void ConcurrentConfig_GetOptimalChunkSizeKB_ReturnsExpectedThresholds(
        long fileSizeBytes,
        int expectedChunkSizeKb
    )
    {
        Assert.Equal(expectedChunkSizeKb, Program.ConcurrentConfig.GetOptimalChunkSizeKB(fileSizeBytes));
    }

    [Fact]
    public void ConcurrentConfig_IsMemorySafe_UsesEstimatedMemoryThreshold()
    {
        var safeChunkSizeMb = Math.Max(
            1,
            1023 / (Program.ConcurrentConfig.MaxConcurrentChunks * 3)
        );
        var unsafeChunkSizeMb = safeChunkSizeMb + 1;
        var safeChunkSizeKb = safeChunkSizeMb * 1024;
        var unsafeChunkSizeKb = unsafeChunkSizeMb * 1024;

        Assert.True(Program.ConcurrentConfig.IsMemorySafe(100L * 1024 * 1024, safeChunkSizeKb));
        Assert.False(Program.ConcurrentConfig.IsMemorySafe(100L * 1024 * 1024, unsafeChunkSizeKb));
    }

    [Fact]
    public void ByteArrayPool_RentReturnsAtLeastRequestedLength()
    {
        var buffer = Program.ByteArrayPool.Rent(128);

        try
        {
            Assert.True(buffer.Length >= 128);
            Encoding.ASCII.GetBytes("Alpha", buffer);
            Assert.Equal((byte)'A', buffer[0]);
        }
        finally
        {
            Program.ByteArrayPool.Return(buffer, clearArray: true);
        }
    }

    private sealed class PooledItem
    {
        public int Value { get; set; }
    }
}
