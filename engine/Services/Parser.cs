using System.Globalization;
using System.IO;
using System.Text.Json;
using YouEDA.Engine.Models;

namespace YouEDA.Engine.Services;

/// <summary>Converts EasyEDA's tilde-delimited CAD records into format-neutral millimetre primitives.</summary>
public sealed class Parser
{
    // EasyEDA library coordinates are in 10-mil increments: 1 unit = 0.254 mm.
    private const double EasyEdaUnitToMm = .254;

    public EdaComponent Parse(string partNumber, string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        var component = new EdaComponent { LcscPartNumber = partNumber };
        PopulateSymbolMetadata(document.RootElement, component);
        var footprintData = FindFootprintData(document.RootElement);
        if (footprintData is not null)
        {
            var (originX, originY) = GetOrigin(footprintData.Value);
            var shapeRecords = FindShapeRecords(footprintData.Value).ToArray();
            Populate3dModelMetadata(component, shapeRecords, originX, originY);

            foreach (var record in shapeRecords)
                ParseRecord(record, component, originX, originY);
        }

        ParseSymbol(document.RootElement, component);

        // Some public records contain a fully usable schematic symbol but no packageDetail/PAD
        // records. Keep those parts: callers export their symbol and metadata only, never a
        // fabricated footprint. A payload with neither is still not importable.
        if (component.SymbolPins.Count == 0 && component.SymbolUnits.Count == 0)
            throw new InvalidDataException("EasyEDA returned no schematic symbol data for this part.");
        return component;
    }

    private static void PopulateSymbolMetadata(JsonElement root, EdaComponent component)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return;
        component.Name = Text(result, "title");
        component.Description = Text(result, "description");
        if (result.TryGetProperty("packageDetail", out var package) && package.ValueKind == JsonValueKind.Object)
            component.FootprintName = Text(package, "title");
        if (string.IsNullOrWhiteSpace(component.Name)) component.Name = component.LcscPartNumber;
        if (result.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var tag in tags.EnumerateArray())
                if (tag.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.GetString())) component.Tags.Add(tag.GetString()!);

        // The symbol's c_para contains the original designator prefix and useful
        // component metadata (Value, Manufacturer, Supplier Part, etc.).
        if (!result.TryGetProperty("dataStr", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("head", out var head) || head.ValueKind != JsonValueKind.Object ||
            !head.TryGetProperty("c_para", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return;
        foreach (var parameter in parameters.EnumerateObject())
        {
            if (parameter.Value.ValueKind == JsonValueKind.String)
                component.Properties[parameter.Name] = parameter.Value.GetString() ?? string.Empty;
        }
    }

    private static void ParseSymbol(JsonElement root, EdaComponent component)
    {
        if (!root.TryGetProperty("result", out var result)) return;
        ParseSymbolUnit(result, component);
        if (result.TryGetProperty("subparts", out var subparts) && subparts.ValueKind == JsonValueKind.Array)
            foreach (var subpart in subparts.EnumerateArray()) ParseSymbolUnit(subpart, component);
    }

    private static void ParseSymbolUnit(JsonElement source, EdaComponent component)
    {
        if (!source.TryGetProperty("dataStr", out var data) ||
            !data.TryGetProperty("shape", out var shapes) || shapes.ValueKind != JsonValueKind.Array) return;
        var (originX, originY) = GetOrigin(data);
        var unit = new EdaSymbolUnit();
        foreach (var item in shapes.EnumerateArray())
        {
            var raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var fields = raw.Split('~');
            switch (fields[0].ToUpperInvariant())
            {
                case "P":
                {
                    var segments = raw.Split("^^", StringSplitOptions.None);
                    var settings = segments[0].Split('~');
                    if (settings.Length < 7) break;
                    var name = segments.Length > 3 ? segments[3].Split('~') : [];
                    var anchor = name.Length > 5 ? name[5] : string.Empty;
                    var pinName = name.Length > 4 ? name[4] : string.Empty;
                    // The leading pin coordinate is the electrical connection point;
                    // the SVG path carries its length back toward the symbol body.
                    var path = segments.Length > 2 ? segments[2] : string.Empty;
                    var length = System.Text.RegularExpressions.Regex.Match(path, @"[hv]\s*(-?\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var pin = new EdaSymbolPin(settings[3], pinName, (int)Number(settings[2]), (int)Number(settings[6]), anchor)
                    {
                        ShowName = name.Length == 0 || name[0] is not "0" and not "hide",
                        Xmm = RelativeMm(settings[4], originX), Ymm = -RelativeMm(settings[5], originY),
                        LengthMm = length.Success ? Mm(Math.Abs(Number(length.Groups[1].Value))) : 2.54
                    };
                    unit.Pins.Add(pin);
                    component.SymbolPins.Add(pin);
                    break;
                }
                case "R" when fields.Length > 6:
                {
                    var x = Number(fields[1]); var y = Number(fields[2]);
                    unit.Graphics.Add(new EdaSymbolGraphic("RECT", [(Mm(x - originX), -Mm(y - originY)),
                        (Mm(x + Number(fields[5]) - originX), -Mm(y + Number(fields[6]) - originY))],
                        Mm(Number(fields.ElementAtOrDefault(8))), !string.Equals(fields.ElementAtOrDefault(10), "none", StringComparison.OrdinalIgnoreCase)));
                    break;
                }
                case "C" when fields.Length > 3:
                case "E" when fields.Length > 4:
                {
                    var radiusX = Number(fields[3]);
                    var radiusY = fields[0].Equals("C", StringComparison.OrdinalIgnoreCase) ? radiusX : Number(fields[4]);
                    unit.Graphics.Add(new EdaSymbolGraphic("ELLIPSE", [(RelativeMm(fields[1], originX), -RelativeMm(fields[2], originY)),
                        (Mm(radiusX), Mm(radiusY))], Mm(Number(fields.ElementAtOrDefault(fields[0] == "C" ? 5 : 6))), false));
                    break;
                }
                case "PL" or "PG" when fields.Length > 1:
                {
                    var points = ParsePoints(fields[1], originX, originY);
                    if (points.Count > 1)
                        unit.Graphics.Add(new EdaSymbolGraphic(fields[0].Equals("PG", StringComparison.OrdinalIgnoreCase) ? "POLYGON" : "POLYLINE",
                            points, Mm(Number(fields.ElementAtOrDefault(3))), false));
                    break;
                }
            }
        }
        if (unit.Pins.Count > 0 || unit.Graphics.Count > 0) component.SymbolUnits.Add(unit);
    }

    private static void Populate3dModelMetadata(EdaComponent component, IEnumerable<string> shapeRecords, double originX, double originY)
    {
        // EasyEDA's actual asset ID and placement live in SVGNODE, which is what the
        // EasyEDA Loader uses. head.uuid_3d identifies a related library record, not
        // necessarily the downloadable STEP asset.
        var raw = shapeRecords.FirstOrDefault(record => record.StartsWith("SVGNODE~", StringComparison.OrdinalIgnoreCase));
        if (raw is null) return;

        try
        {
            using var nodeDocument = JsonDocument.Parse(raw[(raw.IndexOf('~') + 1)..]);
            var root = nodeDocument.RootElement;
            if (!root.TryGetProperty("attrs", out var attrs) || attrs.ValueKind != JsonValueKind.Object) return;
            var uuid = Text(attrs, "uuid");
            if (string.IsNullOrWhiteSpace(uuid)) return;

            var (x, y) = Pair(Text(attrs, "c_origin"));
            var (rx, ry, rz) = Triple(Text(attrs, "c_rotation"));
            var name = Text(attrs, "title");
            component.ThreeDModel = new Eda3dModel(
                uuid,
                string.IsNullOrWhiteSpace(name) ? "EasyEDA-" + uuid : name,
                RelativeMm(x, originX), -RelativeMm(y, originY), Mm(Number(Text(attrs, "z"))),
                rx, ry, rz, Mm(Number(Text(attrs, "c_width"))), Mm(Number(Text(attrs, "c_height"))));
        }
        catch (JsonException)
        {
            // A malformed optional 3D outline must not prevent the 2D footprint export.
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static (string X, string Y) Pair(string value)
    {
        var values = value.Split(',', StringSplitOptions.TrimEntries);
        return (values.ElementAtOrDefault(0) ?? "0", values.ElementAtOrDefault(1) ?? "0");
    }

    private static (double X, double Y, double Z) Triple(string value)
    {
        var values = value.Split(',', StringSplitOptions.TrimEntries);
        return (Number(values.ElementAtOrDefault(0)), Number(values.ElementAtOrDefault(1)), Number(values.ElementAtOrDefault(2)));
    }

    // The component envelope holds both the schematic dataStr and packageDetail.dataStr.
    // Select the latter by looking for a shape array containing a PCB PAD record.
    private static JsonElement? FindFootprintData(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("dataStr", out var dataStr) && dataStr.ValueKind == JsonValueKind.Object &&
                ContainsPadRecord(dataStr))
                return dataStr;

            foreach (var property in element.EnumerateObject())
            {
                var result = FindFootprintData(property.Value);
                if (result.HasValue) return result;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var result = FindFootprintData(item);
                if (result.HasValue) return result;
            }
        }
        return null;
    }

    private static bool ContainsPadRecord(JsonElement dataStr)
    {
        if (!dataStr.TryGetProperty("shape", out var shapes) || shapes.ValueKind != JsonValueKind.Array) return false;
        foreach (var shape in shapes.EnumerateArray())
            if (shape.ValueKind == JsonValueKind.String && shape.GetString()!.StartsWith("PAD~", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static (double X, double Y) GetOrigin(JsonElement dataStr)
    {
        // EasyEDA footprints carry an explicit BBox that defines their local canvas.
        // This is the coordinate reference used by EasyEDALoader and by EasyEDA's own
        // footprint editor: translate to the BBox centre and invert Y.  `head.x/y` is
        // only document metadata; it is frequently the same value, but it is not a
        // reliable footprint origin for every library item.
        if (dataStr.TryGetProperty("BBox", out var boundingBox) && boundingBox.ValueKind == JsonValueKind.Object)
        {
            var width = Number(boundingBox, "width");
            var height = Number(boundingBox, "height");
            if (width > 0 && height > 0)
                return (Number(boundingBox, "x") + width / 2, Number(boundingBox, "y") + height / 2);
        }
        if (dataStr.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object)
            return (Number(head, "x"), Number(head, "y"));
        return (0, 0);
    }

    private static double Number(JsonElement objectElement, string property) =>
        objectElement.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static IEnumerable<string> FindShapeRecords(JsonElement dataStr)
    {
        if (!dataStr.TryGetProperty("shape", out var shapes) || shapes.ValueKind != JsonValueKind.Array) yield break;
        foreach (var shape in shapes.EnumerateArray())
            if (shape.ValueKind == JsonValueKind.String)
                yield return shape.GetString()!;
    }

    private static void ParseRecord(string record, EdaComponent component, double originX, double originY)
    {
        var fields = record.Split('~');
        if (fields.Length == 0) return;
        var kind = fields[0].Trim().ToUpperInvariant();

        if (kind == "PAD" && fields.Length >= 10)
        {
            // PAD~shape~x~y~width~height~layer~net~number~holeRadius~...~rotation
            component.Pads.Add(new EdaPad(
                fields[8], RelativeMm(fields[2], originX), -RelativeMm(fields[3], originY),
                Mm(fields[4]), Mm(fields[5]), Number(fields.ElementAtOrDefault(11)), fields[6],
                Number(fields[9]) > 0 && !string.Equals(fields.ElementAtOrDefault(15), "N", StringComparison.OrdinalIgnoreCase),
                2 * Mm(Number(fields[9])))
            {
                Shape = fields[1].ToUpperInvariant(),
                SlotLengthMm = Mm(Number(fields.ElementAtOrDefault(13))),
                PolygonPointsMm = ParsePoints(fields.ElementAtOrDefault(10) ?? "", originX, originY)
            });
            return;
        }

        if (kind == "TRACK" && fields.Length >= 5)
        {
            var points = ParsePoints(fields[4], originX, originY);
            component.Shapes.Add(new EdaShape(
                kind, fields.ElementAtOrDefault(2) ?? "TopLayer", points, Mm(Number(fields.ElementAtOrDefault(1)))));
            return;
        }
        if (kind == "ARC" && fields.Length >= 5 && TryParsePcbArc(fields[4], originX, originY, out var arc))
        {
            component.Shapes.Add(new EdaShape(kind, fields.ElementAtOrDefault(2) ?? "TopSilkLayer",
                [(arc.CenterXmm, arc.CenterYmm)], Mm(Number(fields.ElementAtOrDefault(1))))
            {
                RadiusMm = arc.RadiusMm,
                StartAngleDeg = arc.StartAngleDeg,
                EndAngleDeg = arc.EndAngleDeg
            });
            return;
        }
        if (kind == "CIRCLE" && fields.Length >= 6)
        {
            component.Shapes.Add(new EdaShape(kind, fields[5],
                [(RelativeMm(fields[1], originX), -RelativeMm(fields[2], originY)), (Mm(fields[3]), 0)],
                Mm(fields[4])));
            return;
        }
        if (kind == "RECT" && fields.Length >= 9)
        {
            var x = Number(fields[1]); var y = Number(fields[2]);
            component.Shapes.Add(new EdaShape(kind, fields[5],
                [(Mm(x - originX), -Mm(y - originY)),
                 (Mm(x + Number(fields[3]) - originX), -Mm(y + Number(fields[4]) - originY))], Mm(fields[8])));
            return;
        }
        if (kind == "HOLE" && fields.Length >= 4)
        {
            component.Shapes.Add(new EdaShape(kind, "11",
                [(RelativeMm(fields[1], originX), -RelativeMm(fields[2], originY)), (Mm(fields[3]), 0)], 0));
            return;
        }
        // Solid regions are used by EasyEDA for package artwork as well as explicit
        // paste and solder-mask apertures.  Keep every native region and its layer;
        // reducing them to a Mechanical-1 outline changes the generated footprint.
        if (kind == "SOLIDREGION" && fields.Length >= 5 && fields[4] is "solid" or "npth")
        {
            var points = ParseSvgPoints(fields[3], originX, originY);
            if (points.Count >= 3) component.Shapes.Add(new EdaShape(kind, fields[1], points, 0));
        }
    }

    private static IReadOnlyList<(double X, double Y)> ParseSvgPoints(string path, double originX, double originY)
    {
        var tokens = System.Text.RegularExpressions.Regex.Matches(path,
            @"[MLHVZmlhvz]|[-+]?(?:\d*\.\d+|\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(match => match.Value).ToArray();
        var result = new List<(double X, double Y)>();
        var x = 0.0; var y = 0.0;
        for (var i = 0; i < tokens.Length;)
        {
            var command = tokens[i++].ToUpperInvariant();
            switch (command)
            {
                case "M" or "L" when i + 1 < tokens.Length:
                    x = Number(tokens[i++]); y = Number(tokens[i++]); break;
                case "H" when i < tokens.Length:
                    x = Number(tokens[i++]); break;
                case "V" when i < tokens.Length:
                    y = Number(tokens[i++]); break;
                case "Z":
                    if (result.Count > 0 && result[0] != result[^1]) result.Add(result[0]);
                    continue;
                default: return result;
            }
            result.Add((Mm(x - originX), -Mm(y - originY)));
        }
        return result;
    }

    // EasyEDA PCB ARC records contain a compact SVG path:
    // M startX startY A rx ry rotation largeArc sweep endX endY.
    // This follows the same SVG-to-circle conversion used by EasyEDALoader, then
    // performs its Y-axis inversion and sweep reversal for Altium coordinates.
    private static bool TryParsePcbArc(string path, double originX, double originY,
        out (double CenterXmm, double CenterYmm, double RadiusMm, double StartAngleDeg, double EndAngleDeg) result)
    {
        result = default;
        var values = System.Text.RegularExpressions.Regex.Matches(path,
                @"[-+]?(?:\d*\.\d+|\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(match => Number(match.Value)).ToArray();
        if (values.Length < 9) return false;

        var startX = values[0]; var startY = values[1];
        var radiusX = Math.Abs(values[2]); var radiusY = Math.Abs(values[3]);
        var rotationDeg = values[4];
        var largeArc = Math.Abs(values[5]) > .5;
        var sweep = Math.Abs(values[6]) > .5;
        var endX = values[7]; var endY = values[8];
        if (radiusX <= 0 || radiusY <= 0) return false;

        var angle = rotationDeg % 360 * Math.PI / 180;
        var halfDx = (startX - endX) / 2; var halfDy = (startY - endY) / 2;
        var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        var x1 = cos * halfDx + sin * halfDy;
        var y1 = -sin * halfDx + cos * halfDy;
        var radiusXSquared = radiusX * radiusX; var radiusYSquared = radiusY * radiusY;
        var radiiCheck = x1 * x1 / radiusXSquared + y1 * y1 / radiusYSquared;
        if (radiiCheck > 1)
        {
            var scale = Math.Sqrt(radiiCheck);
            radiusX *= scale; radiusY *= scale;
            radiusXSquared = radiusX * radiusX; radiusYSquared = radiusY * radiusY;
        }
        var denominator = radiusXSquared * y1 * y1 + radiusYSquared * x1 * x1;
        var numerator = radiusXSquared * radiusYSquared - radiusXSquared * y1 * y1 - radiusYSquared * x1 * x1;
        var coefficient = (largeArc == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, denominator == 0 ? 0 : numerator / denominator));
        var centerX1 = coefficient * radiusX * y1 / radiusY;
        var centerY1 = coefficient * -radiusY * x1 / radiusX;
        var centerX = (startX + endX) / 2 + cos * centerX1 - sin * centerY1;
        var centerY = (startY + endY) / 2 + sin * centerX1 + cos * centerY1;
        var startAngle = NormalizeAngle(Math.Atan2(startY - centerY, startX - centerX) * 180 / Math.PI);
        var endAngle = NormalizeAngle(Math.Atan2(endY - centerY, endX - centerX) * 180 / Math.PI);
        if (sweep && endAngle < startAngle) endAngle += 360;
        if (!sweep && endAngle > startAngle) endAngle -= 360;

        // Invert the SVG/EasyEDA Y axis.  Clockwise SVG arcs reverse their start/end
        // order in Altium, precisely as EasyEDALoader's EeFootprintArc does.
        var altiumStart = sweep ? NormalizeAngle(360 - endAngle) : NormalizeAngle(360 - startAngle);
        var altiumEnd = sweep ? NormalizeAngle(360 - startAngle) : NormalizeAngle(360 - endAngle);
        result = (RelativeMm(centerX, originX),
            -RelativeMm(centerY, originY),
            Mm((radiusX + radiusY) / 2), altiumStart, altiumEnd);
        return true;
    }

    private static double NormalizeAngle(double degrees)
    {
        var normalized = degrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static IReadOnlyList<(double X, double Y)> ParsePoints(string value, double originX, double originY)
    {
        var fields = value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<(double X, double Y)>();
        for (var index = 0; index + 1 < fields.Length; index += 2)
            result.Add((RelativeMm(fields[index], originX), -RelativeMm(fields[index + 1], originY)));
        return result;
    }

    private static double Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static double Mm(string value) => Number(value) * EasyEdaUnitToMm;
    private static double Mm(double value) => value * EasyEdaUnitToMm;
    private static double RelativeMm(string value, double origin) => (Number(value) - origin) * EasyEdaUnitToMm;
    private static double RelativeMm(double value, double origin) => (value - origin) * EasyEdaUnitToMm;
}
