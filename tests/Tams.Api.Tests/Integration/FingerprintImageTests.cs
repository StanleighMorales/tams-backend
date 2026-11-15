using static Tams.Api.Tests.FixtureFingerprints;

namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-128: the kiosk uploads the sensor image, the API builds a SourceAFIS template and matches 1:N.</summary>
[Collection("api")]
public class FingerprintImageTests(ApiFixture api)
{
    static string Body(byte[] img) =>
        $$"""{"image":"{{Convert.ToBase64String(img)}}","width":{{Width}},"height":{{Height}},"sensorModel":"AS608","qualityScore":90}""";

    record Setup(ApiClient Staff, Guid StudentId, Guid StationId, string StationCode, string CardUid);

    async Task<Setup> Enrolled(int fingerSeed)
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (stationId, code) = await Seed.Station(admin, room);
        var student = await Seed.Student(admin);
        var (owner, _, _) = await Seed.User(admin, "INSTRUCTOR");
        var card = Seed.Hex(4);
        await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{card}}","userId":"{{owner}}"}""").Ok(201);

        var session = (await admin.PostRawAsync("/api/enrollment/sessions",
            $$"""{"studentId":"{{student.Id}}","stationId":"{{stationId}}","fingerIndex":1}""").Ok(201)).Id();
        var path = $"/api/kiosk/enrollment/sessions/{session}";
        var h = ("X-Station-Code", code);
        await api.NoAuth().SendAsync(HttpMethod.Post, $"{path}/fingerprint", Body(Finger(fingerSeed, 1, noise: 6)), h).Expect(200);
        await api.NoAuth().SendAsync(HttpMethod.Post, $"{path}/confirm", null, h).Expect(200);
        return new Setup(admin, student.Id, stationId, code, card);
    }

    async Task<Guid> StartCheckout(Setup s) =>
        (await (await api.NoAuth().SendAsync(HttpMethod.Post, $"/api/kiosk/{s.StationCode}/sessions", $$"""{"cardUid":"{{s.CardUid}}"}""",
            ("X-Station-Code", s.StationCode)).Expect(200)).Json()).GetProperty("sessionId").GetGuid();

    Task<HttpResponseMessage> Scan(Setup s, Guid session, string json) =>
        api.NoAuth().SendAsync(HttpMethod.Post, $"/api/kiosk/sessions/{session}/fingerprint", json, ("X-Station-Code", s.StationCode));

    [Fact]
    public async Task Image_enrollment_stores_a_sourceafis_template_and_a_rescan_matches_it()
    {
        var s = await Enrolled(201);
        var list = await s.Staff.GetAsync($"/api/students/{s.StudentId}/fingerprints").Expect(200);
        var text = await list.Content.ReadAsStringAsync();
        Assert.Contains("SOURCEAFIS_V1", text);
        Assert.DoesNotContain("\"image\"", text, StringComparison.OrdinalIgnoreCase);

        var session = await StartCheckout(s);
        var view = await (await Scan(s, session, Body(Finger(201, 2, dx: 2, dy: 2, noise: 6))).Expect(200)).Json();
        Assert.Equal("ASSET", view.GetProperty("nextStep").GetString()); // matched: on to the item tap
        Assert.False(view.GetProperty("finished").GetBoolean());
    }

    [Fact]
    public async Task A_different_finger_is_not_recognised_and_three_failures_deny_the_session()
    {
        var s = await Enrolled(202);
        var session = await StartCheckout(s);
        for (var i = 1; i <= 2; i++)
        {
            var v = await (await Scan(s, session, Body(Finger(900 + i, noise: 6))).Expect(200)).Json();
            Assert.Equal("FINGERPRINT", v.GetProperty("nextStep").GetString());
        }
        var last = await (await Scan(s, session, Body(Finger(903, noise: 6))).Expect(200)).Json();
        Assert.True(last.GetProperty("finished").GetBoolean());
        Assert.Equal("FINGERPRINT_NOT_RECOGNIZED", last.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task Bad_images_are_a_validation_error_and_not_a_failed_scan()
    {
        var s = await Enrolled(203);
        var session = await StartCheckout(s);
        await Scan(s, session, $$"""{"image":"{{Convert.ToBase64String(new byte[100])}}","width":{{Width}},"height":{{Height}}}""").Expect(400, "VALIDATION_FAILED");
        await Scan(s, session, $$"""{"image":"{{Convert.ToBase64String(Finger(203))}}"}""").Expect(400, "VALIDATION_FAILED"); // no size
        await Scan(s, session, $$"""{"image":"AAAA","template":"AAAA","width":64,"height":64}""").Expect(400, "VALIDATION_FAILED");
    }
}
