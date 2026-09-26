using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YouEDA.Engine.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Altium.Models.Sch;

namespace YouEDA.Engine.Services;

/// <summary>
/// Owns both native libraries for a bulk run.  This is deliberately single-writer: Altium
/// compound-library files are mutable binary containers and cannot be safely written by
/// concurrent workers. Fetching/parsing remains parallel; only the short library mutation path
/// is serialised and checkpointed.
/// </summary>
public sealed class BulkLibraryWriter
{
    private readonly string _outputDirectory;
    private readonly bool _include3d;
    private readonly string? _symbolLibrary;
    private readonly UserSymbolLibraryResolver _resolver = new();
    private readonly AltiumV2Exporter _pcbExporter = new();
    private readonly AltiumSchExporter _schExporter = new();
    private readonly EasyEda3dModelDownloader _modelDownloader = new();
    private PcbLibrary? _pcb;
    private SchLibrary? _sch;

    public BulkLibraryWriter(string outputDirectory, string? symbolLibrary, bool include3d)
    {
        _outputDirectory = outputDirectory;
        _symbolLibrary = symbolLibrary;
        _include3d = include3d;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDirectory);
        _pcb = await _pcbExporter.OpenSharedPcbLibAsync(_outputDirectory);
        _sch = await _schExporter.OpenSharedSchLibAsync(_outputDirectory);
    }

    public async Task AddAsync(EdaComponent component, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        Downloaded3dModel? model = null;
        if (_include3d)
        {
            try
            {
                var modelDirectory = Path.Combine(_outputDirectory, "models");
                Directory.CreateDirectory(modelDirectory);
                model = await _modelDownloader.TryDownloadAsync(component.ThreeDModel, modelDirectory, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidDataException)
            {
                // A missing model must not reject otherwise valid CAD data in a catalog run.
            }
        }

        _pcbExporter.Upsert(_pcb!, component, model);
        var match = _resolver.Resolve(component, _symbolLibrary, null);
        SchComponent symbol;
        if (match is null)
        {
            if (component.SymbolPins.Count == 0)
                throw new InvalidDataException("No bundled symbol matched and EasyEDA supplied no schematic pins.");
            symbol = AltiumSchExporter.CreateEasyEdaSymbol(component);
        }
        else
        {
            symbol = _resolver.LoadSelectedComponent(match);
            symbol.Comment = component.Name;
        }
        AltiumSchExporter.PrepareSymbol(symbol, component);
        // Update existing catalogs written by older releases without retaining their LCSC row.
        _sch!.Remove(component.LcscPartNumber);
        _sch.Remove(component.Name);
        AltiumSchExporter.MigrateUnsafeLibraryNames(_sch);
        _schExporter.Upsert(_sch!, symbol);
    }

    public async Task CheckpointAsync()
    {
        EnsureInitialized();
        var pcbPath = Path.Combine(_outputDirectory, "youeda.PcbLib");
        var schPath = Path.Combine(_outputDirectory, "youeda.SchLib");
        await _pcb!.SaveAsync(pcbPath);
        await _sch!.SaveAsync(schPath);

        // A completion entry is only appended after this succeeds.  Reopening the two compound
        // files catches a malformed checkpoint before it is treated as resumable progress.
        _ = await AltiumLibrary.OpenPcbLibAsync(pcbPath);
        _ = await AltiumLibrary.OpenSchLibAsync(schPath);
    }

    private void EnsureInitialized()
    {
        if (_pcb is null || _sch is null)
            throw new InvalidOperationException("The bulk library writer was not initialized.");
    }
}
