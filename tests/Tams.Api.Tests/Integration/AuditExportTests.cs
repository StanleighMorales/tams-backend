using ClosedXML.Excel;
using Tams.Api.Inventory;

namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-125: xlsx next to csv for the checklist, the audit report and the import (FR-7.6, FR-7.7).</summary>
[Collection("api")]
public class AuditExportTests(ApiFixture api)
{
    [Fact]
    public async Task Checklist_exports_as_xlsx_and_a_filled_workbook_imports()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin);
        var cat = await Seed.Category(admin);
        var (_, found, _) = await Seed.BorrowableAsset(admin, cat, room);
        await Seed.BorrowableAsset(admin, cat, room);

        var export = await admin.GetAsync($"/api/rooms/{room}/checklist:export?format=xlsx").Expect(200);
        Assert.Equal(Xlsx.ContentType, export.Content.Headers.ContentType?.MediaType);
        var rows = Xlsx.Read(await export.Content.ReadAsByteArrayAsync());
        Assert.Equal(3, rows.Count); // header + 2 assets
        await admin.GetAsync($"/api/rooms/{room}/checklist:export?format=pdf").Expect(400);
        await admin.GetAsync($"/api/rooms/{room}/checklist:export").Expect(200); // csv stays the default

        // Mark one asset found in the exported workbook and send it back.
        var codeCol = rows[0].Fields.IndexOf("asset_code");
        var foundCol = rows[0].Fields.IndexOf("found") + 1;
        using var wb = new XLWorkbook(new MemoryStream(await export.Content.ReadAsByteArrayAsync()));
        var sheet = wb.Worksheet(1);
        foreach (var r in rows.Skip(1).Where(r => r.Fields[codeCol] == found)) sheet.Cell(r.Line, foundCol).SetValue("Y");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var audit = await admin.PostFileAsync("/api/audits/import", "checklist.xlsx", ms.ToArray(), Xlsx.ContentType, "file", ("roomId", room.ToString()))
            .Ok(201);
        var a = audit.GetProperty("audit");
        Assert.Equal(2, a.GetProperty("expectedCount").GetInt32());
        Assert.Equal(1, a.GetProperty("matchedCount").GetInt32());
        Assert.Equal(1, a.GetProperty("missingCount").GetInt32());
        var id = a.Id();

        var report = await admin.GetAsync($"/api/audits/{id}/report?format=xlsx").Expect(200);
        Assert.Equal(Xlsx.ContentType, report.Content.Headers.ContentType?.MediaType);
        using var rwb = new XLWorkbook(new MemoryStream(await report.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["Summary", "Items"], rwb.Worksheets.Select(w => w.Name));
        Assert.Equal(3, rwb.Worksheet("Items").LastRowUsed()!.RowNumber()); // header + matched + missing
        await admin.GetAsync($"/api/audits/{id}/report?format=csv").Expect(200);
        await admin.GetAsync($"/api/audits/{id}/report?format=docx").Expect(400);
    }

    [Fact]
    public async Task A_broken_xlsx_is_a_400()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin);
        await admin.PostFileAsync("/api/audits/import", "x.xlsx", "PK not a workbook"u8.ToArray(), Xlsx.ContentType, "file", ("roomId", room.ToString()))
            .Expect(400, "CSV_MALFORMED");
    }
}
