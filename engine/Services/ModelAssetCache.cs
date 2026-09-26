using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YouEDA.Engine.Services;

/// <summary>Atomic, validated, seven-day cache keyed by model UUID, never footprint name.</summary>
public sealed class ModelAssetCache(string? directory = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouEDA", "ModelCache");
    public sealed record Entry(int Schema, string Uuid, DateTimeOffset SavedUtc, string Step, string Hash, bool HasBounds, double ZOffsetMm, double HeightMm);
    private string PathFor(string uuid) => Path.Combine(_directory, Hash(uuid.Trim().ToUpperInvariant()) + ".json");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static bool IsStep(string text) => text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("ISO-10303-21;", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("END-ISO-10303-21;", StringComparison.OrdinalIgnoreCase);
    public async Task<Entry?> ReadAsync(string uuid, CancellationToken ct)
    {
        try
        {
            var path = PathFor(uuid);
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path, ct));
            if (entry is null || entry.Schema != 1 || !string.Equals(entry.Uuid, uuid, StringComparison.OrdinalIgnoreCase) ||
                entry.SavedUtc > DateTimeOffset.UtcNow.AddMinutes(5) || DateTimeOffset.UtcNow - entry.SavedUtc >= Lifetime ||
                string.IsNullOrEmpty(entry.Step) || !IsStep(entry.Step) || Hash(entry.Step) != entry.Hash ||
                !double.IsFinite(entry.ZOffsetMm) || !double.IsFinite(entry.HeightMm) || entry.HeightMm < 0) return null;
            return entry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { ImportDiagnostics.Failure("model.cache_read_failed", exception, new { uuid }); return null; }
    }
    public async Task WriteAsync(string uuid, string step, bool hasBounds, double zOffset, double height, CancellationToken ct, DateTimeOffset? savedUtc = null)
    {
        if (!IsStep(step)) return;
        var path = PathFor(uuid);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            var entry = new Entry(1, uuid, savedUtc ?? DateTimeOffset.UtcNow, step, Hash(step), hasBounds, zOffset, height);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(entry), ct);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { ImportDiagnostics.Failure("model.cache_write_failed", exception, new { uuid }); }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
