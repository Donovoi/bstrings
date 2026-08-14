using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace bstrings.Tests;

public sealed class FlossCompletionCoreTests
{
    [Fact]
    public void ComputeRecordId_MatchesPythonCanonicalJsonGolden()
    {
        var record = new
        {
            schemaVersion = 1,
            recordType = "string",
            text = "static evidence",
            sourceFile = @"C:\synthetic\first.exe",
            location = new { kind = "file_offset", value = "0x10" },
            origin = new { extractor = "floss", version = "FLOSS 3.1.1", kind = "static" },
            attributes = new
            {
                magikaLabel = "pebin",
                magikaScore = 0.99,
                magikaMimeType = "application/vnd.microsoft.portable-executable",
                magikaGroup = "executable",
                flossLanguage = "go",
                flossImageBase = 5_368_709_120UL,
                routeDecisionId = "sha256:" + new string('1', 64),
                encoding = "ASCII",
            },
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record));

        Assert.Equal(
            "sha256:c32c42f5374ccc6fc569e56f83e776a8060c56de6845d69c3532e147abb38fe2",
            FlossCompletionCore.ComputeRecordId(document.RootElement)
        );
    }

    [Fact]
    public async Task ValidateAsync_AcceptsAllKindsAndReturnsStableStats()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateValidCaseAsync(scope, cancellationToken);

        var first = await ValidateAsync(completed, scope, cancellationToken);
        var second = await ValidateAsync(completed, scope, cancellationToken);

        var expected = new FlossCompletionStats(2, 2, 6, 1, 1, 1, 1, 1, 1);
        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(scope.DirectoryPath),
            path => Path.GetFileName(path).StartsWith(
                ".bstrings-provenance.",
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsDuplicateRecordIdentifiers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateValidCaseAsync(scope, cancellationToken);
        var lines = await File.ReadAllLinesAsync(completed.OutputPath, cancellationToken);
        await File.WriteAllLinesAsync(
            completed.OutputPath,
            [lines[0], lines[0]],
            cancellationToken
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(completed, scope, cancellationToken)
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsDuplicateJsonProperties()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateValidCaseAsync(scope, cancellationToken);
        var lines = await File.ReadAllLinesAsync(completed.OutputPath, cancellationToken);
        lines[0] = lines[0].Replace(
            "\"recordType\":\"string\"",
            "\"recordType\":\"string\",\"recordType\":\"string\"",
            StringComparison.Ordinal
        );
        await File.WriteAllLinesAsync(completed.OutputPath, lines, cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(completed, scope, cancellationToken)
        );
        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsUnprojectedSourceAndWrongRouteDecision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateValidCaseAsync(scope, cancellationToken);
        var lines = await File.ReadAllLinesAsync(completed.OutputPath, cancellationToken);

        lines[0] = MutateAndReidentify(
            lines[0],
            root => root["sourceFile"] = scope.PathFor("not-projected.bin")
        );
        await File.WriteAllLinesAsync(completed.OutputPath, lines, cancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(completed, scope, cancellationToken)
        );

        completed = await CreateValidCaseAsync(scope, cancellationToken, replaceExisting: true);
        lines = await File.ReadAllLinesAsync(completed.OutputPath, cancellationToken);
        lines[0] = MutateAndReidentify(
            lines[0],
            root =>
                root["attributes"]!["routeDecisionId"] =
                    "sha256:" + new string('0', 64)
        );
        await File.WriteAllLinesAsync(completed.OutputPath, lines, cancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(completed, scope, cancellationToken)
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsMagikaAttributesThatDoNotMatchRouting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateValidCaseAsync(scope, cancellationToken);
        var lines = await File.ReadAllLinesAsync(completed.OutputPath, cancellationToken);
        lines[0] = MutateAndReidentify(
            lines[0],
            root => root["attributes"]!["magikaLabel"] = "pdf"
        );
        await File.WriteAllLinesAsync(completed.OutputPath, lines, cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(completed, scope, cancellationToken)
        );
    }

    [Theory]
    [InlineData("extractor")]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("location")]
    [InlineData("encoding")]
    [InlineData("extra-attribute")]
    [InlineData("missing-attribute")]
    public async Task ValidateAsync_RejectsInvalidProvenanceOrAttributes(string mutation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateValidCaseAsync(scope, cancellationToken);
        var lines = await File.ReadAllLinesAsync(completed.OutputPath, cancellationToken);
        var stackIndex = 3;
        lines[stackIndex] = MutateAndReidentify(
            lines[stackIndex],
            root =>
            {
                switch (mutation)
                {
                    case "extractor":
                        root["origin"]!["extractor"] = "other";
                        break;
                    case "version":
                        root["origin"]!["version"] = "other-version";
                        break;
                    case "kind":
                        root["origin"]!["kind"] = "future";
                        break;
                    case "location":
                        root["location"]!["kind"] = "file_offset";
                        break;
                    case "encoding":
                        root["attributes"]!["encoding"] = "UTF-32";
                        break;
                    case "extra-attribute":
                        root["attributes"]!["unexpected"] = 1;
                        break;
                    case "missing-attribute":
                        root["attributes"]!.AsObject().Remove("function");
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
        );
        await File.WriteAllLinesAsync(completed.OutputPath, lines, cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(completed, scope, cancellationToken)
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsSameLengthRecordIdTamper()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateValidCaseAsync(scope, cancellationToken);
        var lines = await File.ReadAllLinesAsync(completed.OutputPath, cancellationToken);
        using var document = JsonDocument.Parse(lines[0]);
        var recordId = document.RootElement.GetProperty("recordId").GetString()!;
        var replacement = recordId[..^1] + (recordId[^1] == '0' ? "1" : "0");
        lines[0] = lines[0].Replace(recordId, replacement, StringComparison.Ordinal);
        await File.WriteAllLinesAsync(completed.OutputPath, lines, cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(completed, scope, cancellationToken)
        );
        Assert.Contains("record-ID", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsProjectionThatWasNotScheduledForFloss()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateValidCaseAsync(scope, cancellationToken);
        var routes = await File.ReadAllLinesAsync(completed.RoutingPath, cancellationToken);
        var first = JsonNode.Parse(routes[0])!.AsObject();
        first["scheduledRoutes"] = new JsonArray("native");
        first.Remove("decisionId");
        using (var document = JsonDocument.Parse(first.ToJsonString()))
        {
            first["decisionId"] = ContentRoutingCore.ComputeDecisionId(document.RootElement);
        }
        routes[0] = first.ToJsonString();
        await File.WriteAllLinesAsync(completed.RoutingPath, routes, cancellationToken);
        completed = completed with
        {
            RoutingSha256 = await HashFileAsync(completed.RoutingPath, cancellationToken),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(completed, scope, cancellationToken)
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsUnterminatedOutputAndHonorsCancellation()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateValidCaseAsync(scope, testCancellationToken);
        var lines = await File.ReadAllLinesAsync(completed.OutputPath, testCancellationToken);
        await File.WriteAllTextAsync(completed.OutputPath, lines[0], testCancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(completed, scope, testCancellationToken)
        );

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ValidateAsync(completed, scope, cancelled.Token)
        );
    }

    private static Task<FlossCompletionStats> ValidateAsync(
        CompletedCase completed,
        TemporaryDirectory scope,
        CancellationToken cancellationToken
    ) =>
        FlossCompletionCore.ValidateAsync(
            completed.OutputPath,
            completed.InventoryPath,
            completed.ManifestPath,
            completed.ProjectionIdentity,
            completed.RoutingPath,
            completed.RoutingSha256,
            scope.DirectoryPath,
            cancellationToken
        );

    private static async Task<CompletedCase> CreateValidCaseAsync(
        TemporaryDirectory scope,
        CancellationToken cancellationToken,
        bool replaceExisting = false
    )
    {
        var sourceOne = scope.PathFor("first.exe");
        var sourceTwo = scope.PathFor("second.exe");
        var inventory = scope.PathFor("floss-input-files.txt");
        var manifest = scope.PathFor("floss-input-manifest.jsonl");
        var routing = scope.PathFor("content-routing.jsonl");
        var output = scope.PathFor("recovered-strings.jsonl");
        if (replaceExisting)
        {
            foreach (var path in new[] { sourceOne, sourceTwo, inventory, manifest, routing, output })
            {
                File.Delete(path);
            }
        }
        await File.WriteAllBytesAsync(sourceOne, [1, 2, 3, 4], cancellationToken);
        await File.WriteAllBytesAsync(sourceTwo, [5, 6, 7, 8, 9], cancellationToken);
        var projection = await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            [sourceOne, sourceTwo],
            cancellationToken
        );

        var routeRows = new List<string>();
        var decisionIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new[] { sourceOne, sourceTwo };
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index];
            var identity = await FileIdentityAsync(source, cancellationToken);
            var row = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["recordType"] = "content-route",
                ["policyVersion"] = "content-routing-v1",
                ["ordinal"] = index + 1,
                ["sourceFile"] = source,
                ["sourceSize"] = identity.Length,
                ["sourceSha256"] = identity.Sha256,
                ["classifier"] = new Dictionary<string, object?>
                {
                    ["score"] = 0.99,
                    ["output"] = new Dictionary<string, object?>
                    {
                        ["label"] = "pebin",
                        ["mimeType"] = "application/vnd.microsoft.portable-executable",
                        ["group"] = "executable",
                        ["isText"] = false,
                    },
                },
                ["signals"] = Array.Empty<string>(),
                ["eligibleRoutes"] = new[] { "floss", "native" },
                ["scheduledRoutes"] = new[] { "floss", "native" },
                ["conflicts"] = Array.Empty<string>(),
            };
            using (var material = JsonDocument.Parse(JsonSerializer.Serialize(row)))
            {
                row["decisionId"] = ContentRoutingCore.ComputeDecisionId(material.RootElement);
            }
            decisionIds[source] = (string)row["decisionId"]!;
            routeRows.Add(JsonSerializer.Serialize(row));
        }
        await File.WriteAllLinesAsync(routing, routeRows, cancellationToken);
        var routingSha256 = await HashFileAsync(routing, cancellationToken);

        var records = new[]
        {
            CreateRecord(sourceOne, decisionIds[sourceOne], "static", "static evidence", 0x10),
            CreateRecord(sourceOne, decisionIds[sourceOne], "language", "language evidence", 0x20),
            CreateRecord(
                sourceOne,
                decisionIds[sourceOne],
                "language-missed",
                "missed evidence",
                0x30
            ),
            CreateRecord(sourceOne, decisionIds[sourceOne], "stack", "stack evidence", 0x40),
            CreateRecord(sourceTwo, decisionIds[sourceTwo], "tight", "tight evidence", 0x50),
            CreateRecord(sourceTwo, decisionIds[sourceTwo], "decoded", "decoded evidence", 0x60),
        };
        await File.WriteAllLinesAsync(output, records, cancellationToken);
        return new CompletedCase(
            output,
            inventory,
            manifest,
            projection,
            routing,
            routingSha256
        );
    }

    private static string CreateRecord(
        string sourceFile,
        string routeDecisionId,
        string kind,
        string text,
        ulong location
    )
    {
        var locationKind = kind switch
        {
            "static" or "language" or "language-missed" => "file_offset",
            "stack" or "tight" => "program_counter",
            _ => "virtual_address",
        };
        var attributes = new Dictionary<string, object?>
        {
            ["magikaLabel"] = "pebin",
            ["magikaScore"] = 0.99,
            ["magikaMimeType"] = "application/vnd.microsoft.portable-executable",
            ["magikaGroup"] = "executable",
            ["flossLanguage"] = "go",
            ["flossImageBase"] = 0x140000000UL,
            ["routeDecisionId"] = routeDecisionId,
            ["encoding"] = "ASCII",
        };
        if (kind is "stack" or "tight")
        {
            attributes["function"] = 0x140001000UL;
            attributes["stackPointer"] = 0x1000UL;
            attributes["originalStackPointer"] = 0x1100UL;
            attributes["stackOffset"] = -16L;
            attributes["frameOffset"] = 8L;
        }
        else if (kind == "decoded")
        {
            attributes["addressType"] = "HEAP";
            attributes["decodedAt"] = 0x140002000UL;
            attributes["decodingRoutine"] = 0x140003000UL;
        }
        var record = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["recordType"] = "string",
            ["text"] = text,
            ["sourceFile"] = sourceFile,
            ["location"] = new Dictionary<string, object?>
            {
                ["kind"] = locationKind,
                ["value"] = $"0x{location:X}",
            },
            ["origin"] = new Dictionary<string, object?>
            {
                ["extractor"] = "floss",
                ["version"] = "FLOSS 3.1.1",
                ["kind"] = kind,
            },
            ["attributes"] = attributes,
        };
        using (var material = JsonDocument.Parse(JsonSerializer.Serialize(record)))
        {
            record["recordId"] = FlossCompletionCore.ComputeRecordId(material.RootElement);
        }
        return JsonSerializer.Serialize(record);
    }

    private static string MutateAndReidentify(string json, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutation(root);
        root.Remove("recordId");
        using (var material = JsonDocument.Parse(root.ToJsonString()))
        {
            root["recordId"] = FlossCompletionCore.ComputeRecordId(material.RootElement);
        }
        return root.ToJsonString();
    }

    private static async Task<(long Length, string Sha256)> FileIdentityAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return (stream.Length, Convert.ToHexString(digest).ToLowerInvariant());
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert
            .ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private sealed record CompletedCase(
        string OutputPath,
        string InventoryPath,
        string ManifestPath,
        InputManifestInfo ProjectionIdentity,
        string RoutingPath,
        string RoutingSha256
    );

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-floss-completion-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string PathFor(string name) => Path.Combine(DirectoryPath, name);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
