using Xunit;

namespace bstrings.Tests;

public class SearchTargetConfigurationCoreTests
{
    [Fact]
    public void Build_IncludesLiteralStringAndResolvedRegexPatterns()
    {
        var result = SearchTargetConfigurationCore.Build(
            literalString: "Alpha",
            literalRegex: "guid,email",
            stringsFilePath: null,
            regexFilePath: null,
            parsedRegexPatterns:
            [
                ("guid", "guid-pattern"),
                ("email", "email-pattern"),
            ],
            fileExists: _ => false,
            readAllLines: _ => Array.Empty<string>()
        );

        Assert.Equal(["Alpha"], result.FileStrings.OrderBy(value => value));
        Assert.Equal(
            ["email-pattern", "guid-pattern"],
            result.RegexStrings.OrderBy(value => value)
        );
        Assert.Equal(
            [("guid", "guid-pattern"), ("email", "email-pattern")],
            result.RegexPatterns
        );
        Assert.Empty(result.MissingFiles);
    }

    [Fact]
    public void Build_LoadsStringAndRegexTargetsFromFiles()
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var stringsFile = Path.Combine(tempDirectory, "strings.txt");
            var regexFile = Path.Combine(tempDirectory, "regex.txt");
            File.WriteAllLines(stringsFile, ["Alpha", "Beta"]);
            File.WriteAllLines(regexFile, ["Gamma.*", "Delta.*"]);

            var result = SearchTargetConfigurationCore.Build(
                literalString: null,
                literalRegex: null,
                stringsFilePath: stringsFile,
                regexFilePath: regexFile,
                parsedRegexPatterns: Array.Empty<(string name, string pattern)>(),
                fileExists: File.Exists,
                readAllLines: File.ReadAllLines
            );

            Assert.Equal(["Alpha", "Beta"], result.FileStrings.OrderBy(value => value));
            Assert.Equal(
                ["Delta.*", "Gamma.*"],
                result.RegexStrings.OrderBy(value => value)
            );
            Assert.Equal(
                [("file:1", "Gamma.*"), ("file:2", "Delta.*")],
                result.RegexPatterns
            );
            Assert.Empty(result.MissingFiles);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Build_ReportsMissingFilesAndSkipsParsedRegexesWhenLiteralRegexIsMissing()
    {
        var result = SearchTargetConfigurationCore.Build(
            literalString: null,
            literalRegex: null,
            stringsFilePath: "missing-strings.txt",
            regexFilePath: "missing-regex.txt",
            parsedRegexPatterns: [("guid", "guid-pattern")],
            fileExists: _ => false,
            readAllLines: _ => Array.Empty<string>()
        );

        Assert.Empty(result.FileStrings);
        Assert.Empty(result.RegexStrings);
        Assert.Equal(2, result.MissingFiles.Count);
        Assert.Contains("Strings file 'missing-strings.txt' not found", result.MissingFiles);
        Assert.Contains("Regex file 'missing-regex.txt' not found", result.MissingFiles);
    }

    [Fact]
    public async Task Build_MixedNamedAndFileRegexesExecutesBothWithStableFileLineNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var result = SearchTargetConfigurationCore.Build(
            literalString: null,
            literalRegex: "Alpha[0-9]+",
            stringsFilePath: null,
            regexFilePath: "patterns.txt",
            parsedRegexPatterns: [("alpha", "Alpha[0-9]+")],
            fileExists: path => path == "patterns.txt",
            readAllLines: _ => ["", "# ignored", "  Beta[0-9]+  "]
        );

        Assert.Equal(
            [("alpha", "Alpha[0-9]+"), ("file:3", "Beta[0-9]+")],
            result.RegexPatterns
        );
        Assert.Equal(2, result.RegexStrings.Count);

        using var stream = new MemoryStream();
        await using var writer = new StreamWriter(stream, leaveOpen: true);
        var count = await Program.ProcessRegexPatternsConcurrentlyAsync(
            ["Alpha123", "Beta456"],
            result.RegexPatterns.ToList(),
            ro: true,
            off: false,
            s: true,
            sw: writer,
            q: true,
            o: "results.csv",
            currentFile: "synthetic.bin",
            isCsvOutput: true,
            csvHeaderAlreadyWritten: false
        );
        await writer.FlushAsync(cancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var output = await reader.ReadToEndAsync(cancellationToken);

        Assert.Equal(2, count);
        Assert.Contains("\"alpha\",\"Alpha123\"", output);
        Assert.Contains("\"file:3\",\"Beta456\"", output);
        Assert.DoesNotContain("ignored", output);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bstrings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
