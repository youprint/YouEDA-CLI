using YouEDA.CLI.Options;

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

Console.Error.WriteLine(
    "Engine porting has not happened yet (Phase 1 in ROADMAP.md); this is a scaffold build only.");
return 1;

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
