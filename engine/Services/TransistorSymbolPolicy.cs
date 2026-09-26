using System.Text.RegularExpressions;
using YouEDA.Engine.Models;

namespace YouEDA.Engine.Services;

/// <summary>Electrical identity, never package-only or pin-count-only transistor matching.</summary>
public static class TransistorSymbolPolicy
{
    // Reviewed against the supplier records linked in docs/transistor-symbol-profiles.md.
    // Match BOTH supplier reference and exact MPN. Numeric source pins are permitted only
    // for C105432, whose manufacturer datasheet explicitly supplies the missing B/E/C roles.
    private sealed record Profile(string Mpn, string Kind, string[] Roles, bool NumericPins = false);
    private static readonly Dictionary<string, Profile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C10487"] = new("SI2301CDS-T1-GE3", "PMOS", ["G", "S", "D"]),
        ["C15127"] = new("AO3401A", "PMOS", ["G", "S", "D"]),
        ["C20917"] = new("AO3400A", "NMOS", ["G", "S", "D"]),
        ["C8545"] = new("2N7002", "NMOS", ["G", "S", "D"]),
        ["C105432"] = new("S8550(RANGE:120-200)", "PNP", ["B", "E", "C"], true),
        ["C9634"] = new("D882(RANGE:160-320)", "NPN", ["B", "C", "E"]),
        ["C20526"] = new("MMBT3904(RANGE:100-300)", "NPN", ["B", "E", "C"]),
        ["C2145"] = new("MMBT5551(RANGE:200-300)", "NPN", ["B", "E", "C"]),
        ["C2146"] = new("S8050 J3Y(RANGE:200-350)", "NPN", ["B", "E", "C"]),
        ["C2150"] = new("SS8050(RANGE:200-350)", "NPN", ["B", "E", "C"]),
        ["C6749"] = new("S9013 J3(RANGE:200-350)", "NPN", ["B", "E", "C"]),
        ["C8512"] = new("MMBT2222A 1P", "NPN", ["B", "E", "C"]),
        ["C8326"] = new("MMBT5401(RANGE:200-300)", "PNP", ["B", "E", "C"]),
        ["C8542"] = new("SS8550 Y2(RANGE:200-350)", "PNP", ["B", "E", "C"]),
        ["C8543"] = new("S9012 2T1(RANGE:200-350)", "PNP", ["B", "E", "C"])
    };

    private static string Evidence(EdaComponent part) => string.Join(' ', part.Tags.Append(part.Description)
        .Concat(part.Properties.Where(p => p.Key is "Category" or "Component Type" or "Technology" or "Polarity" or "Channel Type")
            .Select(p => p.Value))).ToUpperInvariant();

    public static bool Applies(EdaComponent part) => Profiles.ContainsKey(part.LcscPartNumber) ||
        Regex.IsMatch(Evidence(part), @"\b(MOSFETS?|BJT|TRANSISTORS?|NPN|PNP|NMOS|PMOS)\b");

    private static Profile? VerifiedProfile(EdaComponent part) =>
        Profiles.TryGetValue(part.LcscPartNumber, out var profile) &&
        part.Properties.TryGetValue("Manufacturer Part", out var mpn) &&
        mpn.Trim().Equals(profile.Mpn, StringComparison.OrdinalIgnoreCase) ? profile : null;

    public static string? Function(string name) => name.Trim().ToUpperInvariant() switch
    {
        "G" or "GATE" => "G", "S" or "SOURCE" => "S", "D" or "DRAIN" => "D",
        "B" or "BASE" => "B", "E" or "EMITTER" => "E", "C" or "COLLECTOR" => "C", _ => null
    };

    public static string? SourceFunction(EdaComponent part, string number, string name)
    {
        var role = Function(name);
        if (role is not null) return role;
        var profile = VerifiedProfile(part);
        // A blank/numeric name is missing information; an unrecognised label is not.
        return profile is { NumericPins: true } && (string.IsNullOrWhiteSpace(name) || name.Trim() == number.Trim()) &&
            int.TryParse(number, out var index) && index is >= 1 and <= 3 ? profile.Roles[index - 1] : null;
    }

    public static TransistorPinBinding? Resolve(EdaComponent part)
    {
        if (!Applies(part) || part.SymbolPins.Count != 3 || part.SymbolUnits.Count > 1) return null;
        var evidence = Evidence(part);
        if (Regex.IsMatch(evidence, @"DUAL|ARRAY|DARLINGTON|PRE.?BIASED|DIGITAL TRANSISTOR|JFET|IGBT|DEPLETION|PHOTO|OPTO")) return null;
        var kinds = new List<string>();
        if (Regex.IsMatch(evidence, @"\bNPN\b")) kinds.Add("NPN");
        if (Regex.IsMatch(evidence, @"\bPNP\b")) kinds.Add("PNP");
        if (Regex.IsMatch(evidence, @"\bNMOS\b|\bN[ -]CH(?:ANNEL)?\b")) kinds.Add("NMOS");
        if (Regex.IsMatch(evidence, @"\bPMOS\b|\bP[ -]CH(?:ANNEL)?\b")) kinds.Add("PMOS");
        var profile = VerifiedProfile(part);
        if (profile is not null) kinds.Add(profile.Kind);
        var uniqueKinds = kinds.Distinct().ToArray();
        if (uniqueKinds.Length != 1) return null;
        var kind = uniqueKinds[0];
        var roles = kind is "NMOS" or "PMOS" ? new[] { "G", "D", "S" } : new[] { "B", "C", "E" };
        var pins = part.SymbolPins.Select(p => (Number: p.Number.Trim(), Role: SourceFunction(part, p.Number, p.Name))).ToArray();
        if (pins.Any(p => p.Number.Length == 0 || p.Role is null) || pins.Select(p => p.Number).Distinct().Count() != 3 ||
            !pins.Select(p => p.Role).Order().SequenceEqual(roles.Order())) return null;
        if (profile is not null && pins.Any(p => !int.TryParse(p.Number, out var n) || n is < 1 or > 3 || profile.Roles[n - 1] != p.Role)) return null;
        return new(kind, pins.Single(p => p.Role == roles[0]).Number, pins.Single(p => p.Role == roles[1]).Number,
            pins.Single(p => p.Role == roles[2]).Number,
            profile is null ? "explicit electrical metadata and pin functions" : $"https://www.lcsc.com/product-detail/{part.LcscPartNumber}.html");
    }
}

public sealed record TransistorPinBinding(string Kind, string Control, string Output, string Common, string Evidence);
