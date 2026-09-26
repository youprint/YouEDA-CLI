using System.IO;
using System.Text.RegularExpressions;

namespace YouEDA.Engine.Services;

/// <summary>
/// Criteria for a --category/--catalog import. There is no YouEDA equivalent to port for this
/// (the desktop GUI only accepts a flat LCSC-code list; see MIGRATION_PLAN.md's "New CLI-specific
/// code" section) — this is new code written against a locally-supplied LCSC catalog export.
/// LCSC does not publish a single fixed export schema, so column lookup below is deliberately
/// tolerant: it matches likely header names case-insensitively and falls back to searching every
/// column's text for a match, rather than assuming one exact layout.
/// </summary>
public sealed record CatalogFilterCriteria
{
    public required string Category { get; init; }
    public string? Manufacturer { get; init; }
    public string? Package { get; init; }
    public string? PartClass { get; init; }
    public string? Resistance { get; init; }
    public string? Capacitance { get; init; }
    public string? Inductance { get; init; }
    public string? Voltage { get; init; }
    public string? Tolerance { get; init; }
    public string? Power { get; init; }
    public string? Dielectric { get; init; }
    public string? Mounting { get; init; }
    public IReadOnlyList<string> Contains { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

/// <summary>
/// Filters a local LCSC catalog export (CSV) down to the matching LCSC part numbers. Every
/// supplied condition is an AND filter, per the README's documented command reference.
/// </summary>
public static class CatalogFilter
{
    private static readonly string[] PartNumberHeaders = ["lcscpart", "lcscpartnumber", "lcsc", "partnumber", "part"];
    private static readonly string[] ManufacturerHeaders = ["manufacturer", "mfr", "brand", "mfgname"];
    private static readonly string[] PackageHeaders = ["package", "footprint", "case"];
    private static readonly string[] PartClassHeaders = ["librarytype", "class", "assemblytype", "partclass", "basicextended"];
    private static readonly string[] CategoryHeaders = ["category", "firstcategory", "secondcategory", "subcategory"];

    public static IReadOnlyList<string> Filter(string catalogPath, CatalogFilterCriteria criteria)
    {
        using var reader = new StreamReader(catalogPath);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("Catalog file has no header row.");
        var headers = SplitCsvLine(headerLine);
        var normalizedHeaders = headers.Select(NormalizeHeader).ToArray();

        var partNumberColumn = FindColumn(normalizedHeaders, PartNumberHeaders)
            ?? throw new InvalidDataException("Catalog file has no recognizable LCSC part-number column.");
        var manufacturerColumn = FindColumn(normalizedHeaders, ManufacturerHeaders);
        var packageColumn = FindColumn(normalizedHeaders, PackageHeaders);
        var partClassColumn = FindColumn(normalizedHeaders, PartClassHeaders);
        var categoryColumn = FindColumn(normalizedHeaders, CategoryHeaders);

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitCsvLine(line);
            if (fields.Length != headers.Length) continue; // Skip malformed/truncated rows rather than guessing.

            var partNumber = fields[partNumberColumn].Trim();
            if (!Regex.IsMatch(partNumber, "^C\\d+$", RegexOptions.IgnoreCase)) continue;

            var rowText = string.Join(" | ", fields);

            if (!MatchesColumnOrRow(fields, categoryColumn, rowText, criteria.Category)) continue;
            if (criteria.Manufacturer is not null && !MatchesColumnOrRow(fields, manufacturerColumn, rowText, criteria.Manufacturer)) continue;
            if (criteria.Package is not null && !MatchesColumnOrRow(fields, packageColumn, rowText, criteria.Package)) continue;
            if (criteria.PartClass is not null && !MatchesColumnOrRow(fields, partClassColumn, rowText, criteria.PartClass)) continue;
            if (criteria.Resistance is not null && !RowContains(rowText, criteria.Resistance)) continue;
            if (criteria.Capacitance is not null && !RowContains(rowText, criteria.Capacitance)) continue;
            if (criteria.Inductance is not null && !RowContains(rowText, criteria.Inductance)) continue;
            if (criteria.Voltage is not null && !RowContains(rowText, criteria.Voltage)) continue;
            if (criteria.Tolerance is not null && !RowContains(rowText, criteria.Tolerance)) continue;
            if (criteria.Power is not null && !RowContains(rowText, criteria.Power)) continue;
            if (criteria.Dielectric is not null && !RowContains(rowText, criteria.Dielectric)) continue;
            if (criteria.Mounting is not null && !RowContains(rowText, criteria.Mounting)) continue;
            if (criteria.Contains.Any(term => !RowContains(rowText, term))) continue;
            if (criteria.Exclude.Any(term => RowContains(rowText, term))) continue;

            var normalized = partNumber.ToUpperInvariant();
            if (seen.Add(normalized)) results.Add(normalized);
        }

        return results;
    }

    private static bool MatchesColumnOrRow(string[] fields, int? column, string rowText, string term) =>
        column is { } index ? RowContains(fields[index], term) : RowContains(rowText, term);

    /// <summary>
    /// Case-insensitive substring match after normalizing common electrical-unit spellings
    /// (µ/μ → u, Ω → ohm) so "10uF" matches a catalog cell written as "10µF".
    /// </summary>
    private static bool RowContains(string haystack, string term) =>
        Normalize(haystack).Contains(Normalize(term), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) => value
        .Replace('µ', 'u').Replace('μ', 'u')
        .Replace('Ω', 'R').Replace("ohm", "R", StringComparison.OrdinalIgnoreCase)
        .Trim();

    private static string NormalizeHeader(string header) =>
        new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int? FindColumn(string[] normalizedHeaders, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var index = Array.IndexOf(normalizedHeaders, candidate);
            if (index >= 0) return index;
        }
        // Fall back to a contains match (e.g. "LCSC Part Number#" or "Mfr.Manufacturer").
        for (var i = 0; i < normalizedHeaders.Length; i++)
            if (candidates.Any(candidate => normalizedHeaders[i].Contains(candidate)))
                return i;
        return null;
    }

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
