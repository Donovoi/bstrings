using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class EngineStatusCoreTests
{
    [Fact]
    public async Task WriteAsync_EmitsTerminalRowsForEveryEngineIncludingZeroOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var sources = new[]
        {
            scope.Write("selected.bin", "selected"),
            scope.Write("disabled.bin", "disabled"),
            scope.Write("ordinary.bin", "ordinary"),
        };
        var routingPath = scope.PathFor("content-routing.jsonl");
        var routes = await WriteRoutingAsync(
            routingPath,
            sources,
            [
                (["floss", "native", "ocr"], ["floss", "native", "ocr"]),
                (["floss", "native", "ocr"], ["native"]),
                (["native"], ["native"]),
            ],
            cancellationToken
        );
        var routingSha256 = await HashFileAsync(routingPath, cancellationToken);
        await File.WriteAllTextAsync(
            scope.PathFor("native-strings.jsonl"),
            string.Empty,
            cancellationToken
        );
        await File.WriteAllTextAsync(
            scope.PathFor("recovered-strings.jsonl"),
            string.Empty,
            cancellationToken
        );
        var selected = routes[0];
        await File.WriteAllTextAsync(
            scope.PathFor("ocr-assessments.jsonl"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    recordType = "ocr-assessment",
                    sourceFile = selected.SourceFile,
                    status = "not-applicable",
                    stringRecords = 0,
                    sourceSize = selected.SourceSize,
                    sourceSha256 = selected.SourceSha256,
                    routeDecisionId = selected.DecisionId,
                }
            ) + "\n",
            new UTF8Encoding(false),
            cancellationToken
        );

        var stats = await EngineStatusCore.WriteAsync(
            routingPath,
            routingSha256,
            sources.Length,
            scope.PathFor("native-strings.jsonl"),
            scope.PathFor("recovered-strings.jsonl"),
            scope.PathFor("ocr-assessments.jsonl"),
            scope.PathFor("engine-status.jsonl"),
            cancellationToken
        );

        Assert.Equal(9, stats.Records);
        Assert.Equal(new EngineTerminalCounts(3, 0, 0, 0), stats.Native);
        Assert.Equal(new EngineTerminalCounts(1, 1, 1, 0), stats.Floss);
        Assert.Equal(new EngineTerminalCounts(0, 2, 1, 0), stats.Ocr);
        Assert.Equal(64, stats.ManifestSha256.Length);
        var rows = (await File.ReadAllLinesAsync(
                scope.PathFor("engine-status.jsonl"),
                cancellationToken
            ))
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.All(rows, row => Assert.Equal("engine-status", row.RootElement.GetProperty("recordType").GetString()));
            Assert.All(rows, row => Assert.StartsWith("sha256:", row.RootElement.GetProperty("routeDecisionId").GetString()));
            var selectedFloss = rows.Single(
                row =>
                    row.RootElement.GetProperty("routeOrdinal").GetInt64() == 1
                    && row.RootElement.GetProperty("engine").GetString() == "floss"
            );
            Assert.Equal("succeeded", selectedFloss.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, selectedFloss.RootElement.GetProperty("outputRecords").GetInt64());
            var disabledOcr = rows.Single(
                row =>
                    row.RootElement.GetProperty("routeOrdinal").GetInt64() == 2
                    && row.RootElement.GetProperty("engine").GetString() == "ocr"
            );
            Assert.Equal("disabled-by-user", disabledOcr.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Dispose();
            }
        }
    }

    [Fact]
    public async Task WriteAsync_PreservesExistingLedgerWhenTerminalCoverageFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var source = scope.Write("selected.bin", "selected");
        var routingPath = scope.PathFor("content-routing.jsonl");
        await WriteRoutingAsync(
            routingPath,
            [source],
            [(["native", "ocr"], ["native", "ocr"])],
            cancellationToken
        );
        var routingSha256 = await HashFileAsync(routingPath, cancellationToken);
        foreach (var name in new[]
                 {
                     "native-strings.jsonl",
                     "recovered-strings.jsonl",
                     "ocr-assessments.jsonl",
                 })
        {
            await File.WriteAllTextAsync(scope.PathFor(name), string.Empty, cancellationToken);
        }
        var output = scope.PathFor("engine-status.jsonl");
        await File.WriteAllTextAsync(output, "preserved\n", cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EngineStatusCore.WriteAsync(
                routingPath,
                routingSha256,
                1,
                scope.PathFor("native-strings.jsonl"),
                scope.PathFor("recovered-strings.jsonl"),
                scope.PathFor("ocr-assessments.jsonl"),
                output,
                cancellationToken
            )
        );

        Assert.Contains("assessment is missing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserved\n", await File.ReadAllTextAsync(output, cancellationToken));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    [Fact]
    public async Task AcquireVerifiedFileLeaseAsync_DeniesConcurrentWriteOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var path = scope.Write("manifest.jsonl", "trusted\n");
        var sha256 = await HashFileAsync(path, cancellationToken);

        await using var lease = await ContentRoutingCore.AcquireVerifiedFileLeaseAsync(
            path,
            sha256,
            "test manifest",
            cancellationToken
        );

        Assert.Throws<IOException>(() => File.WriteAllText(path, "mutated\n"));
        Assert.Equal("trusted\n", await File.ReadAllTextAsync(path, cancellationToken));
    }

    private static async Task<RouteIdentity[]> WriteRoutingAsync(
        string path,
        IReadOnlyList<string> sources,
        IReadOnlyList<(string[] Eligible, string[] Scheduled)> routeSets,
        CancellationToken cancellationToken
    )
    {
        var rows = new List<string>();
        var identities = new List<RouteIdentity>();
        for (var index = 0; index < sources.Count; index++)
        {
            var source = Path.GetFullPath(sources[index]);
            var identity = new RouteIdentity(
                source,
                new FileInfo(source).Length,
                await HashFileAsync(source, cancellationToken),
                string.Empty
            );
            var rowWithoutDecision = new
            {
                schemaVersion = 1,
                recordType = "content-route",
                policyVersion = "content-routing-v1",
                ordinal = index + 1,
                sourceFile = identity.SourceFile,
                sourceSize = identity.SourceSize,
                sourceSha256 = identity.SourceSha256,
                classifier = new
                {
                    engine = "magika",
                    version = "1.1.0",
                    predictionMode = "default-thresholded-output-plus-raw-dl",
                    status = "ok",
                    score = 0.99,
                    output = new
                    {
                        label = "unknown",
                        mimeType = "application/octet-stream",
                        group = "unknown",
                        isText = false,
                    },
                    rawPrediction = new
                    {
                        label = "unknown",
                        mimeType = "application/octet-stream",
                        group = "unknown",
                        isText = false,
                    },
                    executable = source,
                    executableSha256 = identity.SourceSha256,
                },
                signals = Array.Empty<string>(),
                eligibleRoutes = routeSets[index].Eligible.Order(StringComparer.Ordinal).ToArray(),
                scheduledRoutes = routeSets[index].Scheduled.Order(StringComparer.Ordinal).ToArray(),
                conflicts = Array.Empty<string>(),
            };
            var material = JsonSerializer.SerializeToElement(rowWithoutDecision);
            var decisionId = ContentRoutingCore.ComputeDecisionId(material);
            var row = material.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal
            );
            row["decisionId"] = JsonSerializer.SerializeToElement(decisionId);
            rows.Add(JsonSerializer.Serialize(row));
            identities.Add(identity with { DecisionId = decisionId });
        }
        await File.WriteAllLinesAsync(path, rows, new UTF8Encoding(false), cancellationToken);
        return identities.ToArray();
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = File.OpenRead(path);
        return Convert
            .ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private readonly record struct RouteIdentity(
        string SourceFile,
        long SourceSize,
        string SourceSha256,
        string DecisionId
    );

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-engine-status-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string PathFor(string name) => Path.Combine(DirectoryPath, name);

        internal string Write(string name, string content)
        {
            var path = PathFor(name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
