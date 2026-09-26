using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using YouEDA.Engine.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Eda.Primitives;
using OriginalCircuit.Eda.Models.Sch;
using PinOrientation = OriginalCircuit.Eda.Enums.PinOrientation;

namespace YouEDA.Engine.Services;

/// <summary>Creates and maintains native Altium symbols in the shared SchLib.</summary>
public sealed class AltiumSchExporter
{
    public Task<string> UpsertEasyEdaSymbolAsync(EdaComponent source, string outputDirectory, bool attachFootprint = true)
    {
        if (source.SymbolPins.Count == 0)
            throw new InvalidDataException("EasyEDA returned no schematic P pin records.");
        var symbol = CreateEasyEdaSymbol(source);
        PrepareSymbol(symbol, source, attachFootprint);
        return UpsertSymbolAsync(symbol, outputDirectory, source.SymbolPins.Count, source.LcscPartNumber, source.Name, attachFootprint);
    }

    /// <summary>Adds or refreshes a symbol while retaining all other symbols in youeda.SchLib.</summary>
    public async Task<string> UpsertSymbolAsync(SchComponent symbol, string outputDirectory, int? expectedPinCount = null, string? legacyLcscName = null, string? legacyComponentName = null, bool attachFootprint = true)
    {
        if (string.IsNullOrWhiteSpace(symbol.Name))
            throw new InvalidDataException("The selected schematic symbol has no component name.");

        var path = Path.Combine(outputDirectory, "youeda.SchLib");
        var library = await OpenSharedSchLibAsync(outputDirectory);

        // Migrate pre-1.2.8 LCSC-named records when the component is re-exported. The human
        // facing library ID is now the actual orderable part name; LCSC remains a parameter.
        if (!string.IsNullOrWhiteSpace(legacyLcscName) && !legacyLcscName.Equals(symbol.Name, StringComparison.OrdinalIgnoreCase))
            library.Remove(legacyLcscName);
        if (!string.IsNullOrWhiteSpace(legacyComponentName) && !legacyComponentName.Equals(symbol.Name, StringComparison.OrdinalIgnoreCase))
            library.Remove(legacyComponentName);
        MigrateUnsafeLibraryNames(library);
        Upsert(library, symbol, attachFootprint);
        await library.SaveAsync(path);

        var verified = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path);
        if (verified[symbol.Name] is not SchComponent written ||
            (expectedPinCount is not null && written.Pins.Count != expectedPinCount) ||
            (attachFootprint && !written.Implementations.Any(model => model.ModelType == "PCBLIB" &&
                model.ModelName == FootprintNameForSymbol(symbol))))
            throw new InvalidDataException("The shared SchLib did not pass post-write verification.");
        return path;
    }

    /// <summary>Opens the shared library once for a single-writer bulk import.</summary>
    public async Task<SchLibrary> OpenSharedSchLibAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "youeda.SchLib");
        var library = File.Exists(path)
            ? (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path)
            : (SchLibrary)AltiumLibrary.CreateSchLib();
        EnsureReadableCanvas(library);
        return library;
    }

    /// <summary>Mutates an already-open library; the caller decides when to checkpoint it.</summary>
    public void Upsert(SchLibrary library, SchComponent symbol, bool attachFootprint = true)
    {
        if (string.IsNullOrWhiteSpace(symbol.Name))
            throw new InvalidDataException("The selected schematic symbol has no component name.");
        EnsureReadableCanvas(library);
        if (attachFootprint) AttachFootprint(symbol);
        else RemoveFootprint(symbol);
        library.Remove(symbol.Name);
        library.Add(symbol);
    }

    /// <summary>
    /// Altium's SchLib index rejects spaces and several punctuation characters in record names.
    /// Repair existing records before saving, so one legacy catalog entry cannot hide the whole
    /// library in Altium's component list.
    /// </summary>
    public static void MigrateUnsafeLibraryNames(SchLibrary library)
    {
        var changes = library.Components.OfType<SchComponent>()
            .Select(component => (Component: component, OldName: component.Name ?? string.Empty, NewName: SafeLibraryName(component.Name)))
            .Where(change => !change.OldName.Equals(change.NewName, StringComparison.Ordinal))
            .ToArray();
        foreach (var change in changes)
        {
            library.Remove(change.OldName);
            var uniqueName = change.NewName;
            for (var suffix = 2; library.Contains(uniqueName); suffix++) uniqueName = change.NewName + "_" + suffix;
            change.Component.Name = uniqueName;
            change.Component.LibReference = uniqueName;
            library.Add(change.Component);
        }
    }

    /// <summary>Keep the bundled artwork while following the user's Value/Comment conventions.</summary>
    public static void PrepareSymbol(SchComponent symbol, EdaComponent source, bool attachFootprint = true)
    {
        var libraryName = LibraryComponentName(source);
        symbol.Name = libraryName;
        symbol.LibReference = libraryName;
        ApplyComponentMetadata(symbol, source);
        if (attachFootprint)
        {
            var footprint = GetOrCreateParameter(symbol, "Footprint", -5.588, 5.08);
            footprint.Value = AltiumFootprintNaming.NameFor(source);
            footprint.IsVisible = false;
        }
        else RemoveParameter(symbol, "Footprint");
        var value = source.Properties.GetValueOrDefault("Value");
        var manufacturerPart = source.Properties.GetValueOrDefault("Manufacturer Part");
        var isDiode = symbol.DesignatorPrefix?.StartsWith('D') == true;
        var isValuePassive = symbol.DesignatorPrefix is { } prefix &&
            (prefix.StartsWith('R') || prefix.StartsWith('C') || prefix.StartsWith('L'));
        // LCSC/EasyEDA does not consistently call a passive's displayed value "Value".
        // For example, many inductors only have "Inductance".  Normalize that source field
        // into Altium's Value parameter so the standard symbol can always show =Value.
        if (string.IsNullOrWhiteSpace(value) && isValuePassive)
        {
            var propertyName = symbol.DesignatorPrefix!.StartsWith('R') ? "Resistance" :
                symbol.DesignatorPrefix.StartsWith('C') ? "Capacitance" : "Inductance";
            value = Property(source, propertyName);
        }
        if (!string.IsNullOrWhiteSpace(value))
        {
            var valueParameter = GetOrCreateParameter(symbol, "Value", -5.588, 5.08);
            valueParameter.Value = value;
            valueParameter.IsVisible = false;
        }

        if (isDiode && !string.IsNullOrWhiteSpace(manufacturerPart))
        {
            var partParameter = GetOrCreateParameter(symbol, "Manufacturer Part", -5.588, 5.08);
            partParameter.Value = manufacturerPart;
            partParameter.IsVisible = false;
            SetComment(symbol, "=Manufacturer Part");
        }
        else if (!string.IsNullOrWhiteSpace(value))
        {
            SetComment(symbol, "=Value");
        }
        else
        {
            SetComment(symbol, source.Name);
        }

        if (isValuePassive && !string.IsNullOrWhiteSpace(value))
        {
            var designator = symbol.Parameters.OfType<SchParameter>().FirstOrDefault(p => p.Name == "Designator");
            if (designator is not null)
                designator.Location = new CoordPoint(designator.Location.X, Coord.FromMm(2.54));
            var comment = symbol.Parameters.OfType<SchParameter>().First(p => p.Name == "Comment");
            comment.Location = new CoordPoint(Coord.FromMm(0), Coord.FromMm(-2.54));
        }
    }

    /// <summary>
    /// Applies a consistent human-readable identity while retaining all supplier metadata as
    /// hidden, searchable Altium parameters. The LCSC identifier remains the stable library key.
    /// </summary>
    private static void ApplyComponentMetadata(SchComponent symbol, EdaComponent source)
    {
        var manufacturer = CleanManufacturer(Property(source, "Manufacturer"));
        var suppliedManufacturerPart = Property(source, "Manufacturer Part");
        var componentName = LibraryComponentName(source);
        // The library row should identify the actual orderable component at a glance.
        // LCSC is retained as metadata, never used as the human-facing Design Item ID.
        var designItemId = FirstNonEmpty(componentName, suppliedManufacturerPart, source.LcscPartNumber);
        var description = CreateLibraryDescription(source, componentName, manufacturer, suppliedManufacturerPart);

        symbol.DesignItemId = designItemId;
        symbol.Description = description;

        SetHiddenParameter(symbol, "LCSC Part", source.LcscPartNumber);
        SetHiddenParameter(symbol, "Source", "EasyEDA / LCSC");
        SetHiddenParameter(symbol, "EasyEDA Component Name", componentName);
        SetHiddenParameter(symbol, "EasyEDA Description", source.Description);
        SetHiddenParameter(symbol, "Description", symbol.Description);
        SetHiddenParameter(symbol, "Manufacturer", manufacturer);
        SetHiddenParameter(symbol, "Manufacturer Part", suppliedManufacturerPart);
        SetHiddenParameter(symbol, "Package", source.FootprintName);
        SetHiddenParameter(symbol, "Designator Prefix", Prefix(source));
        SetHiddenParameter(symbol, "Tags", string.Join("; ", source.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase)));
        SetHiddenParameter(symbol, "EasyEDA Pin Count", source.SymbolPins.Count.ToString());
        SetHiddenParameter(symbol, "EasyEDA Pad Count", source.Pads.Count.ToString());
        if (source.ThreeDModel is { } model)
        {
            SetHiddenParameter(symbol, "EasyEDA 3D Model", model.Name);
            SetHiddenParameter(symbol, "EasyEDA 3D UUID", model.Uuid);
        }

        // Preserve every text property returned by EasyEDA. Known names merge with the
        // standard parameters above; unusual supplier fields remain available for search.
        foreach (var property in source.Properties.Where(item => !string.IsNullOrWhiteSpace(item.Value)))
            SetHiddenParameter(symbol, SafeParameterName(property.Key), property.Value.Trim());
    }

    private static void SetHiddenParameter(SchComponent symbol, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var parameter = GetOrCreateParameter(symbol, name, -5.588, 5.08);
        parameter.Value = value;
        parameter.IsVisible = false;
        parameter.HideName = true;
    }

    private static string Property(EdaComponent source, string name) => source.Properties
        .FirstOrDefault(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;

    private static string CreateLibraryDescription(EdaComponent source, string componentName, string manufacturer, string manufacturerPart)
    {
        var fields = new List<string>();
        var type = ComponentType(source);
        var rawDescription = source.Description.Trim();
        if (!string.IsNullOrWhiteSpace(type)) fields.Add(type);
        else if (IsUseful(rawDescription, componentName, manufacturerPart, source.LcscPartNumber)) fields.Add(rawDescription);
        else
        {
            var category = source.Tags.FirstOrDefault(tag => !string.IsNullOrWhiteSpace(tag));
            if (!string.IsNullOrWhiteSpace(category)) fields.Add(category.Trim());
        }

        // The Description column is deliberately ordered for quick library scanning:
        // type, package, primary value, voltage/rating, tolerance, material/specification, manufacturer.
        // Every other EasyEDA field is still preserved as a hidden Altium parameter.
        var package = DisplayPackage(source.FootprintName);
        if (IsUseful(package, componentName, manufacturerPart, source.LcscPartNumber)) fields.Add(package);
        AddField(FirstProperty(source, ["Value", "Resistance", "Resistance Value", "Capacitance", "Inductance", "Impedance", "Frequency"]));
        // Ratings are additive and retain this order: voltage, power, then current.
        AddField(FirstProperty(source, ["Voltage", "Voltage Rating", "Rated Voltage", "Working Voltage"]));
        AddField(FirstProperty(source, ["Power", "Power Rating"]));
        AddField(FirstProperty(source, ["Current", "Current Rating"]));
        AddField(FirstProperty(source, ["Tolerance", "Accuracy"]));
        AddField(FirstProperty(source, ["Dielectric", "Material", "Technology", "Temperature Coefficient"]));
        if (!string.IsNullOrWhiteSpace(manufacturer)) fields.Add(manufacturer);

        return fields.Count == 0 ? componentName : string.Join(" · ", fields.Distinct(StringComparer.OrdinalIgnoreCase));

        void AddField(string value)
        {
            value = FormatElectricalValue(value, source);
            if (IsUseful(value, componentName, manufacturerPart, source.LcscPartNumber) &&
                !fields.Any(field => field.Equals(value, StringComparison.OrdinalIgnoreCase))) fields.Add(value);
        }
    }

    private static bool IsUseful(string? value, params string[] identities) => !string.IsNullOrWhiteSpace(value) &&
        !identities.Any(identity => value.Trim().Equals(identity, StringComparison.OrdinalIgnoreCase));

    private static string FirstProperty(EdaComponent source, IEnumerable<string> names) => names
        .Select(name => Property(source, name))
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string? ComponentType(EdaComponent source) => UserSymbolLibraryResolver.IsFerriteBead(source)
        ? "Ferrite bead" : Prefix(source).ToUpperInvariant() switch
    {
        "R" => "Resistor", "C" => "Capacitor", "L" => "Inductor", "D" => "Diode", "Q" => "Transistor",
        "LED" => "LED", "F" => "Fuse", "SW" => "Switch", "J" => "Connector", "Y" => "Crystal", "B" => "Battery",
        _ => null
    };

    private static string CleanManufacturer(string value) => System.Text.RegularExpressions.Regex
        .Replace(value ?? string.Empty, @"\s*\([^)]*[\?？][^)]*\)", string.Empty).Trim().Trim('-', '—', ' ');

    private static string DisplayPackage(string value) => System.Text.RegularExpressions.Regex
        .Replace((value ?? string.Empty).Trim(), @"^[RCLD](?=\d{4}\b)", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string FormatElectricalValue(string? value, EdaComponent source)
    {
        var result = (value ?? string.Empty).Trim();
        if (result.Length == 0) return result;
        result = System.Text.RegularExpressions.Regex.Replace(result, @"(?<=\d)\s*([kKmMgG])\s*(Ω|ohms?)", match =>
            $" {match.Groups[1].Value.ToLowerInvariant()}Ω", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(result, @"(?<=\d)\s*([uµnmp])\s*([fFhH])\b", match =>
            $" {match.Groups[1].Value.Replace('u', 'µ').Replace('U', 'µ')}{match.Groups[2].Value.ToUpperInvariant()}");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"(?<=\d)\s*V\b", " V", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(result, @"(?<=\d)\s*A\b", " A", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // EasyEDA sometimes sends a resistor value as a bare number (for example "1000").
        // Convert that to an engineering value only for an R-prefixed component.
        if (Prefix(source).Equals("R", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(result, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ohms))
            result = ohms >= 1000 && ohms % 1000 == 0 ? $"{ohms / 1000:0.###} kΩ" : $"{ohms:0.###} Ω";
        return result;
    }

    private static string FirstNonEmpty(params string?[] candidates) => candidates
        .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim() ?? string.Empty;

    /// <summary>The component name users see in Altium; LCSC is retained as a parameter.</summary>
    public static string LibraryComponentName(EdaComponent source) =>
        SafeLibraryName(FirstNonEmpty(source.Name, Property(source, "Manufacturer Part"), source.LcscPartNumber));

    /// <summary>Converts a human/source name into an Altium SchLib-index-safe record name.</summary>
    public static string SafeLibraryName(string? name)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace((name ?? string.Empty).Trim(), @"[^A-Za-z0-9_+\-.]", "_");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"_+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "YouEDA_Component" : cleaned[..Math.Min(cleaned.Length, 120)];
    }

    private static string SafeParameterName(string name)
    {
        var cleaned = new string(name.Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray());
        cleaned = string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "EasyEDA Property" : cleaned;
    }

    private static SchParameter GetOrCreateParameter(SchComponent symbol, string name, double x, double y)
    {
        var parameter = symbol.Parameters.OfType<SchParameter>().FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (parameter is not null) return parameter;
        parameter = new SchParameter
        {
            Name = name, FontId = 1, Color = 8388608, HideName = true,
            Location = new CoordPoint(Coord.FromMm(x), Coord.FromMm(y))
        };
        symbol.AddParameter(parameter);
        return parameter;
    }

    private static void SetComment(SchComponent symbol, string expression)
    {
        symbol.Comment = expression;
        var comment = GetOrCreateParameter(symbol, "Comment", 0, -2.54);
        comment.Value = expression;
        comment.IsVisible = true;
        comment.HideName = true;
    }

    private static void AttachFootprint(SchComponent symbol)
    {
        // AltiumSharp exposes implementations read-only; its concrete list is still mutable.
        // Check the representation explicitly so a future AltiumSharp change fails visibly.
        if ((object)symbol.Implementations is not List<SchImplementation> models)
            throw new InvalidDataException("AltiumSharp changed the schematic implementation collection.");
        models.RemoveAll(model => model.ModelType?.Equals("PCBLIB", StringComparison.OrdinalIgnoreCase) == true);
        models.Add(new SchImplementation
        {
            Description = "YouEDA footprint",
            ModelName = FootprintNameForSymbol(symbol),
            ModelType = "PCBLIB",
            IsCurrent = true
        });
    }

    private static void RemoveFootprint(SchComponent symbol)
    {
        if ((object)symbol.Implementations is not List<SchImplementation> models)
            throw new InvalidDataException("AltiumSharp changed the schematic implementation collection.");
        models.RemoveAll(model => model.ModelType?.Equals("PCBLIB", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void RemoveParameter(SchComponent symbol, string name)
    {
        foreach (var parameter in symbol.Parameters.OfType<SchParameter>()
                     .Where(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray())
            ((ISchComponent)symbol).RemoveParameter(parameter);
    }

    private static string FootprintNameForSymbol(SchComponent symbol) =>
        symbol.Parameters.OfType<SchParameter>().FirstOrDefault(parameter =>
            parameter.Name.Equals("Footprint", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(parameter.Value))?.Value ?? symbol.Name;

    private static void EnsureReadableCanvas(SchLibrary library)
    {
        // Altium displays an otherwise valid, newly created SchLib on a black canvas when
        // AreaColor is absent; the user's existing Altium library uses this light sheet color.
        library.HeaderParameters ??= new List<KeyValuePair<string, string>>
        {
            new("HEADER", "Protel for Windows - Schematic Library Editor Binary File Version 5.0"),
            new("Weight", "0"), new("MinorVersion", "3"),
            new("UniqueID", new string(Enumerable.Range(0, 8).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray())),
            new("FontIdCount", "1"), new("FontName1", "Times New Roman"), new("Size1", "10"),
            new("UseMBCS", "T"), new("IsBOC", "T"), new("SheetStyle", "9"),
            new("BorderOn", "T"), new("Display_Unit", "0")
        };
        AddHeaderIfMissing(library, "AreaColor", "16317695");
        AddHeaderIfMissing(library, "SnapGridOn", "T");
        AddHeaderIfMissing(library, "SnapGridSize", "10");
        AddHeaderIfMissing(library, "VisibleGridOn", "T");
        AddHeaderIfMissing(library, "VisibleGridSize", "10");
        AddHeaderIfMissing(library, "CustomX", "18000");
        AddHeaderIfMissing(library, "CustomY", "18000");
        AddHeaderIfMissing(library, "UseCustomSheet", "T");
    }

    private static void AddHeaderIfMissing(SchLibrary library, string name, string value)
    {
        if (!library.HeaderParameters!.Any(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
            library.HeaderParameters!.Add(new KeyValuePair<string, string>(name, value));
    }

    public static SchComponent CreateEasyEdaSymbol(EdaComponent source)
    {
        var symbol = new SchComponent
        {
            Name = LibraryComponentName(source),
            LibReference = LibraryComponentName(source),
            Description = string.IsNullOrWhiteSpace(source.Description) ? source.Name : source.Description,
            Comment = source.Name,
            DesignatorPrefix = Prefix(source),
            DesignItemId = source.Properties.GetValueOrDefault("Manufacturer Part"),
            PartCount = 1
        };
        var designator = new SchParameter { Name = "Designator", Value = Prefix(source) + "?", IsVisible = true,
            HideName = true, Color = 8388608, FontId = 1, OwnerPartId = -1 };
        symbol.AddParameter(designator);
        symbol.AddParameter(new SchParameter { Name = "LCSC Part", Value = source.LcscPartNumber, IsVisible = false });
        if (source.Properties.TryGetValue("Value", out var value) && !string.IsNullOrWhiteSpace(value))
            symbol.AddParameter(new SchParameter { Name = "Value", Value = value, IsVisible = true, HideName = true });
        if (source.Properties.TryGetValue("Manufacturer", out var manufacturer) && !string.IsNullOrWhiteSpace(manufacturer))
            symbol.AddParameter(new SchParameter { Name = "Manufacturer", Value = manufacturer, IsVisible = false });

        // A fallback must be immediately readable in Altium. EasyEDA artwork is often made
        // from tiny fragments whose proportions do not translate reliably to an SchLib.
        // Inductors get a dedicated conventional coil; other parts use the general body.
        if (UsesStandardInductorSymbol(source)) AddInductorFallback(symbol, source, designator);
        else AddGeneratedFallback(symbol, source, designator);
        return symbol;
    }

    /// <summary>Only an ordinary two-terminal inductor may use the conventional coil fallback.</summary>
    public static bool UsesStandardInductorSymbol(EdaComponent source)
    {
        if (source.SymbolPins.Count != 2 || source.SymbolPins.Select(pin => pin.Number).Distinct().Count() != 2)
            return false;
        var evidence = string.Join(' ', source.Tags.Append(source.Name).Append(source.Description)
            .Concat(source.Properties.Values));
        if (new[] { "BEAD", "FERRITE CHIP", "COUPLED", "TRANSFORMER", "COMMON MODE", "COMMON-MODE" }
            .Any(term => evidence.Contains(term, StringComparison.OrdinalIgnoreCase))) return false;
        return Prefix(source).Equals("L", StringComparison.OrdinalIgnoreCase) ||
            evidence.Contains("INDUCT", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddInductorFallback(SchComponent symbol, EdaComponent source, SchParameter designator)
    {
        var pins = source.SymbolPins.OrderBy(pin => pin.Number, StringComparer.Ordinal).ToArray();
        if (pins.Length != 2) throw new InvalidDataException("An ordinary inductor needs exactly two EasyEDA schematic pins.");

        // Standard horizontal inductor: two passive pins, four blue coil loops and the pair
        // of core bars shown by Altium/EasyEDA's conventional inductor symbol.  The previous
        // fallback emitted only the loops, leaving an incomplete "arcs on a wire" drawing.
        // All dimensions are in mm and use Altium's Small schematic line width.
        designator.Location = new CoordPoint(Coord.FromMm(-2.54), Coord.FromMm(5.08));
        // Native Altium passive libraries store visible component parameters at owner part 0.
        // Owner 1 renders in our previewer but Altium silently hides the field on the canvas.
        designator.OwnerPartId = 0;
        symbol.AddParameter(new SchParameter { Name = "Comment", Value = "=Value", IsVisible = true,
            HideName = true, Color = 8421504, FontId = 1,
            Location = new CoordPoint(Coord.FromMm(-3.81), Coord.FromMm(-4.572)), OwnerPartId = 0 });
        symbol.AddPin(SchPin.Create(pins[0].Number).WithName(string.Empty)
            .At(Coord.FromMm(-5.08), Coord.FromMm(0)).Length(Coord.FromMm(2.54))
            .Orient(PinOrientation.Left).Electrical(PinElectricalType.Passive).Build());
        symbol.AddPin(SchPin.Create(pins[1].Number).WithName(string.Empty)
            .At(Coord.FromMm(5.08), Coord.FromMm(0)).Length(Coord.FromMm(2.54))
            .Orient(PinOrientation.Right).Electrical(PinElectricalType.Passive).Build());
        const int coilBlue = 16711680; // native BGR: #0000FF
        // SchArc stores the native Altium line-style index rather than a physical Coord.
        // In Altium Designer's UI: 0=Smallest, 1=Small, 2=Medium, 3=Large.  The supplied
        // Inductance.SchLib uses Small, so use index 1 / 2 mil for coil and core artwork.
        const int artworkLineStyle = 1; // Small (2 mil)
        var artworkTrackWidth = Coord.FromMils(2);
        foreach (var x in new[] { -3.81, -1.27, 1.27, 3.81 })
            symbol.AddArc(new SchArc
            {
                Center = new CoordPoint(Coord.FromMm(x), Coord.FromMm(0)), Radius = Coord.FromMm(1.27),
                StartAngle = 0, EndAngle = 180, LineWidth = artworkLineStyle, Color = coilBlue,
                OwnerPartId = 1, IsNotAccessible = true
            });
        // Ferrite/core bars: these are real symbol primitives, not a screen-grid artifact.
        // Keep them above the coils with a clear gap, matching the conventional library
        // drawing and preventing the electrical wire from looking like the whole symbol.
        foreach (var y in new[] { 2.54, 3.81 })
            symbol.AddLine(new SchLine
            {
                Start = new CoordPoint(Coord.FromMm(-5.08), Coord.FromMm(y)),
                End = new CoordPoint(Coord.FromMm(5.08), Coord.FromMm(y)),
                Width = artworkTrackWidth, Color = coilBlue, OwnerPartId = 1,
                IsNotAccessible = true
            });
    }

    private static void AddGeneratedFallback(SchComponent symbol, EdaComponent source, SchParameter designator)
    {
        var left = source.SymbolPins
            .Where(pin => pin.NameAnchor.Equals("start", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pin => pin.Ymm).ThenBy(pin => pin.Number, StringComparer.Ordinal)
            .ToArray();
        var right = source.SymbolPins
            .Where(pin => !pin.NameAnchor.Equals("start", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pin => pin.Ymm).ThenBy(pin => pin.Number, StringComparer.Ordinal)
            .ToArray();
        if (left.Length == 0 || right.Length == 0)
        {
            left = source.SymbolPins.Where((_, index) => index % 2 == 0).ToArray();
            right = source.SymbolPins.Except(left).ToArray();
        }
        var rows = Math.Max(2, Math.Max(left.Length, right.Length));
        // Give the inside pin labels enough room.  This produces the familiar compact,
        // pale-yellow IC body rather than overlapping text on a thin EasyEDA outline.
        const double spacing = 2.54;
        var longestName = source.SymbolPins.Select(pin => (pin.Name ?? string.Empty).Length).DefaultIfEmpty(0).Max();
        var halfWidth = Math.Max(12.7, 2.54 + longestName * 1.8);
        var halfHeight = (rows + 1) * spacing / 2;
        designator.Location = new CoordPoint(Coord.FromMm(-halfWidth), Coord.FromMm(halfHeight + spacing));
        symbol.AddParameter(new SchParameter { Name = "Comment", Value = source.Name, IsVisible = true,
            HideName = true, Color = 8388608, FontId = 1,
            Location = new CoordPoint(Coord.FromMm(-halfWidth), Coord.FromMm(-halfHeight - spacing)) });
        symbol.AddRectangle(new SchRectangle
        {
            Corner1 = new CoordPoint(Coord.FromMm(-halfWidth), Coord.FromMm(-halfHeight)),
            Corner2 = new CoordPoint(Coord.FromMm(halfWidth), Coord.FromMm(halfHeight)),
            Color = 128,
            // Altium's "Small" schematic border (1 mil). Keep the body transparent so
            // the writer's primitive order can never cover pin names with a solid fill.
            LineWidth = Coord.FromMils(1),
            FillColor = 11862015, // pale yellow (BGR in Altium's native colour format)
            IsFilled = true,
            IsTransparent = true,
            // Altium only displays symbol artwork that belongs to an actual part.  Pins are
            // tolerant of the default owner (which is why they showed), but rectangles are not.
            OwnerPartId = 1,
            IsNotAccessible = true
        });
        // In Altium, the pin location is the body-side endpoint and the orientation points
        // outward. Reversing that convention puts pin numbers inside the body and names outside.
        AddPins(symbol, left, -halfWidth, PinOrientation.Left, rows);
        AddPins(symbol, right, halfWidth, PinOrientation.Right, rows);
    }

    private static void AddPins(SchComponent symbol, EdaSymbolPin[] pins, double x, PinOrientation orientation, int rows)
    {
        for (var index = 0; index < pins.Length; index++)
        {
            var y = (rows - 1) * 2.54 / 2 - index * 2.54;
            var pin = pins[index];
            symbol.AddPin(SchPin.Create(pin.Number).WithName(string.IsNullOrWhiteSpace(pin.Name) ? pin.Number : pin.Name)
                .At(Coord.FromMm(x), Coord.FromMm(y)).Length(Coord.FromMm(2.54)).Orient(orientation).Electrical(MapElectrical(pin.ElectricalType)).Build());
        }
    }

    private static PinElectricalType MapElectrical(int type) => type switch
    {
        1 => PinElectricalType.InputOutput,
        2 => PinElectricalType.Output,
        3 => PinElectricalType.Power,
        4 => PinElectricalType.Input,
        _ => PinElectricalType.Passive
    };

    private static string Prefix(EdaComponent component) => component.Properties.TryGetValue("pre", out var prefix) && !string.IsNullOrWhiteSpace(prefix)
        ? prefix.Trim().TrimEnd('?') : "U";
}
