using System.Text.Json;

namespace Tams.Api.Tests.Integration;

/// <summary>TEST-DATA.md sections 6 (asset categories) and 7 (assets, images, QR codes).</summary>
[Collection("api")]
public class AssetTests(ApiFixture api)
{
    // ------------------------------------------------------------ categories

    [Fact]
    public async Task Category_crud_round_trip()
    {
        var admin = await api.As("ADMIN");
        var name = "Laptops-" + Seed.Tag();
        var created = await admin.PostRawAsync("/api/asset-categories", $$"""{"name":"{{name}}","description":"Borrowable laptops","isBorrowable":true}""").Ok(201);
        var id = created.Id();
        Assert.True(created.GetProperty("isBorrowable").GetBoolean());

        var updated = await admin.PutRawAsync($"/api/asset-categories/{id}", $$"""{"name":"{{name}}-2","description":"changed","isBorrowable":false}""").Ok();
        Assert.Equal($"{name}-2", updated.GetProperty("name").GetString());
        Assert.False(updated.GetProperty("isBorrowable").GetBoolean());

        await admin.DeleteAsync($"/api/asset-categories/{id}").Expect(204);
        await admin.GetAsync($"/api/asset-categories/{id}").Expect(404);
    }

    [Fact]
    public async Task Category_create_rejects_a_duplicate_name_and_a_missing_name()
    {
        var admin = await api.As("ADMIN");
        var name = "Dup-" + Seed.Tag();
        var json = $$"""{"name":"{{name}}","isBorrowable":true}""";
        await admin.PostRawAsync("/api/asset-categories", json).Expect(201);
        await admin.PostRawAsync("/api/asset-categories", json).Expect(409);
        await admin.PostRawAsync("/api/asset-categories", """{"name":"","isBorrowable":true}""").Expect(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Category_list_searches_and_pages()
    {
        var admin = await api.As("ADMIN");
        var tag = Seed.Tag();
        await admin.PostRawAsync("/api/asset-categories", $$"""{"name":"Zeta-{{tag}}","isBorrowable":true}""").Expect(201);
        await admin.PostRawAsync("/api/asset-categories", $$"""{"name":"Alpha-{{tag}}","isBorrowable":false}""").Expect(201);

        var body = await admin.GetAsync($"/api/asset-categories?q={tag}&sort=name&page=1&pageSize=10").Ok();
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Equal($"Alpha-{tag}", body.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Category_deleting_one_with_assets_is_refused()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        await Seed.BorrowableAsset(admin, cat, room);

        var res = await admin.DeleteAsync($"/api/asset-categories/{cat}");
        Assert.True((int)res.StatusCode is 409 or 400, $"category with assets was deleted: {(int)res.StatusCode}");
    }

    // ------------------------------------------------------------ assets

    [Fact]
    public async Task Asset_create_returns_the_full_record()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var tag = Seed.Tag();
        var rfid = Seed.Hex(10);

        var body = await admin.PostRawAsync("/api/assets", $$"""
            {"assetCode":"LT-{{tag}}","name":"Dell Latitude 5440","categoryId":"{{cat}}","type":"BORROWABLE","brand":"Dell","model":"Latitude 5440","serialNo":"SN-{{tag}}","rfidTagUid":"{{rfid}}","homeRoomId":"{{room}}","status":"IN_STORAGE","condition":"GOOD","replacementCost":45000.00,"acquiredAt":"2026-06-15","notes":"Charger included"}
            """).Ok(201);

        Assert.Equal($"LT-{tag}", body.GetProperty("assetCode").GetString());
        Assert.Equal("BORROWABLE", body.GetProperty("type").GetString());
        Assert.Equal("IN_STORAGE", body.GetProperty("status").GetString());
        Assert.Equal("GOOD", body.GetProperty("condition").GetString());
        Assert.Equal(45000m, body.GetProperty("replacementCost").GetDecimal());
        Assert.Equal("2026-06-15", body.GetProperty("acquiredAt").GetString());
        Assert.Equal(rfid, body.GetProperty("rfidTagUid").GetString());
    }

    [Fact]
    public async Task Asset_defaults_to_in_storage_when_no_status_is_given()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (id, _, _) = await Seed.BorrowableAsset(admin, cat, room);
        Assert.Equal("IN_STORAGE", (await admin.GetAsync($"/api/assets/{id}").Ok()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_fixed_asset_needs_no_rfid_tag()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin, borrowable: false);
        var room = await Seed.Room(admin);
        await admin.PostRawAsync("/api/assets", $$"""
            {"assetCode":"PC-{{Seed.Tag()}}","name":"Lab Desktop","categoryId":"{{cat}}","type":"FIXED","brand":"HP","model":"ProDesk 400","homeRoomId":"{{room}}","condition":"GOOD","replacementCost":32000}
            """).Ok(201);
    }

    [Fact]
    public async Task Asset_create_rejects_duplicates_and_bad_input()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (_, code, rfid) = await Seed.BorrowableAsset(admin, cat, room);

        string Body(string assetCode, string? tag, string category) =>
            $$"""{"assetCode":"{{assetCode}}","name":"X","categoryId":"{{category}}","type":"BORROWABLE"{{(tag is null ? "" : $",\"rfidTagUid\":\"{tag}\"")}},"homeRoomId":"{{room}}","condition":"GOOD","replacementCost":100}""";

        await admin.PostRawAsync("/api/assets", Body(code, Seed.Hex(10), cat.ToString())).Expect(409);                  // duplicate asset code
        await admin.PostRawAsync("/api/assets", Body("N-" + Seed.Tag(), rfid, cat.ToString())).Expect(409);             // duplicate RFID tag
        await admin.PostRawAsync("/api/assets", Body("N-" + Seed.Tag(), null, cat.ToString())).Expect(400);             // borrowable needs a tag
        // An empty string is not a GUID, so the body never binds: a plain 400 (code BAD_REQUEST), not field errors.
        await admin.PostRawAsync("/api/assets", Body("N-" + Seed.Tag(), Seed.Hex(10), "")).Expect(400);
        await admin.PostRawAsync("/api/assets", """{"categoryId":""}""").Expect(400);
        // A well-formed body with required fields missing does give field errors.
        await admin.PostRawAsync("/api/assets", "{}").Expect(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Asset_get_update_and_unknown_id()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (id, code, rfid) = await Seed.BorrowableAsset(admin, cat, room);

        var updated = await admin.PutRawAsync($"/api/assets/{id}", $$"""
            {"assetCode":"{{code}}","name":"Renamed","categoryId":"{{cat}}","type":"BORROWABLE","rfidTagUid":"{{rfid}}","homeRoomId":"{{room}}","status":"MAINTENANCE","condition":"FAIR","replacementCost":40000}
            """).Ok();
        Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.Equal("MAINTENANCE", updated.GetProperty("status").GetString());
        Assert.Equal("FAIR", updated.GetProperty("condition").GetString());

        await admin.GetAsync($"/api/assets/{Guid.NewGuid()}").Expect(404);
    }

    [Fact]
    public async Task Asset_list_filters_sorts_and_caps_the_page_size()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (a, _, _) = await Seed.BorrowableAsset(admin, cat, room);
        await Seed.BorrowableAsset(admin, cat, room);

        var byCategory = await admin.GetAsync($"/api/assets?categoryId={cat}&homeRoomId={room}&status=IN_STORAGE&type=BORROWABLE&sort=name&page=1&pageSize=10").Ok();
        Assert.Equal(2, byCategory.GetProperty("total").GetInt32());
        Assert.Contains(byCategory.GetProperty("items").EnumerateArray(), x => x.Id() == a);

        var none = await admin.GetAsync($"/api/assets?categoryId={cat}&type=FIXED").Ok();
        Assert.Equal(0, none.GetProperty("total").GetInt32());

        // Asset lists clamp pageSize to 100 (the user list rejects it instead; see UserTests).
        var clamped = await admin.GetAsync("/api/assets?pageSize=101").Ok();
        Assert.Equal(100, clamped.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Asset_list_rejects_an_unknown_status_filter()
    {
        var admin = await api.As("ADMIN");
        await admin.GetAsync("/api/assets?status=FLYING").Expect(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Asset_soft_delete_hide_and_restore()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (id, code, _) = await Seed.BorrowableAsset(admin, cat, room);

        await admin.DeleteAsync($"/api/assets/{id}").Expect(204);
        Assert.Equal(0, (await admin.GetAsync($"/api/assets?q={code}").Ok()).GetProperty("total").GetInt32());
        Assert.Equal(1, (await admin.GetAsync($"/api/assets?q={code}&includeDeleted=true").Ok()).GetProperty("total").GetInt32());

        await admin.PostEmptyAsync($"/api/assets/{id}/restore").Expect(200);
        Assert.Equal(1, (await admin.GetAsync($"/api/assets?q={code}").Ok()).GetProperty("total").GetInt32());
    }

    // ------------------------------------------------------------ image

    [Fact]
    public async Task Asset_image_upload_download_and_delete()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (id, _, _) = await Seed.BorrowableAsset(admin, cat, room);

        await admin.GetAsync($"/api/assets/{id}/image").Expect(404); // none yet
        await admin.PostFileAsync($"/api/assets/{id}/image", "photo.png", Seed.Png, "image/png").Expect(200);

        var download = await admin.GetAsync($"/api/assets/{id}/image").Expect(200);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Seed.Png, await download.Content.ReadAsByteArrayAsync());

        await admin.DeleteAsync($"/api/assets/{id}/image").Expect(204);
        await admin.GetAsync($"/api/assets/{id}/image").Expect(404);
    }

    [Fact]
    public async Task Asset_image_rejects_a_file_that_is_not_an_image()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (id, _, _) = await Seed.BorrowableAsset(admin, cat, room);

        var res = await admin.PostFileAsync($"/api/assets/{id}/image", "notes.txt", "hello"u8.ToArray(), "text/plain");
        Assert.True((int)res.StatusCode is 400 or 415, $"text file accepted as an image: {(int)res.StatusCode}");
    }

    // ------------------------------------------------------------ QR

    [Fact]
    public async Task Asset_qr_is_a_png_by_default_and_an_svg_on_request()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (id, _, _) = await Seed.BorrowableAsset(admin, cat, room);

        var png = await admin.GetAsync($"/api/assets/{id}/qr").Expect(200);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, (await png.Content.ReadAsByteArrayAsync())[..4]);

        var svg = await admin.GetAsync($"/api/assets/{id}/qr?format=svg&size=400").Expect(200);
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", await svg.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Asset_qr_for_an_unknown_asset_is_404()
    {
        var admin = await api.As("ADMIN");
        await admin.GetAsync($"/api/assets/{Guid.NewGuid()}/qr").Expect(404);
    }

    [Fact]
    public async Task Qr_sheet_is_a_pdf_for_ids_or_for_a_category()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (a, _, _) = await Seed.BorrowableAsset(admin, cat, room);
        var (b, _, _) = await Seed.BorrowableAsset(admin, cat, room);

        foreach (var body in new[] { $$"""{"assetIds":["{{a}}","{{b}}"]}""", $$"""{"categoryId":"{{cat}}"}""" })
        {
            var res = await admin.PostRawAsync("/api/assets/qr-sheet", body).Expect(200);
            Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await res.Content.ReadAsByteArrayAsync())[..4]));
        }
    }

    [Fact]
    public async Task Qr_sheet_needs_ids_or_a_category()
    {
        var admin = await api.As("ADMIN");
        await admin.PostRawAsync("/api/assets/qr-sheet", "{}").Expect(400);
    }
}
