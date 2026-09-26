using YouEDA.Engine.Services;

namespace YouEDA.Engine.Tests;

public class CatalogFilterTests
{
    private const string SampleCatalog =
        "LCSC Part Number,Manufacturer,Package,Category,Library Type,Description\n" +
        "C1002,YAGEO,0402,Resistor,Basic Part,Chip Resistor 10k 1%\n" +
        "C1015,YAGEO,0603,Resistor,Basic Part,Chip Resistor 1k 1%\n" +
        "C1017,UNI-ROYAL,0402,Resistor,Extended Part,Chip Resistor 100R 5%\n" +
        "C109227,Everlight,SMD-4,Optocoupler,Preferred Part,Optoisolator LTV-817\n" +
        "C25792,YAGEO,0603,Capacitor,Basic Part,10uF X7R Ceramic Capacitor\n";

    [Fact]
    public void FiltersByCategoryOnly()
    {
        var parts = CatalogFilter.Filter(WriteTemp(SampleCatalog), new CatalogFilterCriteria { Category = "Resistor" });
        Assert.Equal(["C1002", "C1015", "C1017"], parts);
    }

    [Fact]
    public void CombinesCategoryAndManufacturerAsAnd()
    {
        var parts = CatalogFilter.Filter(WriteTemp(SampleCatalog),
            new CatalogFilterCriteria { Category = "Resistor", Manufacturer = "YAGEO" });
        Assert.Equal(["C1002", "C1015"], parts);
    }

    [Fact]
    public void FiltersByPartClass()
    {
        var parts = CatalogFilter.Filter(WriteTemp(SampleCatalog),
            new CatalogFilterCriteria { Category = "Resistor", PartClass = "extended" });
        Assert.Equal(["C1017"], parts);
    }

    [Fact]
    public void FiltersByPackage()
    {
        var parts = CatalogFilter.Filter(WriteTemp(SampleCatalog),
            new CatalogFilterCriteria { Category = "Resistor", Package = "0402" });
        Assert.Equal(["C1002", "C1017"], parts);
    }

    [Fact]
    public void CapacitanceFilterNormalizesMicroSymbol()
    {
        // The catalog cell is plain ASCII "10uF"; this proves the matcher also accepts the
        // µ/μ spelling a real LCSC export might use, per CatalogFilter's normalization.
        var parts = CatalogFilter.Filter(WriteTemp(SampleCatalog),
            new CatalogFilterCriteria { Category = "Capacitor", Capacitance = "10µF" });
        Assert.Equal(["C25792"], parts);
    }

    [Fact]
    public void ContainsAndExcludeAreAndedTogether()
    {
        var parts = CatalogFilter.Filter(WriteTemp(SampleCatalog), new CatalogFilterCriteria
        {
            Category = "Resistor",
            Contains = ["1%"],
            Exclude = ["10k"],
        });
        Assert.Equal(["C1015"], parts);
    }

    [Fact]
    public void NoMatchesReturnsEmpty()
    {
        var parts = CatalogFilter.Filter(WriteTemp(SampleCatalog), new CatalogFilterCriteria { Category = "Inductor" });
        Assert.Empty(parts);
    }

    private static string WriteTemp(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }
}
