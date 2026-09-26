using System.Text.RegularExpressions;
using YouEDA.Engine.Models;

namespace YouEDA.Engine.Services;

/// <summary>Functional bindings to reviewed native optocoupler, quartz and tantalum artwork.</summary>
public static class NativeCommonSymbolPolicy
{
    private static string Evidence(EdaComponent part) => string.Join(' ', part.Tags.Append(part.Description)
        .Concat(part.Properties.Where(p => p.Key is "Category" or "Component Type").Select(p => p.Value))).ToUpperInvariant();
    public static bool Applies(EdaComponent part) => part.LcscPartNumber.ToUpperInvariant() is "C109227" or "C115450" or "C16133" or "C7171" ||
        Regex.IsMatch(Evidence(part), @"OPTO|PHOTOCOUPLER|CRYSTAL|OSCILLATOR|TANTALUM");

    public static string? Role(string name) => name.Trim().ToUpperInvariant() switch
    {
        "A" or "AN" or "ANODE" => "A", "K" or "CAT" or "CATHODE" => "K",
        "E" or "EM" or "EMITTER" => "E", "C" or "COL" or "COLLECTOR" => "C",
        "GND" or "GROUND" => "GND", "OSC1" or "XTAL1" or "X1" => "OSC1",
        "OSC2" or "XTAL2" or "X2" => "OSC2", "XTAL" => "XTAL",
        "+" or "POSITIVE" => "+", "-" or "NEGATIVE" => "-", _ => null
    };

    public static NativeCommonPinBinding? Resolve(EdaComponent part)
    {
        var pins = part.SymbolPins.Select(p => p with { Number = p.Number.Trim() }).ToArray();
        if (part.SymbolUnits.Count > 1 || pins.Any(p => string.IsNullOrWhiteSpace(p.Number)) ||
            pins.Select(p => p.Number).Distinct().Count() != pins.Length) return null;
        var evidence = Evidence(part);
        if (part.LcscPartNumber.ToUpperInvariant() is "C16133" or "C7171" || evidence.Contains("TANTALUM"))
        {
            var mpn = part.Properties.GetValueOrDefault("Manufacturer Part", "").Trim().ToUpperInvariant();
            if ((part.LcscPartNumber.ToUpperInvariant(), mpn) is not
                (("C16133", "TAJB107K006RNJ") or ("C7171", "TAJA106K016RNJ")) || pins.Length != 2) return null;
            // Confirmed in both cached source drawings: the plus cross is connected to pin 1.
            // Do not generalise this numeric-only polarity convention to unknown capacitors.
            foreach (var pin in pins)
            {
                if (pin.Number is not ("1" or "2")) return null;
                var role = Role(pin.Name);
                var numeric = string.IsNullOrWhiteSpace(pin.Name) || pin.Name.Trim() == pin.Number;
                if (!numeric && role != (pin.Number == "1" ? "+" : "-") && role != (pin.Number == "1" ? "A" : "K")) return null;
            }
            return new("polarised capacitor", "Polarised_Capacitor", ["1", "2"], ["+", "-"]);
        }
        if (Regex.IsMatch(evidence, @"OPTO|PHOTOCOUPLER") || part.LcscPartNumber is "C109227" or "C115450")
        {
            // Numeric-only LED/transistor functions are known only for these exact identities.
            var mpn = part.Properties.GetValueOrDefault("Manufacturer Part", "").Trim();
            var known = (part.LcscPartNumber.ToUpperInvariant(), mpn.ToUpperInvariant()) is
                ("C109227", "LTV-817S-TA1-C") or ("C115450", "LTV-217-B-G");
            if (!known || pins.Length != 4 || Regex.IsMatch(evidence, @"TRIAC|DARLINGTON|AC INPUT|LOGIC OUTPUT|PNP")) return null;
            string[] roles = ["A", "K", "E", "C"];
            foreach (var pin in pins)
            {
                if (!int.TryParse(pin.Number, out int n) || n is < 1 or > 4) return null;
                var role = Role(pin.Name);
                var numeric = string.IsNullOrWhiteSpace(pin.Name) || pin.Name.Trim() == pin.Number;
                if (role != roles[n - 1] && !(numeric && part.LcscPartNumber.Equals("C109227", StringComparison.OrdinalIgnoreCase))) return null;
            }
            return new("optoisolator", "Optoisolator", ["1", "2", "3", "4"], roles);
        }
        if (!Regex.IsMatch(evidence, @"\bCRYSTALS?\b") || Regex.IsMatch(evidence, @"OSCILLATOR|TCXO|VCXO|OCXO|MEMS|RESONATOR")) return null;
        if (pins.Length == 2 && pins.All(p => string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim() == p.Number || Role(p.Name) is "OSC1" or "OSC2" or "XTAL"))
            return new("two-terminal crystal", "Abracon_Crystal_ABLS", pins.Select(p => p.Number).Order(StringComparer.Ordinal).ToArray(), ["XTAL", "XTAL"]);
        if (pins.Length != 4 || pins.Count(p => Role(p.Name) == "GND") != 2 ||
            pins.Count(p => Role(p.Name) == "OSC1") != 1 || pins.Count(p => Role(p.Name) == "OSC2") != 1) return null;
        var ground = pins.Where(p => Role(p.Name) == "GND").OrderBy(p => p.Number, StringComparer.Ordinal).ToArray();
        return new("four-terminal crystal", "Abracon_Crystal_ABM8G",
            [pins.Single(p => Role(p.Name) == "OSC1").Number, ground[0].Number,
             pins.Single(p => Role(p.Name) == "OSC2").Number, ground[1].Number], ["OSC1", "GND", "OSC2", "GND"]);
    }
}

// Arrays are in the reviewed template's terminal order 1,2[,3,4].
public sealed record NativeCommonPinBinding(string Kind, string Template, string[] Numbers, string[] Names);
