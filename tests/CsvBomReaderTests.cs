using YouEDA.Engine.Services;

namespace YouEDA.Engine.Tests;

public class CsvBomReaderTests
{
    [Fact]
    public void ReadsOnePartPerLine()
    {
        var path = WriteTemp("LCSC Part\nC11702\nC8678\nC436585\n");
        var parts = CsvBomReader.ReadPartNumbers(path);
        Assert.Equal(["C11702", "C8678", "C436585"], parts);
    }

    [Fact]
    public void AcceptsCommaSeparatedColumnsAndIgnoresExtraFields()
    {
        var path = WriteTemp(
            "LCSC Part,Quantity,Value,Description,JLCPCB Category,Notes\n" +
            "C11702,100,1kΩ,0402 resistor,Basic,\n" +
            "C8678,10,SS34,Schottky diode,Basic,\n");
        var parts = CsvBomReader.ReadPartNumbers(path);
        Assert.Equal(["C11702", "C8678"], parts);
    }

    [Fact]
    public void RemovesDuplicatesCaseInsensitively()
    {
        var path = WriteTemp("C11702\nc11702\nC11702\nC8678\n");
        var parts = CsvBomReader.ReadPartNumbers(path);
        Assert.Equal(["C11702", "C8678"], parts);
    }

    [Fact]
    public void RejectsQuotedPartCodes()
    {
        // Per YouEDA's AGENTS.md CSV contract: "C11702" (quoted) is not accepted until the
        // parser is deliberately upgraded to handle CSV quoting.
        var path = WriteTemp("\"C11702\"\nC8678\n");
        var parts = CsvBomReader.ReadPartNumbers(path);
        Assert.Equal(["C8678"], parts);
    }

    [Fact]
    public void SplitsOnAllDocumentedSeparators()
    {
        var path = WriteTemp("C1;C2,C3\tC4 C5\nC6");
        var parts = CsvBomReader.ReadPartNumbers(path);
        Assert.Equal(["C1", "C2", "C3", "C4", "C5", "C6"], parts);
    }

    private static string WriteTemp(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }
}
