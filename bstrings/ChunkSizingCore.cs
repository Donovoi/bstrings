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
        long fileSizeBytes,
        int targetConcurrency = 0
    )
    {
        var baseChunkSize = gpuAvailable && gpuChunkSizeMB > 0 ? gpuChunkSizeMB : cpuChunkSizeMB;
        return SelectParallelChunkSizeMB(
            baseChunkSize,
            fileSizeBytes,
            targetConcurrency > 0 ? targetConcurrency : Environment.ProcessorCount
        );
    }

    internal static int SelectParallelChunkSizeMB(
        int maximumChunkSizeMB,
        long fileSizeBytes,
        int targetConcurrency,
        int chunksPerWorker = 2,
        int minimumChunkSizeMB = 16
    )
    {
        var safeMaximum = Math.Max(minimumChunkSizeMB, Math.Min(128, maximumChunkSizeMB));
        if (fileSizeBytes <= 0)
        {
            return minimumChunkSizeMB;
        }

        var targetChunks = Math.Max(
            1,
            Math.Max(1, targetConcurrency) * Math.Max(1, chunksPerWorker)
        );
        var mebibytes = Math.Max(1L, (fileSizeBytes + (1024 * 1024 - 1L)) / (1024 * 1024));
        var desired = (int)Math.Min(
            int.MaxValue,
            (mebibytes + targetChunks - 1L) / targetChunks
        );

        return Math.Clamp(desired, minimumChunkSizeMB, safeMaximum);
    }
}
