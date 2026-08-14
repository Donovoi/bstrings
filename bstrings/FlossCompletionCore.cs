#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal readonly record struct FlossCompletionStats(
    long RoutedFiles,
    long FilesWithRecords,
    long OutputRecords,
    long StaticRecords,
    long LanguageRecords,
    long LanguageMissedRecords,
    long StackRecords,
    long TightRecords,
    long DecodedRecords
);

internal static class FlossCompletionCore
{
    private const int MaximumManifestLineCharacters = 256 * 1024;
    private const int MaximumRoutingLineCharacters = 512 * 1024;
    private static readonly string[] ManifestProperties =
        ["schemaVersion", "path", "length", "sha256"];
    private static readonly string[] RoutingProperties =
        [
            "schemaVersion",
            "recordType",
            "policyVersion",
            "ordinal",
            "decisionId",
            "sourceFile",
            "sourceSize",
            "sourceSha256",
            "classifier",
            "signals",
            "eligibleRoutes",
            "scheduledRoutes",
            "conflicts",
        ];
    private static readonly string[] RecordProperties =
        [
            "schemaVersion",
            "recordType",
            "text",
            "sourceFile",
            "location",
            "origin",
            "attributes",
            "recordId",
        ];
    private static readonly string[] LocationProperties = ["kind", "value"];
    private static readonly string[] OriginProperties = ["extractor", "version", "kind"];
    private static readonly string[] ClassificationProperties =
        ["label", "mimeType", "group", "isText"];
    private static readonly string[] BaseAttributeProperties =
        [
            "magikaLabel",
            "magikaScore",
            "magikaMimeType",
            "magikaGroup",
            "flossImageBase",
            "routeDecisionId",
            "encoding",
        ];
    private static readonly HashSet<string> AllowedRoutes = new(
        ["native", "floss", "ocr"],
        StringComparer.Ordinal
    );
    private static readonly HashSet<string> AllowedEncodings = new(
        ["ASCII", "UTF-16LE", "UTF-8"],
        StringComparer.Ordinal
    );
    private static readonly HashSet<string> AllowedAddressTypes = new(
        ["STACK", "GLOBAL", "HEAP"],
        StringComparer.Ordinal
    );

    private sealed record SourceIdentity(string Path, long Length, string Sha256);

    private sealed record RouteIdentity(
        string SourceFile,
        long SourceSize,
        string SourceSha256,
        string DecisionId,
        string MagikaLabel,
        double MagikaScore,
        string MagikaMimeType,
        string MagikaGroup
    );

    internal static async Task<FlossCompletionStats> ValidateAsync(
        string outputPath,
        string projectionInventoryPath,
        string projectionManifestPath,
        InputManifestInfo projectionIdentity,
        string routingManifestPath,
        string expectedRoutingSha256,
        string workingDirectory,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (
            !string.Equals(
                Path.GetFileName(Path.GetFullPath(projectionInventoryPath)),
                projectionIdentity.InventoryFile,
                StringComparison.Ordinal
            )
            || !string.Equals(
                Path.GetFileName(Path.GetFullPath(projectionManifestPath)),
                projectionIdentity.ManifestFile,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                "FLOSS projection paths do not match their committed input identity."
            );
        }

        await InputEvidenceManifest.VerifyInventoryAsync(
            projectionInventoryPath,
            projectionIdentity,
            cancellationToken
        );
        await InputEvidenceManifest.VerifyAsync(
            projectionManifestPath,
            projectionIdentity,
            cancellationToken
        );
        var sources = await ReadProjectionManifestAsync(
            projectionManifestPath,
            projectionIdentity.FileCount,
            cancellationToken
        );
        var routes = await ReadFlossRoutesAsync(
            routingManifestPath,
            expectedRoutingSha256,
            cancellationToken
        );
        if (routes.Count != sources.Count)
        {
            throw new InvalidDataException(
                "FLOSS projection cardinality does not match scheduled routing decisions."
            );
        }
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var route = routes[index];
            if (
                !string.Equals(source.Path, route.SourceFile, StringComparison.Ordinal)
                || source.Length != route.SourceSize
                || !string.Equals(source.Sha256, route.SourceSha256, StringComparison.Ordinal)
            )
            {
                throw new InvalidDataException(
                    "FLOSS projection order or source identity does not match content routing."
                );
            }
        }

        var sourceByPath = sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
        var routeByPath = routes.ToDictionary(route => route.SourceFile, StringComparer.Ordinal);
        var sourcesWithRecords = new HashSet<string>(StringComparer.Ordinal);
        long outputRecords = 0;
        long staticRecords = 0;
        long languageRecords = 0;
        long languageMissedRecords = 0;
        long stackRecords = 0;
        long tightRecords = 0;
        long decodedRecords = 0;
        string? flossVersion = null;

        using var identifiers = new DiskBackedProvenanceValidator(workingDirectory);
        await foreach (
            var line in StrictJsonlCompletionReader.ReadAsync(
                outputPath,
                "FLOSS recovered-string output",
                EnrichmentRegexPipelineCore.MaxJsonLineCharacters,
                cancellationToken
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = ParseObject(
                line.Json,
                "FLOSS recovered-string output",
                line.LineNumber
            );
            var root = document.RootElement;
            RequireExactProperties(
                root,
                RecordProperties,
                optional: null,
                "FLOSS recovered-string record",
                line.LineNumber
            );
            if (
                !RequiredInt32(root, "schemaVersion", out var schemaVersion)
                || schemaVersion != EnrichmentRegexPipelineCore.CurrentSchemaVersion
                || !RequiredString(root, "recordType", out var recordType)
                || recordType != "string"
                || !RequiredString(root, "text", out _, allowEmpty: true)
                || !RequiredString(root, "sourceFile", out var sourceFile)
                || !RequiredString(root, "recordId", out var recordId)
                || !IsPrefixedLowerSha256(recordId)
                || !sourceByPath.ContainsKey(sourceFile)
                || !routeByPath.TryGetValue(sourceFile, out var route)
            )
            {
                throw Invalid(line.LineNumber, "has invalid schema, identity, or source binding");
            }

            var origin = RequiredObject(root, "origin", line.LineNumber);
            RequireExactProperties(
                origin,
                OriginProperties,
                optional: null,
                "FLOSS origin",
                line.LineNumber
            );
            if (
                !RequiredString(origin, "extractor", out var extractor)
                || extractor != "floss"
                || !RequiredString(origin, "version", out var recordFlossVersion)
                || !RequiredString(origin, "kind", out var kind)
            )
            {
                throw Invalid(line.LineNumber, "has invalid FLOSS origin provenance");
            }
            flossVersion ??= recordFlossVersion;
            if (!string.Equals(flossVersion, recordFlossVersion, StringComparison.Ordinal))
            {
                throw Invalid(line.LineNumber, "changed FLOSS version within one output");
            }

            var expectedLocationKind = kind switch
            {
                "static" or "language" or "language-missed" => "file_offset",
                "stack" or "tight" => "program_counter",
                "decoded" => "virtual_address",
                _ => throw Invalid(line.LineNumber, "has an unsupported FLOSS origin kind"),
            };
            var location = RequiredObject(root, "location", line.LineNumber);
            RequireExactProperties(
                location,
                LocationProperties,
                optional: null,
                "FLOSS location",
                line.LineNumber
            );
            if (
                !RequiredString(location, "kind", out var locationKind)
                || locationKind != expectedLocationKind
                || !RequiredString(location, "value", out var locationValue)
                || !TryParseCanonicalUnsignedHex(locationValue, out _)
            )
            {
                throw Invalid(line.LineNumber, "has invalid FLOSS location provenance");
            }

            var attributes = RequiredObject(root, "attributes", line.LineNumber);
            var requiredAttributes = BaseAttributeProperties.ToList();
            if (kind is "stack" or "tight")
            {
                requiredAttributes.AddRange(
                    [
                        "function",
                        "stackPointer",
                        "originalStackPointer",
                        "stackOffset",
                        "frameOffset",
                    ]
                );
            }
            else if (kind == "decoded")
            {
                requiredAttributes.AddRange(
                    ["addressType", "decodedAt", "decodingRoutine"]
                );
            }
            RequireExactProperties(
                attributes,
                requiredAttributes,
                new HashSet<string>(["flossLanguage"], StringComparer.Ordinal),
                "FLOSS attributes",
                line.LineNumber
            );
            if (
                !RequiredString(attributes, "magikaLabel", out var magikaLabel)
                || magikaLabel != route.MagikaLabel
                || !RequiredDouble(attributes, "magikaScore", out var magikaScore)
                || !double.IsFinite(magikaScore)
                || magikaScore is < 0 or > 1
                || magikaScore != route.MagikaScore
                || !RequiredString(attributes, "magikaMimeType", out var magikaMimeType)
                || magikaMimeType != route.MagikaMimeType
                || !RequiredString(attributes, "magikaGroup", out var magikaGroup)
                || magikaGroup != route.MagikaGroup
                || !RequiredUInt64(attributes, "flossImageBase", out _)
                || !RequiredString(attributes, "routeDecisionId", out var routeDecisionId)
                || routeDecisionId != route.DecisionId
                || !RequiredString(attributes, "encoding", out var encoding)
                || !AllowedEncodings.Contains(encoding)
                || attributes.TryGetProperty("flossLanguage", out var flossLanguage)
                    && (
                        flossLanguage.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(flossLanguage.GetString())
                    )
            )
            {
                throw Invalid(line.LineNumber, "has invalid common FLOSS attributes");
            }
            if (
                kind is "stack" or "tight"
                && (
                    !RequiredUInt64(attributes, "function", out _)
                    || !RequiredUInt64(attributes, "stackPointer", out _)
                    || !RequiredUInt64(attributes, "originalStackPointer", out _)
                    || !RequiredInt64(attributes, "stackOffset", out _)
                    || !RequiredInt64(attributes, "frameOffset", out _)
                )
            )
            {
                throw Invalid(line.LineNumber, "has invalid stack-string attributes");
            }
            if (
                kind == "decoded"
                && (
                    !RequiredString(attributes, "addressType", out var addressType)
                    || !AllowedAddressTypes.Contains(addressType)
                    || !RequiredUInt64(attributes, "decodedAt", out _)
                    || !RequiredUInt64(attributes, "decodingRoutine", out _)
                )
            )
            {
                throw Invalid(line.LineNumber, "has invalid decoded-string attributes");
            }

            var expectedRecordId = ComputeRecordId(root);
            if (!string.Equals(recordId, expectedRecordId, StringComparison.Ordinal))
            {
                throw Invalid(line.LineNumber, "failed deterministic record-ID validation");
            }
            identifiers.AddOriginal(recordId);
            sourcesWithRecords.Add(sourceFile);
            outputRecords = checked(outputRecords + 1);
            switch (kind)
            {
                case "static":
                    staticRecords++;
                    break;
                case "language":
                    languageRecords++;
                    break;
                case "language-missed":
                    languageMissedRecords++;
                    break;
                case "stack":
                    stackRecords++;
                    break;
                case "tight":
                    tightRecords++;
                    break;
                case "decoded":
                    decodedRecords++;
                    break;
            }
        }
        identifiers.Validate(cancellationToken);

        await InputEvidenceManifest.VerifyInventoryAsync(
            projectionInventoryPath,
            projectionIdentity,
            cancellationToken
        );
        await InputEvidenceManifest.VerifyAsync(
            projectionManifestPath,
            projectionIdentity,
            cancellationToken
        );
        await ContentRoutingCore.VerifyManifestAsync(
            routingManifestPath,
            expectedRoutingSha256,
            cancellationToken
        );

        return new FlossCompletionStats(
            sources.Count,
            sourcesWithRecords.Count,
            outputRecords,
            staticRecords,
            languageRecords,
            languageMissedRecords,
            stackRecords,
            tightRecords,
            decodedRecords
        );
    }

    internal static string ComputeRecordId(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (
            var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    Indented = false,
                    SkipValidation = false,
                }
            )
        )
        {
            WriteCanonicalJson(writer, root, skipRecordId: true);
        }
        return "sha256:"
            + Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static async Task<List<SourceIdentity>> ReadProjectionManifestAsync(
        string path,
        long expectedCount,
        CancellationToken cancellationToken
    )
    {
        var sources = new List<SourceIdentity>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        await foreach (
            var line in StrictJsonlCompletionReader.ReadAsync(
                path,
                "FLOSS projection manifest",
                MaximumManifestLineCharacters,
                cancellationToken
            )
        )
        {
            using var document = ParseObject(
                line.Json,
                "FLOSS projection manifest",
                line.LineNumber
            );
            var root = document.RootElement;
            RequireExactProperties(
                root,
                ManifestProperties,
                optional: null,
                "FLOSS projection entry",
                line.LineNumber
            );
            if (
                !RequiredInt32(root, "schemaVersion", out var schemaVersion)
                || schemaVersion != 1
                || !RequiredString(root, "path", out var sourcePath)
                || !Path.IsPathFullyQualified(sourcePath)
                || !RequiredInt64(root, "length", out var length)
                || length < 0
                || !RequiredString(root, "sha256", out var sha256)
                || !IsLowerSha256(sha256)
                || !unique.Add(sourcePath)
            )
            {
                throw new InvalidDataException(
                    $"FLOSS projection line {line.LineNumber:N0} has invalid or duplicate source identity."
                );
            }
            sources.Add(new SourceIdentity(sourcePath, length, sha256));
        }
        if (sources.Count != expectedCount)
        {
            throw new InvalidDataException(
                $"FLOSS projection count changed: expected {expectedCount:N0}, found {sources.Count:N0}."
            );
        }
        return sources;
    }

    private static async Task<List<RouteIdentity>> ReadFlossRoutesAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken
    )
    {
        if (!IsLowerSha256(expectedSha256))
        {
            throw new InvalidDataException("Expected content-routing SHA-256 is invalid.");
        }
        await ContentRoutingCore.VerifyManifestAsync(path, expectedSha256, cancellationToken);
        var routes = new List<RouteIdentity>();
        var allSources = new HashSet<string>(StringComparer.Ordinal);
        long expectedOrdinal = 1;
        await foreach (
            var line in StrictJsonlCompletionReader.ReadAsync(
                path,
                "Content-routing manifest",
                MaximumRoutingLineCharacters,
                cancellationToken
            )
        )
        {
            using var document = ParseObject(
                line.Json,
                "Content-routing manifest",
                line.LineNumber
            );
            var root = document.RootElement;
            RequireExactProperties(
                root,
                RoutingProperties,
                optional: null,
                "Content-routing record",
                line.LineNumber
            );
            if (
                !RequiredInt32(root, "schemaVersion", out var schemaVersion)
                || schemaVersion is not (1 or 2)
                || !RequiredString(root, "recordType", out var recordType)
                || recordType != "content-route"
                || !RequiredString(root, "policyVersion", out var policyVersion)
                || policyVersion != (schemaVersion == 1 ? "content-routing-v1" : "content-routing-v2")
                || !RequiredInt64(root, "ordinal", out var ordinal)
                || ordinal != expectedOrdinal
                || !RequiredString(root, "decisionId", out var decisionId)
                || !IsPrefixedLowerSha256(decisionId)
                || decisionId != ContentRoutingCore.ComputeDecisionId(root)
                || !RequiredString(root, "sourceFile", out var sourceFile)
                || !allSources.Add(sourceFile)
                || !RequiredInt64(root, "sourceSize", out var sourceSize)
                || sourceSize < 0
                || !RequiredString(root, "sourceSha256", out var sourceSha256)
                || !IsLowerSha256(sourceSha256)
                || !root.TryGetProperty("classifier", out var classifier)
                || classifier.ValueKind != JsonValueKind.Object
            )
            {
                throw new InvalidDataException(
                    $"Content-routing line {line.LineNumber:N0} has invalid decision identity."
                );
            }
            if (
                !RequiredDouble(classifier, "score", out var magikaScore)
                || !double.IsFinite(magikaScore)
                || magikaScore is < 0 or > 1
            )
            {
                throw new InvalidDataException(
                    $"Content-routing line {line.LineNumber:N0} has an invalid classifier score."
                );
            }
            var output = RequiredRoutingObject(
                classifier,
                "output",
                "classifier output",
                line.LineNumber
            );
            RequireExactProperties(
                output,
                ClassificationProperties,
                optional: null,
                "Content-routing classifier output",
                line.LineNumber
            );
            if (
                !RequiredString(output, "label", out var magikaLabel)
                || !RequiredString(output, "mimeType", out var magikaMimeType)
                || !RequiredString(output, "group", out var magikaGroup)
                || !output.TryGetProperty("isText", out var isText)
                || isText.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            )
            {
                throw new InvalidDataException(
                    $"Content-routing line {line.LineNumber:N0} has invalid classifier output."
                );
            }
            var eligible = ReadSortedUniqueRoutes(root, "eligibleRoutes", line.LineNumber);
            var scheduled = ReadSortedUniqueRoutes(root, "scheduledRoutes", line.LineNumber);
            if (
                (schemaVersion == 1) != scheduled.Contains("native")
                || !scheduled.IsSubsetOf(eligible)
            )
            {
                throw new InvalidDataException(
                    $"Content-routing line {line.LineNumber:N0} has invalid scheduled routes."
                );
            }
            RequireStringArray(root, "signals", line.LineNumber);
            RequireStringArray(root, "conflicts", line.LineNumber);
            if (scheduled.Contains("floss"))
            {
                routes.Add(
                    new RouteIdentity(
                        sourceFile,
                        sourceSize,
                        sourceSha256,
                        decisionId,
                        magikaLabel,
                        magikaScore,
                        magikaMimeType,
                        magikaGroup
                    )
                );
            }
            expectedOrdinal++;
        }
        await ContentRoutingCore.VerifyManifestAsync(path, expectedSha256, cancellationToken);
        return routes;
    }

    private static HashSet<string> ReadSortedUniqueRoutes(
        JsonElement root,
        string name,
        long lineNumber
    )
    {
        var values = RequireStringArray(root, name, lineNumber);
        if (values.Any(value => !AllowedRoutes.Contains(value)))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} has an unsupported '{name}' route."
            );
        }
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private static List<string> RequireStringArray(
        JsonElement root,
        string name,
        long lineNumber
    )
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} has invalid '{name}'."
            );
        }
        var values = new List<string>();
        string? previous = null;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException(
                    $"Content-routing line {lineNumber:N0} has invalid '{name}'."
                );
            }
            var current = item.GetString()!;
            if (previous is not null && string.CompareOrdinal(previous, current) >= 0)
            {
                throw new InvalidDataException(
                    $"Content-routing line {lineNumber:N0} has unsorted or duplicate '{name}'."
                );
            }
            values.Add(current);
            previous = current;
        }
        return values;
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
        IEnumerable<string> required,
        HashSet<string>? optional,
        string description,
        long lineNumber
    )
    {
        var remaining = new HashSet<string>(required, StringComparer.Ordinal);
        var seenOptional = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (remaining.Remove(property.Name))
            {
                continue;
            }
            if (optional is not null && optional.Contains(property.Name) && seenOptional.Add(property.Name))
            {
                continue;
            }
            throw new InvalidDataException(
                $"{description} at line {lineNumber:N0} has unexpected or duplicate property '{property.Name}'."
            );
        }
        if (remaining.Count != 0)
        {
            throw new InvalidDataException(
                $"{description} at line {lineNumber:N0} is missing required properties."
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

    private static JsonElement RequiredRoutingObject(
        JsonElement parent,
        string name,
        string description,
        long lineNumber
    )
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} has invalid {description}."
            );
        }
        return value;
    }

    private static bool RequiredString(
        JsonElement parent,
        string name,
        out string value,
        bool allowEmpty = false
    )
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return allowEmpty || !string.IsNullOrWhiteSpace(value);
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

    private static bool RequiredUInt64(JsonElement parent, string name, out ulong value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var property) && property.TryGetUInt64(out value);
    }

    private static bool RequiredDouble(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var property) && property.TryGetDouble(out value);
    }

    private static bool TryParseCanonicalUnsignedHex(string value, out ulong number)
    {
        number = 0;
        return value.Length >= 3
            && value.StartsWith("0x", StringComparison.Ordinal)
            && ulong.TryParse(
                value.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out number
            )
            && string.Equals(value, $"0x{number:X}", StringComparison.Ordinal);
    }

    private static bool IsPrefixedLowerSha256(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal)
        && IsLowerSha256(value[7..]);

    private static bool IsLowerSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }
        return value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element,
        bool skipRecordId = false
    )
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (
                    var property in element
                        .EnumerateObject()
                        .Where(property => !skipRecordId || property.Name != "recordId")
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                )
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("FLOSS JSON contains an unsupported value kind.");
        }
    }

    private static InvalidDataException Invalid(long lineNumber, string reason) =>
        new($"FLOSS recovered-string line {lineNumber:N0} {reason}.");
}
