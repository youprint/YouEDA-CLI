using System.IO;
using System.Text.RegularExpressions;

namespace YouEDA.Engine.Services;

/// <summary>
/// Parses a BOM/CSV list of LCSC part references, using the same lightweight contract as
/// YouEDA's GUI "Import CSV" command: an unquoted `C` plus digits, separated by newline,
/// comma, semicolon, tab, or space. Headers and extra columns are ignored; duplicates removed.
/// </summary>
public static class CsvBomReader
{
    private static readonly Regex PartPattern = new(@"^C\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> ReadPartNumbers(string path)
    {
        var text = File.ReadAllText(path);
        var tokens = text.Split(['\n', '\r', ',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var token in tokens)
        {
            if (!PartPattern.IsMatch(token)) continue;
            var normalized = token.ToUpperInvariant();
            if (seen.Add(normalized)) result.Add(normalized);
        }
        return result;
    }
}
