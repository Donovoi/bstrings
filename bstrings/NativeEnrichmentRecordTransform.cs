#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace bstrings;

internal sealed class NativeEnrichmentRecordTransform
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly string ExtractorVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    private readonly string _sourceFile;
    private long _recordCount;

    internal NativeEnrichmentRecordTransform(string sourceFile)
    {
        _sourceFile = sourceFile;
    }

    internal long RecordCount => Interlocked.Read(ref _recordCount);

    internal List<string> TransformBatch(List<ExtractedStringHit> hits)
    {
        var output = new List<string>(hits.Count);
        foreach (var hit in hits)
        {
            if (string.IsNullOrEmpty(hit.Data))
            {
                continue;
            }
            if (hit.Data.Length > EnrichmentRegexPipelineCore.MaxNativeTextCharacters)
            {
                throw new InvalidDataException(
                    $"Extracted string at offset 0x{hit.Offset:X} exceeds the analysis workflow's "
                        + $"{EnrichmentRegexPipelineCore.MaxNativeTextCharacters:N0}-character text limit."
                );
            }

            var location = $"0x{hit.Offset:X}";
            var record = new EnrichmentStringRecord
            {
                SchemaVersion = EnrichmentRegexPipelineCore.CurrentSchemaVersion,
                RecordType = "string",
                RecordId = CreateRecordId(_sourceFile, location, hit.Data),
                Text = hit.Data,
                SourceFile = _sourceFile,
                Location = new EnrichmentLocation { Kind = "file_offset", Value = location },
                Origin = new EnrichmentOrigin
                {
                    Extractor = "bstrings",
                    Version = ExtractorVersion,
                    Kind = "static",
                },
                Attributes = new Dictionary<string, JsonElement>
                {
                    ["encoding"] = JsonSerializer.SerializeToElement(hit.Encoding),
                    ["byteLength"] = JsonSerializer.SerializeToElement(hit.ByteLength),
                },
            };
            var line = JsonSerializer.Serialize(record, JsonOptions);
            if (line.Length > EnrichmentRegexPipelineCore.MaxJsonLineCharacters)
            {
                throw new InvalidDataException(
                    $"Serialized string record at offset 0x{hit.Offset:X} exceeds the "
                        + $"{EnrichmentRegexPipelineCore.MaxJsonLineCharacters:N0}-character JSONL safety limit."
                );
            }
            output.Add(line);
        }
        Interlocked.Add(ref _recordCount, output.Count);
        return output;
    }

    internal List<string> TransformBoundaryBatch(
        List<ExtractedStringHit> hits,
        int primaryChunkSize
    )
    {
        if (primaryChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(primaryChunkSize));
        }

        var crossingHits = new List<ExtractedStringHit>();
        foreach (var hit in hits)
        {
            if (hit.ByteLength <= 0 || hit.Offset < 0)
            {
                continue;
            }

            var boundary = checked((hit.Offset / primaryChunkSize + 1) * primaryChunkSize);
            if (hit.Offset < boundary && hit.Offset + hit.ByteLength > boundary)
            {
                crossingHits.Add(hit);
            }
        }
        hits.Clear();
        return TransformBatch(crossingHits);
    }

    internal static string CreateRecordId(string sourceFile, string location, string text)
    {
        var material = Encoding.UTF8.GetBytes(string.Concat(sourceFile, "\0", location, "\0", text));
        return $"sha256:{Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant()}";
    }
}
