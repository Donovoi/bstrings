using System.Text;
using Xunit;

namespace bstrings.Tests;

public class InputResolutionCoreTests
{
    [Fact]
    public void ResolveExplicitInputs_RejectsFileAndDirectoryCombination()
    {
        var result = InputResolutionCore.ResolveExplicitInputs(
            "file.bin",
            "dir",
            null,
            _ => true,
            _ => true,
            (_, _) => Array.Empty<string>(),
            Path.GetFullPath
        );

        Assert.Equal(InputResolutionStatus.Error, result.Status);
        Assert.Contains("Both -f (file) and -d (directory)", result.Message);
    }

    [Fact]
    public void ResolveExplicitInputs_ReturnsFullPathForExistingFile()
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var filePath = Path.Combine(tempDirectory, "sample.bin");
            File.WriteAllText(filePath, "Alpha");

            var result = InputResolutionCore.ResolveExplicitInputs(
                filePath,
                null,
                null,
                File.Exists,
                Directory.Exists,
                (_, _) => Array.Empty<string>(),
                Path.GetFullPath
            );

            Assert.Equal(InputResolutionStatus.Success, result.Status);
            Assert.Equal([Path.GetFullPath(filePath)], result.Files);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveExplicitInputs_EnumeratesDirectoryUsingMask()
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var nestedDirectory = Path.Combine(tempDirectory, "nested");
            Directory.CreateDirectory(nestedDirectory);

            var matchingFile = Path.Combine(nestedDirectory, "match.txt");
            var ignoredFile = Path.Combine(nestedDirectory, "ignore.bin");
            File.WriteAllText(matchingFile, "Alpha");
            File.WriteAllText(ignoredFile, "Beta");

            var result = InputResolutionCore.ResolveExplicitInputs(
                null,
                tempDirectory,
                "*.txt",
                File.Exists,
                Directory.Exists,
                (directoryPath, searchMask) =>
                    Directory.EnumerateFiles(directoryPath, searchMask, SearchOption.AllDirectories),
                Path.GetFullPath
            );

            Assert.Equal(InputResolutionStatus.Success, result.Status);
            Assert.Equal([matchingFile], result.Files);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveExplicitInputs_ReturnsNoFilesFoundWhenDirectoryIsEmpty()
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var result = InputResolutionCore.ResolveExplicitInputs(
                null,
                tempDirectory,
                "*.txt",
                File.Exists,
                Directory.Exists,
                (directoryPath, searchMask) =>
                    Directory.EnumerateFiles(directoryPath, searchMask, SearchOption.AllDirectories),
                Path.GetFullPath
            );

            Assert.Equal(InputResolutionStatus.NoFilesFound, result.Status);
            Assert.Contains("No files found in directory", result.Message);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveExplicitInputs_ReturnsNoInputWhenNothingSpecified()
    {
        var result = InputResolutionCore.ResolveExplicitInputs(
            null,
            null,
            null,
            _ => false,
            _ => false,
            (_, _) => Array.Empty<string>(),
            Path.GetFullPath
        );

        Assert.Equal(InputResolutionStatus.NoInput, result.Status);
        Assert.Contains("A file (-f), directory (-d), or piped input is required", result.Message);
    }

    [Fact]
    public void CaptureRedirectedInput_PersistsNonEmptyStreamToTempFile()
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var tempFilePath = Path.Combine(tempDirectory, "stdin.bin");
            var result = InputResolutionCore.CaptureRedirectedInput(
                () => new MemoryStream(Encoding.UTF8.GetBytes("Alpha")),
                () => tempFilePath,
                path => new FileStream(path, FileMode.Create, FileAccess.Write),
                path => new FileInfo(path).Length,
                File.Delete,
                Path.GetFullPath
            );

            Assert.Equal(InputResolutionStatus.Success, result.Status);
            Assert.Equal([Path.GetFullPath(tempFilePath)], result.Files);
            Assert.Equal(Path.GetFullPath(tempFilePath), result.TempFilePath);
            Assert.True(File.Exists(tempFilePath));
            Assert.Equal("Alpha", File.ReadAllText(tempFilePath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void CaptureRedirectedInput_DeletesEmptyTempFileAndReturnsWarning()
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var tempFilePath = Path.Combine(tempDirectory, "stdin.bin");
            var result = InputResolutionCore.CaptureRedirectedInput(
                () => new MemoryStream(Array.Empty<byte>()),
                () => tempFilePath,
                path => new FileStream(path, FileMode.Create, FileAccess.Write),
                path => new FileInfo(path).Length,
                File.Delete,
                Path.GetFullPath
            );

            Assert.Equal(InputResolutionStatus.EmptyRedirectedInput, result.Status);
            Assert.Contains("no data was received", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(tempFilePath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void CaptureRedirectedInput_CleansUpTempFileWhenLengthCheckFails()
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var tempFilePath = Path.Combine(tempDirectory, "stdin.bin");
            var result = InputResolutionCore.CaptureRedirectedInput(
                () => new MemoryStream(Encoding.UTF8.GetBytes("Alpha")),
                () => tempFilePath,
                path => new FileStream(path, FileMode.Create, FileAccess.Write),
                _ => throw new InvalidOperationException("boom"),
                File.Delete,
                Path.GetFullPath
            );

            Assert.Equal(InputResolutionStatus.Error, result.Status);
            Assert.IsType<InvalidOperationException>(result.Exception);
            Assert.False(File.Exists(tempFilePath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bstrings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
