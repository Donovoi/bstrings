using System.Reflection;
using bstrings.Rapids;
using Xunit;

namespace bstrings.Tests;

public class RapidsProcessorTests
{
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

                await standardWriter.FlushAsync();
                await bridgeWriter.FlushAsync();
                standardStream.Position = 0;
                bridgeStream.Position = 0;

                using var standardReader = new StreamReader(standardStream);
                using var bridgeReader = new StreamReader(bridgeStream);
                var standardOutput = await standardReader.ReadToEndAsync();
                var bridgeOutput = await bridgeReader.ReadToEndAsync();

                Assert.Equal(standardCount, bridgeCount);
                Assert.Equal(standardOutput, bridgeOutput);
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
                        [("alpha", "Alpha\\d+")]
                    )
                );

                Assert.Contains("RAPIDS is not available", exception.Message);
            }
        );
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
