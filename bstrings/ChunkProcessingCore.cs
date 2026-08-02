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
        string unicodeRange,
        int codePage = 1252,
        bool suppressLeadingFragment = false,
        bool suppressTrailingFragment = false,
        int boundaryCrossingOffset = 0
    )
    {
        var results = new List<string>();
        var ownership = new ChunkHitOwnership(
            suppressLeadingFragment,
            suppressTrailingFragment,
            isBoundaryChunk
                ? (boundaryCrossingOffset > 0 ? boundaryCrossingOffset : chunk.Length / 2)
                : -1
        );

        if (unicodeSearch)
        {
            foreach (
                var hit in SearchCore.GetUnicodeHits(
                    chunk,
                    minLength,
                    maxLength,
                    fileOffset,
                    includeOffset,
                    unicodeRange,
                    ownership
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
                    asciiRange,
                    codePage,
                    ownership
                )
            )
            {
                results.Add(isBoundaryChunk ? "  " + hit : hit);
            }
        }

        return results;
    }
}
