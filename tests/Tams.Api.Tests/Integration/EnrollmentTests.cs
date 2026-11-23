namespace Tams.Api.Tests.Integration;

/// <summary>TEST-DATA.md section 10: fingerprint enrollment, staff side and kiosk side.</summary>
[Collection("api")]
public class EnrollmentTests(ApiFixture api)
{
    const string Template = "ZmFrZS1hczYwOC10ZW1wbGF0ZS0wMDAx"; // any base64 works; the matcher is still a placeholder
    const string FingerprintBody = $$"""{"template":"{{Template}}","sensorModel":"AS608","qualityScore":92}""";

    record Setup(ApiClient Staff, Guid StudentId, Guid StationId, string StationCode);

    async Task<Setup> NewSetup()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (stationId, code) = await Seed.Station(admin, room);
        var student = await Seed.Student(admin);
        return new Setup(admin, student.Id, stationId, code);
    }

    static string Start(Setup s, int finger = 1) =>
        $$"""{"studentId":"{{s.StudentId}}","stationId":"{{s.StationId}}","fingerIndex":{{finger}}}""";

    async Task<Guid> StartSession(Setup s, int finger = 1) =>
        (await s.Staff.PostRawAsync("/api/enrollment/sessions", Start(s, finger)).Ok(201)).Id();

    Task<HttpResponseMessage> Kiosk(HttpMethod method, string path, Setup s, string? json = null) =>
        api.NoAuth().SendAsync(method, $"/api/kiosk/enrollment/sessions/{path}", json, ("X-Station-Code", s.StationCode));

    // ------------------------------------------------------------ happy path

    [Fact]
    public async Task Full_enrollment_flow_stores_the_finger_and_never_returns_the_template()
    {
        var s = await NewSetup();
        var id = await StartSession(s);

        var staffView = await s.Staff.GetAsync($"/api/enrollment/sessions/{id}").Ok();
        Assert.Equal(s.StudentId, staffView.GetProperty("studentId").GetGuid());

        var waiting = await (await Kiosk(HttpMethod.Get, id.ToString(), s).Expect(200)).Json();
        Assert.False(waiting.GetProperty("finished").GetBoolean());

        var submitted = await (await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, FingerprintBody).Expect(200)).Json();
        Assert.False(submitted.GetProperty("finished").GetBoolean());

        var confirmed = await (await Kiosk(HttpMethod.Post, $"{id}/confirm", s).Expect(200)).Json();
        Assert.True(confirmed.GetProperty("finished").GetBoolean());

        var fingers = await s.Staff.GetAsync($"/api/students/{s.StudentId}/fingerprints").Expect(200);
        var text = await fingers.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Template, text);
        Assert.DoesNotContain("\"template\"", text, StringComparison.OrdinalIgnoreCase);
        var items = (await fingers.Json()).GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(1, items[0].GetProperty("fingerIndex").GetInt32());
        Assert.Equal("AS608", items[0].GetProperty("sensorModel").GetString());
    }

    [Fact]
    public async Task Deleting_an_enrolled_finger_removes_it()
    {
        var s = await NewSetup();
        var id = await StartSession(s);
        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, FingerprintBody).Expect(200);
        await Kiosk(HttpMethod.Post, $"{id}/confirm", s).Expect(200);

        await s.Staff.DeleteAsync($"/api/students/{s.StudentId}/fingerprints/1").Expect(204);
        var items = (await s.Staff.GetAsync($"/api/students/{s.StudentId}/fingerprints").Ok()).GetProperty("items");
        Assert.Equal(0, items.GetArrayLength());
        await s.Staff.DeleteAsync($"/api/students/{s.StudentId}/fingerprints/1").Expect(404, "FINGERPRINT_NOT_FOUND");
    }

    [Fact]
    public async Task Enrolling_a_finger_twice_is_refused()
    {
        var s = await NewSetup();
        var id = await StartSession(s);
        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, FingerprintBody).Expect(200);
        await Kiosk(HttpMethod.Post, $"{id}/confirm", s).Expect(200);

        await s.Staff.PostRawAsync("/api/enrollment/sessions", Start(s, 1)).Expect(409, "FINGER_ALREADY_ENROLLED");
        await StartSession(s, 2); // a different finger is fine
    }

    // ------------------------------------------------------------ starting a session

    [Fact]
    public async Task Start_rejects_a_finger_index_outside_0_to_9()
    {
        var s = await NewSetup();
        await s.Staff.PostRawAsync("/api/enrollment/sessions", Start(s, 10)).Expect(400, "INVALID_FINGER_INDEX");
        await s.Staff.PostRawAsync("/api/enrollment/sessions", Start(s, -1)).Expect(400, "INVALID_FINGER_INDEX");
        await StartSession(s, 0);
        await StartSession(await NewSetup(), 9);
    }

    [Fact]
    public async Task A_second_open_session_for_the_same_student_is_409()
    {
        var s = await NewSetup();
        await StartSession(s);
        await s.Staff.PostRawAsync("/api/enrollment/sessions", Start(s, 2)).Expect(409, "ENROLLMENT_ALREADY_OPEN");
    }

    [Fact]
    public async Task A_station_with_an_open_session_is_busy_for_other_students()
    {
        var s = await NewSetup();
        await StartSession(s);
        var other = await Seed.Student(s.Staff);
        await s.Staff.PostRawAsync("/api/enrollment/sessions",
            $$"""{"studentId":"{{other.Id}}","stationId":"{{s.StationId}}","fingerIndex":1}""").Expect(409, "STATION_BUSY");
    }

    [Fact]
    public async Task Start_rejects_a_room_station_and_unknown_ids()
    {
        var s = await NewSetup();
        var room = await Seed.Room(s.Staff);
        var (reader, _) = await Seed.Station(s.Staff, room, "ROOM_STATION");

        await s.Staff.PostRawAsync("/api/enrollment/sessions", $$"""{"studentId":"{{s.StudentId}}","stationId":"{{reader}}","fingerIndex":1}""")
            .Expect(400, "STATION_NOT_COUNTER_KIOSK");
        await s.Staff.PostRawAsync("/api/enrollment/sessions", $$"""{"studentId":"{{Guid.NewGuid()}}","stationId":"{{s.StationId}}","fingerIndex":1}""")
            .Expect(404, "STUDENT_NOT_FOUND");
        await s.Staff.PostRawAsync("/api/enrollment/sessions", $$"""{"studentId":"{{s.StudentId}}","stationId":"{{Guid.NewGuid()}}","fingerIndex":1}""")
            .Expect(404, "STATION_NOT_FOUND");
    }

    [Fact]
    public async Task Get_an_unknown_session_is_404()
    {
        var s = await NewSetup();
        await s.Staff.GetAsync($"/api/enrollment/sessions/{Guid.NewGuid()}").Expect(404, "SESSION_NOT_FOUND");
    }

    // ------------------------------------------------------------ kiosk side

    [Fact]
    public async Task Kiosk_routes_need_the_station_code_header()
    {
        var s = await NewSetup();
        var id = await StartSession(s);
        var anon = api.NoAuth();

        await anon.GetAsync($"/api/kiosk/enrollment/sessions/{id}").Expect(403, "STATION_NOT_AUTHENTICATED");
        await anon.SendAsync(HttpMethod.Get, $"/api/kiosk/enrollment/sessions/{id}", null, ("X-Station-Code", "WRONG-CODE")).Expect(403, "STATION_NOT_AUTHENTICATED");
        await anon.SendAsync(HttpMethod.Post, $"/api/kiosk/enrollment/sessions/{id}/fingerprint", FingerprintBody, ("X-Station-Code", "WRONG-CODE")).Expect(403);
        await anon.SendAsync(HttpMethod.Post, $"/api/kiosk/enrollment/sessions/{id}/confirm", null, ("X-Station-Code", "WRONG-CODE")).Expect(403);
        await anon.SendAsync(HttpMethod.Post, $"/api/kiosk/enrollment/sessions/{id}/cancel", null, ("X-Station-Code", "WRONG-CODE")).Expect(403);
    }

    [Fact]
    public async Task Kiosk_header_is_case_insensitive()
    {
        var s = await NewSetup();
        var id = await StartSession(s);
        await api.NoAuth().SendAsync(HttpMethod.Get, $"/api/kiosk/enrollment/sessions/{id}", null, ("X-Station-Code", s.StationCode.ToLowerInvariant())).Expect(200);
    }

    [Fact]
    public async Task Kiosk_fingerprint_validates_the_template_sensor_and_quality()
    {
        var s = await NewSetup();
        var id = await StartSession(s);

        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, """{"template":"!!!not base64!!!","sensorModel":"AS608","qualityScore":90}""").Expect(400, "VALIDATION_FAILED");
        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, """{"sensorModel":"AS608","qualityScore":90}""").Expect(400, "VALIDATION_FAILED");
        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, $$"""{"template":"{{Template}}","sensorModel":"BANANA","qualityScore":90}""").Expect(400, "VALIDATION_FAILED");
        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, $$"""{"template":"{{Template}}","sensorModel":"R307","qualityScore":5000}""").Expect(400, "VALIDATION_FAILED");
        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, $$"""{"template":"{{Template}}","sensorModel":"R307"}""").Expect(200); // quality is optional
    }

    [Fact]
    public async Task Kiosk_accepts_the_zk9500_sensor_model()
    {
        var s = await NewSetup();
        var id = await StartSession(s);

        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, $$"""{"template":"{{Template}}","sensorModel":"ZK9500","qualityScore":90}""").Expect(200);
    }

    [Fact]
    public async Task Kiosk_steps_out_of_order_are_409()
    {
        var s = await NewSetup();
        var id = await StartSession(s);

        await Kiosk(HttpMethod.Post, $"{id}/confirm", s).Expect(409, "INVALID_SESSION_STATE"); // nothing submitted yet
        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, FingerprintBody).Expect(200);
        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, FingerprintBody).Expect(409, "INVALID_SESSION_STATE"); // already submitted
        await Kiosk(HttpMethod.Post, $"{id}/confirm", s).Expect(200);
        await Kiosk(HttpMethod.Post, $"{id}/confirm", s).Expect(409, "INVALID_SESSION_STATE"); // already done
    }

    // ------------------------------------------------------------ cancelling

    [Fact]
    public async Task Staff_can_cancel_an_open_session_and_the_student_can_start_again()
    {
        var s = await NewSetup();
        var id = await StartSession(s);

        await s.Staff.PostEmptyAsync($"/api/enrollment/sessions/{id}/cancel").Expect(200);
        var view = await s.Staff.GetAsync($"/api/enrollment/sessions/{id}").Ok();
        Assert.Equal("CANCELLED", view.GetProperty("status").GetString());

        await s.Staff.PostEmptyAsync($"/api/enrollment/sessions/{id}/cancel").Expect(409, "INVALID_SESSION_STATE");
        await StartSession(s);
    }

    [Fact]
    public async Task Kiosk_can_cancel_and_a_cancel_after_the_fingerprint_removes_the_stored_template()
    {
        var s = await NewSetup();
        var id = await StartSession(s);
        await Kiosk(HttpMethod.Post, $"{id}/fingerprint", s, FingerprintBody).Expect(200);

        var cancelled = await (await Kiosk(HttpMethod.Post, $"{id}/cancel", s).Expect(200)).Json();
        Assert.True(cancelled.GetProperty("finished").GetBoolean());
        var items = (await s.Staff.GetAsync($"/api/students/{s.StudentId}/fingerprints").Ok()).GetProperty("items");
        Assert.Equal(0, items.GetArrayLength());
    }

    [Fact]
    public async Task Fingerprint_list_for_an_unknown_student_is_404()
    {
        var s = await NewSetup();
        await s.Staff.GetAsync($"/api/students/{Guid.NewGuid()}/fingerprints").Expect(404, "STUDENT_NOT_FOUND");
    }
}
