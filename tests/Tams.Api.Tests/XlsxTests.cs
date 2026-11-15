using ClosedXML.Excel;
using Tams.Api.Inventory;

namespace Tams.Api.Tests;

public class XlsxTests
{
    [Fact]
    public void Rows_round_trip_and_formula_looking_text_stays_text()
    {
        using var wb = new XLWorkbook();
        Xlsx.PutRows(wb.AddWorksheet("S"), 1, [["asset_code", "found"], ["=SUM(A1)", "Y"], ["LT-1", ""]], header: true);
        var bytes = Xlsx.ToBytes(wb);

        Assert.True(Xlsx.Looks(bytes));
        var rows = Xlsx.Read(bytes);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["asset_code", "found"], rows[0].Fields);
        Assert.Equal("=SUM(A1)", rows[1].Fields[0]); // stored as text with a quote prefix, never a formula
        Assert.Equal("LT-1", rows[2].Fields[0]);
    }

    [Fact]
    public void Garbage_is_a_format_error_and_csv_is_not_xlsx()
    {
        Assert.False(Xlsx.Looks("asset_code,found\n"u8.ToArray()));
        Assert.Throws<FormatException>(() => Xlsx.Read("PK not really"u8.ToArray()));
    }
}
