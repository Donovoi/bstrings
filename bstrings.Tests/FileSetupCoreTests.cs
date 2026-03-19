using Xunit;

namespace bstrings.Tests;

public class FileSetupCoreTests
{
    [Fact]
    public void CreateReadableFileStream_OpensReadOnlyFile()
    {
        var tempFile = CreateTemporaryFile("Alpha");

        try
        {
            using var stream = FileSetupCore.CreateReadableFileStream(tempFile);

            Assert.True(stream.CanRead);
            Assert.False(stream.CanWrite);
            Assert.Equal(5, stream.Length);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SetupMappedStreamForFile_UsesPrimaryFactoryWhenAvailable()
    {
        var tempFile = CreateTemporaryFile("Alpha");

        try
        {
            var result = FileSetupCore.SetupMappedStreamForFile(
                tempFile,
                FileSetupCore.CreateReadableFileStream,
                _ => throw new InvalidOperationException("fallback should not run")
            );

            using var mappedStream = result.Stream;
            var buffer = new byte[5];
            mappedStream.ReadExactly(buffer, 0, buffer.Length);

            Assert.False(result.UsedFallback);
            Assert.Null(result.PrimaryException);
            Assert.Equal("Alpha", System.Text.Encoding.ASCII.GetString(buffer));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SetupMappedStreamForFile_FallsBackWhenPrimaryFactoryFails()
    {
        var tempFile = CreateTemporaryFile("Beta");
        var debugMessages = new List<string>();
        var errorMessages = new List<string>();

        try
        {
            var result = FileSetupCore.SetupMappedStreamForFile(
                tempFile,
                _ => throw new IOException("primary failed"),
                FileSetupCore.CreateReadableFileStream,
                debugMessages.Add,
                errorMessages.Add
            );

            using var mappedStream = result.Stream;
            var buffer = new byte[4];
            mappedStream.ReadExactly(buffer, 0, buffer.Length);

            Assert.True(result.UsedFallback);
            Assert.IsType<IOException>(result.PrimaryException);
            Assert.Contains(debugMessages, message => message.Contains("Falling back"));
            Assert.Contains(errorMessages, message => message.Contains("primary failed"));
            Assert.Equal("Beta", System.Text.Encoding.ASCII.GetString(buffer));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SetupMappedStreamForFile_ThrowsAggregateExceptionWhenBothPathsFail()
    {
        var exception = Assert.Throws<AggregateException>(() =>
            FileSetupCore.SetupMappedStreamForFile(
                "missing.bin",
                _ => throw new InvalidOperationException("primary failed"),
                _ => throw new IOException("fallback failed")
            )
        );

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Contains(exception.InnerExceptions, ex => ex.Message.Contains("primary failed"));
        Assert.Contains(exception.InnerExceptions, ex => ex.Message.Contains("fallback failed"));
    }

    private static string CreateTemporaryFile(string contents)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"bstrings-tests-{Guid.NewGuid():N}.bin");
        File.WriteAllText(filePath, contents);
        return filePath;
    }
}
