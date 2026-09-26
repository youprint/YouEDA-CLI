using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using YouEDA.Engine.Models;

namespace YouEDA.Engine.Services;

/// <summary>
/// Reads the public LCSC product page used by JLCPCB's component supply chain.  Pricing is a
/// timestamped supplier snapshot, not a binding PCBA quotation: price tiers, stock, currency,
/// region, and logged-in account terms can all change.
/// </summary>
public sealed class JlcPcbPricingService
{
    private const string SourceName = "LCSC public listing (JLCPCB supply chain)";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private static readonly HttpClient Http = CreateClient();
    private static readonly ComponentRequestGate RequestGate = new(TimeSpan.FromSeconds(1));
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouEDA", "PriceCache");
    private readonly Action<string>? _log;

    public JlcPcbPricingService(Action<string>? log = null) => _log = log;

    public async Task EnrichAsync(EdaComponent component, CancellationToken cancellationToken = default)
    {
        var snapshot = await TryGetAsync(component.LcscPartNumber, cancellationToken);
        if (snapshot is null) return;
        ApplySnapshot(component, snapshot);
    }

    /// <summary>Also normalizes older cached snapshots whose offer price was a bulk-tier price.</summary>
    public static void ApplySnapshot(EdaComponent component, JlcPcbPriceSnapshot snapshot)
    {
        snapshot = NormalizeSnapshot(snapshot);
        foreach (var key in new[] { "JLCPCB Unit Price", "JLCPCB Price Quantity", "JLCPCB Minimum Order Quantity",
                     "JLCPCB Price Basis", "JLCPCB Price Tier Quantity", "JLCPCB Price Tiers", "JLCPCB Stock" })
            component.Properties.Remove(key);

        component.Properties["JLCPCB Price Source"] = SourceName;
        component.Properties["JLCPCB Part URL"] = snapshot.ProductUrl;
        component.Properties["JLCPCB Price Checked UTC"] = snapshot.CheckedUtc.ToString("O", CultureInfo.InvariantCulture);
        component.Properties["JLCPCB Currency"] = snapshot.Currency;
        if (snapshot.UnitPrice is { } price)
        {
            component.Properties["JLCPCB Unit Price"] = FormatMoney(price, snapshot.Currency);
            // The primary parameter is always a per-one-component price, as requested. The
            // supplier's first available tier may require a reel or other minimum quantity, so
            // record that requirement separately instead of presenting it as the unit quantity.
            component.Properties["JLCPCB Price Quantity"] = "1";
            component.Properties["JLCPCB Minimum Order Quantity"] = snapshot.MinimumQuantity.ToString(CultureInfo.InvariantCulture);
            var tier = snapshot.PriceTiers.FirstOrDefault();
            component.Properties["JLCPCB Price Basis"] = tier is null ? "Public offer; quantity eligibility unverified" :
                tier.Quantity == 1 ? "One-piece purchase tier" : "Per-piece estimate at minimum purchase tier; single-piece offer unavailable";
            if (tier is not null)
                component.Properties["JLCPCB Price Tier Quantity"] = tier.Quantity.ToString(CultureInfo.InvariantCulture);
        }
        if (snapshot.Stock is { } stock)
            component.Properties["JLCPCB Stock"] = stock.ToString("N0", CultureInfo.InvariantCulture);
        if (snapshot.PriceTiers.Count > 0)
            component.Properties["JLCPCB Price Tiers"] = string.Join("; ", snapshot.PriceTiers.Select(tier =>
                $"{tier.Quantity.ToString(CultureInfo.InvariantCulture)}+: {FormatMoney(tier.UnitPrice, snapshot.Currency)}"));
    }

    public static JlcPcbPriceSnapshot NormalizeSnapshot(JlcPcbPriceSnapshot snapshot)
    {
        var tiers = snapshot.PriceTiers.Where(t => t.Quantity >= 1 && t.UnitPrice > 0).OrderBy(t => t.Quantity).ToArray();
        return tiers.Length == 0 ? snapshot with { PriceTiers = tiers } : snapshot with
        { UnitPrice = tiers[0].UnitPrice, MinimumQuantity = tiers[0].Quantity, PriceTiers = tiers };
    }

    public async Task<JlcPcbPriceSnapshot?> TryGetAsync(string lcscPartNumber, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(lcscPartNumber, "^C\\d+$", RegexOptions.IgnoreCase)) return null;
        var part = lcscPartNumber.ToUpperInvariant();
        var cached = await ReadCacheAsync(part, cancellationToken);
        if (cached is not null) cached = NormalizeSnapshot(cached);
        ImportDiagnostics.Record("price.cache", new { part, present = cached is not null,
            ageSeconds = cached is null ? (double?)null : (DateTimeOffset.UtcNow - cached.CheckedUtc).TotalSeconds });
        if (cached is { CheckedUtc: var checkedUtc } && DateTimeOffset.UtcNow - checkedUtc < TimeSpan.FromHours(12))
        {
            _log?.Invoke("using cached LCSC/JLCPCB price snapshot");
            return cached;
        }

        try
        {
            _log?.Invoke("retrieving LCSC/JLCPCB price snapshot");
            using (ImportDiagnostics.Measure("http.rate-wait", new { service = "price", part })) await RequestGate.WaitAsync(cancellationToken);
            using var request = ImportDiagnostics.Measure("http.request", new { service = "price", part });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Http.GetAsync($"product-detail/{Uri.EscapeDataString(part)}.html", timeout.Token);
            ImportDiagnostics.Record("http.response", new { service = "price", part, status = (int)response.StatusCode });
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2));
                RequestGate.BackOff(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
                ImportDiagnostics.Record("http.backoff", new { service = "price", part, delayMs = Math.Max(0, delay.TotalMilliseconds) });
            }
            if (!response.IsSuccessStatusCode) return cached;
            var html = await response.Content.ReadAsStringAsync(timeout.Token);
            var parsed = ParseProductPage(html, part, DateTimeOffset.UtcNow);
            if (parsed is null) return cached;
            await WriteCacheAsync(part, parsed, cancellationToken);
            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            ImportDiagnostics.Failure("price.optional_failure", exception, new { part });
            _log?.Invoke("price snapshot unavailable; continuing component import");
            return cached;
        }
    }

    /// <summary>Public for deterministic parser tests; the live HTTP request is deliberately separate.</summary>
    public static JlcPcbPriceSnapshot? ParseProductPage(string html, string lcscPartNumber, DateTimeOffset checkedUtc)
    {
        var productUrl = $"https://www.lcsc.com/product-detail/{lcscPartNumber.ToUpperInvariant()}.html";
        decimal? offerPrice = null;
        long? offerStock = null;
        var currency = "USD";
        foreach (Match match in Regex.Matches(html, "<script[^>]+type=[\"']application/ld\\+json[\"'][^>]*>(?<json>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            try
            {
                using var document = JsonDocument.Parse(match.Groups["json"].Value);
                var root = document.RootElement;
                if (!root.TryGetProperty("@type", out var type) || type.GetString() != "Product" ||
                    !root.TryGetProperty("sku", out var sku) || !sku.GetString()!.Equals(lcscPartNumber, StringComparison.OrdinalIgnoreCase)) continue;
                if (!root.TryGetProperty("offers", out var offers)) continue;
                offerPrice = DecimalProperty(offers, "price");
                offerStock = IntegerProperty(offers, "inventoryLevel");
                currency = StringProperty(offers, "priceCurrency") ?? currency;
                break;
            }
            catch (JsonException) { }
        }

        var tiers = ParsePriceTiers(html, lcscPartNumber);
        if (tiers.Count > 0)
        {
            offerPrice = tiers[0].UnitPrice;
            currency = NormalizeCurrency(tiers[0].Currency) ?? currency;
        }
        if (offerPrice is null && offerStock is null && tiers.Count == 0) return null;
        return new JlcPcbPriceSnapshot(productUrl, currency, offerPrice, tiers.FirstOrDefault()?.Quantity ?? 1,
            offerStock, tiers, checkedUtc);
    }

    private static List<JlcPcbPriceTier> ParsePriceTiers(string html, string lcscPartNumber)
    {
        var match = Regex.Match(html, "<script[^>]+id=[\"']__NEXT_DATA__[\"'][^>]*>(?<json>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return [];
        try
        {
            using var document = JsonDocument.Parse(match.Groups["json"].Value);
            var product = FindProduct(document.RootElement, lcscPartNumber);
            if (product.ValueKind != JsonValueKind.Object || !product.TryGetProperty("productPriceList", out var prices) || prices.ValueKind != JsonValueKind.Array)
                return [];
            return prices.EnumerateArray()
                .Select(item => new JlcPcbPriceTier(
                    IntegerProperty(item, "ladder") ?? 1,
                    DecimalProperty(item, "usdPrice") ?? DecimalProperty(item, "productPrice") ?? 0m,
                    DecimalProperty(item, "usdPrice") is not null ? "USD" : StringProperty(item, "currencySymbol")))
                .Where(tier => tier.UnitPrice > 0 && tier.Quantity >= 1)
                .OrderBy(tier => tier.Quantity).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static JsonElement FindProduct(JsonElement item, string part)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            if (StringProperty(item, "productCode")?.Equals(part, StringComparison.OrdinalIgnoreCase) == true &&
                item.TryGetProperty("productPriceList", out _)) return item;
            foreach (var property in item.EnumerateObject())
            {
                var found = FindProduct(property.Value, part);
                if (found.ValueKind == JsonValueKind.Object) return found;
            }
        }
        else if (item.ValueKind == JsonValueKind.Array)
            foreach (var child in item.EnumerateArray())
            {
                var found = FindProduct(child, part);
                if (found.ValueKind == JsonValueKind.Object) return found;
            }
        return default;
    }

    private static decimal? DecimalProperty(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
        decimal.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static long? IntegerProperty(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
        long.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static string? StringProperty(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? NormalizeCurrency(string? value) => value switch
    {
        "$" => "USD",
        "¥" => "CNY",
        { Length: 3 } => value.ToUpperInvariant(),
        _ => null
    };
    private static string FormatMoney(decimal value, string currency) => value.ToString("0.########", CultureInfo.InvariantCulture) + " " + currency;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            BaseAddress = new Uri("https://www.lcsc.com/"), Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return client;
    }

    private static string CachePath(string part) => Path.Combine(CacheDirectory, part + ".json");
    private static async Task<JlcPcbPriceSnapshot?> ReadCacheAsync(string part, CancellationToken ct)
    {
        try
        {
            var path = CachePath(part);
            return File.Exists(path) ? JsonSerializer.Deserialize<JlcPcbPriceSnapshot>(await File.ReadAllTextAsync(path, ct)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static async Task WriteCacheAsync(string part, JlcPcbPriceSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllTextAsync(CachePath(part), JsonSerializer.Serialize(snapshot), ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

public sealed record JlcPcbPriceTier(long Quantity, decimal UnitPrice, string? Currency);
public sealed record JlcPcbPriceSnapshot(string ProductUrl, string Currency, decimal? UnitPrice, long MinimumQuantity,
    long? Stock, IReadOnlyList<JlcPcbPriceTier> PriceTiers, DateTimeOffset CheckedUtc);
