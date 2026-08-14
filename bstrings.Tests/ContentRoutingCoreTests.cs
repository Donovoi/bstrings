using System.Text;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class ContentRoutingCoreTests
{
    [Fact]
    public async Task ValidateAndProjectAsync_CreatesExactOrderedSpecialistSubsets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var text = scope.PathFor("first.txt");
        var image = scope.PathFor("second.bin");
        var executable = scope.PathFor("third.bin");
        await File.WriteAllTextAsync(text, "evidence", cancellationToken);
        await File.WriteAllBytesAsync(image, [0x89, 0x50, 0x4e, 0x47], cancellationToken);
        await File.WriteAllBytesAsync(executable, [0x4d, 0x5a, 0, 0], cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [text, image, executable],
            [["native"], ["native", "ocr"], ["floss", "native"]],
            cancellationToken
        );

        var stats = await ValidateAsync(scope, fixture.Info, fixture.Manifest, fixture.Routing, cancellationToken);

        Assert.Equal(3, stats.InputFiles);
        Assert.Equal(1, stats.FlossCandidates);
        Assert.Equal(1, stats.OcrCandidates);
        Assert.Equal("content-routing-v1", stats.PolicyVersion);
        Assert.Equal(64, stats.ManifestSha256.Length);
        Assert.Equal(
            [Path.GetFullPath(executable)],
            await File.ReadAllLinesAsync(scope.PathFor("floss-input-files.txt"), cancellationToken)
        );
        Assert.Equal(
            [Path.GetFullPath(image)],
            await File.ReadAllLinesAsync(scope.PathFor("ocr-input-files.txt"), cancellationToken)
        );
        await InputEvidenceManifest.VerifyInventoryAsync(
            scope.PathFor("floss-input-files.txt"),
            stats.FlossInput,
            cancellationToken
        );
        await InputEvidenceManifest.VerifyAsync(
            scope.PathFor("floss-input-manifest.jsonl"),
            stats.FlossInput,
            cancellationToken
        );
    }

    [Fact]
    public async Task ValidateAndProjectAsync_AllowsAuditableZeroCandidateProjections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var text = scope.PathFor("evidence.txt");
        await File.WriteAllTextAsync(text, "plain text", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [text],
            [["native"]],
            cancellationToken
        );

        var stats = await ValidateAsync(scope, fixture.Info, fixture.Manifest, fixture.Routing, cancellationToken);

        Assert.Equal(0, stats.FlossCandidates);
        Assert.Equal(0, stats.OcrCandidates);
        Assert.Empty(await File.ReadAllTextAsync(scope.PathFor("floss-input-files.txt"), cancellationToken));
        Assert.Empty(await File.ReadAllTextAsync(scope.PathFor("ocr-input-manifest.jsonl"), cancellationToken));
        await InputEvidenceManifest.VerifyInventoryAsync(
            scope.PathFor("ocr-input-files.txt"),
            stats.OcrInput,
            cancellationToken
        );
    }

    [Fact]
    public async Task ValidateAndProjectAsync_ReadOnlyReuseAcceptsLockedProjections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var text = scope.PathFor("evidence.txt");
        await File.WriteAllTextAsync(text, "plain text", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [text],
            [["floss", "native"]],
            cancellationToken
        );
        var expected = await ValidateAsync(
            scope,
            fixture.Info,
            fixture.Manifest,
            fixture.Routing,
            cancellationToken
        );

        var leases = new[]
        {
            new FileStream(
                scope.PathFor("floss-input-files.txt"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            ),
            new FileStream(
                scope.PathFor("floss-input-manifest.jsonl"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            ),
            new FileStream(
                scope.PathFor("ocr-input-files.txt"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            ),
            new FileStream(
                scope.PathFor("ocr-input-manifest.jsonl"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            ),
        };
        try
        {
            var actual = await ContentRoutingCore.ValidateAndProjectAsync(
                fixture.Manifest,
                fixture.Info,
                fixture.Routing,
                scope.PathFor("floss-input-files.txt"),
                scope.PathFor("floss-input-manifest.jsonl"),
                scope.PathFor("ocr-input-files.txt"),
                scope.PathFor("ocr-input-manifest.jsonl"),
                expectedNativeSelected: true,
                cancellationToken,
                writeProjections: false
            );
            Assert.Equal(expected, actual);
        }
        finally
        {
            foreach (var lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ValidateAndProjectAsync_RejectsNativeSuppression()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        await File.WriteAllTextAsync(evidence, "evidence", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [evidence],
            [["ocr"]],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, fixture.Info, fixture.Manifest, fixture.Routing, cancellationToken)
        );

        Assert.Contains("expected native selection", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAndProjectAsync_AcceptsNativeOffPolicyAndProjectsSpecialists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        await File.WriteAllTextAsync(evidence, "evidence", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [evidence],
            [["ocr"]],
            cancellationToken,
            schemaVersion: 2,
            policyVersion: "content-routing-v2"
        );

        var stats = await ValidateAsync(
            scope,
            fixture.Info,
            fixture.Manifest,
            fixture.Routing,
            cancellationToken,
            expectedNativeSelected: false
        );

        Assert.Equal("content-routing-v2", stats.PolicyVersion);
        Assert.Equal(0, stats.FlossCandidates);
        Assert.Equal(1, stats.OcrCandidates);
    }

    [Theory]
    [InlineData(1, "content-routing-v2")]
    [InlineData(2, "content-routing-v1")]
    public async Task ValidateAndProjectAsync_RejectsMismatchedNativeOffSchemaPolicyPair(
        int schemaVersion,
        string policyVersion
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        await File.WriteAllTextAsync(evidence, "evidence", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [evidence],
            [["ocr"]],
            cancellationToken,
            schemaVersion: schemaVersion,
            policyVersion: policyVersion
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(
                scope,
                fixture.Info,
                fixture.Manifest,
                fixture.Routing,
                cancellationToken,
                expectedNativeSelected: false
            )
        );
    }

    [Fact]
    public async Task ValidateAndProjectAsync_RejectsChangedSourceIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        await File.WriteAllTextAsync(evidence, "evidence", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [evidence],
            [["native"]],
            cancellationToken,
            sourceSha256Override: new string('0', 64)
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, fixture.Info, fixture.Manifest, fixture.Routing, cancellationToken)
        );

        Assert.Contains("trusted source identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAndProjectAsync_RejectsTamperedDecisionIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        await File.WriteAllTextAsync(evidence, "evidence", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [evidence],
            [["native"]],
            cancellationToken
        );
        var line = await File.ReadAllTextAsync(fixture.Routing, cancellationToken);
        using var document = JsonDocument.Parse(line);
        var row = document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal
        );
        row["decisionId"] = JsonSerializer.SerializeToElement("sha256:" + new string('0', 64));
        await File.WriteAllTextAsync(
            fixture.Routing,
            JsonSerializer.Serialize(row) + "\n",
            new UTF8Encoding(false),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, fixture.Info, fixture.Manifest, fixture.Routing, cancellationToken)
        );

        Assert.Contains("decision identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAndProjectAsync_RejectsDuplicateAndUnknownProperties()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        await File.WriteAllTextAsync(evidence, "evidence", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [evidence],
            [["native"]],
            cancellationToken
        );
        var line = await File.ReadAllTextAsync(fixture.Routing, cancellationToken);
        line = line.TrimEnd().TrimEnd('}') + ",\"ordinal\":1}\n";
        await File.WriteAllTextAsync(fixture.Routing, line, new UTF8Encoding(false), cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, fixture.Info, fixture.Manifest, fixture.Routing, cancellationToken)
        );

        Assert.Contains("duplicate property", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAndProjectAsync_RejectsMissingOrOutOfOrderCoverage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var first = scope.PathFor("first.bin");
        var second = scope.PathFor("second.bin");
        await File.WriteAllTextAsync(first, "first", cancellationToken);
        await File.WriteAllTextAsync(second, "second", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [first, second],
            [["native"], ["native"]],
            cancellationToken
        );
        var lines = await File.ReadAllLinesAsync(fixture.Routing, cancellationToken);
        await File.WriteAllLinesAsync(fixture.Routing, [lines[1], lines[0]], cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, fixture.Info, fixture.Manifest, fixture.Routing, cancellationToken)
        );

        Assert.Contains("trusted input order", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAndProjectAsync_RejectsClassifierOutsideVerifiedToolchain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var evidence = scope.PathFor("evidence.bin");
        var verifiedClassifier = scope.PathFor("verified-magika.exe");
        await File.WriteAllTextAsync(evidence, "evidence", cancellationToken);
        await File.WriteAllTextAsync(verifiedClassifier, "verified classifier", cancellationToken);
        var fixture = await CreateFixtureAsync(
            scope,
            [evidence],
            [["native"]],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ContentRoutingCore.ValidateAndProjectAsync(
                fixture.Manifest,
                fixture.Info,
                fixture.Routing,
                scope.PathFor("floss-input-files.txt"),
                scope.PathFor("floss-input-manifest.jsonl"),
                scope.PathFor("ocr-input-files.txt"),
                scope.PathFor("ocr-input-manifest.jsonl"),
                expectedNativeSelected: true,
                cancellationToken: cancellationToken,
                expectedClassifierExecutable: verifiedClassifier
            )
        );

        Assert.Contains("verified Magika toolchain", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<ContentRoutingStats> ValidateAsync(
        TemporaryDirectory scope,
        InputManifestInfo info,
        string manifest,
        string routing,
        CancellationToken cancellationToken,
        bool expectedNativeSelected = true
    ) =>
        ContentRoutingCore.ValidateAndProjectAsync(
            manifest,
            info,
            routing,
            scope.PathFor("floss-input-files.txt"),
            scope.PathFor("floss-input-manifest.jsonl"),
            scope.PathFor("ocr-input-files.txt"),
            scope.PathFor("ocr-input-manifest.jsonl"),
            expectedNativeSelected,
            cancellationToken
        );

    private static async Task<(InputManifestInfo Info, string Manifest, string Routing)> CreateFixtureAsync(
        TemporaryDirectory scope,
        IReadOnlyList<string> files,
        IReadOnlyList<string[]> scheduledRoutes,
        CancellationToken cancellationToken,
        string? sourceSha256Override = null,
        int schemaVersion = 1,
        string policyVersion = "content-routing-v1"
    )
    {
        var inventory = scope.PathFor("input-files.txt");
        var manifest = scope.PathFor("input-manifest.jsonl");
        var routing = scope.PathFor("content-routing.jsonl");
        var info = await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            files,
            cancellationToken
        );
        var identities = (await File.ReadAllLinesAsync(manifest, cancellationToken))
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            var rows = new List<string>();
            for (var index = 0; index < files.Count; index++)
            {
                var identity = identities[index].RootElement;
                var routes = scheduledRoutes[index].Order(StringComparer.Ordinal).ToArray();
                var eligibleRoutes = routes.Append("native").Distinct().Order(StringComparer.Ordinal).ToArray();
                var rowWithoutDecisionId = new
                {
                    schemaVersion,
                    recordType = "content-route",
                    policyVersion,
                    ordinal = index + 1,
                    sourceFile = identity.GetProperty("path").GetString(),
                    sourceSize = identity.GetProperty("length").GetInt64(),
                    sourceSha256 = sourceSha256Override
                        ?? identity.GetProperty("sha256").GetString(),
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
                        executable = Path.GetFullPath(files[0]),
                        executableSha256 = identity.GetProperty("sha256").GetString(),
                    },
                    signals = Array.Empty<string>(),
                    eligibleRoutes,
                    scheduledRoutes = routes,
                    conflicts = Array.Empty<string>(),
                };
                var material = JsonSerializer.SerializeToElement(rowWithoutDecisionId);
                var decisionId = ContentRoutingCore.ComputeDecisionId(material);
                var row = material.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.Ordinal
                );
                row["decisionId"] = JsonSerializer.SerializeToElement(decisionId);
                rows.Add(JsonSerializer.Serialize(row));
            }
            await File.WriteAllLinesAsync(routing, rows, new UTF8Encoding(false), cancellationToken);
        }
        finally
        {
            foreach (var identity in identities)
            {
                identity.Dispose();
            }
        }
        return (info, manifest, routing);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-content-routing-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string PathFor(string name) => Path.Combine(DirectoryPath, name);

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
