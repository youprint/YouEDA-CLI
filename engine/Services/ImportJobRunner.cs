using System.IO;
using System.Text.Json;
using YouEDA.Engine.Models;

namespace YouEDA.Engine.Services;

public sealed record ImportJobOptions
{
    public required string OutputPath { get; init; }
    public string? SymbolLibraryPath { get; init; }
    public int Workers { get; init; } = 1;
    public bool Resume { get; init; }
    public bool With3D { get; init; }
    public int CheckpointInterval { get; init; } = 10;
}

public sealed record ImportJobResult(int Total, int Succeeded, int Failed, int SkippedAlreadyDone);

/// <summary>
/// Orchestrates an import for an already-resolved list of LCSC part numbers, whether they came
/// from a BOM/CSV (<see cref="CsvBomReader"/>) or a --category/--catalog filter
/// (<see cref="CatalogFilter"/>): bounded parallel fetch/parse against
/// <see cref="LcscScraper"/>/<see cref="Parser"/>, serialized through the single-writer
/// <see cref="BulkLibraryWriter"/>, with a completion ledger for --resume and a JSON-lines
/// error log for failed parts. Mirrors the checkpoint/retry rules in YouEDA's AGENTS.md.
/// </summary>
public static class ImportJobRunner
{
    public static async Task<ImportJobResult> RunAsync(
        IReadOnlyList<string> allParts, ImportJobOptions options, Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.OutputPath);
        var stateDirectory = Path.Combine(options.OutputPath, ".youeda-bulk");
        var cacheDirectory = Path.Combine(stateDirectory, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var completedPath = Path.Combine(stateDirectory, "completed.txt");
        var errorsPath = Path.Combine(stateDirectory, "errors.jsonl");

        if (!options.Resume)
        {
            if (File.Exists(completedPath)) File.Delete(completedPath);
            if (File.Exists(errorsPath)) File.Delete(errorsPath);
        }

        var completed = options.Resume && File.Exists(completedPath)
            ? new HashSet<string>(await File.ReadAllLinesAsync(completedPath, cancellationToken), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var pending = allParts.Where(part => !completed.Contains(part)).ToArray();
        log?.Invoke($"{allParts.Count} parts in input, {completed.Count} already checkpointed, {pending.Length} to process.");

        var scraper = new LcscScraper(log, cacheDirectory: cacheDirectory);
        var parser = new Parser();
        var writer = new BulkLibraryWriter(options.OutputPath, options.SymbolLibraryPath, options.With3D);
        await writer.InitializeAsync();

        await using var completedWriter = new StreamWriter(
            new FileStream(completedPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        await using var errorsWriter = new StreamWriter(
            new FileStream(errorsPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

        // The writer, ledger, and error log are all single-writer by construction (per
        // BulkLibraryWriter's own contract); only fetch+parse actually runs in parallel.
        using var writeLock = new SemaphoreSlim(1, 1);
        using var fetchGate = new SemaphoreSlim(Math.Max(1, options.Workers));
        var succeeded = 0;
        var failed = 0;
        var sinceCheckpoint = 0;

        async Task ProcessAsync(string part)
        {
            await fetchGate.WaitAsync(cancellationToken);
            EdaComponent? component = null;
            string? fetchError = null;
            try
            {
                var raw = await scraper.GetRawComponentAsync(part, cancellationToken);
                component = parser.Parse(part, raw);
            }
            catch (Exception exception)
            {
                fetchError = exception.Message;
            }
            finally
            {
                fetchGate.Release();
            }

            await writeLock.WaitAsync(cancellationToken);
            try
            {
                if (component is null)
                {
                    failed++;
                    await errorsWriter.WriteLineAsync(JsonSerializer.Serialize(new { part, phase = "fetch", error = fetchError }));
                    log?.Invoke($"[FAIL] {part}: {fetchError}");
                    return;
                }

                try
                {
                    await writer.AddAsync(component, cancellationToken);
                    await completedWriter.WriteLineAsync(part);
                    succeeded++;
                    log?.Invoke($"[OK]   {part} ({succeeded + failed}/{pending.Length})");

                    if (++sinceCheckpoint >= Math.Max(1, options.CheckpointInterval))
                    {
                        sinceCheckpoint = 0;
                        await writer.CheckpointAsync();
                        log?.Invoke($"-- checkpoint: {succeeded} succeeded, {failed} failed --");
                    }
                }
                catch (Exception exception)
                {
                    failed++;
                    await errorsWriter.WriteLineAsync(JsonSerializer.Serialize(new { part, phase = "export", error = exception.Message }));
                    log?.Invoke($"[FAIL] {part}: {exception.Message}");
                }
            }
            finally
            {
                writeLock.Release();
            }
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressTask = ReportProgressAsync(progressCts.Token);

        await Task.WhenAll(pending.Select(ProcessAsync));
        progressCts.Cancel();
        try { await progressTask; } catch (OperationCanceledException) { }

        await writer.CheckpointAsync();

        return new ImportJobResult(allParts.Count, succeeded, failed, completed.Count);

        async Task ReportProgressAsync(CancellationToken progressToken)
        {
            // Mirrors YouEDA's desktop live speed indicator: average throughput, elapsed time,
            // and an ETA, updated on a periodic tick even while requests are waiting on the
            // network. This never blocks or slows the actual import work.
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), progressToken);
                var done = succeeded + failed;
                if (done == 0 || done >= pending.Length) continue;
                var ratePerMinute = done / Math.Max(stopwatch.Elapsed.TotalMinutes, 0.001);
                var remaining = pending.Length - done;
                var eta = ratePerMinute > 0 ? TimeSpan.FromMinutes(remaining / ratePerMinute) : TimeSpan.Zero;
                log?.Invoke(
                    $"-- progress: {done}/{pending.Length} ({succeeded} ok, {failed} failed) | " +
                    $"{ratePerMinute:0.0}/min | elapsed {FormatDuration(stopwatch.Elapsed)} | ETA {FormatDuration(eta)} --");
            }
        }
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h{duration.Minutes:00}m"
        : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s" : $"{duration.Seconds}s";
}
