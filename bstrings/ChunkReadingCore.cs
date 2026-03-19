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
        Func<int, byte[]> rent = null
    )
    {
        rent ??= Program.ByteArrayPool.Rent;

        var chunks = new List<Program.DataChunk>();
        var bytesRemaining = totalBytes;
        var offset = startOffset;
        var chunkIndex = 0;

        if (isBoundaryMode)
        {
            while (bytesRemaining > 0 && offset + boundaryChunkSize <= totalBytes)
            {
                var chunk = rent(boundaryChunkSize);
                stream.Position = offset;
                var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, boundaryChunkSize));

                if (bytesRead == 0)
                {
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
                var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, currentChunkSize));

                if (bytesRead == 0)
                {
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