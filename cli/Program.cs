using YouEDA.CLI.Options;
using YouEDA.CLI.Terminal;
using YouEDA.Engine.Services;

if (args is ["--help"] or [])
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

CliArguments parsed;
try
{
    parsed = CliArguments.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    PrintUsage();
    return 2;
}

if (parsed.OutputPath is null)
{
    Console.Error.WriteLine("--output is required.");
    return 2;
}

IReadOnlyList<string> partNumbers;
if (parsed.InputPath is not null)
{
    partNumbers = CsvBomReader.ReadPartNumbers(parsed.InputPath);
}
else if (parsed.Category is not null && parsed.CatalogPath is not null)
{
    if (!File.Exists(parsed.CatalogPath))
    {
        Console.Error.WriteLine($"Catalog file not found: {parsed.CatalogPath}");
        return 1;
    }

    var criteria = new CatalogFilterCriteria
    {
        Category = parsed.Category,
        Manufacturer = parsed.Manufacturer,
        Package = parsed.Package,
        PartClass = parsed.PartClass,
        Resistance = parsed.Resistance,
        Capacitance = parsed.Capacitance,
        Inductance = parsed.Inductance,
        Voltage = parsed.Voltage,
        Tolerance = parsed.Tolerance,
        Power = parsed.Power,
        Dielectric = parsed.Dielectric,
        Mounting = parsed.Mounting,
        Contains = parsed.Contains,
        Exclude = parsed.Exclude,
    };
    partNumbers = CatalogFilter.Filter(parsed.CatalogPath, criteria);
    Console.WriteLine($"Catalog filter matched {partNumbers.Count} part(s).");
}
else
{
    Console.Error.WriteLine("Provide either --input <bom.csv>, or --category <name> with --catalog <file>.");
    return 2;
}

if (partNumbers.Count == 0)
{
    Console.Error.WriteLine("No LCSC part numbers to import.");
    return 1;
}

if (parsed.RequestsPerSecond != 1.0)
{
    Console.Error.WriteLine(
        "Note: --rps is informational in this build; the fetch rate gate is fixed at 1 request/second.");
}

var symbolLibraryPath = Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");
if (!File.Exists(symbolLibraryPath))
{
    Console.Error.WriteLine($"Bundled symbol catalog not found at {symbolLibraryPath}.");
    return 1;
}

var options = new ImportJobOptions
{
    OutputPath = parsed.OutputPath,
    SymbolLibraryPath = symbolLibraryPath,
    Workers = Math.Max(1, parsed.Workers),
    Resume = parsed.Resume,
    With3D = parsed.With3D,
    CheckpointInterval = Math.Max(1, parsed.CheckpointInterval),
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("Cancelling; progress up to the last checkpoint is retained (use --resume to continue).");
    cts.Cancel();
};

// A redirected/piped stdout (e.g. > log.txt, or CI) can't usefully redraw an in-place bar,
// so fall back to the plain periodic text line ImportJobRunner already prints for that case.
var useBar = ProgressBar.IsSupported;
var bar = useBar ? new ProgressBar() : null;

void Log(string message)
{
    if (bar is null) { Console.WriteLine(message); return; }
    // Skip the noisy per-request/per-part lines while the bar owns the terminal line — the bar
    // already communicates progress; failures and milestones still print above it.
    if (message.StartsWith("[OK]") || message.StartsWith("GET ")) return;
    bar.Clear();
    Console.WriteLine(message);
}

try
{
    Action<ImportProgress>? onProgress = bar is null ? null : bar.Report;
    var result = await ImportJobRunner.RunAsync(
        partNumbers, options, Log, onProgress, cts.Token);
    bar?.Finish();
    Console.WriteLine(
        $"Done: {result.Succeeded} succeeded, {result.Failed} failed, {result.SkippedAlreadyDone} already checkpointed, {result.Total} total.");
    return result.Failed == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
    bar?.Finish();
    Console.Error.WriteLine("Import cancelled.");
    return 130;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage: YouEDA.CLI --output <path> [options]

          --input <file>            BOM/CSV of LCSC part numbers
          --category <name>         Catalog category (e.g. resistor, capacitor)
          --catalog <file>          LCSC catalog export (CSV/JSON) for --category imports
          --output <dir>            Output directory
          --workers <n>             Parallel fetch/convert workers
          --rps <n>                 Requests per second cap
          --resume                  Resume from an existing checkpoint
          --with-3d                 Download STEP 3D models
          --checkpoint <n>          Checkpoint every n processed parts
          --manufacturer <name>     Filter by manufacturer
          --package <name>          Filter by package
          --part-class <class>      basic | preferred | extended
          --resistance <value>      Filter by resistance (e.g. 10k)
          --capacitance <value>     Filter by capacitance (e.g. 10uF)
          --inductance <value>      Filter by inductance
          --voltage <value>         Filter by voltage rating
          --tolerance <value>       Filter by tolerance
          --power <value>           Filter by power rating
          --dielectric <value>      Filter by dielectric (e.g. X7R)
          --mounting <value>        Filter by mounting type
          --contains <text>         Repeatable AND filter
          --exclude <text>          Repeatable AND filter
        """);
}
