namespace Tams.Api.Tests.Integration;

/// <summary>
/// TEST-DATA.md section 13 and src/Tams.Api/Auth/PERMISSIONS.md. Each row is one (role, request)
/// pair; "allowed" means the role gets past authorization (2xx), "denied" means exactly 403.
/// Requests are read-only or use ids that do not exist, so a 404 also counts as "allowed":
/// authorization runs before the handler looks anything up.
/// </summary>
[Collection("api")]
public class RoleMatrixTests(ApiFixture api)
{
    static readonly Guid Missing = Guid.NewGuid();

    // (role, method, url, body, allowed)
    public static TheoryData<string, string, string, string?, bool> Rows()
    {
        var d = new TheoryData<string, string, string, string?, bool>();
        void Row(string role, string method, string url, bool allowed, string? body = null) => d.Add(role, method, url, body, allowed);

        // Reads: who may see what.
        foreach (var (url, a, t, c, i) in new[]
        {
            ("/api/users", true, true, false, false),
            ("/api/rooms", true, true, true, true),
            ("/api/assets", true, true, true, false),
            ("/api/asset-categories", true, true, true, false),
            ("/api/students", true, true, true, false),
            ("/api/sections", true, true, true, false),
            ("/api/stations", true, true, true, false),
            ("/api/stations/health", true, true, true, false),
            ("/api/auth-cards", true, true, true, false),
            ("/api/settings", true, true, true, false),
            ("/api/audit-log", true, false, false, false),
            ("/api/reports", true, false, false, false),
        })
        {
            Row("ADMIN", "GET", url, a); Row("TECH_STAFF", "GET", url, t);
            Row("CUSTODIAN", "GET", url, c); Row("INSTRUCTOR", "GET", url, i);
        }

        // Writes: staff only, or admin only.
        var room = """{"code":"X","name":"X","building":"B","floor":1,"isStorage":false}""";
        foreach (var role in new[] { "TECH_STAFF", "ADMIN" }) Row(role, "POST", "/api/rooms", true, room); // reaches the handler (201/409/400 all pass auth)
        foreach (var role in new[] { "CUSTODIAN", "INSTRUCTOR" }) Row(role, "POST", "/api/rooms", false, room);

        foreach (var (role, allowed) in new[] { ("ADMIN", true), ("TECH_STAFF", false), ("CUSTODIAN", false), ("INSTRUCTOR", false) })
        {
            Row(role, "POST", "/api/users", allowed, "{}");
            Row(role, "PUT", "/api/settings/grace_minutes", allowed, """{"value":15}""");
            Row(role, "POST", $"/api/assets/{Missing}/restore", allowed);
            Row(role, "POST", $"/api/users/{Missing}/deactivate", allowed);
            Row(role, "DELETE", $"/api/users/{Missing}", allowed);
        }

        foreach (var (role, allowed) in new[] { ("ADMIN", true), ("TECH_STAFF", true), ("CUSTODIAN", false), ("INSTRUCTOR", false) })
        {
            Row(role, "POST", "/api/asset-categories", allowed, "{}");
            Row(role, "POST", "/api/assets", allowed, "{}");
            Row(role, "DELETE", $"/api/assets/{Missing}", allowed);
            Row(role, "POST", "/api/stations", allowed, "{}");
            Row(role, "POST", "/api/sections", allowed, "{}");
            Row(role, "POST", "/api/auth-cards", allowed, "{}");
        }

        // Enrollment: admin, tech staff and custodian; never instructors.
        foreach (var (role, allowed) in new[] { ("ADMIN", true), ("TECH_STAFF", true), ("CUSTODIAN", true), ("INSTRUCTOR", false) })
        {
            Row(role, "GET", $"/api/enrollment/sessions/{Missing}", allowed);
            Row(role, "GET", $"/api/students/{Missing}/fingerprints", allowed);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Role_is_allowed_or_denied(string role, string method, string url, string? body, bool allowed)
    {
        var client = await api.As(role);
        var res = await client.SendAsync(new HttpMethod(method), url, body);
        var body2 = await res.Content.ReadAsStringAsync();

        if (allowed)
            Assert.True((int)res.StatusCode is not (401 or 403),
                $"{role} {method} {url} should pass authorization but got {(int)res.StatusCode}: {body2}");
        else
            Assert.True((int)res.StatusCode == 403, $"{role} {method} {url} should be 403 but got {(int)res.StatusCode}: {body2}");
    }

    [Fact]
    public async Task Soft_deleted_rows_are_hidden_from_a_custodian_and_the_include_deleted_flag_is_403()
    {
        var admin = await api.As("ADMIN");
        var custodian = await api.As("CUSTODIAN");
        var room = await Seed.Room(admin);
        await admin.DeleteAsync($"/api/rooms/{room}").Expect(204);

        await custodian.GetAsync($"/api/rooms/{room}").Expect(404);
        await custodian.GetAsync("/api/rooms?includeDeleted=true").Expect(403);
        await (await api.As("TECH_STAFF")).GetAsync("/api/rooms?includeDeleted=true").Expect(200);
    }

    [Fact]
    public async Task Public_routes_need_no_token_and_everything_else_is_401()
    {
        var anon = api.NoAuth();
        await anon.GetAsync("/api/health").Expect(200);
        await anon.PostRawAsync("/api/auth/login", "{}").Expect(400);   // reached the handler, so not 401
        await anon.PostRawAsync("/api/auth/refresh", "{}").Expect(400); // same
        await anon.GetAsync("/api/rooms").Expect(401);
        await anon.PostRawAsync("/api/auth/logout", "{}").Expect(401);
        await anon.PostRawAsync("/api/auth/change-password", "{}").Expect(401);
    }
}
