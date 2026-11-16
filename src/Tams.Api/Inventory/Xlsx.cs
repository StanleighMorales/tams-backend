using ClosedXML.Excel;
using Tams.Api.Students;

namespace Tams.Api.Inventory;

/// <summary>Small .xlsx helpers over ClosedXML for the checklist and audit report (FR-7.6, FR-7.7).</summary>
public static class Xlsx
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>An .xlsx file is a zip, so it starts with "PK".</summary>
    public static bool Looks(byte[] bytes) => bytes.Length > 3 && bytes[0] == 'P' && bytes[1] == 'K';

    /// <summary>Text cells only; a leading quote on =, +, - and @ keeps a spreadsheet from running a cell as a formula.</summary>
    public static void Put(IXLWorksheet sheet, int row, int col, object? value)
    {
        var text = value switch { null => "", DateTimeOffset t => t.ToString("O"), _ => value.ToString() ?? "" };
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@') text = "'" + text;
        sheet.Cell(row, col).SetValue(text);
    }

    /// <summary>Writes rows from <paramref name="row"/> down and returns the next free row; the first row is bold when <paramref name="header"/>.</summary>
    public static int PutRows(IXLWorksheet sheet, int row, IEnumerable<IReadOnlyList<object?>> rows, bool header)
    {
        foreach (var r in rows)
        {
            for (var c = 0; c < r.Count; c++) Put(sheet, row, c + 1, r[c]);
            if (header) { sheet.Row(row).Style.Font.Bold = true; header = false; }
            row++;
        }
        return row;
    }

    public static byte[] ToBytes(XLWorkbook wb)
    {
        foreach (var s in wb.Worksheets) s.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Reads the first worksheet as records shaped like the CSV reader's, so import logic is shared.</summary>
    public static List<CsvReader.Record> Read(byte[] bytes)
    {
        try
        {
            using var wb = new XLWorkbook(new MemoryStream(bytes));
            var sheet = wb.Worksheets.First();
            var records = new List<CsvReader.Record>();
            var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
            var cols = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            for (var r = 1; r <= last; r++)
            {
                var fields = Enumerable.Range(1, cols).Select(c => sheet.Cell(r, c).GetFormattedString()).ToList();
                if (fields.Any(f => f.Length > 0)) records.Add(new CsvReader.Record(r, fields));
            }
            return records;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FormatException("The file is not a readable .xlsx workbook.", ex);
        }
    }
}
