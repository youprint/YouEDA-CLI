namespace YouEDA.CLI.Options;

/// <summary>
/// Parsed form of the flags documented in the repo README (--input, --category, --catalog,
/// --output, --workers, --rps, --resume, --with-3d, --checkpoint) plus the catalog filter
/// flags (--manufacturer, --package, --part-class, --resistance, --capacitance, --inductance,
/// --voltage, --tolerance, --power, --dielectric, --mounting, --contains, --exclude).
/// Parsing only — Phase 1 wires this into the real engine pipeline.
/// </summary>
public sealed record CliArguments
{
    public string? InputPath { get; init; }
    public string? Category { get; init; }
    public string? CatalogPath { get; init; }
    public string? OutputPath { get; init; }
    public int Workers { get; init; } = 1;
    public double RequestsPerSecond { get; init; } = 1.0;
    public bool Resume { get; init; }
    public bool With3D { get; init; }
    public int CheckpointInterval { get; init; } = 10;

    public string? Manufacturer { get; init; }
    public string? Package { get; init; }
    public string? PartClass { get; init; }
    public IReadOnlyList<string> Contains { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];

    public static CliArguments Parse(string[] args)
    {
        var contains = new List<string>();
        var exclude = new List<string>();
        var result = new CliArguments();

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"Missing value for {args[i]}");

            switch (args[i])
            {
                case "--input": result = result with { InputPath = Next() }; break;
                case "--category": result = result with { Category = Next() }; break;
                case "--catalog": result = result with { CatalogPath = Next() }; break;
                case "--output": result = result with { OutputPath = Next() }; break;
                case "--workers": result = result with { Workers = int.Parse(Next()) }; break;
                case "--rps": result = result with { RequestsPerSecond = double.Parse(Next()) }; break;
                case "--resume": result = result with { Resume = true }; break;
                case "--with-3d": result = result with { With3D = true }; break;
                case "--checkpoint": result = result with { CheckpointInterval = int.Parse(Next()) }; break;
                case "--manufacturer": result = result with { Manufacturer = Next() }; break;
                case "--package": result = result with { Package = Next() }; break;
                case "--part-class": result = result with { PartClass = Next() }; break;
                case "--contains": contains.Add(Next()); break;
                case "--exclude": exclude.Add(Next()); break;
                // Additional catalog filters (--resistance, --capacitance, --inductance,
                // --voltage, --tolerance, --power, --dielectric, --mounting) are deferred
                // to Phase 1, where they attach to the real catalog filter pipeline.
                default:
                    throw new ArgumentException($"Unrecognized argument: {args[i]}");
            }
        }

        return result with { Contains = contains, Exclude = exclude };
    }
}
