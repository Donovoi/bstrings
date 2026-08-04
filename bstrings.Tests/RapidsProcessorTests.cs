using System.Reflection;
using System.Diagnostics;
using bstrings.Rapids;
using Xunit;

namespace bstrings.Tests;

public class RapidsProcessorTests
{
    [Fact]
    public void ResolvePythonExecutable_UsesOnlyTheConfiguredLocalRuntime()
    {
        var previous = Environment.GetEnvironmentVariable("BSTRINGS_RAPIDS_PYTHON");
        var temporary = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable("BSTRINGS_RAPIDS_PYTHON", temporary);
            Assert.Equal(Path.GetFullPath(temporary), RapidsProcessor.ResolvePythonExecutable());

            Environment.SetEnvironmentVariable(
                "BSTRINGS_RAPIDS_PYTHON",
                temporary + ".missing"
            );
            Assert.Throws<FileNotFoundException>(RapidsProcessor.ResolvePythonExecutable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSTRINGS_RAPIDS_PYTHON", previous);
            File.Delete(temporary);
        }
    }

    [Fact]
    public async Task ProcessRegexPatternsBridgeAsync_WhenRapidsUnavailable_MatchesStandardProcessing()
    {
        await WithRapidsAvailabilityAsync(
            available: false,
            async () =>
            {
                using var standardStream = new MemoryStream();
                using var standardWriter = new StreamWriter(standardStream, leaveOpen: true);
                using var bridgeStream = new MemoryStream();
                using var bridgeWriter = new StreamWriter(bridgeStream, leaveOpen: true);
                var hits = new HashSet<string> { "0x201\tAlpha123", "0x202\tBeta456" };
                var patterns = new List<(string name, string pattern)> { ("letters", "Alpha|Beta") };

                var standardCount = await Program.ProcessRegexPatternsConcurrentlyAsync(
                    hits,
                    patterns,
                    ro: true,
                    off: true,
                    s: true,
                    sw: standardWriter,
                    q: true,
                    o: "results.txt",
                    currentFile: "sample.bin",
                    isCsvOutput: false,
                    csvHeaderAlreadyWritten: false
                );

                var bridgeCount = await RapidsProcessor.ProcessRegexPatternsBridgeAsync(
                    hits,
                    patterns,
                    ro: true,
                    off: true,
                    s: true,
                    sw: bridgeWriter,
                    q: true,
                    o: "results.txt",
                    currentFile: "sample.bin",
                    isCsvOutput: false,
                    csvHeaderAlreadyWritten: false
                );

                await standardWriter.FlushAsync(TestContext.Current.CancellationToken);
                await bridgeWriter.FlushAsync(TestContext.Current.CancellationToken);
                standardStream.Position = 0;
                bridgeStream.Position = 0;

                using var standardReader = new StreamReader(standardStream);
                using var bridgeReader = new StreamReader(bridgeStream);
                var standardOutput = await standardReader.ReadToEndAsync(
                    TestContext.Current.CancellationToken
                );
                var bridgeOutput = await bridgeReader.ReadToEndAsync(
                    TestContext.Current.CancellationToken
                );

                Assert.Equal(standardCount, bridgeCount);
                Assert.Equal(
                    standardOutput
                        .Split(
                            Environment.NewLine,
                            StringSplitOptions.RemoveEmptyEntries
                        )
                        .Order(),
                    bridgeOutput
                        .Split(
                            Environment.NewLine,
                            StringSplitOptions.RemoveEmptyEntries
                        )
                        .Order()
                );
            }
        );
    }

    [Fact]
    public async Task RapidsBridgeCpuFallback_AppliesBuiltInSemanticValidation()
    {
        await WithRapidsAvailabilityAsync(
            available: false,
            async () =>
            {
                using var stream = new MemoryStream();
                using var writer = new StreamWriter(stream, leaveOpen: true);
                var definition = BuiltInPatternCatalog.ByName["cc"];

                var count = await RapidsProcessor.ProcessRegexPatternsBridgeAsync(
                    new HashSet<string>
                    {
                        "valid 4111111111111111",
                        "invalid 4111111111111112",
                    },
                    [(definition.Name, definition.Pattern)],
                    ro: true,
                    off: false,
                    s: true,
                    sw: writer,
                    q: true,
                    o: "results.txt",
                    currentFile: "sample.bin",
                    isCsvOutput: false,
                    csvHeaderAlreadyWritten: false
                );

                await writer.FlushAsync(TestContext.Current.CancellationToken);
                stream.Position = 0;
                using var reader = new StreamReader(stream);
                var output = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

                Assert.Equal(1, count);
                Assert.Equal("4111111111111111" + Environment.NewLine, output);
            }
        );
    }

    [Fact]
    public async Task BenchmarkPerformanceAsync_WhenRapidsUnavailable_StillBenchmarksStandardProcessing()
    {
        await WithRapidsAvailabilityAsync(
            available: false,
            async () =>
            {
                var benchmark = await RapidsProcessor.BenchmarkPerformanceAsync(
                    new[] { "Alpha123", "Beta456", "Gamma789" },
                    [("alpha", "Alpha\\d+")]
                );

                Assert.False(benchmark.RapidsAvailable);
                Assert.Equal(1, benchmark.StandardResultCount);
                Assert.True(benchmark.StandardProcessingTimeMs >= 0);
                Assert.Equal(0, benchmark.RapidsResultCount);
                Assert.Equal(0, benchmark.RapidsProcessingTimeMs);
                Assert.False(benchmark.CountParity);
            }
        );
    }

    [Fact]
    public async Task BenchmarkPerformanceAsync_DeduplicatesTheSameRowsForEveryBackend()
    {
        await WithRapidsAvailabilityAsync(
            available: false,
            async () =>
            {
                var benchmark = await RapidsProcessor.BenchmarkPerformanceAsync(
                    new[] { "Alpha1", "Alpha1" },
                    [("alpha", "Alpha[0-9]+")]
                );

                Assert.Equal(1, benchmark.StandardResultCount);
            }
        );
    }

    [Fact]
    public async Task ProcessRegexPatternsBridgeAsync_StrictModeRejectsCpuFallback()
    {
        await WithRapidsAvailabilityAsync(
            available: false,
            async () =>
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    RapidsProcessor.ProcessRegexPatternsBridgeAsync(
                        new HashSet<string> { "Alpha123" },
                        [("alpha", "Alpha[0-9]+")],
                        ro: false,
                        off: false,
                        s: true,
                        sw: null!,
                        q: true,
                        o: string.Empty,
                        allowCpuFallback: false
                    )
                );

                Assert.Contains("fallback is disabled", exception.Message);
            }
        );
    }

    [Fact]
    public async Task ProcessStringsWithRapidsAsync_WhenUnavailable_ThrowsClearError()
    {
        await WithRapidsAvailabilityAsync(
            available: false,
            async () =>
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    RapidsProcessor.ProcessStringsWithRapidsAsync(
                        new[] { "Alpha123" },
                        [("alpha", "Alpha\\d+")],
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                );

                Assert.Contains("RAPIDS is not available", exception.Message);
            }
        );
    }

    [Fact]
    public async Task WaitForExitWithDeadlineAsync_KillsANonTerminatingChild()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("ping -n 30 127.0.0.1 > nul");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 30");
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RapidsProcessor.WaitForExitWithDeadlineAsync(
                process,
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken
            )
        );
        Assert.True(process.HasExited);
    }

    private static async Task WithRapidsAvailabilityAsync(bool available, Func<Task> assertion)
    {
        var processorType = typeof(RapidsProcessor);
        var availableField = processorType.GetField(
            "_rapidsAvailable",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        var checkedField = processorType.GetField(
            "_checkedAvailability",
            BindingFlags.Static | BindingFlags.NonPublic
        );

        Assert.NotNull(availableField);
        Assert.NotNull(checkedField);

        var previousAvailable = (bool)availableField!.GetValue(null)!;
        var previousChecked = (bool)checkedField!.GetValue(null)!;

        try
        {
            availableField.SetValue(null, available);
            checkedField.SetValue(null, true);
            await assertion();
        }
        finally
        {
            availableField.SetValue(null, previousAvailable);
            checkedField.SetValue(null, previousChecked);
        }
    }
}
