namespace YouEDA.Engine.Models;

/// <summary>
/// Placeholder for the intermediate component model that Phase 1 will fork from
/// YouEDA's <c>Services/Parser.cs</c> / <c>Models/EdaComponent.cs</c>. No conversion
/// fields are populated yet; this exists only so <c>engine/</c> has a public surface
/// for <c>cli/</c> to build against.
/// </summary>
public sealed class EdaComponent
{
    public required string LcscPartNumber { get; init; }
}
