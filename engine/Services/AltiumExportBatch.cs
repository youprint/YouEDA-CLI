using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YouEDA.Engine.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Altium.Models.Sch;

namespace YouEDA.Engine.Services;

/// <summary>Single-writer GUI export session. Reuses opened libraries between checkpoints.</summary>
public sealed class AltiumExportBatch
{
    private sealed class Destination(SchLibrary schematic)
    {
        public SchLibrary Schematic { get; } = schematic;
        public PcbLibrary? Footprints { get; set; }
        public bool Dirty { get; set; }
        public Dictionary<string, string> FootprintFingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private readonly Dictionary<string, Destination> _destinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly AltiumSchExporter _sch = new();
    private readonly AltiumV2Exporter _pcb = new();
    public const int CheckpointInterval = 10;

    // The caller awaits each mutation. Never invoke this on multiple workers.
    public async Task UpsertAsync(EdaComponent component, string directory, SchComponent? symbol,
        Downloaded3dModel? model, bool includeFootprint)
    {
        // Prepare/validate the schematic before modifying either library.
        if (symbol is null)
        {
            if (component.SymbolPins.Count == 0) throw new InvalidDataException("EasyEDA returned no schematic pins.");
            symbol = AltiumSchExporter.CreateEasyEdaSymbol(component);
        }
        else symbol.Comment = component.Name;
        AltiumSchExporter.PrepareSymbol(symbol, component, includeFootprint);
        directory = Path.GetFullPath(directory);
        if (!_destinations.TryGetValue(directory, out var destination))
        {
            destination = new(await _sch.OpenSharedSchLibAsync(directory));
            _destinations.Add(directory, destination);
        }
        if (includeFootprint)
        {
            destination.Footprints ??= await _pcb.OpenSharedPcbLibAsync(directory);
            var name = AltiumFootprintNaming.NameFor(component);
            var fingerprint = Fingerprint(component, model);
            if (destination.FootprintFingerprints.TryGetValue(name, out var prior) && prior == fingerprint &&
                destination.Footprints[name] is PcbComponent existing)
            {
                // Retain the original last-import-wins description, without rebuilding geometry.
                existing.Description = component.Name;
                if (!component.LcscPartNumber.Equals(name, StringComparison.OrdinalIgnoreCase)) destination.Footprints.Remove(component.LcscPartNumber);
                ImportDiagnostics.Record("footprint.reused", new { part = component.LcscPartNumber, name, directory });
            }
            else
            {
                _pcb.Upsert(destination.Footprints, component, model);
                destination.FootprintFingerprints[name] = fingerprint;
                ImportDiagnostics.Record("footprint.built", new { part = component.LcscPartNumber, name, directory });
            }
        }
        if (!component.LcscPartNumber.Equals(symbol.Name, StringComparison.OrdinalIgnoreCase))
            destination.Schematic.Remove(component.LcscPartNumber);
        if (!component.Name.Equals(symbol.Name, StringComparison.OrdinalIgnoreCase))
            destination.Schematic.Remove(component.Name);
        AltiumSchExporter.MigrateUnsafeLibraryNames(destination.Schematic);
        _sch.Upsert(destination.Schematic, symbol, includeFootprint);
        destination.Dirty = true;
    }

    private static string Fingerprint(EdaComponent component, Downloaded3dModel? model)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            component.Pads, component.Shapes,
            model = model is null ? null : new {
                model.Source.Uuid, model.Source.Xmm, model.Source.Ymm, model.Source.Zmm,
                model.Source.RotationXDeg, model.Source.RotationYDeg, model.Source.RotationZDeg,
                model.Source.WidthMm, bodyHeightMm = model.Source.HeightMm, model.FileName, model.ZOffsetMm, model.HeightMm,
                payloadHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(model.StepData))) }
        }, new JsonSerializerOptions { IncludeFields = true }); // ValueTuple geometry uses fields.
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public async Task CheckpointAsync()
    {
        foreach (var (directory, destination) in _destinations)
        {
            if (!destination.Dirty) continue;
            using var checkpoint = ImportDiagnostics.Measure("library.checkpoint", new { directory,
                symbols = destination.Schematic.Components.Count(), footprints = destination.Footprints?.Components.Count() ?? 0 });
            // Save and reopen a temporary sibling before replacing the previous checkpoint.
            // Each file replacement is atomic; the pair is not a filesystem transaction.
            if (destination.Footprints is { } footprints)
                await SaveVerifiedAsync(Path.Combine(directory, "youeda.PcbLib"),
                    path => footprints.SaveAsync(path), async path =>
                    {
                        var verified = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(path);
                        if (verified.Components.Count() != footprints.Components.Count() ||
                            footprints.Components.Any(component => !verified.Contains(component.Name)) ||
                            footprints.Models.Any(model => !verified.Models.Any(saved => saved.Name == model.Name)))
                            throw new InvalidDataException("PCB checkpoint failed component/model verification.");
                    });
            await SaveVerifiedAsync(Path.Combine(directory, "youeda.SchLib"),
                path => destination.Schematic.SaveAsync(path), async path =>
                {
                    var verified = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path);
                    if (verified.Components.Count() != destination.Schematic.Components.Count() ||
                        destination.Schematic.Components.OfType<SchComponent>().Any(symbol =>
                            verified[symbol.Name!] is not SchComponent saved || saved.Pins.Count != symbol.Pins.Count ||
                            !saved.Implementations.Select(i => (i.ModelType, i.ModelName)).SequenceEqual(
                                symbol.Implementations.Select(i => (i.ModelType, i.ModelName)))))
                        throw new InvalidDataException("Schematic checkpoint failed pin/model-link verification.");
                });
            destination.Dirty = false;
            ImportDiagnostics.Record("library.checkpoint_verified", new { directory });
        }
    }

    private static async Task SaveVerifiedAsync(string path, Func<string, ValueTask> save, Func<string, Task> verify)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await save(temporary);
            await verify(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
