#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace bstrings;

/// <summary>
/// Applies regex targets to one bounded extraction batch and returns ready-to-write rows.
/// The caller can release the original extracted strings as soon as this method returns.
/// </summary>
internal sealed class StreamingRegexOutputCore
{
    private readonly IReadOnlyList<(string Name, Regex Regex)> _patterns;
    private readonly bool _regexOnly;
    private readonly bool _includeOffset;
    private readonly bool _isCsvOutput;
    private readonly string _sourceFile;
    private readonly ConcurrentDictionary<string, byte> _boundedRetryWarnings =
        new(StringComparer.OrdinalIgnoreCase);
    private long _outputRowCount;

    internal StreamingRegexOutputCore(
        IEnumerable<(string name, string pattern)> patterns,
        bool regexOnly,
        bool includeOffset,
        bool isCsvOutput,
        string sourceFile
    )
    {
        var regexMap = RegexOutputCore.BuildRegexMap(patterns);
        var compiledPatterns = new List<(string Name, Regex Regex)>(regexMap.Count);
        foreach (var (name, regex) in regexMap)
        {
            compiledPatterns.Add((name, regex));
        }

        _patterns = compiledPatterns;
        _regexOnly = regexOnly;
        _includeOffset = includeOffset;
        _isCsvOutput = isCsvOutput;
        _sourceFile = sourceFile ?? string.Empty;
    }

    internal long OutputRowCount => Interlocked.Read(ref _outputRowCount);

    internal List<string> TransformMainBatch(List<string> hits)
    {
        return TransformBatch(hits, isBoundaryBatch: false);
    }

    internal List<string> TransformBoundaryBatch(List<string> hits)
    {
        return TransformBatch(hits, isBoundaryBatch: true);
    }

    private List<string> TransformBatch(List<string> hits, bool isBoundaryBatch)
    {
        var output = new List<string>();

        try
        {
            foreach (var rawHit in hits)
            {
                if (string.IsNullOrEmpty(rawHit))
                {
                    continue;
                }

                // Boundary extraction prefixes hits with two spaces for console display.
                // Remove that presentation marker before parsing the real offset and data.
                var normalizedHit =
                    isBoundaryBatch && rawHit.StartsWith("  ", StringComparison.Ordinal)
                        ? rawHit[2..]
                        : rawHit;
                var parsedHit = RegexOutputCore.ParseHit(normalizedHit, _includeOffset);

                foreach (var (patternName, regex) in _patterns)
                {
                    try
                    {
                        if (_regexOnly)
                        {
                            foreach (
                                var record in RegexOutputCore.CreateRecords(
                                    parsedHit,
                                    patternName,
                                    regex,
                                    regexOutput: true,
                                    _sourceFile,
                                    "Regex"
                                )
                            )
                            {
                                output.Add(FormatRecord(record, parsedHit));
                            }
                        }
                        else if (
                            RegexOutputCore.IsMatch(
                                patternName,
                                regex,
                                parsedHit.Data
                            )
                        )
                        {
                            var record = new RegexOutputRecord(
                                patternName,
                                parsedHit.Data,
                                _sourceFile,
                                parsedHit.Offset,
                                "Regex"
                            );
                            output.Add(FormatRecord(record, parsedHit));
                        }
                    }
                    catch (RegexMatchTimeoutException ex)
                    {
                        if (
                            TryGetBoundedRetryOverlap(
                                patternName,
                                regex,
                                out var retryOverlap
                            )
                        )
                        {
                            if (_boundedRetryWarnings.TryAdd(patternName, 0))
                            {
                                Console.Error.WriteLine(
                                    $"Built-in {patternName} matching exceeded the whole-string timeout; "
                                        + "retrying in bounded overlapping windows."
                                );
                            }

                            if (_regexOnly)
                            {
                                foreach (
                                    var record in CreateBoundedRecords(
                                        parsedHit,
                                        patternName,
                                        regex,
                                        _sourceFile,
                                        retryOverlap
                                    )
                                )
                                {
                                    output.Add(FormatRecord(record, parsedHit));
                                }
                            }
                            else if (
                                HasBoundedMatch(
                                    parsedHit.Data,
                                    patternName,
                                    regex,
                                    retryOverlap
                                )
                            )
                            {
                                var record = new RegexOutputRecord(
                                    patternName,
                                    parsedHit.Data,
                                    _sourceFile,
                                    parsedHit.Offset,
                                    "Regex"
                                );
                                output.Add(FormatRecord(record, parsedHit));
                            }

                            continue;
                        }

                        throw new TimeoutException(
                            $"Regex '{patternName}' timed out; the result set is incomplete.",
                            ex
                        );
                    }
                }
            }
        }
        finally
        {
            // Avoid retaining the much larger unfiltered extraction batch.
            hits.Clear();
        }

        Interlocked.Add(ref _outputRowCount, output.Count);
        return output;
    }

    private string FormatRecord(RegexOutputRecord record, ParsedHit parsedHit)
    {
        if (_isCsvOutput)
        {
            return RegexOutputCore.BuildCsvLine(record);
        }

        return _regexOnly
            ? RegexOutputCore.BuildRegexOnlyText(record)
            : RegexOutputCore.BuildFullHitText(parsedHit);
    }

    internal static IEnumerable<RegexOutputRecord> CreateBoundedIpv6Records(
        ParsedHit parsedHit,
        string patternName,
        Regex regex,
        string sourceFile,
        int primaryWindowLength = 64 * 1024
    )
    {
        return CreateBoundedRecords(
            parsedHit,
            patternName,
            regex,
            sourceFile,
            overlap: 64,
            primaryWindowLength: primaryWindowLength
        );
    }

    internal static IEnumerable<RegexOutputRecord> CreateBoundedRecords(
        ParsedHit parsedHit,
        string patternName,
        Regex regex,
        string sourceFile,
        int overlap,
        int primaryWindowLength = 64 * 1024
    )
    {
        if (primaryWindowLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(primaryWindowLength));
        }
        if (overlap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap));
        }

        var outputGroup =
            BuiltInPatternCatalog.TryGetDefinition(
                patternName,
                regex.ToString(),
                out var definition
            )
                ? definition.OutputGroup
                : null;

        for (var primaryStart = 0; primaryStart < parsedHit.Data.Length; primaryStart += primaryWindowLength)
        {
            var primaryLength = Math.Min(
                primaryWindowLength,
                parsedHit.Data.Length - primaryStart
            );
            var primaryEnd = primaryStart + primaryLength;
            var scanStart = Math.Max(0, primaryStart - overlap);
            var scanEnd = Math.Min(parsedHit.Data.Length, primaryEnd + overlap);
            var window = parsedHit.Data.Substring(scanStart, scanEnd - scanStart);

            foreach (Match match in regex.Matches(window))
            {
                // Both overlaps preserve lookaround context. The absolute starting
                // position belongs to exactly one primary window, which prevents
                // duplicates and partial-token false positives at a window edge.
                var absoluteMatchStart = scanStart + match.Index;
                if (
                    absoluteMatchStart < primaryStart
                    || absoluteMatchStart >= primaryEnd
                )
                {
                    continue;
                }

                var dataFound =
                    outputGroup is not null && match.Groups[outputGroup].Success
                        ? match.Groups[outputGroup].Value
                        : match.Value;
                yield return new RegexOutputRecord(
                    patternName,
                    dataFound,
                    sourceFile,
                    parsedHit.Offset,
                    "Regex"
                );
            }
        }
    }

    private static bool HasBoundedMatch(
        string data,
        string patternName,
        Regex regex,
        int overlap
    )
    {
        return CreateBoundedRecords(
                new ParsedHit(data, data, string.Empty),
                patternName,
                regex,
                string.Empty,
                overlap
            )
            .Any();
    }

    private static bool TryGetBoundedRetryOverlap(
        string patternName,
        Regex regex,
        out int overlap
    )
    {
        if (
            BuiltInPatternCatalog.TryGetDefinition(
                patternName,
                regex.ToString(),
                out var definition
            )
            && definition.BoundedRetryOverlap is > 0
        )
        {
            overlap = definition.BoundedRetryOverlap.Value;
            return true;
        }

        overlap = 0;
        return false;
    }
}
