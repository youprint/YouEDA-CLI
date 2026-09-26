using YouEDA.Engine.Models;
using YouEDA.Engine.Services;

namespace YouEDA.Engine.Tests;

/// <summary>
/// End-to-end proof that the ported bundled-catalog resolution actually finds a real symbol in
/// the shipped BundledUserSymbols.SchLib asset, not just that the policy layer computes the
/// right binding (see SymbolPolicyTests for that). Offline: only reads the local asset file.
/// </summary>
public class UserSymbolLibraryResolverTests
{
    private static string BundledCatalogPath => Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");

    private static EdaSymbolPin Pin(string number, string name) => new(number, name, ElectricalType: 0, RotationDeg: 0, NameAnchor: "");

    [Fact]
    public void BundledCatalogAssetIsPresentInTestOutput()
    {
        Assert.True(File.Exists(BundledCatalogPath), $"Expected the bundled catalog at {BundledCatalogPath}.");
    }

    [Fact]
    public void ResolvesC16133AgainstTheRealBundledCatalog()
    {
        var component = new EdaComponent { LcscPartNumber = "C16133", Description = "10uF tantalum capacitor" };
        component.Properties["Manufacturer Part"] = "TAJB107K006RNJ";
        component.SymbolPins.Add(Pin("1", "1"));
        component.SymbolPins.Add(Pin("2", "2"));

        var resolver = new UserSymbolLibraryResolver();
        var match = resolver.Resolve(component, BundledCatalogPath, null);

        Assert.NotNull(match);
        Assert.Equal("Polarised_Capacitor", match!.ComponentName);

        var symbol = resolver.LoadSelectedComponent(match);
        Assert.Equal(2, symbol.Pins.Count);
    }
}
