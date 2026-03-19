using System;
using System.Collections.Generic;

namespace bstrings;

internal static class ChunkProcessingCore
{
    internal static List<string> ProcessChunk(
        ReadOnlySpan<byte> chunk,
        long fileOffset,
        bool isBoundaryChunk,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        bool includeOffset,
        string asciiRange,
        string unicodeRange
    )
    {
        var results = new List<string>();

        if (unicodeSearch)
        {
            foreach (
                var hit in SearchCore.GetUnicodeHits(
                    chunk,
                    minLength,
                    maxLength,
                    fileOffset,
                    includeOffset,
                    unicodeRange
                )
            )
            {
                results.Add(isBoundaryChunk ? "  " + hit : hit);
            }
        }

        if (asciiSearch)
        {
            foreach (
                var hit in SearchCore.GetAsciiHits(
                    chunk,
                    minLength,
                    maxLength,
                    fileOffset,
                    includeOffset,
                    asciiRange
                )
            )
            {
                results.Add(isBoundaryChunk ? "  " + hit : hit);
            }
        }

        return results;
    }
}