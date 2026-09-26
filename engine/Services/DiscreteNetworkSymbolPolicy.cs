using YouEDA.Engine.Models;
using System.IO;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Eda.Primitives;
using PinOrientation = OriginalCircuit.Eda.Enums.PinOrientation;

namespace YouEDA.Engine.Services;

/// <summary>Reviewed, part-specific network topologies; never inferred from package or pin count.</summary>
public static class DiscreteNetworkSymbolPolicy
{
    private sealed record Profile(string Mpn, string Kind, string Template, string[] Roles, string[] SourceNames);
    private static readonly Dictionary<string, Profile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C2488"] = new("MB10S-50MIL", "bridge", "Diode", ["+", "-", "AC2", "AC1"], ["+", "-", "C2", "C1"]),
        ["C2500"] = new("BAV99,215", "series diodes", "Diode", ["A1", "K2", "K1/A2"], ["1", "2", "3"]),
        ["C68978"] = new("BAV70", "common-cathode diodes", "DIODE_ARRAY_COMMON_CATHODE", ["A1", "A2", "K"], ["1", "2", "3"]),
        ["C78395"] = new("P6SMB6.8CA/TR13", "bidirectional TVS", "Diode_Zener_Bidirectional", ["1", "2"], ["1", "2"]),
        ["C7420377"] = new("SMBJ6.5CA", "bidirectional TVS", "Diode_Zener_Bidirectional", ["1", "2"], ["1", "2"]),
        ["C32677"] = new("PSM712-LF-T7", "asymmetric TVS array", "Diode_Zener", ["IO1", "IO2", "GND"], ["A1", "A1", "K"]),
        ["C318884"] = new("TS-1187A-B-A-B", "four-pin normally-open switch", "SPST-NO_4_PIN", ["A", "B", "C", "D"], ["A", "B", "C", "D"])
    };

    public static bool Applies(EdaComponent part) => Profiles.ContainsKey(part.LcscPartNumber);

    public static DiscreteNetworkBinding? Resolve(EdaComponent part)
    {
        if (!Profiles.TryGetValue(part.LcscPartNumber, out var p) ||
            !part.Properties.TryGetValue("Manufacturer Part", out var mpn) ||
            !mpn.Trim().Equals(p.Mpn, StringComparison.OrdinalIgnoreCase) || part.SymbolUnits.Count > 1 ||
            part.SymbolPins.Count != p.Roles.Length) return null;
        var numbered = part.SymbolPins.OrderBy(pin => pin.Number.Trim(), StringComparer.Ordinal).ToArray();
        for (int i = 0; i < numbered.Length; i++)
        {
            var number = (i + 1).ToString(); var name = numbered[i].Name.Trim();
            if (numbered[i].Number.Trim() != number) return null;
            // Only missing/numeric names, the reviewed raw payload names, or the documented
            // datasheet role are accepted. PSM712's A1/A1/K is a narrowly scoped known correction.
            if (name.Length != 0 && name != number && !name.Equals(p.SourceNames[i], StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(p.Roles[i], StringComparison.OrdinalIgnoreCase)) return null;
        }
        return new(p.Kind, p.Template, p.Roles.ToArray(), $"https://www.lcsc.com/product-detail/{part.LcscPartNumber}.html");
    }

    public static SchComponent Apply(SchComponent native, DiscreteNetworkBinding binding)
    {
        SchComponent result;
        if (binding.Kind is "bridge" or "series diodes" or "asymmetric TVS array")
        {
            result = new SchComponent { Name = native.Name, LibReference = native.Name, DesignatorPrefix = "D" };
            if (binding.Kind == "series diodes")
            {
                CopyDiode(result, native, -100, 0, 0); CopyDiode(result, native, 100, 0, 0);
                Wire(result, -200, 0, -140, 0); Wire(result, -60, 0, 60, 0); Wire(result, 140, 0, 200, 0);
                Wire(result, 0, 0, 0, 150);
                Pin(result, "1", -200, 0, PinOrientation.Left); Pin(result, "2", 200, 0, PinOrientation.Right);
                Pin(result, "3", 0, 150, PinOrientation.Up);
            }
            else if (binding.Kind == "bridge")
            {
                // Four upward diodes: minus -> either AC node -> plus. No crossing wires.
                foreach (var x in new[] { -140d, 140d })
                {
                    CopyDiode(result, native, x, -110, 90); CopyDiode(result, native, x, 110, 90);
                    Wire(result, x, -250, x, -150); Wire(result, x, -70, x, 70); Wire(result, x, 150, x, 250);
                }
                // Authored wire ends and 100-mil pins land on the 50-mil connection grid.
                // Keep the native diode shapes unchanged; extend only the connecting wires.
                Wire(result, -140, 250, 140, 250); Wire(result, -140, -250, 140, -250);
                Wire(result, -300, 0, -140, 0); Wire(result, 140, 0, 300, 0);
                Pin(result, "1", 0, 250, PinOrientation.Up); Pin(result, "2", 0, -250, PinOrientation.Down);
                Pin(result, "3", -300, 0, PinOrientation.Left); Pin(result, "4", 300, 0, PinOrientation.Right);
                result.AddLabel(new SchLabel { Text = "+", Location = Point(-25, 160), Color = 0x800000 });
                result.AddLabel(new SchLabel { Text = "-", Location = Point(-25, -210), Color = 0x800000 });
                result.AddLabel(new SchLabel { Text = "~", Location = Point(-270, -70), Color = 0x800000 });
                result.AddLabel(new SchLabel { Text = "~", Location = Point(215, -70), Color = 0x800000 });
            }
            else
            {
                // Manufacturer diagram: two independent anti-series avalanche pairs with
                // common anodes inside each pair; the 12 V cathodes face IO, the 7 V ones GND.
                // Move each complete branch, including its artwork and labels, onto the grid.
                foreach (var (x, number) in new[] { (-150d, "2"), (150d, "1") })
                {
                    CopyDiode(result, native, x, 90, 90); CopyDiode(result, native, x, -90, -90);
                    Wire(result, x, 130, x, 200); Wire(result, x, -50, x, 50); Wire(result, x, -130, x, -200);
                    Pin(result, number, x, 200, PinOrientation.Up);
                    result.AddLabel(new SchLabel { Text = "12V", Location = Point(x + 80, 75), Color = 0x800000 });
                    result.AddLabel(new SchLabel { Text = "7V", Location = Point(x + 80, -105), Color = 0x800000 });
                }
                Wire(result, -150, -200, 150, -200); Wire(result, 0, -200, 0, -250);
                Pin(result, "3", 0, -250, PinOrientation.Down);
            }
        }
        else
        {
            result = native;
            if (binding.Kind == "four-pin normally-open switch")
            {
                // Source has stacked 1/2 and 3/4 pins. Separate duplicate contacts visibly,
                // retaining the native actuator/contact artwork and all four electrical pins.
                foreach (var (number, x) in new[] { ("2", -100d), ("4", 100d) })
                {
                    result.Pins.OfType<SchPin>().Single(p => p.Designator == number).Location = Point(x, -150);
                    Wire(result, x, -150, x, 0);
                }
            }
        }
        foreach (var pin in result.Pins.OfType<SchPin>())
        {
            pin.Name = binding.Roles[int.Parse(pin.Designator!) - 1];
            pin.ElectricalType = PinElectricalType.Passive;
            pin.ShowName = false; pin.ShowDesignator = true;
            pin.SymbolInnerEdge = pin.SymbolOuterEdge = pin.SymbolInside = pin.SymbolOutside = 0;
        }
        foreach (var line in result.Lines.OfType<SchLine>()) line.Width = Coord.FromMils(2);
        foreach (var line in result.Polylines.OfType<SchPolyline>()) line.LineWidth = 1;
        foreach (var polygon in result.Polygons.OfType<SchPolygon>()) polygon.LineWidth = 1;
        foreach (var arc in result.Arcs.OfType<SchArc>()) arc.LineWidth = 1;
        PlaceIdentityLabels(result, binding.Kind);
        return result;
    }

    private static void PlaceIdentityLabels(SchComponent symbol, string kind)
    {
        // A composed network can be taller than a single diode. Never let the generic
        // comment default (-100 mil) cross its internal circuit, or omit its designator.
        var points = symbol.Polylines.SelectMany(l => l.Vertices).Concat(symbol.Polygons.SelectMany(p => p.Vertices))
            .Concat(symbol.Pins.SelectMany(p => new[] { p.Location, Point(
                p.Location.X.ToMils() + (p.Orientation == PinOrientation.Left ? -1 : p.Orientation == PinOrientation.Right ? 1 : 0) * p.Length.ToMils(),
                p.Location.Y.ToMils() + (p.Orientation == PinOrientation.Down ? -1 : p.Orientation == PinOrientation.Up ? 1 : 0) * p.Length.ToMils()) })).ToArray();
        symbol.DesignatorPrefix = kind == "four-pin normally-open switch" ? "SW" : "D";
        foreach (var name in new[] { "Designator", "Comment" })
        {
            var parameter = symbol.Parameters.OfType<SchParameter>().FirstOrDefault(p => p.Name == name);
            if (parameter is null) { parameter = new SchParameter { Name = name }; symbol.AddParameter(parameter); }
            parameter.Value = name == "Designator" ? symbol.DesignatorPrefix + "?" : "";
            parameter.Location = Point(points.Min(p => p.X.ToMils()), name == "Designator" ?
                points.Max(p => p.Y.ToMils()) + 100 : points.Min(p => p.Y.ToMils()) - 160);
            parameter.FontId = 1; parameter.Color = 0x800000; parameter.HideName = true;
            parameter.IsVisible = true; parameter.OwnerPartId = -1;
        }
    }

    private static CoordPoint Point(double x, double y) => new(Coord.FromMils(x), Coord.FromMils(y));
    private static void Pin(SchComponent symbol, string number, double x, double y, PinOrientation orientation) =>
        symbol.AddPin(new SchPin { Designator = number, Location = Point(x, y), Length = Coord.FromMils(100), Orientation = orientation });
    private static void Wire(SchComponent symbol, double x1, double y1, double x2, double y2) =>
        symbol.AddPolyline(SchPolyline.Create().From(Coord.FromMils(x1), Coord.FromMils(y1))
            .To(Coord.FromMils(x2), Coord.FromMils(y2)).Color(0xFF0000).LineWidth(1).Build());

    private static void CopyDiode(SchComponent target, SchComponent donor, double x, double y, double angle)
    {
        // Copy only reviewed native diode primitives, not pins, donor metadata or models.
        // Fail closed if the template changes to an unreviewed shape collection.
        var zener = donor.Name == "Diode_Zener";
        if (donor.Polygons.Count != 1 || donor.Polylines.Count != (zener ? 3 : 1) || donor.Lines.Count != 0 ||
            donor.Rectangles.Count != 0 || donor.Arcs.Count != 0 || donor.EllipticalArcs.Count != 0 || donor.Beziers.Count != 0 ||
            !donor.Polygons[0].Vertices.Select(v => (v.X.ToMils(), v.Y.ToMils())).SequenceEqual(new[] { (40d, 0d), (-40d, 60d), (-40d, -60d), (40d, 0d) }))
            throw new InvalidDataException("Native diode artwork changed; network composition requires review.");
        CoordPoint Transform(CoordPoint v)
        {
            var radians = angle * Math.PI / 180;
            return Point(x + v.X.ToMils() * Math.Cos(radians) - v.Y.ToMils() * Math.Sin(radians),
                y + v.X.ToMils() * Math.Sin(radians) + v.Y.ToMils() * Math.Cos(radians));
        }
        foreach (var source in donor.Polygons.OfType<SchPolygon>())
        {
            var copy = new SchPolygon { Color = source.Color, FillColor = source.FillColor, IsFilled = source.IsFilled,
                IsTransparent = source.IsTransparent, LineWidth = 1 };
            ((List<CoordPoint>)copy.Vertices).AddRange(source.Vertices.Select(Transform)); target.AddPolygon(copy);
        }
        foreach (var source in donor.Polylines.OfType<SchPolyline>())
        {
            var copy = new SchPolyline { Color = source.Color, LineWidth = 1, LineStyle = source.LineStyle };
            ((List<CoordPoint>)copy.Vertices).AddRange(source.Vertices.Select(Transform)); target.AddPolyline(copy);
        }
    }
}

public sealed record DiscreteNetworkBinding(string Kind, string Template, string[] Roles, string Evidence);
