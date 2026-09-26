using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YouEDA.Engine.Services;

/// <summary>Opt-in local JSON-lines trace; logging failures never stop an import.</summary>
public sealed class ImportDiagnostics : IDisposable
{
    private static readonly AsyncLocal<ImportDiagnostics?> Ambient = new();
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouEDA", "Logs");
    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<long, (string Name, object? Data, long Started)> _operations = [];
    private readonly ImportDiagnostics? _previous;
    private StreamWriter? _writer;
    private Timer? _heartbeat;
    private long _sequence, _operationId;
    private bool _disposed;
    public string? FilePath { get; private set; }
    public string? Error { get; private set; }

    private ImportDiagnostics(string phase, string? directory, TimeSpan heartbeatInterval)
    {
        _previous = Ambient.Value;
        try
        {
            directory ??= DefaultDirectory;
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, $"YouEDA-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{phase}-{Guid.NewGuid():N}.jsonl");
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Write("session.start", new { phase, parentLog = _previous?.FilePath, version = typeof(ImportDiagnostics).Assembly.GetName().Version?.ToString(),
                cpuCount = Environment.ProcessorCount, cadWorkers = 3, priceWorkers = 2, modelWorkers = ModelDownloadBatch.Concurrency,
                requestStartsPerSecond = 1, note = "Local metadata only; no raw CAD, STEP, HTTP headers, or credentials." });
            _heartbeat = new Timer(_ => Heartbeat(), null, heartbeatInterval, heartbeatInterval);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Error = "Diagnostic log unavailable: " + exception.GetType().Name; }
    }

    public static ImportDiagnostics? Begin(bool enabled, string phase, string? directory = null, TimeSpan? heartbeatInterval = null)
    {
        if (!enabled) return null;
        var session = new ImportDiagnostics(phase, directory, heartbeatInterval ?? TimeSpan.FromSeconds(5));
        Ambient.Value = session;
        return session;
    }

    public static void Record(string name, object? data = null) => Ambient.Value?.Write(name, data);
    public static void Failure(string name, Exception exception, object? data = null) =>
        Record(name, new { data, exceptionType = exception.GetType().FullName, exception.HResult,
            // Error codes/types and stack locations identify failures without leaking server bodies
            // or arbitrary response text that an exception message might contain.
            stack = exception.StackTrace });

    public static IDisposable? Measure(string name, object? data = null) => Ambient.Value?.StartOperation(name, data);
    private IDisposable StartOperation(string name, object? data)
    {
        lock (_sync)
        {
            var id = ++_operationId;
            _operations[id] = (name, data, _clock.ElapsedMilliseconds);
            Write("operation.start", new { id, name, data });
            return new Operation(this, id);
        }
    }
    private void EndOperation(long id)
    {
        lock (_sync)
        {
            if (_operations.Remove(id, out var operation))
                Write("operation.end", new { id, name = operation.Name, data = operation.Data,
                    durationMs = _clock.ElapsedMilliseconds - operation.Started });
        }
    }
    private void Heartbeat()
    {
        lock (_sync)
        {
            if (_disposed) return;
            Write("heartbeat", new { pending = _operations.Select(p => new { id = p.Key, name = p.Value.Name,
                data = p.Value.Data, elapsedMs = _clock.ElapsedMilliseconds - p.Value.Started }).ToArray(),
                managedMemoryBytes = GC.GetTotalMemory(false) });
        }
    }
    private void Write(string name, object? data)
    {
        lock (_sync)
        {
            if (_disposed || _writer is null || Error is not null) return;
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(new { sequence = ++_sequence, utc = DateTimeOffset.UtcNow,
                    elapsedMs = _clock.ElapsedMilliseconds, threadId = Environment.CurrentManagedThreadId, name, data }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or JsonException or NotSupportedException)
            { Error = "Diagnostic logging stopped: " + exception.GetType().Name; }
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            Write("session.end", new { unfinishedOperations = _operations.Count, elapsedMs = _clock.ElapsedMilliseconds });
            _disposed = true;
            _heartbeat?.Dispose();
            try { _writer?.Dispose(); }
            catch (IOException) { Error = "Diagnostic log could not be fully flushed."; }
            if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
        }
    }
    private sealed class Operation(ImportDiagnostics owner, long id) : IDisposable
    {
        private int _ended;
        public void Dispose() { if (Interlocked.Exchange(ref _ended, 1) == 0) owner.EndOperation(id); }
    }
}
