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

    // ------------------------------------------------------------ card enrollment at the kiosk ("Add card")

    Task<System.Text.Json.JsonElement> PollKiosk(string code) =>
        api.NoAuth().SendAsync(HttpMethod.Get, $"/api/kiosk/{code}/session", null, ("X-Station-Code", code)).Ok();

    Task<HttpResponseMessage> TapCard(string code, Guid request, string uid, string? header = null) =>
        api.NoAuth().SendAsync(HttpMethod.Post, $"/api/kiosk/{code}/card-enrollment/{request}/tap", $$"""{"cardUid":"{{uid}}"}""",
            ("X-Station-Code", header ?? code));

    async Task<(ApiClient Admin, Guid Owner, string Station)> CardKiosk()
    {
        var (admin, owner) = await CardOwner();
        var (_, station) = await Seed.Station(admin, await Seed.Room(admin, storage: true));
        return (admin, owner, station);
    }

    static bool IsNull(System.Text.Json.JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Card_enrollment_switches_the_idle_kiosk_and_registers_the_next_tapped_card()
    {
        var (admin, owner, station) = await CardKiosk();
        Assert.True(IsNull(await PollKiosk(station), "cardEnrollment"));

        var request = await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{owner}}"}""").Ok(201);
        Assert.Equal("WAITING", request.GetProperty("status").GetString());

        var idle = await PollKiosk(station);
        Assert.Equal(request.Id(), idle.GetProperty("cardEnrollment").Id());
        Assert.Equal("Add card for", idle.GetProperty("lcdLines")[0].GetString());

        var uid = Seed.Hex(4);
        var tap = await (await TapCard(station, request.Id(), uid)).Json();
        Assert.Equal("COMPLETED", tap.GetProperty("status").GetString());
        Assert.Equal("Card registered", tap.GetProperty("lcdLines")[0].GetString());

        Assert.True(IsNull(await PollKiosk(station), "cardEnrollment"));
        var done = await admin.GetAsync($"/api/auth-cards/enrollment/{request.Id()}").Ok();
        Assert.Equal("COMPLETED", done.GetProperty("status").GetString());
        Assert.Equal(uid, done.GetProperty("card").GetProperty("cardUid").GetString());

        // The user now lists the card (users used to come back without authCards).
        var user = await admin.GetAsync($"/api/users/{owner}").Ok();
        Assert.Contains(user.GetProperty("authCards").EnumerateArray(), c => c.GetProperty("cardUid").GetString() == uid);
        var listed = await admin.GetAsync($"/api/users?q={user.GetProperty("employeeNo").GetString()}").Ok();
        Assert.Single(listed.GetProperty("items")[0].GetProperty("authCards").EnumerateArray());
    }

    [Fact]
    public async Task Card_enrollment_keeps_waiting_after_a_taken_card_and_can_be_cancelled()
    {
        var (admin, owner, station) = await CardKiosk();
        var other = (await Seed.User(admin, "INSTRUCTOR")).Id;
        var taken = Seed.Hex(4);
        await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{taken}}","userId":"{{other}}"}""").Expect(201);

        var request = (await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{owner}}"}""").Ok(201)).Id();
        var refused = await (await TapCard(station, request, taken)).Json();
        Assert.Equal("WAITING", refused.GetProperty("status").GetString());
        Assert.Equal("TAG_ALREADY_ASSIGNED", refused.GetProperty("failureCode").GetString());
        var waiting = await admin.GetAsync($"/api/auth-cards/enrollment/{request}").Ok();
        Assert.Equal("TAG_ALREADY_ASSIGNED", waiting.GetProperty("lastError").GetProperty("code").GetString());

        var fresh = await (await TapCard(station, request, Seed.Hex(4))).Json();
        Assert.Equal("COMPLETED", fresh.GetProperty("status").GetString());

        // A finished request cannot be tapped again; cancelling it is a no-op.
        await TapCard(station, request, Seed.Hex(4)).Expect(409, "CARD_ENROLLMENT_NOT_WAITING");
        var again = await admin.PostEmptyAsync($"/api/auth-cards/enrollment/{request}/cancel").Ok();
        Assert.Equal("COMPLETED", again.GetProperty("status").GetString());

        var second = (await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{owner}}"}""").Ok(201)).Id();
        var cancelled = await admin.PostEmptyAsync($"/api/auth-cards/enrollment/{second}/cancel").Ok();
        Assert.Equal("CANCELLED", cancelled.GetProperty("status").GetString());
        Assert.True(IsNull(await PollKiosk(station), "cardEnrollment"));
        await TapCard(station, second, Seed.Hex(4)).Expect(409, "CARD_ENROLLMENT_NOT_WAITING");
    }

    [Fact]
    public async Task Card_enrollment_new_request_replaces_the_waiting_one()
    {
        var (admin, owner, station) = await CardKiosk();
        var other = (await Seed.User(admin, "INSTRUCTOR")).Id;

        var first = (await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{owner}}"}""").Ok(201)).Id();
        var second = (await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{other}}"}""").Ok(201)).Id();

        var old = await admin.GetAsync($"/api/auth-cards/enrollment/{first}").Ok();
        Assert.Equal("CANCELLED", old.GetProperty("status").GetString());
        Assert.Equal(second, (await PollKiosk(station)).GetProperty("cardEnrollment").Id());
    }

    [Fact]
    public async Task Card_enrollment_is_for_instructors_staff_and_known_kiosks()
    {
        var (admin, owner, station) = await CardKiosk();
        var custodian = (await Seed.User(admin, "CUSTODIAN")).Id;

        await admin.PostRawAsync("/api/auth-cards/enrollment", "{}").Expect(400, "VALIDATION_FAILED");
        await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{Guid.NewGuid()}}"}""").Expect(400, "USER_NOT_FOUND");
        await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{custodian}}"}""").Expect(400, "CARD_OWNER_NOT_INSTRUCTOR");
        await (await api.As("CUSTODIAN")).PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{owner}}"}""").Expect(403);

        var request = (await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{owner}}"}""").Ok(201)).Id();
        await TapCard(station, request, Seed.Hex(4), header: "NOT-THE-STATION").Expect(403, "STATION_NOT_AUTHENTICATED");
        await TapCard(station, request, "ZZZ").Expect(400, "INVALID_RFID_UID");
        await TapCard(station, Guid.NewGuid(), Seed.Hex(4)).Expect(404, "CARD_ENROLLMENT_NOT_FOUND");
        await admin.GetAsync($"/api/auth-cards/enrollment/{Guid.NewGuid()}").Expect(404, "CARD_ENROLLMENT_NOT_FOUND");
    }

    // ------------------------------------------------------------ asset tag scan at the kiosk ("Scan tag")

    async Task<(ApiClient Admin, string Station, (Guid Id, string Code, string RfidTag) Asset)> TagKiosk()
    {
        var (admin, _, station) = await CardKiosk();
        var asset = await Seed.BorrowableAsset(admin, await Seed.Category(admin), await Seed.Room(admin, storage: true));
        return (admin, station, asset);
    }

    [Fact]
    public async Task Tag_scan_reads_the_next_tapped_tag_without_saving_anything()
    {
        var (admin, station, other) = await TagKiosk();
        var scan = await admin.PostRawAsync("/api/assets/tag-scan", """{"label":"LP-NEW"}""").Ok(201);
        Assert.Equal("WAITING", scan.GetProperty("status").GetString());
        Assert.Equal("LP-NEW", scan.GetProperty("label").GetString());
        Assert.True(IsNull(scan, "assetId"));

        var prompt = (await PollKiosk(station)).GetProperty("cardEnrollment");
        Assert.Equal(scan.Id(), prompt.Id());
        Assert.Equal("ASSET_TAG", prompt.GetProperty("kind").GetString());

        // Another asset's tag is refused and the request keeps waiting.
        var refused = await (await TapCard(station, scan.Id(), other.RfidTag)).Json();
        Assert.Equal("WAITING", refused.GetProperty("status").GetString());
        Assert.Equal("TAG_ALREADY_ASSIGNED", refused.GetProperty("failureCode").GetString());
        Assert.True(IsNull(refused, "tagUid"));
        var waiting = await admin.GetAsync($"/api/assets/tag-scan/{scan.Id()}").Ok();
        Assert.Equal("TAG_ALREADY_ASSIGNED", waiting.GetProperty("lastError").GetProperty("code").GetString());

        var fresh = Seed.Hex(10);
        var read = await (await TapCard(station, scan.Id(), fresh)).Json();
        Assert.Equal("COMPLETED", read.GetProperty("status").GetString());
        Assert.Equal(fresh, read.GetProperty("tagUid").GetString());
        Assert.Equal("Tag read", read.GetProperty("lcdLines")[0].GetString());

        var done = await admin.GetAsync($"/api/assets/tag-scan/{scan.Id()}").Ok();
        Assert.Equal("COMPLETED", done.GetProperty("status").GetString());
        Assert.Equal(fresh, done.GetProperty("tagUid").GetString());
        Assert.True(IsNull(await PollKiosk(station), "cardEnrollment"));

        // Nothing was saved, and the id is not an Add card request.
        Assert.Empty((await admin.GetAsync($"/api/assets?q={fresh}").Ok()).GetProperty("items").EnumerateArray());
        await admin.GetAsync($"/api/auth-cards/enrollment/{scan.Id()}").Expect(404, "CARD_ENROLLMENT_NOT_FOUND");
    }

    [Fact]
    public async Task Tag_scan_on_edit_accepts_the_assets_own_tag_and_can_be_cancelled()
    {
        var (admin, station, own) = await TagKiosk();
        var scan = await admin.PostRawAsync("/api/assets/tag-scan", $$"""{"assetId":"{{own.Id}}"}""").Ok(201);
        Assert.Equal(own.Code, scan.GetProperty("label").GetString());

        var read = await (await TapCard(station, scan.Id(), own.RfidTag)).Json();
        Assert.Equal("COMPLETED", read.GetProperty("status").GetString());
        Assert.Equal(own.RfidTag, read.GetProperty("tagUid").GetString());

        // A finished request is returned unchanged by cancel.
        var again = await admin.PostEmptyAsync($"/api/assets/tag-scan/{scan.Id()}/cancel").Ok();
        Assert.Equal("COMPLETED", again.GetProperty("status").GetString());

        var second = await admin.PostRawAsync("/api/assets/tag-scan", "{}").Ok(201);
        Assert.Equal("New item", second.GetProperty("label").GetString());
        var cancelled = await admin.PostEmptyAsync($"/api/assets/tag-scan/{second.Id()}/cancel").Ok();
        Assert.Equal("CANCELLED", cancelled.GetProperty("status").GetString());
        Assert.True(IsNull(await PollKiosk(station), "cardEnrollment"));
        await TapCard(station, second.Id(), Seed.Hex(10)).Expect(409, "CARD_ENROLLMENT_NOT_WAITING");

        await admin.PostRawAsync("/api/assets/tag-scan", $$"""{"assetId":"{{Guid.NewGuid()}}"}""").Expect(404, "ASSET_NOT_FOUND");
        await admin.GetAsync($"/api/assets/tag-scan/{Guid.NewGuid()}").Expect(404, "TAG_SCAN_NOT_FOUND");
        await (await api.As("CUSTODIAN")).PostRawAsync("/api/assets/tag-scan", "{}").Expect(403);
    }

    [Fact]
    public async Task Tag_scan_and_add_card_replace_each_other_and_keep_their_ids_apart()
    {
        var (admin, owner, station) = await CardKiosk();
        var card = (await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{owner}}"}""").Ok(201)).Id();
        var scan = (await admin.PostRawAsync("/api/assets/tag-scan", "{}").Ok(201)).Id();

        var old = await admin.GetAsync($"/api/auth-cards/enrollment/{card}").Ok();
        Assert.Equal("CANCELLED", old.GetProperty("status").GetString());
        Assert.Equal(scan, (await PollKiosk(station)).GetProperty("cardEnrollment").Id());
        await admin.GetAsync($"/api/assets/tag-scan/{card}").Expect(404, "TAG_SCAN_NOT_FOUND");

        var next = (await admin.PostRawAsync("/api/auth-cards/enrollment", $$"""{"userId":"{{owner}}"}""").Ok(201)).Id();
        Assert.Equal("CANCELLED", (await admin.GetAsync($"/api/assets/tag-scan/{scan}").Ok()).GetProperty("status").GetString());
        var prompt = (await PollKiosk(station)).GetProperty("cardEnrollment");
        Assert.Equal(next, prompt.Id());
        Assert.Equal("CARD", prompt.GetProperty("kind").GetString());
    }

    // ------------------------------------------------------------ a revoked card's UID can be issued again

    [Fact]
    public async Task A_revoked_card_can_be_issued_again_and_the_old_row_stays_as_history()
    {
        var (admin, owner) = await CardOwner();
        var other = (await Seed.User(admin, "INSTRUCTOR")).Id;
        var uid = Seed.Hex(4);
        var old = (await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{uid}}","userId":"{{owner}}"}""").Ok(201)).Id();
        await admin.PostRawAsync($"/api/auth-cards/{old}/revoke", """{"status":"REVOKED"}""").Ok();

        var again = (await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{uid}}","userId":"{{other}}"}""").Ok(201)).Id();
        Assert.NotEqual(old, again);
        Assert.Equal("REVOKED", (await admin.GetAsync($"/api/auth-cards/{old}").Ok()).GetProperty("status").GetString());

        // Only one ACTIVE card per UID, and an old row cannot be switched back on beside it.
        await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{uid}}","userId":"{{owner}}"}""").Expect(409, "TAG_ALREADY_ASSIGNED");
        await admin.PutRawAsync($"/api/auth-cards/{old}", """{"status":"ACTIVE"}""").Expect(409, "TAG_ALREADY_ASSIGNED");

        // The user that held it before still lists the revoked row.
        var cards = (await admin.GetAsync($"/api/users/{owner}").Ok()).GetProperty("authCards");
        Assert.Equal("REVOKED", cards[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_kiosk_refuses_a_revoked_card_and_accepts_it_once_issued_again()
    {
        var rig = await KioskRig.Create(api);
        var cards = await rig.Admin.GetAsync($"/api/auth-cards?userId={rig.InstructorId}").Ok();
        await rig.Admin.PostEmptyAsync($"/api/auth-cards/{cards.GetProperty("items")[0].Id()}/revoke").Ok();

        var denied = await rig.Start();
        Assert.Equal("FAILED", KioskRig.Status(denied));
        Assert.Equal("CARD_INACTIVE", denied.GetProperty("failureCode").GetString());

        await rig.Admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{rig.CardUid}}","userId":"{{rig.InstructorId}}"}""").Expect(201);
        var open = await rig.Start();
        Assert.Equal("AWAITING_FINGERPRINT", KioskRig.Status(open));
        await rig.Cancel(KioskRig.SessionId(open));
    }
}
