using Xunit;

namespace bstrings.Tests;

public sealed class ConsolePercentageProgressTests
{
    [Fact]
    public void Report_PrintsStableTabSafePercentageLines()
    {
        using var writer = new StringWriter();
        var progress = new ConsolePercentageProgress(
            writer,
            minimumPercentStep: 0,
            maximumSilence: TimeSpan.Zero
        );

        progress.Report("analysis", 0, 4, "stages", force: true);
        progress.Report("analysis", 1, 4, "stages", "native extraction", force: true);
        progress.Report("analysis", 4, 4, "stages", force: true);

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Progress: analysis: 0.0% (0/4 stages)", lines[0]);
        Assert.Equal("Progress: analysis: 25.0% (1/4 stages; native extraction)", lines[1]);
        Assert.Equal("Progress: analysis: 100.0% (4/4 stages)", lines[2]);
        Assert.All(lines, line => Assert.DoesNotContain('\t', line));
    }
}
