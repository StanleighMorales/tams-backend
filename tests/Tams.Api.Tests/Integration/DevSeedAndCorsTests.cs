using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;

namespace Tams.Api.Tests.Integration;

/// <summary>The Development demo seeder (Seed:Demo) and the CORS policy front ends depend on.</summary>
[Collection("api")]
public class DevSeedAndCorsTests(ApiFixture api)
{
    [Fact]
    public async Task Demo_data_is_loaded_and_the_demo_accounts_can_log_in()
    {
        var admin = await api.As("ADMIN");
        Assert.Equal(1, (await admin.GetAsync("/api/rooms?q=STORE-1").Ok()).GetProperty("total").GetInt32());
        Assert.Equal(1, (await admin.GetAsync("/api/assets?q=LT-001").Ok()).GetProperty("total").GetInt32());
        Assert.Equal(1, (await admin.GetAsync("/api/stations?type=COUNTER_KIOSK&roomId=" + await RoomId(admin, "STORE-1")).Ok()).GetProperty("total").GetInt32());
        Assert.Equal(1, (await admin.GetAsync("/api/students?q=2024-0001").Ok()).GetProperty("total").GetInt32());

        foreach (var email in new[] { "tech@tams.test", "custodian@tams.test", "instructor@tams.test", "instructor2@tams.test" })
            await api.NoAuth().PostRawAsync("/api/auth/login", $$"""{"email":"{{email}}","password":"{{DevSeed.Password}}"}""").Expect(200);
    }

    [Fact]
    public async Task Running_the_seeder_again_adds_nothing()
    {
        var admin = await api.As("ADMIN");
        var before = (await admin.GetAsync("/api/rooms?pageSize=100").Ok()).GetProperty("total").GetInt32();

        await api.Services.SeedDemoDataAsync();

        Assert.Equal(before, (await admin.GetAsync("/api/rooms?pageSize=100").Ok()).GetProperty("total").GetInt32());
        Assert.Equal(1, (await admin.GetAsync("/api/rooms?q=STORE-1").Ok()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_demo_borrowable_laptops_have_their_rfid_tags()
    {
        var admin = await api.As("ADMIN");
        var laptop = (await admin.GetAsync("/api/assets?q=LT-001").Ok()).GetProperty("items")[0];
        Assert.Equal("E2000017221101441890", laptop.GetProperty("rfidTagUid").GetString());
    }

    static async Task<Guid> RoomId(ApiClient admin, string code) =>
        (await admin.GetAsync($"/api/rooms?q={code}").Ok()).GetProperty("items")[0].Id();

    // ------------------------------------------------------------ CORS

    [Fact]
    public async Task A_preflight_from_the_vite_dev_server_is_allowed()
    {
        var res = await api.NoAuth().SendAsync(HttpMethod.Options, "/api/assets", null,
            ("Origin", "http://localhost:5173"),
            ("Access-Control-Request-Method", "GET"),
            ("Access-Control-Request-Headers", "authorization,content-type")).Expect(204);
        Assert.Equal("http://localhost:5173", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("authorization", string.Join(",", res.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_real_request_from_an_allowed_origin_gets_the_cors_header()
    {
        var res = await api.NoAuth().SendAsync(HttpMethod.Get, "/api/health", null, ("Origin", "http://localhost:5173")).Expect(200);
        Assert.Equal("http://localhost:5173", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task An_unlisted_origin_gets_no_cors_header()
    {
        var res = await api.NoAuth().SendAsync(HttpMethod.Get, "/api/health", null, ("Origin", "http://evil.example")).Expect(200);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
