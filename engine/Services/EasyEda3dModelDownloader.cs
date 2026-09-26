using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YouEDA.Engine.Models;

namespace YouEDA.Engine.Services;

/// <summary>Downloads the public STEP payload referenced by an EasyEDA footprint's uuid_3d field.</summary>
public sealed class EasyEda3dModelDownloader
{
    // This is the same public endpoint used by the EasyEDA client and open-source importers.
    private const string StepEndpoint = "https://modules.easyeda.com/qAxj6KHrDKw4blvCG8QJPs7Y/";
    private const string ObjEndpoint = "https://modules.easyeda.com/3dmodel/";

    private static readonly HttpClient Client = CreateClient();
    private static readonly ComponentRequestGate RequestGate = new(TimeSpan.FromSeconds(1));
    private readonly HttpClient _client;
    private readonly ComponentRequestGate _gate;
    private readonly ModelAssetCache _cache;
    public EasyEda3dModelDownloader(HttpClient? client = null, ComponentRequestGate? gate = null, string? cacheDirectory = null)
    { _client = client ?? Client; _gate = gate ?? RequestGate; _cache = new(cacheDirectory); }

    public async Task<Downloaded3dModel?> TryDownloadAsync(Eda3dModel? model, string outputDirectory, CancellationToken cancellationToken)
    {
        if (model is null || string.IsNullOrWhiteSpace(model.Uuid)) return null;

        cancellationToken.ThrowIfCancellationRequested();
        var cached = await _cache.ReadAsync(model.Uuid, cancellationToken);
        ImportDiagnostics.Record("model.cache", new { uuid = model.Uuid, hit = cached is not null, boundsCached = cached?.HasBounds == true });
        string stepData;
        if (cached is not null) stepData = cached.Step;
        else
        {
            using var response = await GetWithBackoffAsync(StepEndpoint + Uri.EscapeDataString(model.Uuid), cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            stepData = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!ModelAssetCache.IsStep(stepData)) throw new InvalidDataException("EasyEDA returned a 3D payload that is not a complete STEP model.");
        }

        var fileName = MakeSafeFileName(model.Name) + ".step";
        var path = Path.Combine(outputDirectory, fileName);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(path, stepData, new UTF8Encoding(false), cancellationToken);
        var bounds = cached?.HasBounds == true ? new ObjBounds(cached.ZOffsetMm, cached.HeightMm) : await TryGetObjBoundsAsync(model.Uuid, cancellationToken);
        // No negative cache: missing OBJ bounds are retried next time without re-fetching STEP.
        if (cached is null || !cached.HasBounds)
            await _cache.WriteAsync(model.Uuid, stepData, bounds.HasValue, bounds?.ZOffsetMm ?? 0, bounds?.HeightMm ?? 0, cancellationToken, cached?.SavedUtc);
        return new Downloaded3dModel(model, fileName, path, stepData, bounds?.ZOffsetMm ?? 0, bounds?.HeightMm ?? 0);
    }

    private static HttpClient CreateClient()
    {
        // modules.easyeda.com is an older CDN endpoint. Pinning its connection to TLS 1.2
        // avoids an SSPI credential negotiation failure seen with newer Schannel defaults.
        var handler = new HttpClientHandler
        {
            SslProtocols = SslProtocols.Tls12,
            CheckCertificateRevocationList = false,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YouEDA.Engine/1.0");
        client.DefaultRequestHeaders.Referrer = new Uri("https://easyeda.com/");
        return client;
    }

    // OBJ vertices are already in millimetres.  EasyEDALoader uses the lowest vertex
    // as the mounting-plane correction because that information is not in STEP metadata.
    private async Task<ObjBounds?> TryGetObjBoundsAsync(string uuid, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await GetWithBackoffAsync(ObjEndpoint + Uri.EscapeDataString(uuid), cancellationToken);
            if (!response.IsSuccessStatusCode) return default;
            var obj = await response.Content.ReadAsStringAsync(cancellationToken);
            double? minZ = null, maxZ = null;
            foreach (var line in obj.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("v ", StringComparison.OrdinalIgnoreCase)) continue;
                var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (values.Length >= 4 && double.TryParse(values[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z) && double.IsFinite(z))
                {
                    minZ = !minZ.HasValue || z < minZ.Value ? z : minZ;
                    maxZ = !maxZ.HasValue || z > maxZ.Value ? z : maxZ;
                }
            }
            return minZ.HasValue && maxZ.HasValue ? new ObjBounds(Math.Abs(minZ.Value), Math.Max(0, maxZ.Value - minZ.Value)) : default;
        }
        catch (HttpRequestException) { return default; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return default; }
    }

    private async Task<HttpResponseMessage> GetWithBackoffAsync(string uri, CancellationToken ct)
    {
        var service = uri.StartsWith(StepEndpoint, StringComparison.Ordinal) ? "STEP" : "OBJ";
        var modelId = uri[(uri.LastIndexOf('/') + 1)..];
        for (var attempt = 1; ; attempt++)
        {
            using (ImportDiagnostics.Measure("http.rate-wait", new { service, modelId, attempt })) await _gate.WaitAsync(ct);
            using var request = ImportDiagnostics.Measure("http.request", new { service, modelId, attempt });
            HttpResponseMessage response;
            try { response = await _client.GetAsync(uri, ct); }
            catch (Exception exception) { ImportDiagnostics.Failure("http.failed", exception, new { service, modelId, attempt }); throw; }
            ImportDiagnostics.Record("http.response", new { service, modelId, attempt, status = (int)response.StatusCode });
            if ((int)response.StatusCode != 429 && (int)response.StatusCode < 500) return response;
            var delay = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt * 2));
            _gate.BackOff(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
            ImportDiagnostics.Record("http.backoff", new { service, modelId, attempt, delayMs = Math.Max(0, delay.TotalMilliseconds) });
            if (attempt >= 3) return response;
            response.Dispose();
        }
    }

    private static string MakeSafeFileName(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "EasyEDA-3D-Model" : safe;
    }
}

public sealed record Downloaded3dModel(Eda3dModel Source, string FileName, string Path, string StepData, double ZOffsetMm, double HeightMm);
internal readonly record struct ObjBounds(double ZOffsetMm, double HeightMm);
