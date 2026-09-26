using System.IO;
using System.Net.Http;
using YouEDA.Engine.Models;

namespace YouEDA.Engine.Services;

/// <summary>Three shared model workers, with bounded look-ahead for active family consumers.</summary>
public sealed class ModelDownloadBatch : IAsyncDisposable
{
    public const int Concurrency = 3;
    private int _active, _finished;
    public int Active => Volatile.Read(ref _active);
    public int Finished => Volatile.Read(ref _finished);
    public int Total => _results.Count;
    public int UniqueModels => _claimed.Length;
    public int ReusedParts => Total - UniqueModels;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, TaskCompletionSource<Downloaded3dModel?>> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueLock = new();
    private readonly Dictionary<string, string[]> _priorities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _indices = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool[] _claimed;
    private int _next;
    private readonly Task _workers;
    private readonly string _staging = Path.Combine(Path.GetTempPath(), "YouEDA-models-" + Guid.NewGuid().ToString("N"));
    public ModelDownloadBatch(IReadOnlyList<EdaComponent> components,
        Func<Eda3dModel?, string, CancellationToken, Task<Downloaded3dModel?>>? download = null)
    {
        download ??= new EasyEda3dModelDownloader().TryDownloadAsync;
        var items = components.DistinctBy(c => c.LcscPartNumber, StringComparer.OrdinalIgnoreCase)
            .GroupBy(c => string.IsNullOrWhiteSpace(c.ThreeDModel?.Uuid) ? "part:" + c.LcscPartNumber : "model:" + c.ThreeDModel.Uuid.Trim(),
                StringComparer.OrdinalIgnoreCase).Select(g => g.ToArray()).ToArray();
        _claimed = new bool[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            foreach (var item in items[index])
            {
                _results.Add(item.LcscPartNumber, new(TaskCreationOptions.RunContinuationsAsynchronously));
                _indices.Add(item.LcscPartNumber, index);
            }
        }
        ImportDiagnostics.Record("models.queue", new { parts = Total, uniqueModels = UniqueModels, reusedParts = ReusedParts, concurrency = Concurrency });
        async Task Worker()
        {
            while (!_stop.IsCancellationRequested)
            {
                var index = TakeNext();
                if (index < 0) return;
                var aliases = items[index];
                var component = aliases[0];
                Interlocked.Increment(ref _active);
                using var operation = ImportDiagnostics.Measure("model.download", new { part = component.LcscPartNumber, modelId = component.ThreeDModel?.Uuid });
                try
                {
                    // Index rather than a supplier-provided string keeps staging paths bounded.
                    var directory = Path.Combine(_staging, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(directory);
                    var result = await download(component.ThreeDModel, directory, _stop.Token).ConfigureAwait(false);
                    ImportDiagnostics.Record("model.result", new { part = component.LcscPartNumber, available = result is not null });
                    foreach (var alias in aliases)
                    {
                        // Payload is shared; placement belongs to the individual component.
                        _results[alias.LcscPartNumber].TrySetResult(result is null ? null : result with { Source = alias.ThreeDModel ?? result.Source });
                        if (!ReferenceEquals(alias, component)) ImportDiagnostics.Record("model.reused", new { part = alias.LcscPartNumber, fromPart = component.LcscPartNumber });
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { ImportDiagnostics.Record("model.cancelled", new { part = component.LcscPartNumber }); foreach (var alias in aliases) _results[alias.LcscPartNumber].TrySetCanceled(_stop.Token); }
                catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException)
                {
                    // Models are optional; a server timeout must not reject usable CAD.
                    ImportDiagnostics.Failure("model.optional_failure", exception, new { part = component.LcscPartNumber });
                    foreach (var alias in aliases) _results[alias.LcscPartNumber].TrySetResult(null);
                }
                catch (Exception exception) { ImportDiagnostics.Failure("model.failed", exception, new { part = component.LcscPartNumber }); foreach (var alias in aliases) _results[alias.LcscPartNumber].TrySetException(exception); }
                finally { Interlocked.Decrement(ref _active); Interlocked.Add(ref _finished, aliases.Length); }
            }
        }
        _workers = Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => Worker()));
    }

    public Task<Downloaded3dModel?> GetAsync(string part) =>
        _results.TryGetValue(part, out var result) ? result.Task : Task.FromResult<Downloaded3dModel?>(null);

    /// <summary>
    /// Current part first, followed by at most two future parts of one active family.
    /// Current requests across families outrank look-ahead; all other work retains CSV order.
    /// Already claimed downloads are never cancelled/restarted. An empty window releases priority.
    /// </summary>
    public void SetPriority(string family, IEnumerable<string> parts)
    {
        // Several LCSC parts may share a model; aliases must not consume all look-ahead slots.
        var window = parts.Where(p => _indices.ContainsKey(p)).DistinctBy(p => _indices[p]).Take(3).ToArray();
        lock (_queueLock)
        {
            if (window.Length == 0) _priorities.Remove(family);
            else _priorities[family] = window;
            ImportDiagnostics.Record("models.priority", new { family, parts = window });
        }
    }

    private int TakeNext()
    {
        lock (_queueLock)
        {
            // Round-robin by look-ahead depth: never drain one family's entire window
            // before considering another family's current dependency.
            for (var depth = 0; depth < 3; depth++)
                foreach (var window in _priorities.Values)
                    if (depth < window.Length && _indices.TryGetValue(window[depth], out var index) && !_claimed[index])
                    {
                        _claimed[index] = true;
                        ImportDiagnostics.Record("models.dispatch", new { index, part = window[depth], reason = depth == 0 ? "current-family-dependency" : "family-lookahead" });
                        return index;
                    }
            while (_next < _claimed.Length && _claimed[_next]) _next++;
            if (_next == _claimed.Length) return -1;
            _claimed[_next] = true;
            ImportDiagnostics.Record("models.dispatch", new { index = _next, reason = "csv-prefetch" });
            return _next++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var disposal = ImportDiagnostics.Measure("models.stop-and-join", new { Finished, Total, Active });
        await _stop.CancelAsync();
        await _workers;
        foreach (var result in _results.Values) result.TrySetCanceled(_stop.Token);
        _stop.Dispose();
        // Only the unique directory allocated by this instance is removed.
        try { if (Directory.Exists(_staging)) Directory.Delete(_staging, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
