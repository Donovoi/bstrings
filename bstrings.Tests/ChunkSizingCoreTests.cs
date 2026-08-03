using Xunit;

namespace bstrings.Tests;

public class ChunkSizingCoreTests
{
    [Theory]
    [InlineData(512L * 1024 * 1024, 4, 32)]
    [InlineData(16L * 1024 * 1024 * 1024, 8, 102)]
    [InlineData(128L * 1024 * 1024 * 1024, 8, 256)]
    [InlineData(1L * 1024 * 1024 * 1024, 0, 51)]
    public void CalculateCpuChunkSizeMBFromMemory_ReturnsExpectedValue(
        long availableMemoryBytes,
        int maxConcurrentChunks,
        int expectedChunkSizeMb
    )
    {
        var chunkSize = ChunkSizingCore.CalculateCpuChunkSizeMBFromMemory(
            availableMemoryBytes,
            maxConcurrentChunks
        );

        Assert.Equal(expectedChunkSizeMb, chunkSize);
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(100L * 1024 * 1024, 64)]
    [InlineData(512L * 1024 * 1024, 102)]
    [InlineData(2L * 1024 * 1024 * 1024, 409)]
    public void CalculateGpuChunkSizeMBFromMemory_ReturnsExpectedValue(
        long usableGpuMemoryBytes,
        int expectedChunkSizeMb
    )
    {
        var chunkSize = ChunkSizingCore.CalculateGpuChunkSizeMBFromMemory(usableGpuMemoryBytes);

        Assert.Equal(expectedChunkSizeMb, chunkSize);
    }

    [Fact]
    public void CalculateGpuChunkSizeMBFromMemory_ClampsToSafeMaximum()
    {
        var chunkSize = ChunkSizingCore.CalculateGpuChunkSizeMBFromMemory(
            20L * 1024 * 1024 * 1024
        );

        Assert.Equal(2047, chunkSize);
    }

    [Theory]
    [InlineData(true, 128, 64, 50L * 1024 * 1024, 4, 16)]
    [InlineData(false, 128, 64, 500L * 1024 * 1024, 8, 16)]
    [InlineData(true, 256, 64, 2L * 1024 * 1024 * 1024, 8, 16)]
    [InlineData(false, 0, 32, 20L * 1024 * 1024 * 1024, 8, 32)]
    public void SelectOptimalChunkSize_UsesExpectedSourceAndAdaptation(
        bool gpuAvailable,
        int gpuChunkSizeMb,
        int cpuChunkSizeMb,
        long fileSizeBytes,
        int targetConcurrency,
        int expectedChunkSizeMb
    )
    {
        var chunkSize = ChunkSizingCore.SelectOptimalChunkSize(
            gpuAvailable,
            gpuChunkSizeMb,
            cpuChunkSizeMb,
            fileSizeBytes,
            targetConcurrency
        );

        Assert.Equal(expectedChunkSizeMb, chunkSize);
    }

    [Theory]
    [InlineData(1L * 1024 * 1024 * 1024, 22, 16)]
    [InlineData(10L * 1024 * 1024 * 1024, 22, 16)]
    [InlineData(100L * 1024 * 1024 * 1024, 22, 128)]
    public void SelectOptimalChunkSize_KeepsCpuPipelineFedAcrossLargeFiles(
        long fileSizeBytes,
        int workers,
        int expectedChunkSizeMb
    )
    {
        var chunkSize = ChunkSizingCore.SelectOptimalChunkSize(
            gpuAvailable: false,
            gpuChunkSizeMB: 0,
            cpuChunkSizeMB: 256,
            fileSizeBytes,
            targetConcurrency: workers
        );

        Assert.Equal(expectedChunkSizeMb, chunkSize);
    }

    [Theory]
    [InlineData(256L * 1024 * 1024, 22, 2, 16)]
    [InlineData(1L * 1024 * 1024 * 1024, 22, 2, 24)]
    [InlineData(10L * 1024 * 1024 * 1024, 22, 2, 128)]
    public void SelectParallelChunkSize_KeepsEnoughChunksForWorkers(
        long fileSizeBytes,
        int workers,
        int chunksPerWorker,
        int expectedChunkSizeMb
    )
    {
        var chunkSize = ChunkSizingCore.SelectParallelChunkSizeMB(
            256,
            fileSizeBytes,
            workers,
            chunksPerWorker
        );

        Assert.Equal(expectedChunkSizeMb, chunkSize);
    }
}
