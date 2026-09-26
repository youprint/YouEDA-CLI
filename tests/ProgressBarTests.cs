using YouEDA.CLI.Terminal;
using YouEDA.Engine.Services;

namespace YouEDA.Engine.Tests;

/// <summary>
/// Verifies the progress bar's actual rendered output by capturing Console.Out, since this
/// harness's Console.IsOutputRedirected is always true (stdout is piped) and so never exercises
/// ProgressBar.IsSupported's true branch end-to-end. These tests construct a ProgressBar
/// directly and inspect what it writes, independent of that terminal detection.
/// </summary>
public class ProgressBarTests
{
    private static string CaptureReport(ImportProgress progress)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            new ProgressBar().Report(progress);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return writer.ToString();
    }

    [Fact]
    public void ShowsPercentageDoneAndTotal()
    {
        var output = CaptureReport(new ImportProgress(
            Done: 175, Pending: 351, Succeeded: 170, Failed: 5,
            Elapsed: TimeSpan.FromMinutes(3), RatePerMinute: 58.3, Eta: TimeSpan.FromMinutes(3)));

        Assert.Contains("50%", output);
        Assert.Contains("175/351", output);
        Assert.Contains("170 ok, 5 failed", output);
    }

    [Fact]
    public void FillsTheBarProportionallyToCompletion()
    {
        var half = CaptureReport(new ImportProgress(50, 100, 50, 0, TimeSpan.FromMinutes(1), 50, TimeSpan.FromMinutes(1)));
        var full = CaptureReport(new ImportProgress(100, 100, 100, 0, TimeSpan.FromMinutes(2), 50, TimeSpan.Zero));

        // 30-character bar: half done -> 15 filled, fully done -> all 30 filled.
        Assert.Contains(new string('#', 15) + new string('-', 15), half);
        Assert.Contains(new string('#', 30), full);
        Assert.DoesNotContain('-', full.Split(']')[0]);
    }

    [Fact]
    public void RedrawsInPlaceWithCarriageReturnAndNoNewline()
    {
        var output = CaptureReport(new ImportProgress(1, 10, 1, 0, TimeSpan.FromSeconds(5), 12, TimeSpan.FromSeconds(45)));

        Assert.StartsWith("\r", output);
        Assert.EndsWith("\r", output);
        Assert.DoesNotContain('\n', output);
    }
}
