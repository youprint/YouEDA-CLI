using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.IO;

namespace YouEDA.Engine.Services;

/// <summary>Fetches the public EasyEDA CAD payload for an LCSC part number.</summary>
public sealed class LcscScraper
{
    // EasyEDA's public library endpoint serves a browser client. Keep its web-contract details here.
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateClient();
    private static readonly ComponentRequestGate SharedGate = new(TimeSpan.FromSeconds(1));
    private static Task? _warmUpTask;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly ComponentRequestGate _gate;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouEDA", "ComponentCache");

    public LcscScraper(Action<string>? log = null, HttpClient? httpClient = null,
        string? cacheDirectory = null, ComponentRequestGate? requestGate = null)
    {
        _log = log;
        _http = httpClient ?? Http;
        _cacheDirectory = cacheDirectory ?? CacheDirectory;
        _gate = requestGate ?? SharedGate;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // LCEDA occasionally drops a keep-alive connection without returning a response.
            // Rotate pooled sockets proactively so a stalled connection cannot poison every
            // subsequent component lookup in this desktop session.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        var client = new HttpClient(handler)
        {
            // easyeda.com is currently blocked by its CloudFront distribution for some
            // desktop clients (HTTP 403). LCEDA is EasyEDA's public component-library
            // host and serves the identical v6 payload without that distribution rule.
            BaseAddress = new Uri("https://lceda.cn/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.ParseAdd("text/javascript, */*; q=0.01");
        client.DefaultRequestHeaders.Referrer = new Uri("https://lceda.cn/");
        return client;
    }

    /// <summary>
    /// Starts the TLS/DNS connection while the desktop UI is opening. This is deliberately
    /// best-effort: it must never block the window or change a later component-search result.
    /// </summary>
    public static Task WarmUpAsync()
    {
        return LazyInitializer.EnsureInitialized(ref _warmUpTask, static () => WarmUpCoreAsync())!;
    }

    private static async Task WarmUpCoreAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The real component request retains its normal retry/cache behavior.
        }
    }

    public async Task<string> GetRawComponentAsync(string partNumber, CancellationToken ct = default, bool forceRefresh = false)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                partNumber, "^C\\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("Part number must look like C12345.");

        var normalizedPart = partNumber.ToUpperInvariant();
        ct.ThrowIfCancellationRequested();
        var cached = await ReadCacheAsync(normalizedPart, ct);
        ImportDiagnostics.Record("cad.cache", new { part = normalizedPart, present = cached is not null,
            ageSeconds = cached is null ? (double?)null : (DateTime.UtcNow - cached.Value.ModifiedUtc).TotalSeconds, forceRefresh });
        if (!forceRefresh && cached is not null && DateTime.UtcNow - cached.Value.ModifiedUtc < CacheLifetime)
        {
            _log?.Invoke("Using fresh cached CAD data.");
            return cached.Value.Json;
        }
        // /svgs contains preview graphics only. /components contains dataStr and packageDetail.dataStr.
        var uri = $"api/products/{Uri.EscapeDataString(normalizedPart)}/components?version=6.4.19.5";

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using (ImportDiagnostics.Measure("http.rate-wait", new { service = "CAD", part = normalizedPart, attempt })) await _gate.WaitAsync(ct);
                using var request = ImportDiagnostics.Measure("http.request", new { service = "CAD", part = normalizedPart, attempt });
                _log?.Invoke($"GET {uri} (attempt {attempt}/3)");
                using var response = await _http.GetAsync(uri, ct);
                ImportDiagnostics.Record("http.response", new { service = "CAD", part = normalizedPart, attempt, status = (int)response.StatusCode });

                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"{normalizedPart} was not found by EasyEDA.");

                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new InvalidOperationException(
                        "The EasyEDA component service denied the request (403). This is a provider-side access block, " +
                        "not an Internet-connection failure. Please try again later.");

                if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
                {
                    var delay = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date is { } retryDate ? retryDate - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt * 2));
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    _gate.BackOff(delay);
                    ImportDiagnostics.Record("http.backoff", new { service = "CAD", part = normalizedPart, attempt, delayMs = delay.TotalMilliseconds });
                    if (attempt == 3)
                        throw new HttpRequestException($"EasyEDA returned {(int)response.StatusCode} after retries.");
                    _log?.Invoke($"Server returned {(int)response.StatusCode}; waiting {delay.TotalSeconds:0}s.");
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                ValidateEnvelope(json, normalizedPart);
                await WriteCacheAsync(normalizedPart, json, ct);
                return json;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 3)
            {
                ImportDiagnostics.Record("http.timeout", new { service = "CAD", part = normalizedPart, attempt });
                lastFailure = new TimeoutException("The EasyEDA component request timed out.");
                _log?.Invoke("Request timed out; retrying.");
            }
            catch (HttpRequestException exception) when (attempt < 3)
            {
                ImportDiagnostics.Failure("http.retry", exception, new { service = "CAD", part = normalizedPart, attempt });
                lastFailure = exception;
                _log?.Invoke($"Connection failed ({exception.Message}); retrying.");
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
            catch (Exception exception)
            {
                ImportDiagnostics.Failure("http.failed", exception, new { service = "CAD", part = normalizedPart, attempt });
                lastFailure = exception;
                break;
            }
        }

        if (cached is not null)
        {
            ImportDiagnostics.Record("cad.stale-cache-fallback", new { part = normalizedPart });
            _log?.Invoke("EasyEDA is unavailable; using the last validated local CAD payload.");
            return cached.Value.Json;
        }
        throw new HttpRequestException(
            "EasyEDA request failed after three attempts and no cached CAD payload is available.", lastFailure);
    }

    private string CachePath(string partNumber) => Path.Combine(_cacheDirectory, partNumber + ".json");

    private async Task WriteCacheAsync(string partNumber, string json, CancellationToken ct)
    {
        var temporary = CachePath(partNumber) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(temporary, json, ct);
            File.Move(temporary, CachePath(partNumber), overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache miss must never prevent a valid live EasyEDA import.
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<(string Json, DateTime ModifiedUtc)?> ReadCacheAsync(string partNumber, CancellationToken ct)
    {
        try
        {
            var path = CachePath(partNumber);
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path, ct);
            ValidateEnvelope(json, partNumber);
            return (json, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void ValidateEnvelope(string json, string partNumber)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("EasyEDA returned an empty payload.");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException($"EasyEDA reported no usable CAD data for {partNumber}.");
            if (!root.TryGetProperty("result", out var result) || result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidOperationException($"EasyEDA returned no component CAD result for {partNumber}.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("EasyEDA returned an invalid JSON response.", exception);
        }
    }
}

/// <summary>Shared start-rate limit and provider-requested cooldown, including retries.</summary>
public sealed class ComponentRequestGate(TimeSpan interval)
{
    private readonly object _sync = new();
    private DateTimeOffset _nextStart;

    public void BackOff(TimeSpan delay)
    {
        lock (_sync)
        {
            var until = DateTimeOffset.UtcNow + delay;
            if (until > _nextStart) _nextStart = until;
        }
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan wait;
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                wait = _nextStart - now;
                if (wait <= TimeSpan.Zero)
                {
                    _nextStart = now + interval;
                    return;
                }
            }
            // Recheck the shared deadline after sleeping: another request may extend it.
            await Task.Delay(wait, ct);
        }
    }
}
