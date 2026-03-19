using Xunit;

namespace bstrings.Tests;

public class OutputConfigurationCoreTests
{
    [Fact]
    public void Prepare_DisablesOutputWhenPathIsMissing()
    {
        var result = OutputConfigurationCore.Prepare(
            null,
            Path.GetFullPath,
            Path.GetDirectoryName,
            Directory.Exists,
            path => Directory.CreateDirectory(path)
        );

        Assert.False(result.IsEnabled);
        Assert.False(result.IsCsvOutput);
        Assert.Equal(string.Empty, result.OutputPath);
        Assert.Null(result.WarningMessage);
    }

    [Fact]
    public void Prepare_NormalizesExistingPathAndDetectsCsvOutput()
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var outputPath = Path.Combine(tempDirectory, "results.csv");

            var result = OutputConfigurationCore.Prepare(
                outputPath,
                Path.GetFullPath,
                Path.GetDirectoryName,
                Directory.Exists,
                path => Directory.CreateDirectory(path)
            );

            Assert.True(result.IsEnabled);
            Assert.True(result.IsCsvOutput);
            Assert.Equal(Path.GetFullPath(outputPath), result.OutputPath);
            Assert.Null(result.WarningMessage);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Prepare_CreatesMissingDirectoryForValidOutputPath()
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var missingDirectory = Path.Combine(tempDirectory, "nested");
            var outputPath = Path.Combine(missingDirectory, "results.txt");

            var result = OutputConfigurationCore.Prepare(
                outputPath,
                Path.GetFullPath,
                Path.GetDirectoryName,
                Directory.Exists,
                path => Directory.CreateDirectory(path)
            );

            Assert.True(result.IsEnabled);
            Assert.False(result.IsCsvOutput);
            Assert.Equal(Path.GetFullPath(outputPath), result.OutputPath);
            Assert.True(Directory.Exists(missingDirectory));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Prepare_ReturnsWarningWhenDirectoryCreationFails()
    {
        var result = OutputConfigurationCore.Prepare(
            "results.txt",
            Path.GetFullPath,
            _ => "C:\\blocked",
            _ => false,
            _ => throw new InvalidOperationException("nope")
        );

        Assert.False(result.IsEnabled);
        Assert.False(result.IsCsvOutput);
        Assert.Equal(string.Empty, result.OutputPath);
        Assert.Contains("Results will not be saved", result.WarningMessage);
    }

    [Fact]
    public void Prepare_ReturnsWarningWhenDirectoryNameCannotBeResolved()
    {
        var result = OutputConfigurationCore.Prepare(
            "results.txt",
            _ => "results.txt",
            _ => null,
            _ => true,
            _ => { }
        );

        Assert.False(result.IsEnabled);
        Assert.Equal(string.Empty, result.OutputPath);
        Assert.Contains("Invalid path", result.WarningMessage);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bstrings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
