using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bstrings;

if (args.Length == 1 && args[0] is "--help" or "-h")
{
    PrintHelp();
    return;
}

var arguments = ParseArguments(args);
var experiment = ParseExperiment(arguments.GetValueOrDefault("experiment", "reuse"));
var routingCorpusSpecified = arguments.TryGetValue("routing-corpus", out var routingCorpusValue);
if (experiment == BenchmarkExperiment.Reuse && routingCorpusSpecified)
{
    throw new ArgumentException(
        "--routing-corpus applies only to --experiment routing-shadow; reuse keeps its historical fixture.",
        "routing-corpus"
    );
}
var routingCorpus = experiment == BenchmarkExperiment.RoutingShadow
    ? ParseRoutingCorpus(routingCorpusValue ?? "mixed")
    : RoutingCorpus.Mixed;
var recordCount = ParsePositiveInt(arguments.GetValueOrDefault("records", "100000"), "records");
var duplicatePercent = ParsePercentage(
    arguments.GetValueOrDefault("duplicate-percent", "50"),
    "duplicate-percent"
);
var rounds = ParsePositiveInt(arguments.GetValueOrDefault("rounds", "7"), "rounds");
if (routingCorpus == RoutingCorpus.Machine && duplicatePercent != 0)
{
    throw new ArgumentException(
        "--routing-corpus machine requires --duplicate-percent 0 so every machine record is unique.",
        "duplicate-percent"
    );
}
if (
    !LanguageDetectionCore.TryParseMode(
        arguments.GetValueOrDefault("mode", "accurate"),
        out var detectionMode,
        out var modeError
    )
)
{
    throw new ArgumentException(modeError, "mode");
}

var outputPath = Path.GetFullPath(
    arguments.GetValueOrDefault("output", "language-triage-benchmark.csv")
);
if (File.Exists(outputPath))
{
    throw new IOException($"Refusing to overwrite {outputPath}.");
}
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

if (!LanguageDetectionCore.TryVerifyAvailability(out var availabilityError))
{
    throw new InvalidOperationException(availabilityError);
}

var workRoot = Path.Combine(
    Path.GetTempPath(),
    "bstrings-language-triage-benchmark-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(workRoot);
var inputPath = Path.Combine(workRoot, "synthetic-records.jsonl");
var benchmarkRows = new List<BenchmarkRow>(rounds * 2);
var completed = false;

try
{
    await WriteFixtureAsync(inputPath, recordCount, duplicatePercent, experiment, routingCorpus);
    WarmDetector(detectionMode);

    for (var round = 1; round <= rounds; round++)
    {
        var variants = GetVariants(experiment, round);
        var pair = new List<BenchmarkRow>(2);

        for (var order = 0; order < variants.Length; order++)
        {
            var variant = variants[order];
            var prefix = $"round-{round:D2}-{VariantName(variant)}";
            var candidatesPath = Path.Combine(workRoot, prefix + "-candidates.jsonl");
            var assessmentsPath = Path.Combine(workRoot, prefix + "-assessments.jsonl");

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var gen0Before = GC.CollectionCount(0);
            var gen1Before = GC.CollectionCount(1);
            var gen2Before = GC.CollectionCount(2);
            var workingSetBefore = process.WorkingSet64;
            var stopwatch = Stopwatch.StartNew();

            var includeTranslationRouting = variant == BenchmarkVariant.RoutingShadow;
            var reuseSuccessfulDetections = experiment == BenchmarkExperiment.RoutingShadow
                || variant == BenchmarkVariant.Reuse;

            Task<LanguageTriageStats> RunTriage() =>
                LanguageTriageCore.ProcessAsync(
                    inputPath,
                    candidatesPath,
                    assessmentsPath,
                    new LanguageTriageOptions(
                        TargetLanguage: "en",
                        DetectionMode: detectionMode,
                        Policy: LanguageTriagePolicy.HighRecall,
                        MinimumConfidence: 0.65,
                        MinimumTargetMargin: 0.08,
                        MinimumCharacters: 8,
                        MaximumCharacters: 2048,
                        BatchSize: 2048,
                        MaxDegreeOfParallelism: Math.Max(1, Environment.ProcessorCount)
                    ),
                    detector: null,
                    reuseSuccessfulDetections: reuseSuccessfulDetections,
                    includeTranslationRouting: includeTranslationRouting
                );
            var measuredRun = experiment == BenchmarkExperiment.RoutingShadow
                ? await ObservePeakWorkingSetAsync(RunTriage, process, workingSetBefore)
                : new MeasuredTriageRun(
                    await RunTriage(),
                    Math.Max(workingSetBefore, process.WorkingSet64)
                );
            var stats = measuredRun.Stats;

            stopwatch.Stop();
            process.Refresh();
            var assessmentProjection = InspectAssessments(
                assessmentsPath,
                expectTranslationRouting: includeTranslationRouting
            );
            var row = new BenchmarkRow(
                ExperimentName(experiment),
                RoutingCorpusName(routingCorpus),
                recordCount,
                duplicatePercent,
                detectionMode.ToString().ToLowerInvariant(),
                VariantName(variant),
                round,
                order,
                stopwatch.Elapsed.TotalSeconds,
                (process.TotalProcessorTime - cpuBefore).TotalSeconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                GC.CollectionCount(0) - gen0Before,
                GC.CollectionCount(1) - gen1Before,
                GC.CollectionCount(2) - gen2Before,
                workingSetBefore,
                process.WorkingSet64,
                measuredRun.SampledPeakWorkingSetBytes,
                stats,
                new FileInfo(candidatesPath).Length,
                new FileInfo(assessmentsPath).Length,
                GetSha256(candidatesPath),
                GetSha256(assessmentsPath),
                assessmentProjection.ProjectedBytes,
                assessmentProjection.ProjectedSha256,
                assessmentProjection.RecordCount,
                assessmentProjection.RoutingObjectCount,
                0,
                assessmentsPath
            );
            pair.Add(row);
        }

        if (experiment == BenchmarkExperiment.Reuse)
        {
            AssertReusePairExact(pair, recordCount);
        }
        else
        {
            AssertRoutingPairExact(pair, recordCount);
            var disabled = pair.Single(row => row.Variant == "routing-disabled");
            var shadow = pair.Single(row => row.Variant == "routing-shadow");
            var delta = shadow.AssessmentsBytes - disabled.AssessmentsBytes;
            pair = pair
                .Select(row => row with
                {
                    AssessmentBytesDeltaFromDisabled = row.Variant == "routing-shadow"
                        ? delta
                        : 0,
                })
                .ToList();
        }
        benchmarkRows.AddRange(pair);
    }

    await WriteCsvAsync(outputPath, benchmarkRows, experiment);
    WriteSummary(benchmarkRows, outputPath, experiment);
    AssertRoutingShadowScaleGate(benchmarkRows, recordCount, rounds, experiment);
    completed = true;
}
finally
{
    if (completed && Directory.Exists(workRoot))
    {
        Directory.Delete(workRoot, recursive: true);
    }
    else if (Directory.Exists(workRoot))
    {
        Console.Error.WriteLine($"Failed benchmark retained for diagnosis: {workRoot}");
    }
}

static void WarmDetector(LanguageDetectionMode mode)
{
    foreach (
        var text in new[]
        {
            "This deterministic English benchmark sentence warms the bundled language detector before measurement.",
            "Esta frase espanola determinista prepara el detector de idioma incluido antes de la medicion.",
        }
    )
    {
        if (!LanguageDetectionCore.TryDetect(text, mode, "en", out _, out var error))
        {
            throw new InvalidOperationException($"Language-detector warmup failed: {error}");
        }
    }
}

static async Task WriteFixtureAsync(
    string path,
    int records,
    double duplicatePercent,
    BenchmarkExperiment experiment,
    RoutingCorpus routingCorpus
)
{
    await using var stream = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.Read,
        1024 * 1024,
        FileOptions.SequentialScan
    );
    await using var writer = new StreamWriter(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        1024 * 1024
    );
    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    const int batchSize = 2048;

    for (var batchStart = 0; batchStart < records; batchStart += batchSize)
    {
        var batchCount = Math.Min(batchSize, records - batchStart);
        var duplicateCount = (int)Math.Round(
            batchCount * duplicatePercent / 100,
            MidpointRounding.AwayFromZero
        );
        duplicateCount = Math.Clamp(duplicateCount, 0, Math.Max(0, batchCount - 1));
        var uniqueCount = batchCount - duplicateCount;
        var duplicatePool = Math.Max(1, Math.Min(uniqueCount, 16));

        for (var localIndex = 0; localIndex < batchCount; localIndex++)
        {
            var recordIndex = batchStart + localIndex;
            var textIdentity = localIndex < uniqueCount
                ? recordIndex
                : batchStart + ((localIndex - uniqueCount) % duplicatePool);
            var fixture = experiment == BenchmarkExperiment.RoutingShadow
                ? CreateRoutingFixture(textIdentity, routingCorpus)
                : SyntheticRoutingFixture.Default(CreateText(textIdentity));
            var record = new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId = $"synthetic-language-{recordIndex:D10}",
                text = fixture.Text,
                sourceFile = "synthetic-language-benchmark.jsonl",
                location = new
                {
                    kind = "file-offset",
                    value = recordIndex.ToString(CultureInfo.InvariantCulture),
                },
                origin = new
                {
                    extractor = fixture.OriginExtractor,
                    kind = fixture.OriginKind,
                },
                attributes = fixture.Attributes,
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, jsonOptions));
        }
    }
}

static string CreateText(int identity) =>
    (identity & 1) == 0
        ? $"This is deterministic English forensic benchmark sentence {identity:D10}. It contains enough natural language words to exercise accurate and fast classification while retaining a unique synthetic identity."
        : $"Esta es la frase forense espanola determinista {identity:D10}. Contiene suficientes palabras de lenguaje natural para ejercitar la clasificacion precisa y rapida con una identidad sintetica unica.";

static SyntheticRoutingFixture CreateRoutingFixture(int identity, RoutingCorpus corpus) =>
    corpus switch
    {
        RoutingCorpus.Natural => SyntheticRoutingFixture.Default(CreateNaturalText(identity)),
        RoutingCorpus.Machine => SyntheticRoutingFixture.Default(CreateMachineText(identity)),
        RoutingCorpus.Short => SyntheticRoutingFixture.Default(CreateShortText(identity)),
        RoutingCorpus.Max => SyntheticRoutingFixture.Default(CreateMaxText(identity)),
        RoutingCorpus.Encoded => SyntheticRoutingFixture.Default(CreateEncodedText(identity)),
        RoutingCorpus.Provenance => CreateProvenanceFixture(identity),
        _ => CreateMixedFixture(identity),
    };

static string CreateNaturalText(int identity) =>
    (identity % 8) switch
    {
        0 => $"This synthetic English sentence has natural language and identity {identity:D10}.",
        1 => $"Esta frase sintetica en espanol contiene lenguaje natural e identidad {identity:D10}.",
        2 => $"Cette phrase francaise synthetique contient un langage naturel et l'identite {identity:D10}.",
        3 => $"Dieser synthetische deutsche Satz enthaelt natuerliche Sprache und Kennung {identity:D10}.",
        4 => $"Это синтетическое русское предложение содержит естественный язык и номер {identity:D10}.",
        5 => $"هذه جملة عربية اصطناعية تحتوي على لغة طبيعية ورقم {identity:D10}.",
        6 => $"これは自然言語と識別番号 {identity:D10} を含む合成日本語文です。",
        _ => $"यह कृत्रिम हिंदी वाक्य प्राकृतिक भाषा और पहचान {identity:D10} रखता है।",
    };

static string CreateMachineText(int identity) =>
    (identity & 7) switch
    {
        0 => $"00000000-0000-4000-8000-{identity:x12}",
        1 => $"00000000-0000-4000-8000-{identity:x12}x",
        2 => "sha256:" + FixedHex(identity, 64),
        3 => "sha256:" + FixedHex(identity, 63) + "g",
        4 => CreateIpAddress(identity),
        5 => $"mov rax,[rbx+0x{identity:x8}];call synthetic_{identity:x8}",
        6 => $"HKLM\\Software\\Synthetic\\Component{identity:D10}",
        _ => $"Q7$z_{identity:x8}_9F!v_{identity:D10}_machine_noise",
    };

static string CreateShortText(int identity) =>
    (identity & 3) switch
    {
        0 => $"Hola {identity}",
        1 => $"Привет {identity}",
        2 => $"x_{identity:x}",
        _ => CreateIpAddress(identity),
    };

static string CreateMaxText(int identity)
{
    var seed = (identity & 1) == 0
        ? "This is bounded synthetic natural language near the maximum accepted record length. "
        : $"config_{identity:x8}=value; mensaje sintetico mezclado con estructura y lenguaje natural. ";
    return FitToLength(seed, $" [{identity:D10}]", 2048);
}

static string CreateEncodedText(int identity) =>
    (identity & 3) switch
    {
        0 => Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"synthetic encoded payload {identity:D10} 0123456789")
        ),
        1 => CreateJwt(identity),
        2 => "0x" + FixedHex(identity, 64),
        _ => "sha256:" + FixedHex(identity, 64),
    };

static SyntheticRoutingFixture CreateProvenanceFixture(int identity)
{
    var variant = identity & 3;
    var text = variant switch
    {
        0 => CreateNaturalText(identity),
        1 => $"00000000-0000-4000-8000-{identity:x12}",
        2 => $"synthetic_code_{identity:x8}() {{ return {identity}; }}",
        _ => $"HKCU\\Software\\Synthetic\\Localized{identity:D10}",
    };
    return variant switch
    {
        0 => new(text, "ocr", "ocr", new Dictionary<string, object?>
        {
            ["benchmark"] = true,
            ["pageNumber"] = identity + 1,
        }),
        1 => new(text, "floss", "decoded", new Dictionary<string, object?>
        {
            ["benchmark"] = true,
            ["decoder"] = "synthetic-floss",
        }),
        2 => new(text, "pdf", "text-layer", new Dictionary<string, object?>
        {
            ["benchmark"] = true,
            ["sourceLineNumber"] = identity + 1,
        }),
        _ => new(text, "bstrings", "native", new Dictionary<string, object?>
        {
            ["benchmark"] = true,
            ["encoding"] = "utf-16le",
        }),
    };
}

static SyntheticRoutingFixture CreateMixedFixture(int identity) =>
    (identity % 7) switch
    {
        0 => SyntheticRoutingFixture.Default(CreateNaturalText(identity)),
        1 => SyntheticRoutingFixture.Default($"00000000-0000-4000-8000-{identity:x12}"),
        2 => SyntheticRoutingFixture.Default($"mov eax,{identity};call synthetic_{identity:x8}"),
        3 => SyntheticRoutingFixture.Default($"Bonjour {identity}"),
        4 => SyntheticRoutingFixture.Default(CreateMaxText(identity)),
        5 => SyntheticRoutingFixture.Default(CreateEncodedText(identity & ~3)),
        _ => CreateProvenanceFixture((identity & ~3) | 1),
    };

static string CreateIpAddress(int identity) =>
    $"10.{identity / 65025 % 250 + 1}.{identity / 255 % 250 + 1}.{identity % 250 + 1}";

static string FixedHex(int identity, int length)
{
    var seed = identity.ToString("x8", CultureInfo.InvariantCulture);
    var builder = new StringBuilder(length);
    while (builder.Length < length)
    {
        builder.Append(seed);
    }
    return builder.ToString(0, length);
}

static string FitToLength(string seed, string suffix, int length)
{
    var builder = new StringBuilder(length);
    while (builder.Length + suffix.Length < length)
    {
        var remaining = length - suffix.Length - builder.Length;
        builder.Append(seed.AsSpan(0, Math.Min(seed.Length, remaining)));
    }
    builder.Append(suffix);
    return builder.ToString();
}

static string CreateJwt(int identity)
{
    var header = Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
    var payload = Base64Url($"{{\"sub\":\"synthetic-{identity:D10}\",\"scope\":\"benchmark\"}}");
    var signature = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString(CultureInfo.InvariantCulture)))
        )
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    return $"{header}.{payload}.{signature}";
}

static string Base64Url(string value) =>
    Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

static void AssertReusePairExact(IReadOnlyList<BenchmarkRow> pair, int expectedRecords)
{
    if (pair.Count != 2)
    {
        throw new InvalidOperationException("A benchmark round did not produce two variants.");
    }
    var baseline = pair.Single(row => row.Variant == "baseline");
    var reuse = pair.Single(row => row.Variant == "reuse");
    if (
        baseline.Stats != reuse.Stats
        || baseline.Stats.InputRecords != expectedRecords
        || baseline.CandidatesBytes != reuse.CandidatesBytes
        || baseline.AssessmentsBytes != reuse.AssessmentsBytes
        || !string.Equals(
            baseline.CandidatesSha256,
            reuse.CandidatesSha256,
            StringComparison.Ordinal
        )
        || !string.Equals(
            baseline.AssessmentsSha256,
            reuse.AssessmentsSha256,
            StringComparison.Ordinal
        )
    )
    {
        throw new InvalidDataException(
            $"Round {baseline.Round} produced different baseline and reuse outputs: "
                + $"statsEqual={baseline.Stats == reuse.Stats}, "
                + $"candidateBytes={baseline.CandidatesBytes}/{reuse.CandidatesBytes}, "
                + $"assessmentBytes={baseline.AssessmentsBytes}/{reuse.AssessmentsBytes}, "
                + $"candidateHashesEqual={string.Equals(baseline.CandidatesSha256, reuse.CandidatesSha256, StringComparison.Ordinal)}, "
                + $"assessmentHashesEqual={string.Equals(baseline.AssessmentsSha256, reuse.AssessmentsSha256, StringComparison.Ordinal)}."
        );
    }
}

static async Task<MeasuredTriageRun> ObservePeakWorkingSetAsync(
    Func<Task<LanguageTriageStats>> startTriage,
    Process process,
    long initialWorkingSetBytes
)
{
    using var stop = new ManualResetEventSlim(false);
    long sampledPeak = initialWorkingSetBytes;
    Exception? samplerError = null;
    var sampler = new Thread(() =>
    {
        try
        {
            while (true)
            {
                process.Refresh();
                sampledPeak = Math.Max(sampledPeak, process.WorkingSet64);
                if (stop.Wait(TimeSpan.FromMilliseconds(20)))
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            samplerError = ex;
        }
    })
    {
        IsBackground = true,
        Name = "language-triage-benchmark-working-set-sampler",
    };
    sampler.Start();

    LanguageTriageStats stats;
    try
    {
        stats = await startTriage();
    }
    finally
    {
        stop.Set();
        sampler.Join();
    }
    if (samplerError is not null)
    {
        throw new InvalidOperationException("Working-set sampling failed.", samplerError);
    }
    return new MeasuredTriageRun(stats, sampledPeak);
}

static void AssertRoutingPairExact(IReadOnlyList<BenchmarkRow> pair, int expectedRecords)
{
    if (pair.Count != 2)
    {
        throw new InvalidOperationException("A routing benchmark round did not produce two variants.");
    }
    var disabled = pair.Single(row => row.Variant == "routing-disabled");
    var shadow = pair.Single(row => row.Variant == "routing-shadow");
    var candidateHashesEqual = string.Equals(
        disabled.CandidatesSha256,
        shadow.CandidatesSha256,
        StringComparison.Ordinal
    );
    var projectionsEqual = string.Equals(
        disabled.AssessmentProjectionSha256,
        shadow.AssessmentProjectionSha256,
        StringComparison.Ordinal
    );
    var projectionsSemanticallyEquivalent = AssessmentProjectionsAreEquivalent(
        disabled.AssessmentsPath,
        shadow.AssessmentsPath,
        expectedRecords
    );
    var disabledRoutingIsEmpty =
        string.Equals(
            disabled.Stats.TranslationRoutingPolicyVersion,
            "disabled",
            StringComparison.Ordinal
        )
        && disabled.Stats.TranslationRoutingRetained == 0
        && disabled.Stats.TranslationRoutingProspectiveBypasses == 0
        && disabled.Stats.TranslationRoutingUnknown == 0
        && disabled.RoutingObjectCount == 0;
    var shadowRoutingCount =
        shadow.Stats.TranslationRoutingRetained
        + shadow.Stats.TranslationRoutingProspectiveBypasses
        + shadow.Stats.TranslationRoutingUnknown;
    var shadowRoutingIsComplete =
        !string.IsNullOrWhiteSpace(shadow.Stats.TranslationRoutingPolicyVersion)
        && !string.Equals(
            shadow.Stats.TranslationRoutingPolicyVersion,
            "disabled",
            StringComparison.Ordinal
        )
        && shadowRoutingCount == expectedRecords
        && shadow.RoutingObjectCount == expectedRecords;
    var assessmentDelta = shadow.AssessmentsBytes - disabled.AssessmentsBytes;
    var averageRoutingBytes = assessmentDelta / (double)expectedRecords;

    if (
        !LegacyStatsEqual(disabled.Stats, shadow.Stats)
        || disabled.Stats.InputRecords != expectedRecords
        || disabled.CandidatesBytes != shadow.CandidatesBytes
        || !candidateHashesEqual
        || disabled.AssessmentProjectionRecordCount != expectedRecords
        || shadow.AssessmentProjectionRecordCount != expectedRecords
        || !projectionsSemanticallyEquivalent
        || !disabledRoutingIsEmpty
        || !shadowRoutingIsComplete
        || assessmentDelta <= 0
        || averageRoutingBytes > 256
    )
    {
        throw new InvalidDataException(
            $"Round {disabled.Round} produced an invalid routing-shadow pair: "
                + $"legacyStatsEqual={LegacyStatsEqual(disabled.Stats, shadow.Stats)}, "
                + $"candidateBytes={disabled.CandidatesBytes}/{shadow.CandidatesBytes}, "
                + $"candidateHashesEqual={candidateHashesEqual}, "
                + $"assessmentProjectionBytes={disabled.AssessmentProjectionBytes}/{shadow.AssessmentProjectionBytes}, "
                + $"assessmentProjectionHashesEqual={projectionsEqual}, "
                + $"assessmentProjectionsEquivalent={projectionsSemanticallyEquivalent}, "
                + $"routingObjects={disabled.RoutingObjectCount}/{shadow.RoutingObjectCount}, "
                + $"routingCounts={shadow.Stats.TranslationRoutingRetained}/"
                + $"{shadow.Stats.TranslationRoutingProspectiveBypasses}/"
                + $"{shadow.Stats.TranslationRoutingUnknown}, "
                + $"assessmentDelta={assessmentDelta}, "
                + $"averageRoutingBytes={averageRoutingBytes:F3}."
        );
    }
}

static bool AssessmentProjectionsAreEquivalent(
    string disabledPath,
    string shadowPath,
    int expectedRecords
)
{
    using var disabledReader = CreateStrictReader(disabledPath);
    using var shadowReader = CreateStrictReader(shadowPath);
    long record = 0;
    while (true)
    {
        var disabledLine = disabledReader.ReadLine();
        var shadowLine = shadowReader.ReadLine();
        if (disabledLine is null || shadowLine is null)
        {
            return disabledLine is null && shadowLine is null && record == expectedRecords;
        }

        record++;
        using var disabledDocument = JsonDocument.Parse(disabledLine);
        using var shadowDocument = JsonDocument.Parse(shadowLine);
        var disabledProperties = disabledDocument.RootElement.EnumerateObject().ToArray();
        var shadowProperties = shadowDocument
            .RootElement.EnumerateObject()
            .Where(property => !property.NameEquals("translationRouting"))
            .ToArray();
        if (disabledProperties.Length != shadowProperties.Length)
        {
            return false;
        }

        for (var index = 0; index < disabledProperties.Length; index++)
        {
            var disabledProperty = disabledProperties[index];
            var shadowProperty = shadowProperties[index];
            if (!disabledProperty.NameEquals(shadowProperty.Name))
            {
                return false;
            }
            if (DiagnosticScoreEqual(disabledProperty, shadowProperty))
            {
                continue;
            }
            if (
                !string.Equals(
                    disabledProperty.Value.GetRawText(),
                    shadowProperty.Value.GetRawText(),
                    StringComparison.Ordinal
                )
            )
            {
                return false;
            }
        }
    }
}

static bool DiagnosticScoreEqual(JsonProperty left, JsonProperty right)
{
    if (
        left.Value.ValueKind != JsonValueKind.Number
        || right.Value.ValueKind != JsonValueKind.Number
        || left.Name is not (
            "confidence"
            or "targetConfidence"
            or "secondConfidence"
            or "topLanguageMargin"
            or "targetMargin"
        )
    )
    {
        return false;
    }

    const decimal publishedScoreQuantum = 0.000000000001m;
    return decimal.TryParse(
            left.Value.GetRawText(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var leftValue
        )
        && decimal.TryParse(
            right.Value.GetRawText(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var rightValue
        )
        && Math.Abs(leftValue - rightValue) <= publishedScoreQuantum;
}

static StreamReader CreateStrictReader(string path) =>
    new(
        new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan
        ),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        detectEncodingFromByteOrderMarks: false,
        1024 * 1024
    );

static bool LegacyStatsEqual(LanguageTriageStats left, LanguageTriageStats right) =>
    left.InputRecords == right.InputRecords
    && left.TargetLanguageRecords == right.TargetLanguageRecords
    && left.TranslationCandidates == right.TranslationCandidates
    && left.AmbiguousRecords == right.AmbiguousRecords
    && left.NonLinguisticRecords == right.NonLinguisticRecords
    && left.DetectorFailures == right.DetectorFailures
    && left.EffectiveMode == right.EffectiveMode;

static AssessmentProjection InspectAssessments(string path, bool expectTranslationRouting)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        1024 * 1024,
        FileOptions.SequentialScan
    );
    using var reader = new StreamReader(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        detectEncodingFromByteOrderMarks: false,
        1024 * 1024
    );
    var buffer = new ArrayBufferWriter<byte>(2048);
    ReadOnlySpan<byte> newline = "\n"u8;
    long projectedBytes = 0;
    long recordCount = 0;
    long routingObjectCount = 0;
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        recordCount++;
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Assessment row {recordCount:N0} is not an object.");
        }

        buffer.Clear();
        var foundRouting = false;
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("translationRouting"))
                {
                    if (
                        foundRouting
                        || property.Value.ValueKind != JsonValueKind.Object
                        || !IsCompactRoutingObject(property.Value)
                    )
                    {
                        throw new InvalidDataException(
                            $"Assessment row {recordCount:N0} has invalid translationRouting metadata."
                        );
                    }
                    foundRouting = true;
                    routingObjectCount++;
                    continue;
                }
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        if (foundRouting != expectTranslationRouting)
        {
            throw new InvalidDataException(
                $"Assessment row {recordCount:N0} routing presence did not match the variant."
            );
        }
        hash.AppendData(buffer.WrittenSpan);
        hash.AppendData(newline);
        projectedBytes += buffer.WrittenCount + newline.Length;
    }

    return new AssessmentProjection(
        projectedBytes,
        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
        recordCount,
        routingObjectCount
    );
}

static bool IsCompactRoutingObject(JsonElement value)
{
    var properties = value.EnumerateObject().ToArray();
    if (
        properties.Length != 1
        || !properties[0].NameEquals("code")
        || properties[0].Value.ValueKind != JsonValueKind.String
    )
    {
        return false;
    }
    var code = properties[0].Value.GetString();
    return !string.IsNullOrWhiteSpace(code)
        && code.Length <= 32
        && code.All(character => character is >= (char)0x21 and <= (char)0x7e);
}

static string GetSha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static async Task WriteCsvAsync(
    string path,
    IReadOnlyCollection<BenchmarkRow> rows,
    BenchmarkExperiment experiment
)
{
    await using var writer = new StreamWriter(
        path,
        append: false,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    );
    if (experiment == BenchmarkExperiment.Reuse)
    {
        await writer.WriteLineAsync(
            "records,duplicatePercent,mode,variant,round,order,elapsedSeconds,recordsPerSecond,cpuSeconds,allocatedBytes,gen0,gen1,gen2,workingSetBytes,inputRecords,targetRecords,translationCandidates,ambiguousRecords,nonLinguisticRecords,detectorFailures,candidatesBytes,assessmentsBytes,candidatesSha256,assessmentsSha256"
        );
    }
    else
    {
        await writer.WriteLineAsync(
            "experiment,routingCorpus,records,duplicatePercent,mode,variant,round,order,elapsedSeconds,recordsPerSecond,cpuSeconds,allocatedBytes,gen0,gen1,gen2,workingSetBeforeBytes,workingSetAfterBytes,sampledPeakWorkingSetBytes,inputRecords,targetRecords,translationCandidates,ambiguousRecords,nonLinguisticRecords,detectorFailures,candidatesBytes,assessmentsBytes,assessmentBytesDeltaFromDisabled,candidatesSha256,assessmentsSha256,assessmentProjectionBytes,assessmentProjectionSha256,assessmentProjectionRecordCount,routingObjectCount,translationRoutingPolicyVersion,translationRoutingRetained,translationRoutingProspectiveBypasses,translationRoutingUnknown"
        );
    }
    foreach (var row in rows)
    {
        if (experiment == BenchmarkExperiment.Reuse)
        {
            await writer.WriteLineAsync(
                string.Join(
                    ',',
                    row.Records.ToString(CultureInfo.InvariantCulture),
                    row.DuplicatePercent.ToString("0.####", CultureInfo.InvariantCulture),
                    row.Mode,
                    row.Variant,
                    row.Round.ToString(CultureInfo.InvariantCulture),
                    row.Order.ToString(CultureInfo.InvariantCulture),
                    row.ElapsedSeconds.ToString("0.000000", CultureInfo.InvariantCulture),
                    (row.Records / row.ElapsedSeconds).ToString("0.0", CultureInfo.InvariantCulture),
                    row.CpuSeconds.ToString("0.000000", CultureInfo.InvariantCulture),
                    row.AllocatedBytes.ToString(CultureInfo.InvariantCulture),
                    row.Gen0.ToString(CultureInfo.InvariantCulture),
                    row.Gen1.ToString(CultureInfo.InvariantCulture),
                    row.Gen2.ToString(CultureInfo.InvariantCulture),
                    row.WorkingSetAfterBytes.ToString(CultureInfo.InvariantCulture),
                    row.Stats.InputRecords.ToString(CultureInfo.InvariantCulture),
                    row.Stats.TargetLanguageRecords.ToString(CultureInfo.InvariantCulture),
                    row.Stats.TranslationCandidates.ToString(CultureInfo.InvariantCulture),
                    row.Stats.AmbiguousRecords.ToString(CultureInfo.InvariantCulture),
                    row.Stats.NonLinguisticRecords.ToString(CultureInfo.InvariantCulture),
                    row.Stats.DetectorFailures.ToString(CultureInfo.InvariantCulture),
                    row.CandidatesBytes.ToString(CultureInfo.InvariantCulture),
                    row.AssessmentsBytes.ToString(CultureInfo.InvariantCulture),
                    row.CandidatesSha256,
                    row.AssessmentsSha256
                )
            );
        }
        else
        {
            await writer.WriteLineAsync(
                string.Join(
                    ',',
                    row.Experiment,
                    row.RoutingCorpus,
                    row.Records.ToString(CultureInfo.InvariantCulture),
                    row.DuplicatePercent.ToString("0.####", CultureInfo.InvariantCulture),
                    row.Mode,
                    row.Variant,
                    row.Round.ToString(CultureInfo.InvariantCulture),
                    row.Order.ToString(CultureInfo.InvariantCulture),
                    row.ElapsedSeconds.ToString("0.000000", CultureInfo.InvariantCulture),
                    (row.Records / row.ElapsedSeconds).ToString("0.0", CultureInfo.InvariantCulture),
                    row.CpuSeconds.ToString("0.000000", CultureInfo.InvariantCulture),
                    row.AllocatedBytes.ToString(CultureInfo.InvariantCulture),
                    row.Gen0.ToString(CultureInfo.InvariantCulture),
                    row.Gen1.ToString(CultureInfo.InvariantCulture),
                    row.Gen2.ToString(CultureInfo.InvariantCulture),
                    row.WorkingSetBeforeBytes.ToString(CultureInfo.InvariantCulture),
                    row.WorkingSetAfterBytes.ToString(CultureInfo.InvariantCulture),
                    row.SampledPeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture),
                    row.Stats.InputRecords.ToString(CultureInfo.InvariantCulture),
                    row.Stats.TargetLanguageRecords.ToString(CultureInfo.InvariantCulture),
                    row.Stats.TranslationCandidates.ToString(CultureInfo.InvariantCulture),
                    row.Stats.AmbiguousRecords.ToString(CultureInfo.InvariantCulture),
                    row.Stats.NonLinguisticRecords.ToString(CultureInfo.InvariantCulture),
                    row.Stats.DetectorFailures.ToString(CultureInfo.InvariantCulture),
                    row.CandidatesBytes.ToString(CultureInfo.InvariantCulture),
                    row.AssessmentsBytes.ToString(CultureInfo.InvariantCulture),
                    row.AssessmentBytesDeltaFromDisabled.ToString(CultureInfo.InvariantCulture),
                    row.CandidatesSha256,
                    row.AssessmentsSha256,
                    row.AssessmentProjectionBytes.ToString(CultureInfo.InvariantCulture),
                    row.AssessmentProjectionSha256,
                    row.AssessmentProjectionRecordCount.ToString(CultureInfo.InvariantCulture),
                    row.RoutingObjectCount.ToString(CultureInfo.InvariantCulture),
                    row.Stats.TranslationRoutingPolicyVersion,
                    row.Stats.TranslationRoutingRetained.ToString(CultureInfo.InvariantCulture),
                    row.Stats.TranslationRoutingProspectiveBypasses.ToString(
                        CultureInfo.InvariantCulture
                    ),
                    row.Stats.TranslationRoutingUnknown.ToString(CultureInfo.InvariantCulture)
                )
            );
        }
    }
}

static void WriteSummary(
    IReadOnlyCollection<BenchmarkRow> rows,
    string outputPath,
    BenchmarkExperiment experiment
)
{
    if (experiment == BenchmarkExperiment.Reuse)
    {
        var baseline = Median(
            rows.Where(row => row.Variant == "baseline").Select(row => row.ElapsedSeconds)
        );
        var reuse = Median(
            rows.Where(row => row.Variant == "reuse").Select(row => row.ElapsedSeconds)
        );
        Console.WriteLine($"Baseline median: {baseline:F3} s");
        Console.WriteLine($"Reuse median: {reuse:F3} s");
        Console.WriteLine($"Speedup: {baseline / reuse:F3}x");
        Console.WriteLine("Output parity: passed");
    }
    else
    {
        var disabled = Median(
            rows.Where(row => row.Variant == "routing-disabled")
                .Select(row => row.ElapsedSeconds)
        );
        var shadow = Median(
            rows.Where(row => row.Variant == "routing-shadow")
                .Select(row => row.ElapsedSeconds)
        );
        var assessmentDelta = Median(
            rows.Where(row => row.Variant == "routing-shadow")
                .Select(row => (double)row.AssessmentBytesDeltaFromDisabled)
        );
        var sample = rows.First(row => row.Variant == "routing-shadow");
        var wallRegression = MedianPairedRegressionPercent(
            rows,
            row => row.ElapsedSeconds
        );
        var peakWorkingSetRegression = MedianPairedRegressionPercent(
            rows,
            row => row.SampledPeakWorkingSetBytes
        );
        var resultByteRegression = MedianPairedRegressionPercent(
            rows,
            row => row.CandidatesBytes + row.AssessmentsBytes
        );
        Console.WriteLine($"Routing disabled median: {disabled:F3} s");
        Console.WriteLine($"Routing shadow median: {shadow:F3} s");
        Console.WriteLine($"Routing corpus: {sample.RoutingCorpus}");
        Console.WriteLine($"Shadow/disabled time ratio: {shadow / disabled:F3}x");
        Console.WriteLine($"Median paired wall-time regression: {wallRegression:F3}%");
        Console.WriteLine(
            $"Median paired sampled-peak working-set regression: {peakWorkingSetRegression:F3}%"
        );
        Console.WriteLine($"Median paired result-byte regression: {resultByteRegression:F3}%");
        Console.WriteLine($"Median assessment byte delta: {assessmentDelta:F0}");
        Console.WriteLine(
            "Routing counts (retained/prospective/unknown): "
                + $"{sample.Stats.TranslationRoutingRetained}/"
                + $"{sample.Stats.TranslationRoutingProspectiveBypasses}/"
                + $"{sample.Stats.TranslationRoutingUnknown}"
        );
        Console.WriteLine("Candidate and pre-existing assessment parity: passed");
    }
    Console.WriteLine($"Results: {outputPath}");
}

static void AssertRoutingShadowScaleGate(
    IReadOnlyCollection<BenchmarkRow> rows,
    int records,
    int rounds,
    BenchmarkExperiment experiment
)
{
    if (
        experiment != BenchmarkExperiment.RoutingShadow
        || records < 100_000
        || rounds < 7
    )
    {
        return;
    }
    var wallRegression = MedianPairedRegressionPercent(rows, row => row.ElapsedSeconds);
    var peakWorkingSetRegression = MedianPairedRegressionPercent(
        rows,
        row => row.SampledPeakWorkingSetBytes
    );
    var resultByteRegression = MedianPairedRegressionPercent(
        rows,
        row => row.CandidatesBytes + row.AssessmentsBytes
    );
    if (wallRegression > 5 || peakWorkingSetRegression > 5 || resultByteRegression > 5)
    {
        throw new InvalidDataException(
            "Routing-shadow ADR scale gate failed: "
                + $"medianWallRegression={wallRegression:F3}%, "
                + $"medianSampledPeakWorkingSetRegression={peakWorkingSetRegression:F3}%, "
                + $"medianResultByteRegression={resultByteRegression:F3}%, "
                + "maximum=5.000%."
        );
    }
    Console.WriteLine("Routing-shadow ADR scale gate: passed");
}

static double MedianPairedRegressionPercent(
    IReadOnlyCollection<BenchmarkRow> rows,
    Func<BenchmarkRow, double> selector
)
{
    var regressions = rows
        .GroupBy(row => row.Round)
        .Select(group =>
        {
            var disabled = selector(group.Single(row => row.Variant == "routing-disabled"));
            var shadow = selector(group.Single(row => row.Variant == "routing-shadow"));
            if (disabled <= 0)
            {
                throw new InvalidDataException(
                    $"Round {group.Key} has a non-positive disabled measurement."
                );
            }
            return (shadow - disabled) / disabled * 100;
        });
    return Median(regressions);
}

static double Median(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    if (ordered.Length == 0)
    {
        throw new InvalidOperationException("No benchmark values were recorded.");
    }
    return (ordered.Length & 1) == 1
        ? ordered[ordered.Length / 2]
        : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (
            !arguments[index].StartsWith("--", StringComparison.Ordinal)
            || index + 1 >= arguments.Length
        )
        {
            throw new ArgumentException($"Invalid benchmark argument near '{arguments[index]}'.");
        }
        parsed.Add(arguments[index][2..], arguments[index + 1]);
    }
    return parsed;
}

static BenchmarkExperiment ParseExperiment(string value) =>
    value.ToLowerInvariant() switch
    {
        "reuse" => BenchmarkExperiment.Reuse,
        "routing-shadow" => BenchmarkExperiment.RoutingShadow,
        _ => throw new ArgumentException(
            "--experiment must be 'reuse' or 'routing-shadow'.",
            "experiment"
        ),
    };

static RoutingCorpus ParseRoutingCorpus(string value) =>
    value.ToLowerInvariant() switch
    {
        "mixed" => RoutingCorpus.Mixed,
        "natural" => RoutingCorpus.Natural,
        "machine" => RoutingCorpus.Machine,
        "short" => RoutingCorpus.Short,
        "max" => RoutingCorpus.Max,
        "encoded" => RoutingCorpus.Encoded,
        "provenance" => RoutingCorpus.Provenance,
        _ => throw new ArgumentException(
            "--routing-corpus must be mixed, natural, machine, short, max, encoded, or provenance.",
            "routing-corpus"
        ),
    };

static int ParsePositiveInt(string value, string name)
{
    if (
        !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        || parsed < 1
    )
    {
        throw new ArgumentOutOfRangeException(name, $"--{name} must be a positive integer.");
    }
    return parsed;
}

static double ParsePercentage(string value, string name)
{
    if (
        !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        || !double.IsFinite(parsed)
        || parsed is < 0 or > 100
    )
    {
        throw new ArgumentOutOfRangeException(name, $"--{name} must be between 0 and 100.");
    }
    return parsed;
}

static BenchmarkVariant[] GetVariants(BenchmarkExperiment experiment, int round)
{
    var first = experiment == BenchmarkExperiment.Reuse
        ? BenchmarkVariant.Baseline
        : BenchmarkVariant.RoutingDisabled;
    var second = experiment == BenchmarkExperiment.Reuse
        ? BenchmarkVariant.Reuse
        : BenchmarkVariant.RoutingShadow;
    return (round & 1) == 1 ? new[] { first, second } : new[] { second, first };
}

static string ExperimentName(BenchmarkExperiment experiment) =>
    experiment == BenchmarkExperiment.RoutingShadow ? "routing-shadow" : "reuse";

static string RoutingCorpusName(RoutingCorpus corpus) => corpus.ToString().ToLowerInvariant();

static string VariantName(BenchmarkVariant variant) =>
    variant switch
    {
        BenchmarkVariant.Baseline => "baseline",
        BenchmarkVariant.Reuse => "reuse",
        BenchmarkVariant.RoutingDisabled => "routing-disabled",
        BenchmarkVariant.RoutingShadow => "routing-shadow",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

static void PrintHelp()
{
    Console.WriteLine(
        """
        LanguageTriageBenchmark

        Synthetic, privacy-safe A/B benchmark for offline language triage.

        Usage:
          dotnet run --project .\benchmarks\LanguageTriageBenchmark -c Release -- [options]

        Options:
          --experiment reuse|routing-shadow  Experiment to run (default: reuse).
          --routing-corpus NAME              mixed|natural|machine|short|max|encoded|provenance.
          --records N                        Positive record count (default: 100000).
          --duplicate-percent N              Batch-local exact duplicates, 0-100 (default: 50).
          --rounds N                         Alternating A/B rounds (default: 7).
          --mode accurate|fast|adaptive      Lingua mode (default: accurate).
          --output PATH                      New CSV output path.
          --help, -h                         Show this help.

        The reuse experiment preserves the historical baseline/reuse output contract.
        The routing-shadow experiment enables bounded reuse on both variants and changes
        only includeTranslationRouting. It requires identical candidate bytes/hash and
        identical routing-stripped assessment outcomes before reporting measurements.
        The machine corpus requires --duplicate-percent 0. The reuse experiment rejects
        --routing-corpus so its historical fixture cannot drift.
        """
    );
}

internal enum BenchmarkExperiment
{
    Reuse,
    RoutingShadow,
}

internal enum RoutingCorpus
{
    Mixed,
    Natural,
    Machine,
    Short,
    Max,
    Encoded,
    Provenance,
}

internal enum BenchmarkVariant
{
    Baseline,
    Reuse,
    RoutingDisabled,
    RoutingShadow,
}

internal sealed record BenchmarkRow(
    string Experiment,
    string RoutingCorpus,
    int Records,
    double DuplicatePercent,
    string Mode,
    string Variant,
    int Round,
    int Order,
    double ElapsedSeconds,
    double CpuSeconds,
    long AllocatedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long SampledPeakWorkingSetBytes,
    LanguageTriageStats Stats,
    long CandidatesBytes,
    long AssessmentsBytes,
    string CandidatesSha256,
    string AssessmentsSha256,
    long AssessmentProjectionBytes,
    string AssessmentProjectionSha256,
    long AssessmentProjectionRecordCount,
    long RoutingObjectCount,
    long AssessmentBytesDeltaFromDisabled,
    string AssessmentsPath
);

internal sealed record SyntheticRoutingFixture(
    string Text,
    string OriginExtractor,
    string OriginKind,
    IReadOnlyDictionary<string, object?> Attributes
)
{
    internal static SyntheticRoutingFixture Default(string text) =>
        new(
            text,
            "benchmark",
            "native",
            new Dictionary<string, object?> { ["benchmark"] = true }
        );
}

internal readonly record struct AssessmentProjection(
    long ProjectedBytes,
    string ProjectedSha256,
    long RecordCount,
    long RoutingObjectCount
);

internal readonly record struct MeasuredTriageRun(
    LanguageTriageStats Stats,
    long SampledPeakWorkingSetBytes
);
