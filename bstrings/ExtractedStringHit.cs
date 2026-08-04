namespace bstrings;

/// <summary>
/// Carries an extracted string and its absolute byte range without mixing
/// presentation formatting into the extraction hot path.
/// </summary>
public readonly record struct ExtractedStringHit(
    string Data,
    long Offset,
    int ByteLength = 0,
    string Encoding = "unknown"
);
