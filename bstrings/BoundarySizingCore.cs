using System;

namespace bstrings;

internal static class BoundarySizingCore
{
    internal const int DefaultContextBytes = 128 * 1024;

    internal static int CalculateWindowSize(int chunkSizeBytes, int minLength, int maxLength)
    {
        if (chunkSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes));
        }

        var minimumContext = Math.Max(1L, minLength) * 20L;
        var requestedContext =
            maxLength > 0
                ? Math.Max(minimumContext, checked((long)maxLength * 2L))
                : Math.Max(minimumContext, DefaultContextBytes);
        // A boundary window is backed by a single byte array, so its total size
        // must remain a valid array length even at the 1 GiB chunk-size limit.
        var context = Math.Min(
            Math.Min(chunkSizeBytes, requestedContext),
            Array.MaxLength / 2L
        );
        return checked((int)(context * 2L));
    }
}
