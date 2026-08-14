#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal readonly record struct NativeEnrichmentCompletionStats(
    long OutputRecords,
    IReadOnlyDictionary<string, long> SourceRecords
);

internal static class NativeEnrichmentCompletionCore
{
    private const int MaximumManifestLineCharacters = 256 * 1024;
    private static readonly string[] ManifestProperties =
        ["schemaVersion", "path", "length", "sha256"];
    private static readonly string[] RecordProperties =
        [
            "schemaVersion",
            "recordType",
            "recordId",
            "text",
            "sourceFile",
            "location",
            "origin",
            "attributes",
        ];
    private static readonly string[] LocationProperties = ["kind", "value"];
    private static readonly string[] OriginProperties = ["extractor", "version", "kind"];
    private static readonly string[] AttributeProperties = ["encoding", "byteLength"];
    private static readonly string ExpectedExtractorVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    private static readonly Encoding CodePage1252 = CreateCodePage1252();
    private static readonly Encoding Utf16Le = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: false
    );

    internal static async Task<NativeEnrichmentCompletionStats> ValidateAsync(
        string outputPath,
        string inputManifestPath,
        string workingDirectory,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var sources = await ReadManifestAsync(inputManifestPath, cancellationToken);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long outputRecords = 0;

        using var identifiers = new DiskBackedProvenanceValidator(workingDirectory);
        await using var sourceReader = new SourceRecordReader();
        await foreach (
            var line in StrictJsonlCompletionReader.ReadAsync(
                outputPath,
                "Native enrichment output",
                EnrichmentRegexPipelineCore.MaxJsonLineCharacters,
                cancellationToken
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = ParseObject(line.Json, "Native enrichment output", line.LineNumber);
            var root = document.RootElement;
            RequireExactProperties(root, RecordProperties, "Native enrichment record", line.LineNumber);

            if (
                !RequiredInt32(root, "schemaVersion", out var schemaVersion)
                || schemaVersion != EnrichmentRegexPipelineCore.CurrentSchemaVersion
                || !RequiredString(root, "recordType", out var recordType)
                || !string.Equals(recordType, "string", StringComparison.Ordinal)
                || !RequiredString(root, "recordId", out var recordId)
                || !RequiredString(root, "text", out var text, allowWhitespace: true)
                || text.Length > EnrichmentRegexPipelineCore.MaxNativeTextCharacters
                || !RequiredString(root, "sourceFile", out var sourceFile)
            )
            {
                throw Invalid(line.LineNumber, "has invalid schema or required string fields");
            }
            if (!sources.TryGetValue(sourceFile, out var sourceLength))
            {
                throw Invalid(line.LineNumber, "is not bound to the exact input manifest");
            }

            var location = RequiredObject(root, "location", line.LineNumber);
            RequireExactProperties(location, LocationProperties, "Native location", line.LineNumber);
            if (
                !RequiredString(location, "kind", out var locationKind)
                || !string.Equals(locationKind, "file_offset", StringComparison.Ordinal)
                || !RequiredString(location, "value", out var locationValue)
                || !TryParseCanonicalOffset(locationValue, out var offset)
            )
            {
                throw Invalid(line.LineNumber, "has invalid native location provenance");
            }

            var origin = RequiredObject(root, "origin", line.LineNumber);
            RequireExactProperties(origin, OriginProperties, "Native origin", line.LineNumber);
            if (
                !RequiredString(origin, "extractor", out var extractor)
                || !string.Equals(extractor, "bstrings", StringComparison.Ordinal)
                || !RequiredString(origin, "version", out var extractorVersion)
                || !string.Equals(
                    extractorVersion,
                    ExpectedExtractorVersion,
                    StringComparison.Ordinal
                )
                || !RequiredString(origin, "kind", out var originKind)
                || !string.Equals(originKind, "static", StringComparison.Ordinal)
            )
            {
                throw Invalid(line.LineNumber, "has invalid native origin provenance");
            }

            var attributes = RequiredObject(root, "attributes", line.LineNumber);
            RequireExactProperties(attributes, AttributeProperties, "Native attributes", line.LineNumber);
            if (
                !RequiredString(attributes, "encoding", out var encoding)
                || encoding is not ("code-page-1252" or "utf-16le")
                || !RequiredInt64(attributes, "byteLength", out var byteLength)
                || byteLength <= 0
                || byteLength > EnrichmentRegexPipelineCore.MaxNativeTextCharacters * 2L
                || offset > sourceLength
                || byteLength > sourceLength - offset
            )
            {
                throw Invalid(line.LineNumber, "has invalid native byte-range provenance");
            }

            if (
                !await sourceReader.MatchesAsync(
                    sourceFile,
                    offset,
                    checked((int)byteLength),
                    encoding,
                    text,
                    cancellationToken
                )
            )
            {
                throw Invalid(
                    line.LineNumber,
                    "does not decode exactly from its claimed source byte range"
                );
            }

            var expectedRecordId = NativeEnrichmentRecordTransform.CreateRecordId(
                sourceFile,
                locationValue,
                text
            );
            if (!string.Equals(recordId, expectedRecordId, StringComparison.Ordinal))
            {
                throw Invalid(line.LineNumber, "failed deterministic record-ID validation");
            }

            identifiers.AddOriginal(recordId);
            counts[sourceFile] = checked(counts.GetValueOrDefault(sourceFile) + 1);
            outputRecords = checked(outputRecords + 1);
        }

        identifiers.Validate(cancellationToken);
        return new NativeEnrichmentCompletionStats(outputRecords, counts);
    }

    private static async Task<Dictionary<string, long>> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var sources = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (
            var line in StrictJsonlCompletionReader.ReadAsync(
                path,
                "Input evidence manifest",
                MaximumManifestLineCharacters,
                cancellationToken
            )
        )
        {
            using var document = ParseObject(line.Json, "Input evidence manifest", line.LineNumber);
            var root = document.RootElement;
            RequireExactProperties(root, ManifestProperties, "Input manifest record", line.LineNumber);
            if (
                !RequiredInt32(root, "schemaVersion", out var schemaVersion)
                || schemaVersion != 1
                || !RequiredString(root, "path", out var sourcePath)
                || !RequiredInt64(root, "length", out var length)
                || length < 0
                || !RequiredString(root, "sha256", out var sha256)
                || !IsLowerHexSha256(sha256)
            )
            {
                throw new InvalidDataException(
                    $"Input manifest line {line.LineNumber:N0} has invalid source identity fields."
                );
            }
            if (!sources.TryAdd(sourcePath, length))
            {
                throw new InvalidDataException(
                    $"Input manifest line {line.LineNumber:N0} duplicates a source path."
                );
            }
        }
        if (sources.Count == 0)
        {
            throw new InvalidDataException("Input evidence manifest is empty.");
        }
        return sources;
    }

    private static JsonDocument ParseObject(string json, string description, long lineNumber)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException(
                    $"{description} line {lineNumber:N0} is not a JSON object."
                );
            }
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{description} line {lineNumber:N0} is invalid JSON: {ex.Message}",
                ex
            );
        }
    }

    private static void RequireExactProperties(
        JsonElement value,
        IReadOnlyCollection<string> expected,
        string description,
        long lineNumber
    )
    {
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                throw new InvalidDataException(
                    $"{description} at line {lineNumber:N0} has an unexpected or duplicate property '{property.Name}'."
                );
            }
        }
        if (remaining.Count != 0)
        {
            throw new InvalidDataException(
                $"{description} at line {lineNumber:N0} is missing property '{FirstOrdinal(remaining)}'."
            );
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string name, long lineNumber)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(lineNumber, $"has invalid '{name}' provenance");
        }
        return value;
    }

    private static bool RequiredString(
        JsonElement parent,
        string name,
        out string value,
        bool allowWhitespace = false
    )
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return allowWhitespace ? value.Length != 0 : !string.IsNullOrWhiteSpace(value);
    }

    private static bool RequiredInt32(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static bool RequiredInt64(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var property) && property.TryGetInt64(out value);
    }

    private static bool TryParseCanonicalOffset(string value, out long offset)
    {
        offset = 0;
        if (
            value.Length < 3
            || !value.StartsWith("0x", StringComparison.Ordinal)
            || !long.TryParse(
                value.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out offset
            )
            || offset < 0
        )
        {
            return false;
        }
        return string.Equals(value, $"0x{offset:X}", StringComparison.Ordinal);
    }

    private static bool IsLowerHexSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }
        foreach (var character in value)
        {
            if (
                !(
                    character >= '0'
                    && character <= '9'
                    || character >= 'a'
                    && character <= 'f'
                )
            )
            {
                return false;
            }
        }
        return true;
    }

    private static Encoding CreateCodePage1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private sealed class SourceRecordReader : IAsyncDisposable
    {
        private string? _sourcePath;
        private FileStream? _stream;

        internal async Task<bool> MatchesAsync(
            string sourcePath,
            long offset,
            int byteLength,
            string encoding,
            string expectedText,
            CancellationToken cancellationToken
        )
        {
            if (!string.Equals(_sourcePath, sourcePath, StringComparison.Ordinal))
            {
                if (_stream is not null)
                {
                    await _stream.DisposeAsync();
                }
                AnalysisOrchestrator.EnsureNoReparsePoints(
                    sourcePath,
                    "native record source"
                );
                _stream = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.RandomAccess
                );
                _sourcePath = sourcePath;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(byteLength);
            try
            {
                _stream!.Seek(offset, SeekOrigin.Begin);
                var completed = 0;
                while (completed < byteLength)
                {
                    var read = await _stream.ReadAsync(
                        buffer.AsMemory(completed, byteLength - completed),
                        cancellationToken
                    );
                    if (read == 0)
                    {
                        return false;
                    }
                    completed += read;
                }
                var decoder = encoding == "utf-16le" ? Utf16Le : CodePage1252;
                return string.Equals(
                    decoder.GetString(buffer, 0, byteLength),
                    expectedText,
                    StringComparison.Ordinal
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
            }
        }
    }

    private static InvalidDataException Invalid(long lineNumber, string reason) =>
        new($"Native enrichment line {lineNumber:N0} {reason}.");

    private static string FirstOrdinal(IEnumerable<string> values)
    {
        string? first = null;
        foreach (var value in values)
        {
            if (first is null || string.CompareOrdinal(value, first) < 0)
            {
                first = value;
            }
        }
        return first ?? string.Empty;
    }
}
