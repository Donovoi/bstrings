namespace bstrings;

/// <summary>
/// Carries an extracted string and its absolute byte offset without mixing
/// presentation formatting into the extraction hot path.
/// </summary>
public readonly record struct ExtractedStringHit(string Data, long Offset);
