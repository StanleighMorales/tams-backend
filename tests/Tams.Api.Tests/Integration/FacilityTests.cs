namespace Tams.Api.Tests.Integration;

/// <summary>TEST-DATA.md sections 3 (rooms), 4 (stations) and 5 (authorization cards).</summary>
[Collection("api")]
public class FacilityTests(ApiFixture api)
{
    // ------------------------------------------------------------ rooms

    [Fact]
    public async Task Room_crud_round_trip()
    {
        var admin = await api.As("ADMIN");
        var tag = Seed.Tag();
        var created = await admin.PostRawAsync("/api/rooms",
            $$"""{"code":"LAB-{{tag}}","name":"Computer Lab","building":"Main","floor":2,"isStorage":false}""").Ok(201);
        var id = created.Id();
        Assert.Equal($"LAB-{tag}", created.GetProperty("code").GetString());

        var got = await admin.GetAsync($"/api/rooms/{id}").Ok();
        Assert.Equal(2, got.GetProperty("floor").GetInt32());

        var updated = await admin.PutRawAsync($"/api/rooms/{id}",
            $$"""{"code":"LAB-{{tag}}","name":"Renamed","building":"Main","floor":2,"isStorage":false,"status":"ACTIVE"}""").Ok();
        Assert.Equal("Renamed", updated.GetProperty("name").GetString());

        await admin.DeleteAsync($"/api/rooms/{id}").Expect(204);
        // ADMIN still sees the soft-deleted row (deletedAt set); everyone else gets 404.
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, (await admin.GetAsync($"/api/rooms/{id}").Ok()).GetProperty("deletedAt").ValueKind);
        await (await api.As("CUSTODIAN")).GetAsync($"/api/rooms/{id}").Expect(404);
        await admin.PostEmptyAsync($"/api/rooms/{id}/restore").Expect(200);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await admin.GetAsync($"/api/rooms/{id}").Ok()).GetProperty("deletedAt").ValueKind);
    }

    [Fact]
    public async Task Room_create_rejects_a_duplicate_code_and_an_out_of_range_floor()
    {
        var admin = await api.As("ADMIN");
        var tag = Seed.Tag();
        var json = $$"""{"code":"D-{{tag}}","name":"Dup","building":"Main","floor":1,"isStorage":false}""";
        await admin.PostRawAsync("/api/rooms", json).Expect(201);
        await admin.PostRawAsync("/api/rooms", json).Expect(409);
        await admin.PostRawAsync("/api/rooms", json.Replace($"D-{tag}", "F-" + tag).Replace("\"floor\":1", "\"floor\":101")).Expect(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Room_list_searches_sorts_and_pages()
    {
        var admin = await api.As("ADMIN");
        var tag = Seed.Tag();
        await admin.PostRawAsync("/api/rooms", $$"""{"code":"Q-{{tag}}-B","name":"B","building":"Main","floor":1,"isStorage":false}""").Expect(201);
        await admin.PostRawAsync("/api/rooms", $$"""{"code":"Q-{{tag}}-A","name":"A","building":"Main","floor":1,"isStorage":false}""").Expect(201);

        var body = await admin.GetAsync($"/api/rooms?q={tag}&sort=code&page=1&pageSize=10").Ok();
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Equal($"Q-{tag}-A", body.GetProperty("items")[0].GetProperty("code").GetString());
    }

    // ------------------------------------------------------------ stations

    [Fact]
    public async Task Station_crud_round_trip()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (id, code) = await Seed.Station(admin, room);

        var got = await admin.GetAsync($"/api/stations/{id}").Ok();
        Assert.Equal(code, got.GetProperty("stationCode").GetString());
        Assert.Equal("COUNTER_KIOSK", got.GetProperty("type").GetString());

        var updated = await admin.PutRawAsync($"/api/stations/{id}",
            $$"""{"stationCode":"{{code}}","type":"COUNTER_KIOSK","roomId":"{{room}}","name":"Renamed","status":"ACTIVE"}""").Ok();
        Assert.Equal("Renamed", updated.GetProperty("name").GetString());

        await admin.DeleteAsync($"/api/stations/{id}").Expect(204);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, (await admin.GetAsync($"/api/stations/{id}").Ok()).GetProperty("deletedAt").ValueKind);
        await (await api.As("CUSTODIAN")).GetAsync($"/api/stations/{id}").Expect(404);
        await admin.PostEmptyAsync($"/api/stations/{id}/restore").Expect(200);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await admin.GetAsync($"/api/stations/{id}").Ok()).GetProperty("deletedAt").ValueKind);
    }

    [Fact]
    public async Task Station_create_rejects_a_duplicate_code_an_unknown_room_and_a_kiosk_outside_storage()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (_, code) = await Seed.Station(admin, room);

        await admin.PostRawAsync("/api/stations", $$"""{"stationCode":"{{code}}","type":"COUNTER_KIOSK","roomId":"{{room}}","name":"Dup"}""").Expect(409);
        var res = await admin.PostRawAsync("/api/stations", $$"""{"stationCode":"X-{{Seed.Tag()}}","type":"COUNTER_KIOSK","roomId":"{{Guid.NewGuid()}}","name":"Orphan"}""");
        Assert.True((int)res.StatusCode is 400 or 404, $"unknown room accepted: {(int)res.StatusCode}");

        var classroom = await Seed.Room(admin, storage: false);
        await admin.PostRawAsync("/api/stations", $$"""{"stationCode":"X-{{Seed.Tag()}}","type":"COUNTER_KIOSK","roomId":"{{classroom}}","name":"Wrong room"}""")
            .Expect(400, "KIOSK_ROOM_MISMATCH");
    }

    [Fact]
    public async Task Station_list_filters_by_type_and_room()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (kiosk, _) = await Seed.Station(admin, room, "COUNTER_KIOSK");
        var (reader, _) = await Seed.Station(admin, room, "ROOM_STATION");

        var inRoom = await admin.GetAsync($"/api/stations?roomId={room}").Ok();
        Assert.Equal(2, inRoom.GetProperty("total").GetInt32());

        var readers = await admin.GetAsync($"/api/stations?type=ROOM_STATION&roomId={room}").Ok();
        Assert.Equal(1, readers.GetProperty("total").GetInt32());
        Assert.Equal(reader, readers.GetProperty("items")[0].Id());
        Assert.NotEqual(kiosk, reader);
    }

    [Fact]
    public async Task Station_health_summarises_online_warning_and_offline()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        await Seed.Station(admin, room);

        var body = await admin.GetAsync("/api/stations/health").Ok();
        var counts = body.GetProperty("counts");
        Assert.Equal(
            counts.GetProperty("online").GetInt32() + counts.GetProperty("warning").GetInt32() + counts.GetProperty("offline").GetInt32(),
            counts.GetProperty("total").GetInt32());
        Assert.True(counts.GetProperty("total").GetInt32() >= 1);
    }

    // ------------------------------------------------------------ authorization cards

    async Task<(ApiClient Admin, Guid InstructorId)> CardOwner()
    {
        var admin = await api.As("ADMIN");
        var (id, _, _) = await Seed.User(admin, "INSTRUCTOR");
        return (admin, id);
    }

    [Fact]
    public async Task Card_is_stored_uppercase_without_separators()
    {
        var (admin, owner) = await CardOwner();
        var hex = Seed.Hex(4);
        var spaced = string.Join(":", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 2, 2).ToLowerInvariant()));

        var card = await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{spaced}}","userId":"{{owner}}"}""").Ok(201);
        Assert.Equal(hex, card.GetProperty("cardUid").GetString());
        Assert.Equal("ACTIVE", card.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Card_create_rejects_a_bad_uid_a_duplicate_uid_and_a_non_instructor_owner()
    {
        var (admin, owner) = await CardOwner();
        var other = (await Seed.User(admin, "INSTRUCTOR")).Id;
        var custodian = (await Seed.User(admin, "CUSTODIAN")).Id;
        var uid = Seed.Hex(4);

        await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"ZZZ","userId":"{{owner}}"}""").Expect(400);
        await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{uid}}","userId":"{{owner}}"}""").Expect(201);
        await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{uid}}","userId":"{{other}}"}""").Expect(409, "TAG_ALREADY_ASSIGNED");
        await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{Seed.Hex(4)}}","userId":"{{custodian}}"}""").Expect(400, "CARD_OWNER_NOT_INSTRUCTOR");
    }

    [Fact]
    public async Task Card_list_get_and_update_status()
    {
        var (admin, owner) = await CardOwner();
        var card = await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{Seed.Hex(4)}}","userId":"{{owner}}"}""").Ok(201);
        var id = card.Id();

        var list = await admin.GetAsync($"/api/auth-cards?userId={owner}&status=ACTIVE").Ok();
        Assert.Contains(list.GetProperty("items").EnumerateArray(), c => c.Id() == id);
        await admin.GetAsync($"/api/auth-cards/{id}").Ok();

        var lost = await admin.PutRawAsync($"/api/auth-cards/{id}", """{"status":"LOST"}""").Ok();
        Assert.Equal("LOST", lost.GetProperty("status").GetString());
        await admin.PutRawAsync($"/api/auth-cards/{id}", """{"status":"NOPE"}""").Expect(400);
    }

    [Fact]
    public async Task Card_replace_marks_the_old_card_and_issues_a_new_one()
    {
        var (admin, owner) = await CardOwner();
        var old = await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{Seed.Hex(4)}}","userId":"{{owner}}"}""").Ok(201);
        var newUid = Seed.Hex(4);

        var body = await admin.PostRawAsync($"/api/auth-cards/{old.Id()}/replace", $$"""{"newCardUid":"{{newUid}}","oldStatus":"LOST"}""").Ok();
        Assert.Equal("LOST", body.GetProperty("oldCard").GetProperty("status").GetString());
        Assert.Equal(newUid, body.GetProperty("newCard").GetProperty("cardUid").GetString());
        Assert.Equal("ACTIVE", body.GetProperty("newCard").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Card_revoke_sets_the_status()
    {
        var (admin, owner) = await CardOwner();
        var card = await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{Seed.Hex(4)}}","userId":"{{owner}}"}""").Ok(201);

        var body = await admin.PostRawAsync($"/api/auth-cards/{card.Id()}/revoke", """{"status":"REVOKED"}""").Ok();
        Assert.Equal("REVOKED", body.GetProperty("status").GetString());
    }
}
