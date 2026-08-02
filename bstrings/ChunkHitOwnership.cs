namespace bstrings;

internal readonly record struct ChunkHitOwnership(
    bool SuppressLeadingFragment,
    bool SuppressTrailingFragment,
    int RequiredCrossingOffset = -1
)
{
    internal static ChunkHitOwnership None => new(false, false);

    internal bool IsUnrestricted =>
        !SuppressLeadingFragment
        && !SuppressTrailingFragment
        && RequiredCrossingOffset <= 0;

    internal bool Accepts(int start, int byteLength, int dataLength)
    {
        if (start < 0 || byteLength <= 0 || start > dataLength - byteLength)
        {
            return false;
        }

        var end = start + byteLength;
        if (SuppressLeadingFragment && start == 0)
        {
            return false;
        }

        if (SuppressTrailingFragment && end == dataLength)
        {
            return false;
        }

        return RequiredCrossingOffset <= 0
            || (start < RequiredCrossingOffset && end > RequiredCrossingOffset);
    }
}
