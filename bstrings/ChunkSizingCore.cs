#nullable enable

using System;

namespace bstrings;

internal static class ChunkSizingCore
{
    internal static int CalculateCpuChunkSizeMBFromMemory(
        long availableMemoryBytes,
        int maxConcurrentChunks,
        double memoryUsageFraction = 0.05,
        int minPracticalMB = 32,
        int maxPracticalMB = 256
    )
    {
        var usableMemory = (long)(availableMemoryBytes * memoryUsageFraction);
        var memoryPerChunk = usableMemory / Math.Max(1, maxConcurrentChunks);
        var chunkSizeMB = (int)(memoryPerChunk / (1024 * 1024));

        return Math.Max(minPracticalMB, Math.Min(maxPracticalMB, chunkSizeMB));
    }

    internal static int CalculateGpuChunkSizeMBFromMemory(
        long usableGpuMemoryBytes,
        int gpuHitStructSizeBytes = 12,
        int defaultMinStringLengthBytes = 3,
        int minPracticalMB = 64,
        int maxPracticalMB = 2048
    )
    {
        if (usableGpuMemoryBytes <= 0)
        {
            return 0;
        }

        var memoryFactor = 1.0 + ((double)gpuHitStructSizeBytes / defaultMinStringLengthBytes);
        var calculatedChunkSizeBytes = (long)(usableGpuMemoryBytes / memoryFactor);
        var calculatedMB = (int)(calculatedChunkSizeBytes / (1024 * 1024));
        var maxSafeBytesForInt = int.MaxValue / (1024 * 1024);
        var maxAllowedMB = Math.Min(maxPracticalMB, maxSafeBytesForInt);

        return Math.Max(minPracticalMB, Math.Min(maxAllowedMB, calculatedMB));
    }

    internal static int SelectOptimalChunkSize(
        bool gpuAvailable,
        int gpuChunkSizeMB,
        int cpuChunkSizeMB,
        long fileSizeBytes
    )
    {
        var baseChunkSize = gpuAvailable && gpuChunkSizeMB > 0 ? gpuChunkSizeMB : cpuChunkSizeMB;
        return RuntimeUtilityCore.AdaptChunkSizeForFile(baseChunkSize, fileSizeBytes);
    }
}