using YouEDA.CLI.Options;
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

if (parsed.InputPath is null)
{
    Console.Error.WriteLine(
        "--category/--catalog filtered import is not implemented yet (see ROADMAP.md); use --input <bom.csv>.");
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
    InputPath = parsed.InputPath,
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

try
{
    var result = await ImportJobRunner.RunAsync(options, Console.WriteLine, cts.Token);
    Console.WriteLine(
        $"Done: {result.Succeeded} succeeded, {result.Failed} failed, {result.SkippedAlreadyDone} already checkpointed, {result.Total} total.");
    return result.Failed == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
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
          --contains <text>         Repeatable AND filter
          --exclude <text>          Repeatable AND filter
        """);
}
