using Colo.Core;
using Xunit;

namespace Colo.Tests;

public class OfficeTextTests
{
    [Fact]
    public void Columns_and_table()
    {
        Assert.Equal("A", OfficeText.Column(1));
        Assert.Equal("Z", OfficeText.Column(26));
        Assert.Equal("AA", OfficeText.Column(27));
        Assert.Equal("AZ", OfficeText.Column(52));
        // Como Range.Value: base 1.
        var values = (object?[,])Array.CreateInstance(typeof(object), [2, 3], [1, 1]);
        values[1, 1] = "Site"; values[1, 2] = "Valor"; values[1, 3] = "Fecha";
        values[2, 1] = "SP-01"; values[2, 2] = 1234.5; values[2, 3] = new DateTime(2026, 10, 31);
        var t = OfficeText.Table(values, 4, 2, totalRows: 500);
        Assert.Contains("\tB\tC\tD", t);
        Assert.Contains("5\tSP-01\t1234.5\t31/10/2026", t);
        Assert.Contains("500 filas", t);
        var err = new object?[,] { { -2146826281 } };
        Assert.Contains("#DIV/0!", OfficeText.Table(err, 1, 1));
    }

    [Fact]
    public void Values_to_write()
    {
        Assert.Equal("=SUM(A1:A3)", OfficeText.Value("=SUM(A1:A3)"));
        Assert.Equal(12.5, OfficeText.Value("12.5"));
        Assert.Equal(12.5, OfficeText.Value("12,5"));
        Assert.Equal(1234.56, OfficeText.Value("1.234,56"));
        Assert.Equal(1000.0, OfficeText.Value("1000"));
        Assert.Equal("Pago pendiente", OfficeText.Value("Pago pendiente"));
        Assert.Equal("SP-0123", OfficeText.Value("SP-0123"));
    }

    [Fact]
    public void Outline_levels_and_matching()
    {
        var o = OfficeText.Outline(["Introducción", "  Alcance", "  Riscos e mitigação", "Cronograma", "", "    Fase 1"]);
        Assert.Equal(5, o.Count);
        Assert.Equal((2, 2, "Alcance"), o[1]);
        Assert.Equal(3, o[4].Level);
        Assert.Equal(3, OfficeText.Match(o, "riscos"));
        Assert.Equal(3, OfficeText.Match(o, "la parte de los riscos y mitigación"));
        Assert.Equal(4, OfficeText.Match(o, "4"));
        Assert.Equal(4, OfficeText.Match(o, "cronograma"));
        Assert.Equal(0, OfficeText.Match(o, "presupuesto"));
        Assert.Equal("a\rb\rc", OfficeText.WordParagraphs("a\r\nb\nc"));
    }
}
