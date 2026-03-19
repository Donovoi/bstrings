using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace bstrings;

internal static class RuntimeUtilityCore
{
    internal static string GetSizeReadable(long value)
    {
        var sign = value < 0 ? "-" : string.Empty;
        double readable;
        string suffix;

        if (value >= 0x1000000000000000)
        {
            suffix = "EB";
            readable = value >> 50;
        }
        else if (value >= 0x4000000000000)
        {
            suffix = "PB";
            readable = value >> 40;
        }
        else if (value >= 0x10000000000)
        {
            suffix = "TB";
            readable = value >> 30;
        }
        else if (value >= 0x40000000)
        {
            suffix = "GB";
            readable = value >> 20;
        }
        else if (value >= 0x100000)
        {
            suffix = "MB";
            readable = value >> 10;
        }
        else if (value >= 0x400)
        {
            suffix = "KB";
            readable = value;
        }
        else
        {
            return value.ToString(sign + "0 B");
        }

        readable /= 1024;
        return sign + readable.ToString("0.### ") + suffix;
    }

    internal static int AdaptChunkSizeForFile(int baseChunkSizeMB, long fileSizeBytes)
    {
        long fileSizeMB = fileSizeBytes / (1024 * 1024);

        if (fileSizeMB < 100)
        {
            return Math.Max(16, (int)Math.Min(baseChunkSizeMB, fileSizeMB / 2));
        }

        if (fileSizeMB < 1024)
        {
            return Math.Max(32, baseChunkSizeMB);
        }

        if (fileSizeMB < 10240)
        {
            return Math.Max(64, Math.Min(baseChunkSizeMB, 128));
        }

        return Math.Max(64, Math.Min(baseChunkSizeMB, 128));
    }

    internal static async IAsyncEnumerable<List<T>> CreateBatchedAsyncEnumerable<T>(
        IAsyncEnumerable<T> source,
        int batchSize
    )
    {
        var batch = new List<T>(batchSize);

        await foreach (var item in source)
        {
            batch.Add(item);

            if (batch.Count >= batchSize)
            {
                yield return new List<T>(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}