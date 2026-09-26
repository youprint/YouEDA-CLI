namespace YouEDA.Engine.Services;

/// <summary>
/// Placeholder options record mirroring the CLI's documented flags. Phase 1 will replace
/// this with real BOM/catalog import wiring against the forked Parser/LcscScraper/
/// BulkLibraryWriter/AltiumSchExporter/AltiumV2Exporter services.
/// </summary>
public sealed record ImportJobOptions
{
    public string? InputPath { get; init; }
    public string? Category { get; init; }
    public string? CatalogPath { get; init; }
    public required string OutputPath { get; init; }
    public int Workers { get; init; } = 1;
    public double RequestsPerSecond { get; init; } = 1.0;
    public bool Resume { get; init; }
    public bool With3D { get; init; }
    public int CheckpointInterval { get; init; } = 10;
}

/// <summary>
/// Placeholder entry point for the eventual import pipeline. Not implemented in this pass.
/// </summary>
public static class ImportJobRunner
{
    public static Task RunAsync(ImportJobOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "Engine porting is Phase 1 (see MIGRATION_PLAN.md); no conversion logic exists yet.");
}
