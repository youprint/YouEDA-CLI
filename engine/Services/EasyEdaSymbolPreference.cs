using System.Text.RegularExpressions;
using YouEDA.Engine.Models;

namespace YouEDA.Engine.Services;

/// <summary>User-requested source preference, not an electrical pin-mapping rule.</summary>
public static class EasyEdaSymbolPreference
{
    // Confirmed picker choices from the 351-part import. CJ431 is a voltage reference,
    // not an LDO; retain that individual choice without classifying all references as LDOs.
    private static readonly HashSet<string> ConfirmedParts = new(StringComparer.OrdinalIgnoreCase)
    { "C14289", "C5446", "C58069", "C6186", "C6187", "C71136", "C3113" };

    private static readonly Regex RegulatorType = new(
        @"\b(?:LDOs?|(?:linear\s+)?voltage\s+regulators?|linear\s+regulators?|low[\s-]*drop[\s-]*out(?:\s+voltage)?\s+regulators?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? Reason(EdaComponent component)
    {
        if (ConfirmedParts.Contains(component.LcscPartNumber.Trim())) return "confirmed per-part EasyEDA choice";
        // Use category/type evidence, not package, pin names, URLs, or arbitrary properties.
        var types = component.Tags.Concat(component.Properties.Where(p =>
            p.Key.Equals("Category", StringComparison.OrdinalIgnoreCase) ||
            p.Key.Equals("JLCPCB Category", StringComparison.OrdinalIgnoreCase) ||
            p.Key.Equals("LCSC Category", StringComparison.OrdinalIgnoreCase) ||
            p.Key.Equals("Component Type", StringComparison.OrdinalIgnoreCase) ||
            p.Key.Equals("Type", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value));
        if (types.Any(t => RegulatorType.IsMatch(t))) return "LDO/voltage-regulator category preference";
        // Description must identify the device up front; an unrelated IC merely mentioning
        // an integrated LDO later in its prose is not sufficient evidence.
        if (RegulatorType.Match(component.Description) is { Success: true, Index: 0 })
            return "LDO/voltage-regulator description preference";
        return null;
    }
}
