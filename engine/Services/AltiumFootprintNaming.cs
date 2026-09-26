using System;
using System.Linq;
using YouEDA.Engine.Models;

namespace YouEDA.Engine.Services;

/// <summary>One naming rule for PcbLib entries and schematic footprint links.</summary>
public static class AltiumFootprintNaming
{
    public static string NameFor(EdaComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var name = new string((component.FootprintName ?? string.Empty)
            .Where(character => !char.IsControl(character)).ToArray()).Trim();
        return name.Length == 0 ? component.LcscPartNumber : name;
    }
}
