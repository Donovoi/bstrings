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
            parsedRegexPatterns: ["guid-pattern", "email-pattern"],
            fileExists: _ => false,
            readAllLines: _ => Array.Empty<string>()
        );

        Assert.Equal(["Alpha"], result.FileStrings.OrderBy(value => value));
        Assert.Equal(
            ["email-pattern", "guid-pattern"],
            result.RegexStrings.OrderBy(value => value)
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
                parsedRegexPatterns: Array.Empty<string>(),
                fileExists: File.Exists,
                readAllLines: File.ReadAllLines
            );

            Assert.Equal(["Alpha", "Beta"], result.FileStrings.OrderBy(value => value));
            Assert.Equal(
                ["Delta.*", "Gamma.*"],
                result.RegexStrings.OrderBy(value => value)
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
            parsedRegexPatterns: ["guid-pattern"],
            fileExists: _ => false,
            readAllLines: _ => Array.Empty<string>()
        );

        Assert.Empty(result.FileStrings);
        Assert.Empty(result.RegexStrings);
        Assert.Equal(2, result.MissingFiles.Count);
        Assert.Contains("Strings file 'missing-strings.txt' not found", result.MissingFiles);
        Assert.Contains("Regex file 'missing-regex.txt' not found", result.MissingFiles);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bstrings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
