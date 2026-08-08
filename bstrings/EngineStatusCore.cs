#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal readonly record struct EngineTerminalCounts(
    long Succeeded,
    long NotApplicable,
    long DisabledByUser,
    long OutputRecords
);

internal readonly record struct EngineStatusStats(
    string Manifest,
    string ManifestSha256,
    long Records,
    EngineTerminalCounts Native,
    EngineTerminalCounts Floss,
    EngineTerminalCounts Ocr
);

internal static class EngineStatusCore
{
    private const int MaximumRoutingLineCharacters = 512 * 1024;
    private const int MaximumAssessmentLineCharacters = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly JsonSerializerOptions AssessmentJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal static async Task<EngineStatusStats> WriteAsync(
        string routingManifestPath,
        string expectedRoutingSha256,
        long expectedInputFiles,
        string nativeStringsPath,
        string recoveredStringsPath,
        string ocrAssessmentsPath,
        string outputPath,
        CancellationToken cancellationToken = default
    )
    {
        if (expectedInputFiles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedInputFiles));
        }

        var native = await CountNativeRecordsAsync(nativeStringsPath, cancellationToken);
        var recovered = await CountRecoveredRecordsAsync(
            recoveredStringsPath,
            cancellationToken
        );
        var assessments = await ReadOcrAssessmentsAsync(
            ocrAssessmentsPath,
            cancellationToken
        );
        var outputFullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        var temporaryPath = outputFullPath + ".partial." + Guid.NewGuid().ToString("N");

        var statusRecords = 0L;
        var nativeCounts = new MutableCounts();
        var flossCounts = new MutableCounts();
        var ocrCounts = new MutableCounts();

        try
        {
            await using var routingLease = await ContentRoutingCore.AcquireVerifiedFileLeaseAsync(
                routingManifestPath,
                expectedRoutingSha256,
                "content-routing manifest",
                cancellationToken
            );
            using var routingReader = new StreamReader(
                routingLease,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                64 * 1024,
                leaveOpen: true
            );
            await using (
                var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan
                )
            )
            await using (var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true))
            {
                long ordinal = 0;
                while (await routingReader.ReadLineAsync(cancellationToken) is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ordinal++;
                    if (line.Length == 0 || line.Length > MaximumRoutingLineCharacters)
                    {
                        throw new InvalidDataException(
                            $"Content-routing line {ordinal:N0} is empty or exceeds the safety limit."
                        );
                    }

                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var routeOrdinal = RequireInt64(root, "ordinal", ordinal, "content route");
                    if (routeOrdinal != ordinal)
                    {
                        throw new InvalidDataException(
                            $"Content-routing line {ordinal:N0} is out of order while writing engine statuses."
                        );
                    }
                    var decisionId = RequireText(root, "decisionId", ordinal, "content route");
                    var sourceFile = RequireText(root, "sourceFile", ordinal, "content route");
                    var sourceSize = RequireInt64(root, "sourceSize", ordinal, "content route");
                    var sourceSha256 = RequireText(
                        root,
                        "sourceSha256",
                        ordinal,
                        "content route"
                    );
                    var eligible = ReadRouteSet(root, "eligibleRoutes", ordinal);
                    var scheduled = ReadRouteSet(root, "scheduledRoutes", ordinal);

                    var nativeOutput = native.Remove(sourceFile, out var nativeValue)
                        ? nativeValue
                        : 0;
                    await WriteStatusAsync(
                        writer,
                        ordinal,
                        decisionId,
                        sourceFile,
                        sourceSize,
                        sourceSha256,
                        "native",
                        eligible: true,
                        selected: true,
                        status: "succeeded",
                        nativeOutput,
                        cancellationToken
                    );
                    nativeCounts.Add("succeeded", nativeOutput);
                    statusRecords++;

                    var recoveredKey = new RoutedOutputKey(sourceFile, decisionId);
                    var flossOutput = recovered.Remove(recoveredKey, out var recoveredValue)
                        ? recoveredValue
                        : 0;
                    var flossEligible = eligible.Contains("floss");
                    var flossSelected = scheduled.Contains("floss");
                    if (!flossSelected && flossOutput != 0)
                    {
                        throw new InvalidDataException(
                            $"FLOSS emitted records for unscheduled route '{decisionId}'."
                        );
                    }
                    var flossStatus = flossSelected
                        ? "succeeded"
                        : flossEligible
                            ? "disabled-by-user"
                            : "not-applicable";
                    await WriteStatusAsync(
                        writer,
                        ordinal,
                        decisionId,
                        sourceFile,
                        sourceSize,
                        sourceSha256,
                        "floss",
                        flossEligible,
                        flossSelected,
                        flossStatus,
                        flossOutput,
                        cancellationToken
                    );
                    flossCounts.Add(flossStatus, flossOutput);
                    statusRecords++;

                    var hasAssessment = assessments.Remove(sourceFile, out var assessment);
                    if (hasAssessment)
                    {
                        if (
                            !string.Equals(
                                assessment!.SourceSha256,
                                sourceSha256,
                                StringComparison.Ordinal
                            )
                            || assessment.SourceSize != sourceSize
                            || !string.Equals(
                                assessment.RouteDecisionId,
                                decisionId,
                                StringComparison.Ordinal
                            )
                        )
                        {
                            throw new InvalidDataException(
                                $"OCR assessment identity does not match route '{decisionId}'."
                            );
                        }
                    }
                    var ocrEligible = eligible.Contains("ocr");
                    var ocrSelected = scheduled.Contains("ocr");
                    if (ocrSelected != hasAssessment)
                    {
                        throw new InvalidDataException(
                            ocrSelected
                                ? $"OCR assessment is missing for selected route '{decisionId}'."
                                : $"OCR assessment exists for unscheduled route '{decisionId}'."
                        );
                    }
                    var ocrOutput = assessment?.StringRecords ?? 0;
                    var ocrStatus = ocrSelected
                        ? assessment!.Status == "processed"
                            ? "succeeded"
                            : "not-applicable"
                        : ocrEligible
                            ? "disabled-by-user"
                            : "not-applicable";
                    await WriteStatusAsync(
                        writer,
                        ordinal,
                        decisionId,
                        sourceFile,
                        sourceSize,
                        sourceSha256,
                        "ocr",
                        ocrEligible,
                        ocrSelected,
                        ocrStatus,
                        ocrOutput,
                        cancellationToken
                    );
                    ocrCounts.Add(ocrStatus, ocrOutput);
                    statusRecords++;
                }

                if (ordinal != expectedInputFiles)
                {
                    throw new InvalidDataException(
                        $"Engine-status routing coverage changed: expected {expectedInputFiles:N0}, found {ordinal:N0}."
                    );
                }
                if (native.Count != 0 || recovered.Count != 0 || assessments.Count != 0)
                {
                    throw new InvalidDataException(
                        "Engine outputs contain source identities not covered by the routing manifest."
                    );
                }
                await writer.FlushAsync(cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var manifestSha256 = await HashFileAsync(temporaryPath, cancellationToken);
            File.Move(temporaryPath, outputFullPath, overwrite: true);
            temporaryPath = string.Empty;
            return new EngineStatusStats(
                Path.GetFileName(outputFullPath),
                manifestSha256,
                statusRecords,
                nativeCounts.Freeze(),
                flossCounts.Freeze(),
                ocrCounts.Freeze()
            );
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<Dictionary<string, long>> CountNativeRecordsAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var line in EnrichmentJsonlReader.ReadAsync(path, cancellationToken))
        {
            counts[line.Record.SourceFile] = checked(
                counts.GetValueOrDefault(line.Record.SourceFile) + 1
            );
        }
        return counts;
    }

    private static async Task<Dictionary<RoutedOutputKey, long>> CountRecoveredRecordsAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var counts = new Dictionary<RoutedOutputKey, long>();
        await foreach (var line in EnrichmentJsonlReader.ReadAsync(path, cancellationToken))
        {
            if (
                line.Record.Attributes is null
                || !line.Record.Attributes.TryGetValue("routeDecisionId", out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString())
            )
            {
                throw new InvalidDataException(
                    $"Recovered record at line {line.LineNumber:N0} has no routing decision identity."
                );
            }
            var key = new RoutedOutputKey(line.Record.SourceFile, value.GetString()!);
            counts[key] = checked(counts.GetValueOrDefault(key) + 1);
        }
        return counts;
    }

    private static async Task<Dictionary<string, OcrTerminalAssessment>> ReadOcrAssessmentsAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("OCR assessment output was not found.", fullPath);
        }
        var result = new Dictionary<string, OcrTerminalAssessment>(StringComparer.Ordinal);
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
        long lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (line.Length == 0 || line.Length > MaximumAssessmentLineCharacters)
            {
                throw new InvalidDataException(
                    $"OCR assessment line {lineNumber:N0} is empty or exceeds the safety limit."
                );
            }
            OcrAssessmentRecord assessment;
            try
            {
                assessment =
                    JsonSerializer.Deserialize<OcrAssessmentRecord>(line, AssessmentJsonOptions)
                    ?? throw new JsonException("The OCR assessment was null.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"OCR assessment line {lineNumber:N0} is invalid: {ex.Message}",
                    ex
                );
            }
            if (
                string.IsNullOrWhiteSpace(assessment.SourceFile)
                || assessment.Status is not ("processed" or "not-applicable")
                || assessment.StringRecords is not >= 0
                || assessment.SourceSize is not >= 0
                || string.IsNullOrWhiteSpace(assessment.SourceSha256)
            )
            {
                throw new InvalidDataException(
                    $"OCR assessment line {lineNumber:N0} has invalid terminal fields."
                );
            }
            if (
                !result.TryAdd(
                    assessment.SourceFile,
                    new OcrTerminalAssessment(
                        assessment.Status,
                        assessment.StringRecords.Value,
                        assessment.SourceSize.Value,
                        assessment.SourceSha256,
                        assessment.RouteDecisionId
                    )
                )
            )
            {
                throw new InvalidDataException(
                    $"OCR assessment line {lineNumber:N0} duplicates a source identity."
                );
            }
        }
        return result;
    }

    private static HashSet<string> ReadRouteSet(
        JsonElement root,
        string propertyName,
        long lineNumber
    )
    {
        if (
            !root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array
        )
        {
            throw new InvalidDataException(
                $"Content-routing line {lineNumber:N0} has invalid '{propertyName}'."
            );
        }
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !result.Add(item.GetString()!))
            {
                throw new InvalidDataException(
                    $"Content-routing line {lineNumber:N0} has invalid '{propertyName}'."
                );
            }
        }
        return result;
    }

    private static string RequireText(
        JsonElement root,
        string propertyName,
        long lineNumber,
        string description
    )
    {
        if (
            !root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
        )
        {
            throw new InvalidDataException(
                $"{description} line {lineNumber:N0} has invalid '{propertyName}'."
            );
        }
        return value.GetString()!;
    }

    private static long RequireInt64(
        JsonElement root,
        string propertyName,
        long lineNumber,
        string description
    )
    {
        if (
            !root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result)
        )
        {
            throw new InvalidDataException(
                $"{description} line {lineNumber:N0} has invalid '{propertyName}'."
            );
        }
        return result;
    }

    private static async Task WriteStatusAsync(
        StreamWriter writer,
        long ordinal,
        string routeDecisionId,
        string sourceFile,
        long sourceSize,
        string sourceSha256,
        string engine,
        bool eligible,
        bool selected,
        string status,
        long outputRecords,
        CancellationToken cancellationToken
    )
    {
        var record = new
        {
            schemaVersion = 1,
            recordType = "engine-status",
            routeOrdinal = ordinal,
            routeDecisionId,
            sourceFile,
            sourceSize,
            sourceSha256,
            engine,
            eligible,
            selected,
            status,
            outputRecords,
        };
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(record, JsonOptions).AsMemory(),
            cancellationToken
        );
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

    private readonly record struct RoutedOutputKey(string SourceFile, string RouteDecisionId);

    private sealed record OcrTerminalAssessment(
        string Status,
        long StringRecords,
        long SourceSize,
        string SourceSha256,
        string? RouteDecisionId
    );

    private sealed class MutableCounts
    {
        private long _succeeded;
        private long _notApplicable;
        private long _disabledByUser;
        private long _outputRecords;

        internal void Add(string status, long outputRecords)
        {
            switch (status)
            {
                case "succeeded":
                    _succeeded++;
                    break;
                case "not-applicable":
                    _notApplicable++;
                    break;
                case "disabled-by-user":
                    _disabledByUser++;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported engine terminal status '{status}'.");
            }
            _outputRecords = checked(_outputRecords + outputRecords);
        }

        internal EngineTerminalCounts Freeze() =>
            new(_succeeded, _notApplicable, _disabledByUser, _outputRecords);
    }
}
