using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YouEDA.Engine.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Eda.Primitives;

namespace YouEDA.Engine.Services;

/// <summary>Locates the user's existing native Altium symbols without altering their artwork or pin mapping.</summary>
public sealed class UserSymbolLibraryResolver
{
    /// <summary>Maximum pin count for a reusable common bundled symbol.</summary>
    public const int CommonSymbolMaximumPins = 6;

    // This is an intentionally curated, stable catalog of native records selected from the
    // user's C:\altium-library\symbols collection.  It covers the common two-to-six pin
    // market families without silently substituting a vendor-specific IC or a different pin
    // mapping.  Every entry retains its source Altium artwork, arcs, labels, and line styles.
    private static readonly HashSet<string> SourceCommonSymbolNames = new(StringComparer.Ordinal)
    {
        "Capacitor", "Polarised Capacitor", "VARIABLE CAPACITOR", "Inductor", "Ferrite Chip",
        "UNMOUNTED FUSE", "Thermistor PTC", "Thermistor", "Resistor", "Potentiometer", "RTD",
        "Diode", "Diode Zener", "DIODE TVS UNI", "LED", "PHOTODIODE",
        "DIODES INC NPN SOT-23-3", "DIODES INC MOSFET N-CH SOT-23-3",
        "DIODES MOSFET P-CH SOT-223", "Abracon Crystal ABLS", "SPST-NO 2 PIN", "SPST-NO 4 PIN", "NKK AB11AH-HA",
        "C&K ELUMOASAQ2C12", "Optoisolator", "1 Pin", "2 Pin", "3 Pin", "4 Pin", "5 Pin", "6 Pin",
        "Barrel Connector", "Basic Antenna", "RF Connector - SMA", "MICROPHONE ELECTRET", "SPEAKER 2 PIN",
        "Diode TVS", "Diode Zener Bidirectional", "DIODE ARRAY COMMON CATHODE", "LED RGB COMMON ANODE",
        "LED RGB INDEPENDENT", "LED W.THERMAL PAD", "Buzzer 2 pin", "MOUNTED FUSE", "FUSE HOLDER",
        "MICROPHONE BAL VDD GND OUTPM", "MICROPHONE UNBAL VDD GND OUT", "DIODES BJT NPN SOT23-3 BEC",
        "DIODES INC MOSFET N-CH SOT-223", "DIODES MOSFET P-CH SOT-23-3", "DIODES DUAL MOSFET P-CH SOT-363",
        "Abracon Crystal ABM10", "Abracon Crystal ABM8G", "Abracon Crystal ABS06", "Abracon Crystal ABS07",
        "Abracon Crystal ABS25", "ECS Crystal CSM-3X", "Epson Crystal TSX-3225", "Kyocera Crystal CX2016DB",
        "NDK Crystal NX2520SA", "NDK Crystal NX3225GA", "NDK Crystal NX3225SA", "NDK Crystal NX5032GA",
        "NDK Crystal NX8045GB", "TXC Crystal 9C", "DIODES INC DUAL MOSFET N-CH SOT363",
        "FAIRCHILD MOSFET N-CH SOT-23-3", "ON SEMI MOSFET N-CH SOT-23-3", "ON SEMI MOSFET P-CH SOT-223",
        "NXP MOSFET N-CH SOT-23-3", "NXP MOSFET P-CH SOT-23-3"
    };
    private static readonly HashSet<string> CommonSymbolNames = new(
        SourceCommonSymbolNames.Select(AltiumSchExporter.SafeLibraryName), StringComparer.Ordinal);
    // A 300-part import creates several resolver instances. Cache native source libraries
    // process-wide so the same personal SchLib is parsed once, not once per part.
    private static readonly ConcurrentDictionary<string, Lazy<SchLibrary>> LibraryCache = new(StringComparer.OrdinalIgnoreCase);
    public SymbolLibraryMatch? Resolve(EdaComponent component, string? symbolRoot, string? overrideFile)
    {
        if (!string.IsNullOrWhiteSpace(overrideFile))
        {
            if (!File.Exists(overrideFile)) throw new FileNotFoundException("Selected schematic symbol library was not found.", overrideFile);
            if (!string.Equals(Path.GetExtension(overrideFile), ".SchLib", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected symbol file must be an Altium .SchLib file.");
            return new SymbolLibraryMatch(overrideFile, "selected override");
        }

        // The user's explicit source preference wins over automatic native matching.
        // A manually supplied override above remains available.
        if (EasyEdaSymbolPreference.Reason(component) is { } preference)
        {
            ImportDiagnostics.Record("symbol.easyeda_preference", new { part = component.LcscPartNumber, reason = preference });
            return null;
        }
        var files = SymbolFiles(symbolRoot).ToArray();
        if (files.Length == 0) return null;
        // An exact component from a supplied native SchLib is authoritative, including for
        // multi-pin ICs.  The EasyEDA fallback rule applies only after that safe lookup fails.
        var exactNative = FindExactNativeMatch(component, files);
        if (exactNative is not null) return exactNative;
        if (MustGenerateFromEasyEda(component)) return null;
        if (NativeCommonSymbolPolicy.Applies(component)) return ResolveNativeCommon(component, files);
        if (DiscreteNetworkSymbolPolicy.Applies(component)) return ResolveNetwork(component, files);
        // Curated diode drawings have numeric, hidden pin names. Bind their verified
        // artwork terminals to the source's explicit A/K functions on a fresh copy.
        var diode = ResolveDiode(component, files);
        if (diode is not null) return diode;
        // Never fall through to a generic BJT match: B/C/E alone cannot prove NPN/PNP.
        if (TransistorSymbolPolicy.Applies(component)) return ResolveTransistor(component, files);
        if (files.Length == 1 && IsMasterLibrary(files[0])) return ResolveInMaster(component, files[0]);
        // Exact manufacturer/name matches win; a generic passive symbol is only a safe fallback.
        // A non-master .SchLib cannot safely be treated as a single reusable symbol: it may
        // contain multiple records with different pin mappings.  Only the curated master is
        // used for family fallback; otherwise preserve EasyEDA's own symbol.
        return files.Where(IsMasterLibrary).Select(path => ResolveInMaster(component, path)).FirstOrDefault(match => match is not null);
    }

    public string CopyToOutput(SymbolLibraryMatch match, EdaComponent component, string outputFolder)
    {
        var name = Path.GetFileNameWithoutExtension(match.Path);
        var safeName = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var destination = Path.Combine(outputFolder, $"{component.LcscPartNumber}_symbol_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.SchLib");
        if (match.ComponentName is null)
        {
            File.Copy(match.Path, destination);
            return destination;
        }

        // Components are mutable (the caller changes Name/LibReference to the LCSC key),
        // so return a fresh deserialised instance rather than leaking the cached catalog item.
        var source = (SchLibrary)AltiumLibrary.OpenSchLibAsync(match.Path).GetAwaiter().GetResult();
        if (source[match.ComponentName] is not SchComponent selected)
            throw new InvalidDataException($"Bundled symbol '{match.ComponentName}' was not found.");
        ApplyDiodeBinding(selected, match);
        ApplyTransistorBinding(selected, match);
        ApplyCommonBinding(selected, match);
        if (match.Network is { } network) { selected = DiscreteNetworkSymbolPolicy.Apply(selected, network); ClearDonorMetadata(selected); }
        var export = (SchLibrary)AltiumLibrary.CreateSchLib();
        export.Add(selected);
        export.SaveAsync(destination).GetAwaiter().GetResult();
        return destination;
    }

    /// <summary>
    /// Loads the exact bundled component selected by the matcher. The returned object is an
    /// in-memory copy of the source-library record and can safely be renamed for youeda.SchLib.
    /// </summary>
    public SchComponent LoadSelectedComponent(SymbolLibraryMatch match)
    {
        if (match.ComponentName is null)
            throw new InvalidDataException("A library-wide selection cannot be added to the shared SchLib. Select a component symbol instead.");

        // The selected component will be renamed to the LCSC key by the output writer.
        // Reloading makes each selection independent from the read-only lookup cache.
        var source = (SchLibrary)AltiumLibrary.OpenSchLibAsync(match.Path).GetAwaiter().GetResult();
        if (source[match.ComponentName] is not SchComponent selected)
            throw new InvalidDataException($"Bundled symbol '{match.ComponentName}' was not found.");
        ApplyDiodeBinding(selected, match);
        ApplyTransistorBinding(selected, match);
        ApplyCommonBinding(selected, match);
        if (match.Network is { } network) { selected = DiscreteNetworkSymbolPolicy.Apply(selected, network); ClearDonorMetadata(selected); }
        return selected;
    }

    /// <summary>Returns a read-only catalog component for preview without reopening the master library.</summary>
    public SchComponent LoadPreviewComponent(SymbolLibraryMatch match)
    {
        if (match.DiodePins is not null || match.TransistorPins is not null || match.Network is not null || match.CommonPins is not null) return LoadSelectedComponent(match);
        if (match.ComponentName is null)
            throw new InvalidDataException("Select a component in the bundled library to preview it.");
        var library = LoadLibrary(match.Path);
        var selected = library[match.ComponentName] as SchComponent ??
            throw new InvalidDataException($"Bundled symbol '{match.ComponentName}' was not found.");
        return selected;
    }

    public IReadOnlyList<SymbolLibraryMatch> ListMasterSymbols(string masterPath)
    {
        return SymbolFiles(masterPath).Where(IsMasterLibrary).SelectMany(path => LoadLibrary(path).Components.OfType<SchComponent>()
            .Where(IsCommonBundledSymbol)
            .Select(symbol => new SymbolLibraryMatch(path, "user-selected bundled symbol", symbol.Name)))
            .OrderBy(match => match.ComponentName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private SymbolLibraryMatch? ResolveNativeCommon(EdaComponent component, IReadOnlyList<string> files)
    {
        var binding = NativeCommonSymbolPolicy.Resolve(component);
        if (binding is null) return null;
        foreach (var path in files.Where(IsMasterLibrary))
        {
            if (LoadLibrary(path)[binding.Template] is not SchComponent) continue;
            var match = new SymbolLibraryMatch(path, $"native {binding.Kind} with verified function binding", binding.Template, CommonPins: binding);
            if (!CheckPinCompatibility(component, LoadSelectedComponent(match), true).Compatible) continue;
            ImportDiagnostics.Record("symbol.common_binding", new { part = component.LcscPartNumber, binding, sourceLibrary = path });
            return match;
        }
        return null;
    }

    private static void ApplyCommonBinding(SchComponent symbol, SymbolLibraryMatch match)
    {
        if (match.CommonPins is not { } binding) return;
        if (symbol.Pins.Count != binding.Numbers.Length) throw new InvalidDataException("Native common template terminal count changed.");
        var pins = Enumerable.Range(1, binding.Numbers.Length)
            .Select(n => symbol.Pins.OfType<SchPin>().Single(p => p.Designator == n.ToString())).ToArray();
        for (int i = 0; i < pins.Length; i++) { pins[i].Designator = binding.Numbers[i]; pins[i].Name = binding.Names[i]; }
        ClearDonorMetadata(symbol);
    }

    private SymbolLibraryMatch? ResolveNetwork(EdaComponent component, IReadOnlyList<string> files)
    {
        var binding = DiscreteNetworkSymbolPolicy.Resolve(component);
        if (binding is null) return null;
        foreach (var path in files.Where(IsMasterLibrary))
        {
            if (LoadLibrary(path)[binding.Template] is not SchComponent) continue;
            var match = new SymbolLibraryMatch(path, $"verified {binding.Kind} using native artwork", binding.Template, Network: binding);
            if (!CheckPinCompatibility(component, LoadPreviewComponent(match), true).Compatible) continue;
            ImportDiagnostics.Record("symbol.network_binding", new { part = component.LcscPartNumber, binding, sourceLibrary = path,
                sourcePins = component.SymbolPins.Select(p => new { p.Number, p.Name }), lineStyle = "Small" });
            return match;
        }
        return null;
    }

    private SymbolLibraryMatch? ResolveTransistor(EdaComponent component, IReadOnlyList<string> files)
    {
        var binding = TransistorSymbolPolicy.Resolve(component);
        if (binding is null) return null;
        var name = binding.Kind switch
        {
            "NMOS" => "DIODES_INC_MOSFET_N-CH_SOT-23-3",
            "PMOS" => "DIODES_MOSFET_P-CH_SOT-23-3",
            _ => "DIODES_INC_NPN_SOT-23-3"
        };
        foreach (var path in files.Where(IsMasterLibrary))
        {
            if (LoadLibrary(path)[name] is not SchComponent template || template.Pins.Count != 3) continue;
            var match = new SymbolLibraryMatch(path, $"native {binding.Kind.ToLowerInvariant()} with verified function binding", name,
                TransistorPins: binding);
            if (!CheckPinCompatibility(component, LoadPreviewComponent(match), true).Compatible) continue;
            ImportDiagnostics.Record("symbol.transistor_binding", new { part = component.LcscPartNumber, template = name,
                sourceLibrary = path, binding, pnpArrowVariant = binding.Kind == "PNP", lineStyle = "Small" });
            return match;
        }
        return null;
    }

    private static void ApplyTransistorBinding(SchComponent symbol, SymbolLibraryMatch match)
    {
        if (match.TransistorPins is not { } binding) return;
        var fet = binding.Kind is "NMOS" or "PMOS";
        var roles = fet ? new[] { "G", "D", "S" } : new[] { "B", "C", "E" };
        var numbers = new[] { binding.Control, binding.Output, binding.Common };
        // Resolve all terminals before mutating so permutations cannot collide.
        var pins = roles.Select(role => symbol.Pins.OfType<SchPin>().Single(p => TransistorSymbolPolicy.Function(p.Name ?? "") == role)).ToArray();
        for (int i = 0; i < pins.Length; i++) { pins[i].Designator = numbers[i]; pins[i].Name = roles[i]; }
        if (binding.Kind == "PNP")
        {
            // The source collection has only NPN SchLib records (but a PNP reference image).
            // Reverse only this reviewed emitter arrow; all other native geometry stays intact.
            var arrow = symbol.Polygons.OfType<SchPolygon>().Single();
            var expected = new[] { (100d, -100d), (70d, -50d), (40d, -90d) };
            if (!arrow.Vertices.Select(p => (p.X.ToMils(), p.Y.ToMils())).SequenceEqual(expected))
                throw new InvalidDataException("Native BJT arrow changed; the PNP variant needs artwork review.");
            // Keep the existing primitive identity: the native writer also retains its
            // read-order list, so removing/adding would serialize BOTH old and new arrows.
            if (arrow.Vertices is not List<CoordPoint> vertices)
                throw new InvalidDataException("AltiumSharp changed the polygon vertex collection.");
            for (int i = 0; i < vertices.Count; i++)
                vertices[i] = new(Coord.FromMils(100 - vertices[i].X.ToMils()), Coord.FromMils(-130 - vertices[i].Y.ToMils()));
        }
        // User's explicit schematic preference: Small, not the native hairline/Smallest.
        foreach (var line in symbol.Lines.OfType<SchLine>()) line.Width = Coord.FromMils(2);
        foreach (var line in symbol.Polylines.OfType<SchPolyline>()) line.LineWidth = 1;
        foreach (var polygon in symbol.Polygons.OfType<SchPolygon>()) polygon.LineWidth = 1;
        foreach (var arc in symbol.Arcs.OfType<SchArc>()) arc.LineWidth = 1;
        ClearDonorMetadata(symbol);
    }

    private SymbolLibraryMatch? ResolveDiode(EdaComponent component, IReadOnlyList<string> files)
    {
        var kind = DiodeKind(component);
        if (kind is null || component.SymbolPins.Count != 2) return null;
        var anodes = component.SymbolPins.Where(p => DiodeFunction(p.Name) == "ANODE").ToArray();
        var cathodes = component.SymbolPins.Where(p => DiodeFunction(p.Name) == "CATHODE").ToArray();
        if (anodes.Length != 1 || cathodes.Length != 1 || string.IsNullOrWhiteSpace(anodes[0].Number) ||
            string.IsNullOrWhiteSpace(cathodes[0].Number) || anodes[0].Number.Trim() == cathodes[0].Number.Trim()) return null;

        // Native artwork verified in the supplied libraries: common catalog A=1/K=2;
        // the user's SS34 Schottky artwork faces left, with A=2/K=1. Never infer
        // source polarity from its part number, package, pin order, or designator.
        var name = kind switch { "LED" => "LED", "Zener" => "Diode_Zener", "Schottky" => "SS34_C52023881", _ => "Diode" };
        var candidates = kind == "Schottky" ? files.Where(p => !IsMasterLibrary(p)).Take(200) : files.Where(IsMasterLibrary);
        foreach (var path in candidates)
        {
            var native = LoadLibrary(path).Components.OfType<SchComponent>().FirstOrDefault(s => s.Name == name);
            if (native is null || native.Pins.Count != 2 ||
                !native.Pins.Select(p => p.Designator).Order().SequenceEqual(new[] { "1", "2" })) continue;
            var match = new SymbolLibraryMatch(path, $"native {kind.ToLowerInvariant()} with verified polarity binding", name,
                new(kind == "Schottky" ? "2" : "1", kind == "Schottky" ? "1" : "2",
                    anodes[0].Number.Trim(), cathodes[0].Number.Trim()));
            if (CheckPinCompatibility(component, LoadPreviewComponent(match), true).Compatible)
            {
                ImportDiagnostics.Record("symbol.diode_binding", new { part = component.LcscPartNumber, kind, template = name,
                    sourceLibrary = path, binding = match.DiodePins });
                return match;
            }
        }
        return null;
    }

    private static void ApplyDiodeBinding(SchComponent symbol, SymbolLibraryMatch match)
    {
        if (match.DiodePins is not { } binding) return;
        var anode = symbol.Pins.OfType<SchPin>().Single(p => p.Designator == binding.TemplateAnode);
        var cathode = symbol.Pins.OfType<SchPin>().Single(p => p.Designator == binding.TemplateCathode);
        anode.Designator = binding.SourceAnode; anode.Name = "A";
        cathode.Designator = binding.SourceCathode; cathode.Name = "K";
        ClearDonorMetadata(symbol);
    }

    private static void ClearDonorMetadata(SchComponent symbol)
    {
        // This is artwork reuse, not a replacement part. Do not inherit the donor
        // SS34's ratings, supplier references, footprint, or simulation model.
        // PrepareSymbol will populate the actual imported part's metadata/models.
        foreach (var parameter in symbol.Parameters.Where(p => p.Name is not ("Designator" or "Comment")).ToArray())
            ((OriginalCircuit.Eda.Models.Sch.ISchComponent)symbol).RemoveParameter(parameter);
        if ((object)symbol.Implementations is not List<SchImplementation> models)
            throw new InvalidDataException("AltiumSharp changed the schematic implementation collection.");
        models.Clear();
    }

    private static string? DiodeKind(EdaComponent component)
    {
        if (component.SymbolPins.Count != 2) return null;
        // Only relevant electrical metadata: a manufacturer name or URL containing
        // 'LED' must not turn a rectifier into an LED, nor a photodiode into one.
        var text = string.Join(' ', component.Tags.Append(component.Description)
            .Concat(component.Properties.Where(p => p.Key is "Category" or "Component Type" or "Technology").Select(p => p.Value))).ToUpperInvariant();
        if (new[] { "TVS", "ESD", "PHOTO", "ARRAY", "BRIDGE", "BIDIRECTION", "DUAL" }.Any(text.Contains)) return null;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bLED(S)?\b|LIGHT EMITTING")) return "LED";
        if (text.Contains("ZENER")) return "Zener";
        if (text.Contains("SCHOTTKY")) return "Schottky";
        return text.Contains("DIODE") || text.Contains("RECTIFIER") ? "Diode" : null;
    }

    private static string? DiodeFunction(string name) => name.Trim().ToUpperInvariant() switch
    {
        "A" or "ANODE" or "+" => "ANODE", "K" or "C" or "CATHODE" or "-" => "CATHODE", _ => null
    };

    /// <summary>
    /// Writes a physically slimmed copy of the bundled catalog while preserving the source
    /// library's headers, fonts, and native Altium records. This is used when distributing the
    /// deliberately small common-symbol catalog rather than merely hiding entries in the UI.
    /// </summary>
    public static async Task<int> WriteCommonCatalogAsync(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Bundled symbol catalog was not found.", sourcePath);
        var library = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(sourcePath);
        var remove = library.Components.OfType<SchComponent>()
            .Where(symbol => !IsCommonBundledSymbol(symbol))
            .Select(symbol => symbol.Name).ToArray();
        foreach (var name in remove) library.Remove(name);

        var retained = library.Components.OfType<SchComponent>().Count();
        if (retained != CommonSymbolNames.Count)
            throw new InvalidDataException($"Common catalog contains {retained} symbols; expected {CommonSymbolNames.Count}.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await library.SaveAsync(destinationPath);

        var verified = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(destinationPath);
        var verifiedCount = verified.Components.OfType<SchComponent>().Count();
        if (verifiedCount != CommonSymbolNames.Count)
            throw new InvalidDataException($"Cleaned catalog verification failed: expected {CommonSymbolNames.Count}, reopened {verifiedCount}.");
        return verifiedCount;
    }

    /// <summary>
    /// Recreates the curated 71-symbol fallback catalog from a directory of native Altium SchLib
    /// source files. The source files are only read; their original artwork is copied unchanged.
    /// </summary>
    public static async Task<int> WriteCommonCatalogFromDirectoryAsync(string sourceDirectory, string destinationPath)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Altium symbol directory was not found: {sourceDirectory}");

        var files = Directory.EnumerateFiles(sourceDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".SchLib", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("The selected directory does not contain any Altium .SchLib files.");

        // Do not reuse a source library container: its cached component table of contents can
        // retain source-only section keys and make an otherwise valid curated save reopen empty.
        var catalog = (SchLibrary)AltiumLibrary.CreateSchLib();
        foreach (var path in files)
        {
            var source = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path);
            foreach (var symbol in source.Components.OfType<SchComponent>())
            {
                if (IsSourceCommonSymbol(symbol)) AddSafeSourceSymbol(catalog, symbol);
            }
            if (catalog.Components.OfType<SchComponent>().Count() == CommonSymbolNames.Count) break;
        }

        var retained = catalog.Components.OfType<SchComponent>().Count();
        if (retained != CommonSymbolNames.Count)
        {
            var found = catalog.Components.OfType<SchComponent>().Select(component => component.Name)
                .ToHashSet(StringComparer.Ordinal);
            var missing = CommonSymbolNames.Where(name => !found.Contains(name));
            throw new InvalidDataException($"Source catalog supplied {retained} of {CommonSymbolNames.Count} agreed symbols. Missing: {string.Join(", ", missing)}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await catalog.SaveAsync(destinationPath);
        var verified = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(destinationPath);
        var reopenedCount = verified.Components.OfType<SchComponent>().Count();
        if (reopenedCount != CommonSymbolNames.Count)
            throw new InvalidDataException($"The new fallback catalog reopened with {reopenedCount}, not {CommonSymbolNames.Count}, symbols. Diagnostics: {string.Join(" | ", verified.Diagnostics.Select(item => item.Message))}");
        return retained;
    }

    private static IEnumerable<string> SearchKeys(EdaComponent component)
    {
        yield return Normalize(component.Name);
        // Package names identify geometry, not component identity.
        foreach (var key in new[] { "Manufacturer Part", "name" })
            if (component.Properties.TryGetValue(key, out var value)) yield return Normalize(value);
    }

    private static string Normalize(string? value) => string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static IEnumerable<string> SymbolFiles(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return [];
        return source.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(root =>
            {
                if (File.Exists(root)) return new[] { root };
                if (!Directory.Exists(root)) return [];
                return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(path => string.Equals(Path.GetExtension(path), ".SchLib", StringComparison.OrdinalIgnoreCase));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private SymbolLibraryMatch? FindExactNativeMatch(EdaComponent component, IReadOnlyList<string> files)
    {
        var keys = SearchKeys(component).Where(key => key.Length >= 3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0) return null;

        // First check library filenames (fast even for large source collections).
        foreach (var path in files.Where(path => !IsMasterLibrary(path)))
            if (keys.Contains(Normalize(Path.GetFileNameWithoutExtension(path))))
            {
                var library = LoadLibrary(path);
                var candidates = library.Components.OfType<SchComponent>().ToArray();
                var identified = candidates.Where(item => keys.Contains(Normalize(item.Name)) ||
                    keys.Contains(Normalize(item.DesignItemId))).ToArray();
                // A filename may identify a single-record library, never an arbitrary first record.
                var candidate = identified.Length == 1 ? identified[0] : identified.Length == 0 && candidates.Length == 1 ? candidates[0] : null;
                if (candidate is not null && IsSafeAutomaticMatch(component, candidate))
                    return new SymbolLibraryMatch(path, "native source exact match", candidate.Name);
            }

        // A small user-supplied collection commonly groups many exact parts in one SchLib
        // (for example Desktop\\Library\\Inductance). Index those records by name. Do not open
        // a massive vendor collection during an import; its curated master handles safe family
        // fallbacks instead.
        var sourceFiles = files.Where(path => !IsMasterLibrary(path)).Take(201).ToArray();
        if (sourceFiles.Length > 200) return null;
        foreach (var path in sourceFiles)
        {
            var identified = LoadLibrary(path).Components.OfType<SchComponent>().Where(item =>
                keys.Contains(Normalize(item.Name)) || keys.Contains(Normalize(item.DesignItemId))).ToArray();
            if (identified.Length == 1 && IsSafeAutomaticMatch(component, identified[0]))
                return new SymbolLibraryMatch(path, "native source exact match", identified[0].Name);
        }
        return null;
    }

    private SymbolLibraryMatch? ResolveInMaster(EdaComponent component, string masterPath)
    {
        var library = LoadLibrary(masterPath);
        var commonSymbols = library.Components.OfType<SchComponent>().Where(IsCommonBundledSymbol).ToArray();
        var keys = SearchKeys(component).Where(key => key.Length >= 5).ToArray();
        foreach (var key in keys)
        {
            var exact = commonSymbols.FirstOrDefault(symbol =>
                Normalize(symbol.DesignItemId).Equals(key, StringComparison.OrdinalIgnoreCase) ||
                Normalize(symbol.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (exact is not null && IsSafeAutomaticMatch(component, exact, requireKnownFunctions: DiodeKind(component) is not null)) return new SymbolLibraryMatch(masterPath, "bundled exact match", exact.Name);
        }

        var target = GenericFamily(component);
        var functional = target is null ? null : commonSymbols.FirstOrDefault(symbol =>
        {
            var desiredName = target == "FUSE" ? "UNMOUNTED_FUSE" : target;
            return Normalize(symbol.Name) == Normalize(desiredName) &&
                IsSafeAutomaticMatch(component, symbol, requireKnownFunctions: target == "DIODE");
        });
        if (functional is not null) return new SymbolLibraryMatch(masterPath, $"bundled {target!.ToLowerInvariant()} symbol", functional.Name);

        // For other non-IC families, only auto-select when category and pin count
        // identify one unambiguous user symbol.  Ambiguous cases go to the picker.
        var family = LibraryFamily(component);
        if (family is null || component.SymbolPins.Count == 0) return null;
        var matches = commonSymbols.Where(symbol =>
        {
            var text = $"{symbol.Name} {symbol.DesignItemId} {symbol.Description}".ToUpperInvariant();
            return text.Contains(family, StringComparison.OrdinalIgnoreCase) && symbol.Pins.Count == component.SymbolPins.Count &&
                IsSafeAutomaticMatch(component, symbol, requireKnownFunctions: true);
        }).ToArray();
        return matches.Length == 1 ? new SymbolLibraryMatch(masterPath, $"bundled {family.ToLowerInvariant()} match", matches[0].Name) : null;
    }

    public bool MustGenerateFromEasyEda(EdaComponent component)
    {
        if (EasyEdaSymbolPreference.Reason(component) is not null) return true;
        // A generic bundled drawing is only a sensible default for simple parts.  Larger
        // devices have too many possible pin functions and used to cause a picker prompt for
        // every item in a batch.  Preserve their actual EasyEDA pin definition automatically.
        if (component.SymbolPins.Count > CommonSymbolMaximumPins) return true;

        var tagText = string.Join(' ', component.Tags).ToUpperInvariant();
        // These families have package-specific signal sets; do not substitute a
        // superficially similar catalog entry for their EasyEDA pin definition.
        return tagText.Contains("MICROCONTROLLER") || tagText.Contains("MCU") || tagText.Contains("MPU") ||
               tagText.Contains("SOC") || tagText.Contains("ARM CORTEX") || tagText.Contains("LOGIC") ||
               tagText.Contains("MEMORY") || tagText.Contains("OPAMP") || tagText.Contains("INTERFACE") ||
               tagText.Contains("ADC") || tagText.Contains("DAC") || tagText.Contains("LEVEL TRANSLATOR") ||
               tagText.Contains("DIGITAL ISOLATOR") || tagText.Contains("POWER -");
    }

    /// <summary>Checks numbered terminals and known functions without altering native artwork.</summary>
    public static SymbolPinCompatibility CheckPinCompatibility(EdaComponent component, SchComponent symbol,
        bool requireKnownFunctions = false)
    {
        var pins = component.SymbolPins.Select(pin => (Number: pin.Number.Trim(), pin.Name)).ToArray();
        if (pins.Length == 0)
            pins = component.Pads.Where(pad => !string.IsNullOrWhiteSpace(pad.Number))
                .Select(pad => pad.Number.Trim()).Distinct(StringComparer.Ordinal)
                .Select(number => (Number: number, Name: "")).ToArray();
        var selected = symbol.Pins.Select(pin => (Number: (pin.Designator ?? "").Trim(), Name: pin.Name ?? "")).ToArray();
        if (pins.Length == 0 || selected.Length == 0)
            return new(false, "No source or selected-symbol terminals available to verify.");
        if (pins.Any(p => p.Number.Length == 0) || selected.Any(p => p.Number.Length == 0) ||
            !pins.Select(p => p.Number).Order(StringComparer.Ordinal).SequenceEqual(selected.Select(p => p.Number).Order(StringComparer.Ordinal)))
            return new(false, "Source and selected symbol have different numbered terminals.");
        foreach (var group in pins.GroupBy(p => p.Number))
        {
            if (NativeCommonSymbolPolicy.Resolve(component) is { } common)
            {
                var expectedRole = common.Names[Array.IndexOf(common.Numbers, group.Key)];
                if (selected.Where(p => p.Number == group.Key).Any(p => NativeCommonSymbolPolicy.Role(p.Name) != expectedRole))
                    return new(false, $"Pin {group.Key}: selected symbol does not match native common role {expectedRole}.");
                continue;
            }
            var network = DiscreteNetworkSymbolPolicy.Resolve(component);
            if (network is not null)
            {
                var expectedRole = network.Roles[int.Parse(group.Key) - 1];
                if (selected.Where(p => p.Number == group.Key).Any(p => !p.Name.Equals(expectedRole, StringComparison.OrdinalIgnoreCase)))
                    return new(false, $"Pin {group.Key}: selected symbol does not match the reviewed network role {expectedRole}.");
                continue;
            }
            var diode = DiodeKind(component) is not null;
            var transistor = TransistorSymbolPolicy.Resolve(component);
            var expected = group.Select(p => diode ? DiodeFunction(p.Name) : transistor is not null
                ? PinFunction(TransistorSymbolPolicy.SourceFunction(component, p.Number, p.Name) ?? p.Name) : PinFunction(p.Name)).ToArray();
            var actual = selected.Where(p => p.Number == group.Key).Select(p => diode ? DiodeFunction(p.Name) : PinFunction(p.Name)).ToArray();
            if (requireKnownFunctions && (expected.Any(r => r is null) || actual.Any(r => r is null)))
                return new(false, $"Pin {group.Key}: function mapping is not proven for a generic symbol.");
            if (expected.All(r => r is not null) && actual.All(r => r is not null) &&
                !expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)))
                return new(false, $"Pin {group.Key}: source and selected symbol have conflicting functions.");
        }
        return new(true, requireKnownFunctions ? "Numbered terminals and recognised pin functions match." :
            "Numbered terminals match; no recognised pin-function conflict. Not a datasheet certification.");
    }

    private static string? PinFunction(string name) => name.Trim().ToUpperInvariant() switch
    {
        "A" or "ANODE" => "ANODE", "K" or "CATHODE" => "CATHODE",
        "B" or "BASE" => "BASE", "C" or "COLLECTOR" => "COLLECTOR", "E" or "EMITTER" => "EMITTER",
        "G" or "GATE" => "GATE", "D" or "DRAIN" => "DRAIN", "S" or "SOURCE" => "SOURCE",
        "+" or "POSITIVE" => "POSITIVE", "-" or "NEGATIVE" => "NEGATIVE",
        "GND" or "GROUND" => "GROUND", _ => null
    };

    private static bool IsSafeAutomaticMatch(EdaComponent component, SchComponent symbol, bool requireKnownFunctions = false)
    {
        var check = CheckPinCompatibility(component, symbol, requireKnownFunctions);
        if (!check.Compatible) ImportDiagnostics.Record("symbol.candidate_rejected",
            new { part = component.LcscPartNumber, candidate = symbol.Name, check.Reason });
        return check.Compatible;
    }

    private static string? GenericFamily(EdaComponent component)
    {
        var evidence = string.Join(' ', component.Tags.Append(component.Name).Append(component.Description)
            .Concat(component.Properties.Values)).ToUpperInvariant();
        var prefix = component.Properties.TryGetValue("pre", out var value)
            ? value.Trim().TrimEnd('?').ToUpperInvariant()
            : string.Empty;
        // Never flatten a specialised passive into an ordinary R/C/L symbol by designator alone.
        if (IsFerriteBead(component))
            return "FERRITE CHIP";
        if (new[] { "POLARI", "ELECTROLYT", "TANTALUM", "VARIABLE", "TRIMMER", "POTENTIOMETER",
            "THERMISTOR", "PHOTORESIST", "ARRAY", "NETWORK", "COUPLED", "TRANSFORMER", "BEAD", "COMMON MODE", "COMMON-MODE" }
            .Any(term => evidence.Contains(term, StringComparison.Ordinal))) return null;
        // Common EasyEDA passives can have sparse title/description data. Their designator is
        // useful when no specialised-family evidence conflicts; numbered terminals are checked separately.
        return prefix == "R" ? "RESISTOR"
            : prefix == "C" ? "CAPACITOR"
            : evidence.Contains("RESIST") ? "RESISTOR"
            : evidence.Contains("CAPACIT") ? "CAPACITOR"
            : evidence.Contains("INDUCT") ? "INDUCTOR"
            : evidence.Contains("FUSE") ? "FUSE"
            : evidence.Contains("DIODE") && !evidence.Contains("ZENER") && !evidence.Contains("TVS") &&
              !evidence.Contains("PHOTO") && !evidence.Contains("LED") && !evidence.Contains("ARRAY") ? "DIODE"
            : null;
    }

    private static string? LibraryFamily(EdaComponent component)
    {
        var evidence = string.Join(' ', component.Tags.Append(component.Name).Append(component.Description)
            .Concat(component.Properties.Values)).ToUpperInvariant();
        return evidence.Contains("CRYSTAL") || evidence.Contains("OSCILLATOR") ? "CRYSTAL"
            : evidence.Contains("CONNECTOR") ? "CONNECTOR"
            : evidence.Contains("SENSOR") ? "SENSOR"
            : evidence.Contains("TRANSISTOR") && evidence.Contains("NPN") ? "NPN"
            : evidence.Contains("TRANSISTOR") && evidence.Contains("PNP") ? "PNP"
            : evidence.Contains("TRANSISTOR") ? "BJT"
            : evidence.Contains("LED") ? "LED"
            : evidence.Contains("ZENER") ? "ZENER"
            : evidence.Contains("TVS") ? "TVS"
            : evidence.Contains("DIODE") && evidence.Contains("ARRAY") ? "DIODE ARRAY"
            : null;
    }

    public static bool IsFerriteBead(EdaComponent component)
    {
        var evidence = string.Join(' ', component.Tags.Append(component.Description)
            .Concat(component.Properties.Where(p => p.Key is "Category" or "Component Type" or "Technology").Select(p => p.Value)));
        return evidence.Contains("ferrite", StringComparison.OrdinalIgnoreCase) &&
            (evidence.Contains("bead", StringComparison.OrdinalIgnoreCase) || evidence.Contains("ferrite chip", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMasterLibrary(string path) => string.Equals(Path.GetFileName(path), "BundledUserSymbols.SchLib", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommonBundledSymbol(SchComponent symbol) =>
        symbol.Pins.Count <= CommonSymbolMaximumPins && CommonSymbolNames.Contains(symbol.Name);

    private static bool IsSourceCommonSymbol(SchComponent symbol) =>
        symbol.Pins.Count <= CommonSymbolMaximumPins && SourceCommonSymbolNames.Contains(symbol.Name);

    private static void AddSafeSourceSymbol(SchLibrary catalog, SchComponent symbol)
    {
        var safeName = AltiumSchExporter.SafeLibraryName(symbol.Name);
        if (catalog.Contains(safeName)) return;
        symbol.Name = safeName;
        symbol.LibReference = safeName;
        catalog.Add(symbol);
    }

    private SchLibrary LoadLibrary(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return LibraryCache.GetOrAdd(fullPath, value => new Lazy<SchLibrary>(() =>
            (SchLibrary)AltiumLibrary.OpenSchLibAsync(value).GetAwaiter().GetResult())).Value;
    }
}

public sealed record SymbolLibraryMatch(string Path, string MatchKind, string? ComponentName = null, DiodePinBinding? DiodePins = null,
    TransistorPinBinding? TransistorPins = null, DiscreteNetworkBinding? Network = null, NativeCommonPinBinding? CommonPins = null);
public sealed record DiodePinBinding(string TemplateAnode, string TemplateCathode, string SourceAnode, string SourceCathode);
public sealed record SymbolPinCompatibility(bool Compatible, string Reason);
