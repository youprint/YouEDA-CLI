using YouEDA.Engine.Services;

namespace YouEDA.CLI.Terminal;

/// <summary>
/// Renders a single in-place terminal progress bar (redrawn with \r, no new lines) showing
/// percentage complete, counts, throughput, elapsed time, and ETA. Only used when stdout is an
/// actual interactive console; a redirected/piped output falls back to plain periodic log lines
/// (see ImportJobRunner's own onProgress-less path), since carriage-return redraws are meaningless
/// once captured to a file or another process.
/// </summary>
public sealed class ProgressBar
{
    private const int BarWidth = 30;
    private int _lastLineLength;

    public static bool IsSupported => !Console.IsOutputRedirected;

    public void Report(ImportProgress progress)
    {
        var fraction = progress.Pending == 0 ? 1.0 : Math.Clamp((double)progress.Done / progress.Pending, 0, 1);
        var filled = (int)Math.Round(fraction * BarWidth);
        var bar = new string('#', filled) + new string('-', BarWidth - filled);
        var percent = (int)Math.Round(fraction * 100);

        var line =
            $"[{bar}] {percent,3}% {progress.Done}/{progress.Pending} | " +
            $"{progress.Succeeded} ok, {progress.Failed} failed | {progress.RatePerMinute:0.0}/min | " +
            $"elapsed {Format(progress.Elapsed)} | ETA {Format(progress.Eta)}";

        // Pad over any leftover characters from a longer previous line, then return the cursor
        // to column 0 without emitting a newline, so the next tick overwrites this one in place.
        var padded = line.Length < _lastLineLength ? line.PadRight(_lastLineLength) : line;
        Console.Write('\r' + padded + '\r');
        _lastLineLength = line.Length;
    }

    /// <summary>Clears the current bar line so a caller can print a normal message above it (e.g. a failure), then finish() to move past it.</summary>
    public void Clear()
    {
        if (_lastLineLength == 0) return;
        Console.Write('\r' + new string(' ', _lastLineLength) + '\r');
    }

    public void Finish()
    {
        if (_lastLineLength == 0) return;
        Console.WriteLine();
        _lastLineLength = 0;
    }

    private static string Format(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h{duration.Minutes:00}m"
        : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s" : $"{duration.Seconds}s";
}
