using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace bstrings;

internal readonly record struct ParsedHit(string RawHit, string Data, string Offset);

internal readonly record struct RegexOutputRecord(
    string PatternName,
    string DataFound,
    string SourceFile,
    string Offset,
    string PatternType
);

internal static class RegexOutputCore
{
    private readonly record struct CandidateRange(int Start, int Length);

    internal const string CsvHeader = "Name of search pattern,Data found,Source file,Offset,Pattern type";
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ShortInputMatchTimeout = TimeSpan.FromMilliseconds(10);
    private static readonly ConcurrentDictionary<RegexCacheKey, Regex> RegexCache = new();
    private static long _shortInputFallbackCount;

    internal static long ShortInputFallbackCount => Interlocked.Read(ref _shortInputFallbackCount);

    internal static ParsedHit ParseHit(string hit, bool includeOffset)
    {
        if (!includeOffset || string.IsNullOrEmpty(hit))
        {
            return new ParsedHit(hit, hit, string.Empty);
        }

        var tabIndex = hit.IndexOf('\t');
        if (tabIndex > 0 && hit.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedHit(hit, hit[(tabIndex + 1)..], hit[..tabIndex]);
        }

        return new ParsedHit(hit, hit, string.Empty);
    }

    internal static IEnumerable<RegexOutputRecord> CreateRecords(
        ParsedHit parsedHit,
        string patternName,
        Regex regex,
        bool regexOutput,
        string sourceFile,
        string patternType
    )
    {
        if (regexOutput)
        {
            var isBuiltIn = BuiltInPatternCatalog.TryGetDefinition(
                patternName,
                regex.ToString(),
                out var definition
            );
            if (
                isBuiltIn
                && string.Equals(definition.Name, "b64", StringComparison.OrdinalIgnoreCase)
            )
            {
                foreach (var candidate in EnumerateBase64Candidates(parsedHit.Data))
                {
                    yield return new RegexOutputRecord(
                        patternName,
                        parsedHit.Data.Substring(candidate.Start, candidate.Length),
                        sourceFile,
                        parsedHit.Offset,
                        patternType
                    );
                }

                yield break;
            }
            if (
                isBuiltIn
                && string.Equals(definition.Name, "xml", StringComparison.OrdinalIgnoreCase)
            )
            {
                if (IsSimpleXmlElementMatch(parsedHit.Data))
                {
                    yield return new RegexOutputRecord(
                        patternName,
                        parsedHit.Data,
                        sourceFile,
                        parsedHit.Offset,
                        patternType
                    );
                }

                yield break;
            }

            var outputGroup = isBuiltIn ? definition.OutputGroup : null;
            MatchCollection matches;
            if (
                isBuiltIn
                && definition.GeneratedShortInputLimit is int generatedShortInputLimit
                && parsedHit.Data.Length <= generatedShortInputLimit
                && TryGetGeneratedShortInputRegex(
                    definition,
                    parsedHit.Data,
                    out var shortInputRegex
                )
            )
            {
                foreach (
                    var dataFound in GetUrlValuesWithFallback(
                        parsedHit.Data,
                        shortInputRegex,
                        regex
                    )
                )
                {
                    yield return new RegexOutputRecord(
                        patternName,
                        dataFound,
                        sourceFile,
                        parsedHit.Offset,
                        patternType
                    );
                }

                yield break;
            }
            matches = regex.Matches(parsedHit.Data);

            foreach (Match match in matches)
            {
                var dataFound =
                    outputGroup is not null && match.Groups[outputGroup].Success
                        ? match.Groups[outputGroup].Value
                        : match.Value;
                yield return new RegexOutputRecord(
                    patternName,
                    dataFound,
                    sourceFile,
                    parsedHit.Offset,
                    patternType
                );
            }

            yield break;
        }

        yield return new RegexOutputRecord(
            patternName,
            parsedHit.Data,
            sourceFile,
            parsedHit.Offset,
            patternType
        );
    }

    /// <summary>
    /// Formats regex-only streaming results without allocating an iterator, a
    /// Match object for every ordinary match, or an intermediate URL value list.
    /// Match ranges are buffered before output so a timeout can be retried without
    /// duplicating records that were discovered before the timeout.
    /// </summary>
    internal static void AppendStreamingRecords(
        ParsedHit parsedHit,
        string patternName,
        Regex regex,
        BuiltInPatternDefinition definition,
        bool isCsvOutput,
        string sourceFile,
        List<string> output
    )
    {
        Span<CandidateRange> inlineRanges = stackalloc CandidateRange[4];
        List<CandidateRange> overflowRanges = null;
        var rangeCount = 0;

        if (
            definition is not null
            && string.Equals(definition.Name, "b64", StringComparison.OrdinalIgnoreCase)
        )
        {
            foreach (var candidate in EnumerateBase64Candidates(parsedHit.Data))
            {
                AddCandidateRange(
                    inlineRanges,
                    ref overflowRanges,
                    ref rangeCount,
                    candidate
                );
            }
        }
        else if (
            definition is not null
            && string.Equals(definition.Name, "xml", StringComparison.OrdinalIgnoreCase)
        )
        {
            if (IsSimpleXmlElementMatch(parsedHit.Data))
            {
                AddCandidateRange(
                    inlineRanges,
                    ref overflowRanges,
                    ref rangeCount,
                    new CandidateRange(0, parsedHit.Data.Length)
                );
            }
        }
        else if (
            definition is not null
            && definition.GeneratedShortInputLimit is int generatedShortInputLimit
            && parsedHit.Data.Length <= generatedShortInputLimit
            && TryGetGeneratedShortInputRegex(
                definition,
                parsedHit.Data,
                out var shortInputRegex
            )
        )
        {
            try
            {
                foreach (var match in shortInputRegex.EnumerateMatches(parsedHit.Data))
                {
                    // The generated URL pattern has either a zero-width start anchor
                    // or one leading delimiter outside the URI capture.
                    var groupOffset = IsAsciiLetter(parsedHit.Data[match.Index]) ? 0 : 1;
                    AddCandidateRange(
                        inlineRanges,
                        ref overflowRanges,
                        ref rangeCount,
                        new CandidateRange(
                            match.Index + groupOffset,
                            match.Length - groupOffset
                        )
                    );
                }
            }
            catch (RegexMatchTimeoutException)
            {
                Interlocked.Increment(ref _shortInputFallbackCount);
                overflowRanges?.Clear();
                rangeCount = 0;
                CollectMatchRanges(
                    parsedHit.Data,
                    regex,
                    definition.OutputGroup,
                    inlineRanges,
                    ref overflowRanges,
                    ref rangeCount
                );
            }
        }
        else
        {
            CollectMatchRanges(
                parsedHit.Data,
                regex,
                definition?.OutputGroup,
                inlineRanges,
                ref overflowRanges,
                ref rangeCount
            );
        }

        for (var index = 0; index < rangeCount; index++)
        {
            var range =
                overflowRanges is null ? inlineRanges[index] : overflowRanges[index];
            var record = new RegexOutputRecord(
                patternName,
                parsedHit.Data.Substring(range.Start, range.Length),
                sourceFile,
                parsedHit.Offset,
                "Regex"
            );
            output.Add(
                isCsvOutput ? BuildCsvLine(record) : BuildRegexOnlyText(record)
            );
        }
    }

    private static void CollectMatchRanges(
        string data,
        Regex regex,
        string outputGroup,
        Span<CandidateRange> inlineRanges,
        ref List<CandidateRange> overflowRanges,
        ref int rangeCount
    )
    {
        if (outputGroup is null)
        {
            foreach (var match in regex.EnumerateMatches(data))
            {
                AddCandidateRange(
                    inlineRanges,
                    ref overflowRanges,
                    ref rangeCount,
                    new CandidateRange(match.Index, match.Length)
                );
            }

            return;
        }

        foreach (Match match in regex.Matches(data))
        {
            var group = match.Groups[outputGroup];
            AddCandidateRange(
                inlineRanges,
                ref overflowRanges,
                ref rangeCount,
                group.Success
                    ? new CandidateRange(group.Index, group.Length)
                    : new CandidateRange(match.Index, match.Length)
            );
        }
    }

    private static void AddCandidateRange(
        Span<CandidateRange> inlineRanges,
        ref List<CandidateRange> overflowRanges,
        ref int rangeCount,
        CandidateRange candidate
    )
    {
        if (overflowRanges is null && rangeCount < inlineRanges.Length)
        {
            inlineRanges[rangeCount++] = candidate;
            return;
        }

        if (overflowRanges is null)
        {
            overflowRanges = new List<CandidateRange>(inlineRanges.Length * 2);
            for (var index = 0; index < rangeCount; index++)
            {
                overflowRanges.Add(inlineRanges[index]);
            }
        }

        overflowRanges.Add(candidate);
        rangeCount++;
    }

    internal static string BuildCsvLine(RegexOutputRecord record)
    {
        return string.Join(
            ",",
            CsvEscape(record.PatternName),
            CsvEscape(record.DataFound),
            CsvEscape(record.SourceFile),
            CsvEscape(record.Offset),
            CsvEscape(record.PatternType)
        );
    }

    internal static string BuildRegexOnlyText(RegexOutputRecord record)
    {
        return string.IsNullOrEmpty(record.Offset)
            ? record.DataFound
            : $"{record.DataFound}\t~{record.Offset}";
    }

    internal static string BuildFullHitText(ParsedHit parsedHit)
    {
        return string.IsNullOrEmpty(parsedHit.Offset) ? parsedHit.Data : parsedHit.RawHit;
    }

    internal static Dictionary<string, Regex> BuildRegexMap(
        IEnumerable<(string name, string pattern)> regexPatternsWithNames
    )
    {
        // Built-in aliases are deduplicated case-insensitively by SearchCore.
        // Custom regex text is case-sensitive, so its map key must be ordinal.
        var regexes = new Dictionary<string, Regex>(StringComparer.Ordinal);
        foreach (var (name, pattern) in regexPatternsWithNames)
        {
            if (string.IsNullOrWhiteSpace(name) || regexes.ContainsKey(name))
            {
                continue;
            }

            regexes.Add(name, GetOrCreateRegex(name, pattern));
        }

        return regexes;
    }

    internal static Regex GetOrCreateRegex(
        string name,
        string pattern,
        bool preferCompiledBuiltIn = false
    )
    {
        var options = RegexOptions.CultureInvariant;
        if (BuiltInPatternCatalog.TryGetDefinition(name, pattern, out var definition))
        {
            options |= definition.Options;
            options |= definition.UseNonBacktracking
                ? RegexOptions.NonBacktracking
                : preferCompiledBuiltIn
                    ? RegexOptions.Compiled
                    : RegexOptions.None;
        }
        else
        {
            // Preserve normal .NET regex semantics for user-supplied expressions.
            // Callers can request case-insensitive or free-spacing behavior with
            // explicit inline options such as (?i) and (?x).
            options |= RegexOptions.Compiled;
        }

        var cacheKey = new RegexCacheKey(pattern, options);
        return RegexCache.GetOrAdd(
            cacheKey,
            static key => new Regex(key.Pattern, key.Options, MatchTimeout)
        );
    }

    internal static bool IsMatch(string patternName, Regex regex, string data)
    {
        if (
            BuiltInPatternCatalog.TryGetDefinition(
                patternName,
                regex.ToString(),
                out var definition
            )
        )
        {
            if (string.Equals(definition.Name, "b64", StringComparison.OrdinalIgnoreCase))
            {
                return EnumerateBase64Candidates(data).Any();
            }
            if (string.Equals(definition.Name, "xml", StringComparison.OrdinalIgnoreCase))
            {
                return IsSimpleXmlElementMatch(data);
            }
            if (
                definition.GeneratedShortInputLimit is int generatedShortInputLimit
                && data.Length <= generatedShortInputLimit
                && TryGetGeneratedShortInputRegex(
                    definition,
                    data,
                    out var shortInputRegex
                )
            )
            {
                try
                {
                    return shortInputRegex.IsMatch(data);
                }
                catch (RegexMatchTimeoutException)
                {
                    Interlocked.Increment(ref _shortInputFallbackCount);
                    return regex.IsMatch(data);
                }
            }
        }

        return regex.IsMatch(data);
    }

    internal static bool IsMatchWithGeneratedShortInput(
        string patternName,
        string pattern,
        Regex fallbackRegex,
        string data
    )
    {
        if (
            data.Length <= BuiltInPatternCatalog.Url3986GeneratedInputLimit
            && string.Equals(patternName, "url3986", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                pattern,
                BuiltInPatternCatalog.Url3986Pattern,
                StringComparison.Ordinal
            )
        )
        {
            try
            {
                return BuiltInGeneratedRegexes.Url3986ShortInput().IsMatch(data);
            }
            catch (RegexMatchTimeoutException)
            {
                Interlocked.Increment(ref _shortInputFallbackCount);
            }
        }

        return fallbackRegex.IsMatch(data);
    }

    internal static bool TryGetGeneratedShortInputRegex(
        BuiltInPatternDefinition definition,
        string data,
        out Regex regex
    )
    {
        if (
            definition.GeneratedShortInputLimit is not > 0
            || data.Length > definition.GeneratedShortInputLimit.Value
        )
        {
            regex = null!;
            return false;
        }

        if (
            string.Equals(definition.Name, "url3986", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                definition.Pattern,
                BuiltInPatternCatalog.Url3986Pattern,
                StringComparison.Ordinal
            )
        )
        {
            regex = BuiltInGeneratedRegexes.Url3986ShortInput();
            return true;
        }

        regex = null!;
        return false;
    }

    internal static IReadOnlyList<string> GetUrlValuesWithFallback(
        string data,
        Regex preferredRegex,
        Regex fallbackRegex
    )
    {
        var values = new List<string>(capacity: 1);
        try
        {
            foreach (var match in preferredRegex.EnumerateMatches(data))
            {
                // The only text outside the named URI group is either the zero-width
                // start anchor or one leading delimiter. RFC 3986 schemes must start
                // with an ASCII letter, so the group range is recoverable without a
                // capture allocation.
                var groupOffset = IsAsciiLetter(data[match.Index]) ? 0 : 1;
                values.Add(
                    data.Substring(match.Index + groupOffset, match.Length - groupOffset)
                );
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Values are buffered before the caller emits rows. A timeout can
            // therefore discard them and replay the whole input without duplicates.
            Interlocked.Increment(ref _shortInputFallbackCount);
            values.Clear();
            var matches = fallbackRegex.Matches(data);
            _ = matches.Count;
            foreach (Match match in matches)
            {
                values.Add(match.Groups["uri"].Value);
            }
        }

        return values;
    }

    private static IEnumerable<CandidateRange> EnumerateBase64Candidates(string data)
    {
        var index = 0;
        while (index < data.Length)
        {
            while (index < data.Length && !IsBase64Character(data[index]))
            {
                index++;
            }
            if (index >= data.Length)
            {
                yield break;
            }

            var start = index;
            while (index < data.Length && IsBase64Character(data[index]))
            {
                index++;
            }
            var baseLength = index - start;
            var paddingLength = 0;
            while (
                paddingLength < 2
                && index + paddingLength < data.Length
                && data[index + paddingLength] == '='
            )
            {
                paddingLength++;
            }
            var afterCandidate = index + paddingLength;
            var hasForbiddenTrailingCharacter =
                afterCandidate < data.Length
                && (
                    data[afterCandidate] == '='
                    || IsBase64Character(data[afterCandidate])
                );
            var validLength = 0;

            if (!hasForbiddenTrailingCharacter)
            {
                if (paddingLength == 0 && baseLength >= 8 && baseLength % 4 == 0)
                {
                    validLength = baseLength;
                }
                else if (
                    paddingLength == 1
                    && baseLength >= 7
                    && baseLength % 4 == 3
                )
                {
                    validLength = baseLength + 1;
                }
                else if (
                    paddingLength == 2
                    && baseLength >= 6
                    && baseLength % 4 == 2
                )
                {
                    validLength = baseLength + 2;
                }
            }

            if (validLength > 0)
            {
                yield return new CandidateRange(start, validLength);
            }

            index = Math.Max(afterCandidate, start + 1);
        }
    }

    private static bool IsBase64Character(char value)
    {
        return (value >= 'A' && value <= 'Z')
            || (value >= 'a' && value <= 'z')
            || (value >= '0' && value <= '9')
            || value is '+' or '/';
    }

    internal static bool IsSimpleXmlElementMatch(string data)
    {
        if (data.Length < 7 || data[0] != '<' || !IsAsciiLetter(data[1]))
        {
            return false;
        }

        var tagEnd = 2;
        while (
            tagEnd < data.Length
            && (IsAsciiLetter(data[tagEnd]) || char.IsAsciiDigit(data[tagEnd]))
        )
        {
            tagEnd++;
        }
        if (tagEnd >= data.Length || IsRegexWordCharacter(data[tagEnd]))
        {
            return false;
        }

        var openingEnd = data.IndexOf('>', tagEnd);
        if (openingEnd < 0)
        {
            return false;
        }
        var tag = data[1..tagEnd];
        var closing = $"</{tag}>";
        if (!data.EndsWith(closing, StringComparison.Ordinal))
        {
            return false;
        }

        var contentStart = openingEnd + 1;
        var closingStart = data.Length - closing.Length;
        if (closingStart < contentStart)
        {
            return false;
        }
        return data.IndexOf('\n', contentStart, closingStart - contentStart) < 0;
    }

    private static bool IsAsciiLetter(char value)
    {
        return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
    }

    private static bool IsRegexWordCharacter(char value)
    {
        var category = char.GetUnicodeCategory(value);
        return category
                is UnicodeCategory.UppercaseLetter
                    or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter
                    or UnicodeCategory.OtherLetter
                    or UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.DecimalDigitNumber
                    or UnicodeCategory.ConnectorPunctuation
            || value is '\u200C' or '\u200D';
    }

    internal static bool TryGetRapidsSupersetPattern(
        string name,
        string pattern,
        out string rapidsPattern
    )
    {
        if (
            BuiltInPatternCatalog.TryGetDefinition(name, pattern, out var definition)
            && !string.IsNullOrWhiteSpace(definition.RapidsSupersetPattern)
            && RapidsRegexPolicy.IsSupportedSuperset(
                definition.RapidsSupersetPattern,
                out _
            )
        )
        {
            rapidsPattern = definition.RapidsSupersetPattern;
            return true;
        }

        rapidsPattern = string.Empty;
        return false;
    }

    private static string CsvEscape(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    }

    private readonly record struct RegexCacheKey(string Pattern, RegexOptions Options);
}
