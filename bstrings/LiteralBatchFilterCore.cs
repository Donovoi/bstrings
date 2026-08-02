#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;

namespace bstrings;

/// <summary>
/// Applies a precomputed, ordinal case-insensitive multi-string search to one
/// extraction batch before the batch reaches the global deduplication set.
/// </summary>
internal sealed class LiteralBatchFilterCore
{
    private readonly SearchValues<string>? _searchValues;
    private readonly bool _includeOffset;

    internal LiteralBatchFilterCore(IEnumerable<string> literals, bool includeOffset)
    {
        ArgumentNullException.ThrowIfNull(literals);

        var usableLiterals = literals
            .Where(literal => !string.IsNullOrWhiteSpace(literal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (usableLiterals.Length > 0)
        {
            _searchValues = SearchValues.Create(
                usableLiterals,
                StringComparison.OrdinalIgnoreCase
            );
        }

        _includeOffset = includeOffset;
    }

    internal List<string> TransformBatch(List<string> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        if (_searchValues is null)
        {
            hits.Clear();
            return hits;
        }

        var writeIndex = 0;
        for (var readIndex = 0; readIndex < hits.Count; readIndex++)
        {
            var hit = hits[readIndex];
            if (string.IsNullOrEmpty(hit) || !GetDataSpan(hit).ContainsAny(_searchValues))
            {
                continue;
            }

            hits[writeIndex++] = hit;
        }

        if (writeIndex < hits.Count)
        {
            hits.RemoveRange(writeIndex, hits.Count - writeIndex);
        }

        return hits;
    }

    private ReadOnlySpan<char> GetDataSpan(string hit)
    {
        if (!_includeOffset)
        {
            return hit;
        }

        var tabIndex = hit.IndexOf('\t');
        return tabIndex > 0 && hit.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? hit.AsSpan(tabIndex + 1)
            : hit;
    }
}
