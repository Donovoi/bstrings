using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace bstrings;

internal static class ChunkReadingCore
{
    internal static async Task<List<Program.DataChunk>> ReadChunksAsync(
        Stream stream,
        long totalBytes,
        int chunkSizeBytes,
        long startOffset = 0,
        bool isBoundaryMode = false,
        int boundaryChunkSize = 0,
        int maxReadAheadChunks = 8,
        Func<int, byte[]> rent = null,
        Action<byte[]> returnBuffer = null
    )
    {
        rent ??= Program.ByteArrayPool.Rent;

        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (chunkSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes));
        }

        if (isBoundaryMode && boundaryChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boundaryChunkSize));
        }

        maxReadAheadChunks = Math.Max(1, maxReadAheadChunks);
        var chunks = new List<Program.DataChunk>();
        var bytesRemaining = Math.Max(0, totalBytes - startOffset);
        var offset = startOffset;
        var chunkIndex = 0;

        if (isBoundaryMode)
        {
            while (
                bytesRemaining > 0
                && offset + boundaryChunkSize / 2L < totalBytes
            )
            {
                var currentChunkSize = (int)Math.Min(boundaryChunkSize, totalBytes - offset);
                var chunk = rent(currentChunkSize);
                int bytesRead;
                try
                {
                    stream.Position = offset;
                    bytesRead = await stream.ReadAsync(chunk.AsMemory(0, currentChunkSize));
                }
                catch
                {
                    returnBuffer?.Invoke(chunk);
                    throw;
                }

                if (bytesRead == 0)
                {
                    returnBuffer?.Invoke(chunk);
                    break;
                }

                chunks.Add(
                    new Program.DataChunk
                    {
                        Data = chunk,
                        ValidBytes = bytesRead,
                        FileOffset = offset,
                        ChunkIndex = chunkIndex,
                        IsBoundaryChunk = true,
                        BoundaryCrossingOffset = boundaryChunkSize / 2,
                        SuppressLeadingFragment = offset > 0,
                        SuppressTrailingFragment = offset + bytesRead < totalBytes,
                    }
                );

                offset += chunkSizeBytes;
                bytesRemaining -= chunkSizeBytes;
                chunkIndex++;

                if (chunks.Count >= maxReadAheadChunks)
                {
                    break;
                }
            }
        }
        else
        {
            stream.Position = startOffset;
            while (bytesRemaining > 0)
            {
                var currentChunkSize = (int)Math.Min(chunkSizeBytes, bytesRemaining);
                var chunk = rent(currentChunkSize);
                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(chunk.AsMemory(0, currentChunkSize));
                }
                catch
                {
                    returnBuffer?.Invoke(chunk);
                    throw;
                }

                if (bytesRead == 0)
                {
                    returnBuffer?.Invoke(chunk);
                    break;
                }

                chunks.Add(
                    new Program.DataChunk
                    {
                        Data = chunk,
                        ValidBytes = bytesRead,
                        FileOffset = offset,
                        ChunkIndex = chunkIndex,
                        IsBoundaryChunk = false,
                        SuppressLeadingFragment = offset > 0,
                        SuppressTrailingFragment = offset + bytesRead < totalBytes,
                    }
                );

                offset += bytesRead;
                bytesRemaining -= bytesRead;
                chunkIndex++;

                if (chunks.Count >= maxReadAheadChunks)
                {
                    break;
                }
            }
        }

        return chunks;
    }
}
