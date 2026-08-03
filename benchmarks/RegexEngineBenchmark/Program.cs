using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using bstrings;
using bstrings.Benchmarks;

var options = ParseArguments(args);
var iterations = options
    .GetValueOrDefault("iterations", "1,1000,10000,100000,1000000")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(value => ParsePositiveInt(value, "iterations"))
    .Distinct()
    .Order()
    .ToArray();
var rounds = ParsePositiveInt(options.GetValueOrDefault("rounds", "5"), "rounds");
var outputPath = Path.GetFullPath(options.GetValueOrDefault("output", "regex-engine-results.csv"));
if (File.Exists(outputPath))
{
    throw new IOException($"Refusing to overwrite {outputPath}.");
}

var rows = new List<ResultRow>();
foreach (var definition in BuiltInPatternCatalog.Definitions.Where(value => !value.UseNonBacktracking))
{
    var witness = PatternWitnessCatalog.ByName[definition.Name];
    foreach (var iterationCount in iterations)
    {
        for (var round = 0; round < rounds; round++)
        {
            var engines = round % 2 == 0
                ? new[] { EngineKind.Interpreted, EngineKind.Compiled }
                : new[] { EngineKind.Compiled, EngineKind.Interpreted };
            foreach (var engine in engines)
            {
                var optionsForEngine = RegexOptions.CultureInvariant | definition.Options;
                if (engine == EngineKind.Compiled)
                {
                    optionsForEngine |= RegexOptions.Compiled;
                }

                var stopwatch = Stopwatch.StartNew();
                var regex = new Regex(
                    definition.Pattern,
                    optionsForEngine,
                    RegexOutputCore.MatchTimeout
                );
                var matches = 0;
                for (var index = 0; index < iterationCount; index++)
                {
                    var candidate = index % 1024 == 0 ? witness.Positive : witness.Negative;
                    if (regex.IsMatch(candidate))
                    {
                        matches++;
                    }
                }
                stopwatch.Stop();

                var expectedMatches = (iterationCount + 1023) / 1024;
                rows.Add(
                    new ResultRow(
                        definition.Name,
                        engine.ToString().ToLowerInvariant(),
                        iterationCount,
                        round + 1,
                        stopwatch.Elapsed.TotalSeconds,
                        matches,
                        expectedMatches,
                        matches == expectedMatches
                    )
                );
                Console.WriteLine(
                    $"{definition.Name},{engine},{iterationCount},{round + 1},{stopwatch.Elapsed.TotalMilliseconds:F3} ms"
                );
            }
        }
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var csv = new StringBuilder();
csv.AppendLine(
    "Pattern,Engine,Iterations,Round,ElapsedSeconds,Matches,ExpectedMatches,Exact"
);
foreach (var row in rows)
{
    csv.Append(Escape(row.Pattern)).Append(',');
    csv.Append(Escape(row.Engine)).Append(',');
    csv.Append(row.Iterations.ToString(CultureInfo.InvariantCulture)).Append(',');
    csv.Append(row.Round.ToString(CultureInfo.InvariantCulture)).Append(',');
    csv.Append(row.ElapsedSeconds.ToString("R", CultureInfo.InvariantCulture)).Append(',');
    csv.Append(row.Matches.ToString(CultureInfo.InvariantCulture)).Append(',');
    csv.Append(row.ExpectedMatches.ToString(CultureInfo.InvariantCulture)).Append(',');
    csv.AppendLine(row.Exact ? "true" : "false");
}
File.WriteAllText(outputPath, csv.ToString(), new UTF8Encoding(false));

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal) || ++index >= arguments.Length)
        {
            throw new ArgumentException($"Expected --name value, received {argument}.");
        }
        parsed[argument[2..]] = arguments[index];
    }
    return parsed;
}

static int ParsePositiveInt(string value, string name) =>
    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
    && parsed > 0
        ? parsed
        : throw new ArgumentOutOfRangeException(name, $"--{name} must contain positive integers.");

static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

internal enum EngineKind
{
    Interpreted,
    Compiled,
}

internal sealed record ResultRow(
    string Pattern,
    string Engine,
    int Iterations,
    int Round,
    double ElapsedSeconds,
    int Matches,
    int ExpectedMatches,
    bool Exact
);
