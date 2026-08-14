#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal readonly record struct ContentRoutingStats(
    string Manifest,
    string ManifestSha256,
    string PolicyVersion,
    long InputFiles,
    long FlossCandidates,
    long OcrCandidates,
    long ClassifierErrors,
    long Conflicts,
    InputManifestInfo FlossInput,
    InputManifestInfo OcrInput
);

internal static class ContentRoutingCore
{
    private const int MaximumLineCharacters = 512 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly HashSet<string> AllowedTopLevelProperties = new(
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
        ],
        StringComparer.Ordinal
    );
    private static readonly HashSet<string> AllowedRoutes = new(
        ["native", "floss", "ocr"],
        StringComparer.Ordinal
    );
    private static readonly HashSet<string> AllowedClassifierProperties = new(
        [
            "engine",
            "version",
            "predictionMode",
            "status",
            "score",
            "output",
            "rawPrediction",
            "executable",
            "executableSha256",
            "runtime",
            "errorCode",
        ],
        StringComparer.Ordinal
    );
    private static readonly HashSet<string> AllowedClassificationProperties = new(
        ["label", "mimeType", "group", "isText"],
        StringComparer.Ordinal
    );

    internal static async Task<ContentRoutingStats> ValidateAndProjectAsync(
        string trustedManifestPath,
        InputManifestInfo trustedManifest,
        string routingManifestPath,
        string flossInventoryPath,
        string flossManifestPath,
        string ocrInventoryPath,
        string ocrManifestPath,
        bool expectedNativeSelected,
        CancellationToken cancellationToken = default,
        string? expectedClassifierExecutable = null,
        bool writeProjections = true
    )
    {
        var trustedEntries = await ReadTrustedEntriesAsync(
            trustedManifestPath,
            trustedManifest,
            cancellationToken
        );
        var routingFullPath = Path.GetFullPath(routingManifestPath);
        if (!File.Exists(routingFullPath))
        {
            throw new FileNotFoundException("The content-routing manifest is missing.", routingFullPath);
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(
            routingFullPath,
            "content-routing manifest"
        );

        var routingSha256 = await HashFileAsync(routingFullPath, cancellationToken);
        string? expectedClassifierPath = null;
        string? expectedClassifierSha256 = null;
        if (!string.IsNullOrWhiteSpace(expectedClassifierExecutable))
        {
            expectedClassifierPath = Path.GetFullPath(expectedClassifierExecutable);
            if (!File.Exists(expectedClassifierPath))
            {
                throw new FileNotFoundException(
                    "The expected Magika classifier executable is missing.",
                    expectedClassifierPath
                );
            }
            AnalysisOrchestrator.EnsureNoReparsePoints(
                expectedClassifierPath,
                "Magika classifier executable"
            );
            expectedClassifierSha256 = await HashFileAsync(
                expectedClassifierPath,
                cancellationToken
            );
        }
        var flossEntries = new List<InputManifestEntry>();
        var ocrEntries = new List<InputManifestEntry>();
        long classifierErrors = 0;
        long conflicts = 0;
        string? policyVersion = null;
        long ordinal = 0;

        try
        {
            using var reader = new StreamReader(
                new FileStream(
                    routingFullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan
                ),
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false
            );
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ordinal++;
                if (line.Length == 0 || line.Length > MaximumLineCharacters)
                {
                    throw new InvalidDataException(
                        $"Content-routing line {ordinal:N0} is empty or exceeds the safety limit."
                    );
                }
                if (ordinal > trustedEntries.Count)
                {
                    throw new InvalidDataException(
                        "The content-routing manifest contains more rows than the trusted input manifest."
                    );
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Content-routing line {ordinal:N0} is invalid JSON: {ex.Message}",
                        ex
                    );
                }
                using (document)
                {
                    var root = document.RootElement;
                    RequireObject(root, "content-routing row", ordinal);
                    ValidateTopLevelProperties(root, ordinal);
                    var expectedSchemaVersion = expectedNativeSelected ? 1 : 2;
                    var expectedPolicyVersion = expectedNativeSelected
                        ? "content-routing-v1"
                        : "content-routing-v2";
                    RequireInt32(
                        root,
                        "schemaVersion",
                        ordinal,
                        expected: expectedSchemaVersion
                    );
                    RequireText(root, "recordType", ordinal, expected: "content-route");
                    var rowPolicy = RequireText(root, "policyVersion", ordinal);
                    if (
                        !string.Equals(
                            rowPolicy,
                            expectedPolicyVersion,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        throw new InvalidDataException(
                            $"Content-routing line {ordinal:N0} does not match the expected native-selection policy."
                        );
                    }
                    policyVersion ??= rowPolicy;
                    if (!string.Equals(policyVersion, rowPolicy, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Content-routing line {ordinal:N0} changed policyVersion within one manifest."
                        );
                    }
                    if (RequireInt64(root, "ordinal", ordinal) != ordinal)
                    {
                        throw new InvalidDataException(
                            $"Content-routing line {ordinal:N0} is out of trusted input order."
                        );
                    }
                    var decisionId = RequireText(root, "decisionId", ordinal);
                    var expectedDecisionId = ComputeDecisionId(root);
                    if (!string.Equals(decisionId, expectedDecisionId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Content-routing line {ordinal:N0} has an invalid routing decision identity."
                        );
                    }

                    var trusted = trustedEntries[(int)ordinal - 1];
                    if (
                        !string.Equals(
                            RequireText(root, "sourceFile", ordinal),
                            trusted.Path,
                            StringComparison.Ordinal
                        )
                        || RequireInt64(root, "sourceSize", ordinal) != trusted.Length
                        || !string.Equals(
                            RequireText(root, "sourceSha256", ordinal),
                            trusted.Sha256,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        throw new InvalidDataException(
                            $"Content-routing line {ordinal:N0} does not match the trusted source identity and order."
                        );
                    }

                    var classifier = RequireProperty(root, "classifier", ordinal);
                    var classifierStatus = ValidateClassifier(
                        classifier,
                        ordinal,
                        expectedClassifierPath,
                        expectedClassifierSha256
                    );
                    if (classifierStatus == "error")
                    {
                        classifierErrors++;
                    }

                    _ = ReadSortedUniqueStrings(
                        RequireProperty(root, "signals", ordinal),
                        "signals",
                        ordinal
                    );
                    var eligibleRoutes = ReadSortedUniqueStrings(
                        RequireProperty(root, "eligibleRoutes", ordinal),
                        "eligibleRoutes",
                        ordinal
                    );
                    ValidateRoutes(eligibleRoutes, "eligibleRoutes", ordinal);
                    if (!eligibleRoutes.Contains("native", StringComparer.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Content-routing line {ordinal:N0} removes native eligibility."
                        );
                    }
                    var scheduledRoutes = ReadSortedUniqueStrings(
                        RequireProperty(root, "scheduledRoutes", ordinal),
                        "scheduledRoutes",
                        ordinal
                    );
                    var nativeSelected = scheduledRoutes.Contains(
                        "native",
                        StringComparer.Ordinal
                    );
                    if (nativeSelected != expectedNativeSelected)
                    {
                        throw new InvalidDataException(
                            $"Content-routing line {ordinal:N0} does not match the expected native selection."
                        );
                    }
                    ValidateRoutes(scheduledRoutes, "scheduledRoutes", ordinal);
                    foreach (var route in scheduledRoutes)
                    {
                        if (!eligibleRoutes.Contains(route, StringComparer.Ordinal))
                        {
                            throw new InvalidDataException(
                                $"Content-routing line {ordinal:N0} schedules ineligible route '{route}'."
                            );
                        }
                    }
                    var rowConflicts = ReadSortedUniqueStrings(
                        RequireProperty(root, "conflicts", ordinal),
                        "conflicts",
                        ordinal
                    );
                    conflicts += rowConflicts.Count;

                    if (scheduledRoutes.Contains("floss", StringComparer.Ordinal))
                    {
                        flossEntries.Add(trusted);
                    }
                    if (scheduledRoutes.Contains("ocr", StringComparer.Ordinal))
                    {
                        ocrEntries.Add(trusted);
                    }
                }
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "The content-routing manifest is not valid UTF-8.",
                ex
            );
        }

        if (ordinal != trustedEntries.Count)
        {
            throw new InvalidDataException(
                $"Content-routing row count changed: expected {trustedEntries.Count:N0}, found {ordinal:N0}."
            );
        }
        if (string.IsNullOrWhiteSpace(policyVersion))
        {
            throw new InvalidDataException("The content-routing manifest did not declare a policy version.");
        }
        var routingSha256After = await HashFileAsync(routingFullPath, cancellationToken);
        if (!string.Equals(routingSha256, routingSha256After, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The content-routing manifest changed during validation.");
        }

        var flossInput = writeProjections
            ? await WriteProjectionAsync(
                flossInventoryPath,
                flossManifestPath,
                flossEntries,
                cancellationToken
            )
            : await ValidateProjectionAsync(
                flossInventoryPath,
                flossManifestPath,
                flossEntries,
                cancellationToken
            );
        var ocrInput = writeProjections
            ? await WriteProjectionAsync(
                ocrInventoryPath,
                ocrManifestPath,
                ocrEntries,
                cancellationToken
            )
            : await ValidateProjectionAsync(
                ocrInventoryPath,
                ocrManifestPath,
                ocrEntries,
                cancellationToken
            );
        return new ContentRoutingStats(
            Path.GetFileName(routingFullPath),
            routingSha256,
            policyVersion,
            ordinal,
            flossEntries.Count,
            ocrEntries.Count,
            classifierErrors,
            conflicts,
            flossInput,
            ocrInput
        );
    }

    internal static async Task VerifyManifestAsync(
        string routingManifestPath,
        string expectedSha256,
        CancellationToken cancellationToken = default
    )
    {
        var fullPath = Path.GetFullPath(routingManifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The content-routing manifest is missing.", fullPath);
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(fullPath, "content-routing manifest");
        var actual = await HashFileAsync(fullPath, cancellationToken);
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Content-routing manifest SHA-256 mismatch. Expected {expectedSha256}, found {actual}."
            );
        }
    }

    internal static async Task<FileStream> AcquireVerifiedFileLeaseAsync(
        string path,
        string expectedSha256,
        string description,
        CancellationToken cancellationToken = default
    )
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The {description} is missing.", fullPath);
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(fullPath, description);
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        try
        {
            var actual = Convert
                .ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{description} SHA-256 mismatch. Expected {expectedSha256}, found {actual}."
                );
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static async Task<List<InputManifestEntry>> ReadTrustedEntriesAsync(
        string manifestPath,
        InputManifestInfo expected,
        CancellationToken cancellationToken
    )
    {
        var fullPath = Path.GetFullPath(manifestPath);
        var digest = await HashFileAsync(fullPath, cancellationToken);
        if (!string.Equals(digest, expected.ManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Evidence manifest SHA-256 mismatch. Expected {expected.ManifestSha256}, found {digest}."
            );
        }
        var entries = new List<InputManifestEntry>();
        using var reader = new StreamReader(
            new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ),
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false
        );
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InputManifestEntry entry;
            try
            {
                entry =
                    JsonSerializer.Deserialize<InputManifestEntry>(line, CompactJson)
                    ?? throw new JsonException("The input manifest entry was null.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Trusted input manifest line {entries.Count + 1:N0} is invalid: {ex.Message}",
                    ex
                );
            }
            entries.Add(entry);
        }
        if (entries.Count != expected.FileCount)
        {
            throw new InvalidDataException(
                $"Trusted input manifest count changed: expected {expected.FileCount:N0}, found {entries.Count:N0}."
            );
        }
        return entries;
    }

    private static async Task<InputManifestInfo> WriteProjectionAsync(
        string inventoryPath,
        string manifestPath,
        IReadOnlyList<InputManifestEntry> entries,
        CancellationToken cancellationToken
    )
    {
        var inventoryFullPath = Path.GetFullPath(inventoryPath);
        var manifestFullPath = Path.GetFullPath(manifestPath);
        var inventoryTemporaryPath = inventoryFullPath + ".partial." + Guid.NewGuid().ToString("N");
        var manifestTemporaryPath = manifestFullPath + ".partial." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(inventoryFullPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestFullPath)!);
        try
        {
            await using (var inventoryWriter = CreateWriter(inventoryTemporaryPath))
            await using (var manifestWriter = CreateWriter(manifestTemporaryPath))
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await inventoryWriter.WriteLineAsync(entry.Path.AsMemory(), cancellationToken);
                    await manifestWriter.WriteLineAsync(
                        JsonSerializer.Serialize(entry, CompactJson).AsMemory(),
                        cancellationToken
                    );
                }
                await inventoryWriter.FlushAsync(cancellationToken);
                await manifestWriter.FlushAsync(cancellationToken);
            }
            var inventorySha256 = await HashFileAsync(inventoryTemporaryPath, cancellationToken);
            var manifestSha256 = await HashFileAsync(manifestTemporaryPath, cancellationToken);
            File.Move(inventoryTemporaryPath, inventoryFullPath, overwrite: true);
            inventoryTemporaryPath = string.Empty;
            File.Move(manifestTemporaryPath, manifestFullPath, overwrite: true);
            manifestTemporaryPath = string.Empty;
            return new InputManifestInfo(
                entries.Count,
                Path.GetFileName(inventoryFullPath),
                inventorySha256,
                Path.GetFileName(manifestFullPath),
                manifestSha256
            );
        }
        finally
        {
            DeleteTemporary(inventoryTemporaryPath);
            DeleteTemporary(manifestTemporaryPath);
        }
    }

    private static async Task<InputManifestInfo> ValidateProjectionAsync(
        string inventoryPath,
        string manifestPath,
        IReadOnlyList<InputManifestEntry> entries,
        CancellationToken cancellationToken
    )
    {
        var inventoryFullPath = Path.GetFullPath(inventoryPath);
        var manifestFullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(inventoryFullPath) || !File.Exists(manifestFullPath))
        {
            throw new FileNotFoundException("A committed content-routing projection is missing.");
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(
            inventoryFullPath,
            "content-routing inventory projection"
        );
        AnalysisOrchestrator.EnsureNoReparsePoints(
            manifestFullPath,
            "content-routing manifest projection"
        );

        var expectedInventorySha256 = ComputeProjectionSha256(entries, entry => entry.Path);
        var expectedManifestSha256 = ComputeProjectionSha256(
            entries,
            entry => JsonSerializer.Serialize(entry, CompactJson)
        );
        var actualInventorySha256 = await HashFileAsync(inventoryFullPath, cancellationToken);
        var actualManifestSha256 = await HashFileAsync(manifestFullPath, cancellationToken);
        if (
            !string.Equals(
                actualInventorySha256,
                expectedInventorySha256,
                StringComparison.Ordinal
            )
            || !string.Equals(
                actualManifestSha256,
                expectedManifestSha256,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                "A committed content-routing projection does not match the authenticated routing decisions."
            );
        }
        return new InputManifestInfo(
            entries.Count,
            Path.GetFileName(inventoryFullPath),
            actualInventorySha256,
            Path.GetFileName(manifestFullPath),
            actualManifestSha256
        );
    }

    private static string ComputeProjectionSha256(
        IReadOnlyList<InputManifestEntry> entries,
        Func<InputManifestEntry, string> selectLine
    )
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            hash.AppendData(StrictUtf8.GetBytes(selectLine(entry)));
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateTopLevelProperties(JsonElement root, long lineNumber)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Content-routing line {lineNumber:N0} contains duplicate property '{property.Name}'."
                );
            }
            if (!AllowedTopLevelProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"Content-routing line {lineNumber:N0} contains unknown property '{property.Name}'."
                );
            }
        }
    }

    internal static string ComputeDecisionId(JsonElement root)
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
            WriteCanonicalJson(writer, root, skipDecisionId: true);
        }
        return "sha256:"
            + Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element,
        bool skipDecisionId = false
    )
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (
                    var property in element
                        .EnumerateObject()
                        .Where(property => !skipDecisionId || property.Name != "decisionId")
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
                throw new InvalidDataException("Routing JSON contains an unsupported value kind.");
        }
    }

    private static string ValidateClassifier(
        JsonElement classifier,
        long lineNumber,
        string? expectedExecutablePath,
        string? expectedExecutableSha256
    )
    {
        RequireObject(classifier, "classifier", lineNumber);
        ValidateObjectProperties(
            classifier,
            AllowedClassifierProperties,
            "classifier",
            lineNumber
        );
        RequireText(classifier, "engine", lineNumber, expected: "magika");
        _ = RequireText(classifier, "version", lineNumber);
        RequireText(
            classifier,
            "predictionMode",
            lineNumber,
            expected: "default-thresholded-output-plus-raw-dl"
        );
        var executable = RequireText(classifier, "executable", lineNumber);
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} classifier executable is not absolute."
            );
        }
        var executableSha256 = RequireText(classifier, "executableSha256", lineNumber);
        ValidateSha256(
            executableSha256,
            "classifier executable",
            lineNumber
        );
        if (
            expectedExecutablePath is not null
            && (
                !string.Equals(
                    Path.GetFullPath(executable),
                    expectedExecutablePath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal
                )
                || !string.Equals(
                    executableSha256,
                    expectedExecutableSha256,
                    StringComparison.Ordinal
                )
            )
        )
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} classifier identity does not match the verified Magika toolchain."
            );
        }
        var score = RequireProperty(classifier, "score", lineNumber);
        if (
            score.ValueKind != JsonValueKind.Number
            || !score.TryGetDouble(out var scoreValue)
            || !double.IsFinite(scoreValue)
            || scoreValue is < 0 or > 1
        )
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} classifier score is invalid."
            );
        }
        ValidateClassification(
            RequireProperty(classifier, "output", lineNumber),
            "classifier output",
            lineNumber
        );
        ValidateClassification(
            RequireProperty(classifier, "rawPrediction", lineNumber),
            "classifier raw prediction",
            lineNumber
        );
        var status = RequireText(classifier, "status", lineNumber);
        if (status is not ("ok" or "error"))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} has an unsupported classifier status."
            );
        }
        if (status == "error")
        {
            _ = RequireText(classifier, "errorCode", lineNumber);
        }
        else if (classifier.TryGetProperty("errorCode", out _))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} has an error code on a successful classification."
            );
        }
        if (classifier.TryGetProperty("runtime", out var runtime))
        {
            RequireObject(runtime, "classifier runtime", lineNumber);
            ValidateObjectProperties(
                runtime,
                new HashSet<string>(["path", "sha256"], StringComparer.Ordinal),
                "classifier runtime",
                lineNumber
            );
            var runtimePath = RequireText(runtime, "path", lineNumber);
            if (!Path.IsPathFullyQualified(runtimePath))
            {
                throw new InvalidDataException(
                    $"Content-routing line {lineNumber:N0} classifier runtime path is not absolute."
                );
            }
            ValidateSha256(
                RequireText(runtime, "sha256", lineNumber),
                "classifier runtime",
                lineNumber
            );
        }
        return status;
    }

    private static void ValidateClassification(
        JsonElement value,
        string description,
        long lineNumber
    )
    {
        RequireObject(value, description, lineNumber);
        ValidateObjectProperties(
            value,
            AllowedClassificationProperties,
            description,
            lineNumber
        );
        _ = RequireText(value, "label", lineNumber);
        _ = RequireText(value, "mimeType", lineNumber);
        _ = RequireText(value, "group", lineNumber);
        if (RequireProperty(value, "isText", lineNumber).ValueKind is not (
            JsonValueKind.True or JsonValueKind.False
        ))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} {description} has invalid isText."
            );
        }
    }

    private static void ValidateObjectProperties(
        JsonElement value,
        IReadOnlySet<string> allowed,
        string description,
        long lineNumber
    )
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"Content-routing line {lineNumber:N0} {description} has duplicate or unknown property '{property.Name}'."
                );
            }
        }
    }

    private static void ValidateSha256(string value, string description, long lineNumber)
    {
        if (
            value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
        )
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} {description} SHA-256 is invalid."
            );
        }
    }

    private static void ValidateRoutes(
        IEnumerable<string> routes,
        string description,
        long lineNumber
    )
    {
        foreach (var route in routes)
        {
            if (!AllowedRoutes.Contains(route))
            {
                throw new InvalidDataException(
                    $"Content-routing line {lineNumber:N0} {description} contains unsupported route '{route}'."
                );
            }
        }
    }

    private static List<string> ReadSortedUniqueStrings(
        JsonElement value,
        string name,
        long lineNumber
    )
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} property '{name}' must be an array."
            );
        }
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException(
                    $"Content-routing line {lineNumber:N0} property '{name}' contains an invalid value."
                );
            }
            result.Add(item.GetString()!);
        }
        if (!result.SequenceEqual(result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} property '{name}' must be sorted and unique."
            );
        }
        return result;
    }

    private static JsonElement RequireProperty(JsonElement root, string name, long lineNumber)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} is missing '{name}'."
            );
        }
        return value;
    }

    private static string RequireText(
        JsonElement root,
        string name,
        long lineNumber,
        string? expected = null
    )
    {
        var value = RequireProperty(root, name, lineNumber);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} property '{name}' must be non-empty text."
            );
        }
        var text = value.GetString()!;
        if (expected is not null && !string.Equals(text, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} property '{name}' must be '{expected}'."
            );
        }
        return text;
    }

    private static long RequireInt64(JsonElement root, string name, long lineNumber)
    {
        var value = RequireProperty(root, name, lineNumber);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} property '{name}' must be an integer."
            );
        }
        return result;
    }

    private static void RequireInt32(
        JsonElement root,
        string name,
        long lineNumber,
        int expected
    )
    {
        if (RequireInt64(root, name, lineNumber) != expected)
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} uses an unsupported '{name}'."
            );
        }
    }

    private static void RequireObject(JsonElement value, string name, long lineNumber)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} '{name}' must be an object."
            );
        }
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        return Convert
            .ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static StreamWriter CreateWriter(string path)
    {
        var writer = new StreamWriter(
            new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ),
            StrictUtf8,
            64 * 1024
        );
        writer.NewLine = "\n";
        return writer;
    }

    private static void DeleteTemporary(string path)
    {
        if (path.Length == 0)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
