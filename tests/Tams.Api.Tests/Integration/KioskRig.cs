using System.Security.Cryptography;
using System.Text.Json;

namespace Tams.Api.Tests.Integration;

/// <summary>
/// One counter kiosk with everything a kiosk flow needs: a storage room and a destination room, an
/// instructor who is also an event coordinator with a card, and a student with an enrolled finger.
/// Every rig has its own station, people and fingerprint, so tests never collide in the shared database.
/// </summary>
public sealed class KioskRig
{
    public required ApiFixture Api { get; init; }
    public required ApiClient Admin { get; init; }
    public required ApiClient Instructor { get; init; }
    public required Guid InstructorId { get; init; }
    public required Guid StorageRoomId { get; init; }
    public required Guid DestinationRoomId { get; init; }
    public required Guid CategoryId { get; init; }
    public required string StationCode { get; init; }
    public required string CardUid { get; init; }
    public required string Template { get; init; }
    public required Guid StudentId { get; init; }

    public static async Task<KioskRig> Create(ApiFixture api)
    {
        var admin = await api.As("ADMIN");
        var storage = await Seed.Room(admin, storage: true);
        var destination = await Seed.Room(admin);
        var (stationId, code) = await Seed.Station(admin, storage);
        var student = await Seed.Student(admin);
        var (instructorId, email, password) = await Seed.User(admin, "INSTRUCTOR", coordinator: true);
        var cardUid = Seed.Hex(4);
        await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{cardUid}}","userId":"{{instructorId}}"}""").Ok(201);

        var login = await api.NoAuth().PostRawAsync("/api/auth/login", $$"""{"email":"{{email}}","password":"{{password}}"}""").Ok();
        var instructor = api.WithToken(login.GetProperty("accessToken").GetString()!);

        // Enroll the student's finger through the real enrollment flow.
        var template = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var session = (await admin.PostRawAsync("/api/enrollment/sessions",
            $$"""{"studentId":"{{student.Id}}","stationId":"{{stationId}}","fingerIndex":1}""").Ok(201)).Id();
        var h = ("X-Station-Code", code);
        await api.NoAuth().SendAsync(HttpMethod.Post, $"/api/kiosk/enrollment/sessions/{session}/fingerprint",
            $$"""{"template":"{{template}}","sensorModel":"AS608","qualityScore":92}""", h).Expect(200);
        await api.NoAuth().SendAsync(HttpMethod.Post, $"/api/kiosk/enrollment/sessions/{session}/confirm", null, h).Expect(200);

        return new KioskRig
        {
            Api = api, Admin = admin, Instructor = instructor, InstructorId = instructorId, StorageRoomId = storage,
            DestinationRoomId = destination, CategoryId = await Seed.Category(admin), StationCode = code, CardUid = cardUid,
            Template = template, StudentId = student.Id,
        };
    }

    /// <summary>A borrowable asset in storage with the given condition (GOOD, FAIR or DAMAGED).</summary>
    public async Task<(Guid Id, string Code, string RfidTag)> Asset(string condition = "GOOD")
    {
        var tag = Seed.Tag();
        var rfid = Seed.Hex(10);
        var body = await Admin.PostRawAsync("/api/assets",
            $$"""{"assetCode":"KT-{{tag}}","name":"Laptop {{tag}}","categoryId":"{{CategoryId}}","type":"BORROWABLE","rfidTagUid":"{{rfid}}","homeRoomId":"{{StorageRoomId}}","condition":"{{condition}}","replacementCost":45000}""").Ok(201);
        return (body.Id(), $"KT-{tag}", rfid);
    }

    /// <summary>A kiosk call that must answer 200 (a denial is still 200 with status FAILED).</summary>
    public async Task<JsonElement> Kiosk(HttpMethod method, string path, string? json = null) =>
        await Api.NoAuth().SendAsync(method, $"/api/kiosk/{path}", json, ("X-Station-Code", StationCode)).Ok();

    public Task<JsonElement> Start(string? type = null) =>
        Kiosk(HttpMethod.Post, $"{StationCode}/sessions",
            type is null ? $$"""{"cardUid":"{{CardUid}}"}""" : $$"""{"cardUid":"{{CardUid}}","type":"{{type}}"}""");

    public Task<JsonElement> Fingerprint(Guid session) =>
        Kiosk(HttpMethod.Post, $"sessions/{session}/fingerprint", $$"""{"template":"{{Template}}"}""");

    public Task<JsonElement> Tap(Guid session, string rfidTag) =>
        Kiosk(HttpMethod.Post, $"sessions/{session}/asset", $$"""{"tagUid":"{{rfidTag}}"}""");

    public Task<HttpResponseMessage> TapRaw(Guid session, string rfidTag) =>
        Api.NoAuth().SendAsync(HttpMethod.Post, $"/api/kiosk/sessions/{session}/asset", $$"""{"tagUid":"{{rfidTag}}"}""", ("X-Station-Code", StationCode));

    public Task<JsonElement> Confirm(Guid session) => Kiosk(HttpMethod.Post, $"sessions/{session}/confirm");
    public Task<JsonElement> Cancel(Guid session) => Kiosk(HttpMethod.Post, $"sessions/{session}/cancel");
    public Task<JsonElement> Poll(Guid session) => Kiosk(HttpMethod.Get, $"sessions/{session}");

    /// <summary>Uploads a JPEG, which moves the session to AWAITING_APPROVAL.</summary>
    public async Task<JsonElement> Photo(Guid session)
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 0xFF, 0xD9];
        return await Api.NoAuth().PostFileAsync($"/api/kiosk/sessions/{session}/photo", jpeg, "image/jpeg", ("X-Station-Code", StationCode)).Ok();
    }

    public Task<JsonElement> Approve(Guid session) =>
        Instructor.PostRawAsync($"/api/approvals/{session}/approve", $$"""{"destinationRoomId":"{{DestinationRoomId}}","classLengthMinutes":90}""").Ok();

    /// <summary>An event running now for the instructor, so their card opens batch mode.</summary>
    public async Task<Guid> RunningEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var body = await Admin.PostRawAsync("/api/events",
            $$"""{"title":"Event {{Seed.Tag()}}","venueText":"Gym","startsAt":"{{now.AddHours(-1):O}}","endsAt":"{{now.AddHours(2):O}}","coordinatorUserId":"{{InstructorId}}"}""").Ok(201);
        return body.Id();
    }

    public async Task<string> AssetStatus(Guid assetId) =>
        (await Admin.GetAsync($"/api/assets/{assetId}").Ok()).GetProperty("status").GetString()!;

    /// <summary>The whole checkout for the given tags: tap the items, photo, approval, confirm. Returns the session id.</summary>
    public async Task<Guid> Checkout(params string[] rfidTags)
    {
        var id = SessionId(await Start("CHECKOUT"));
        await Fingerprint(id);
        foreach (var tag in rfidTags) await Tap(id, tag);
        if (Status(await Poll(id)) == "AWAITING_ASSET") await Confirm(id); // several items: OK ends the item step
        await Photo(id);
        await Approve(id);
        var done = await Confirm(id);
        Assert.Equal("COMPLETED", Status(done));
        return id;
    }

    /// <summary>Sets settings for one test and puts the defaults back afterwards (settings are global).</summary>
    public async Task<IAsyncDisposable> Settings(int maxItems, int maxActive)
    {
        await Admin.PutRawAsync("/api/settings", $$"""{"max_items_per_transaction":{{maxItems}},"max_active_loans_per_student":{{maxActive}}}""").Expect(200);
        return new Restore(Admin);
    }

    sealed class Restore(ApiClient admin) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await admin.PutRawAsync("/api/settings", """{"max_items_per_transaction":1,"max_active_loans_per_student":1}""").Expect(200);
    }

    /// <summary>The newest loan row of an asset, as the loans list returns it.</summary>
    public async Task<JsonElement> LoanOf(Guid assetId) =>
        (await Admin.GetAsync($"/api/loans?assetId={assetId}").Ok()).GetProperty("items")[0];

    public static Guid SessionId(JsonElement view) => view.GetProperty("sessionId").GetGuid();
    public static string[] Lcd(JsonElement view) => view.GetProperty("lcdLines").EnumerateArray().Select(l => l.GetString()!).ToArray();
    public static string Status(JsonElement view) => view.GetProperty("status").GetString()!;
}
