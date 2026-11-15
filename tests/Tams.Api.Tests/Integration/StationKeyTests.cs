using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;
using Tams.Api.Enrollment;

namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-127: room stations prove themselves with X-Station-Key on scans and heartbeats.</summary>
[Collection("api")]
public class StationKeyTests(ApiFixture api)
{
    const string Scan = """{"tagUid":"04AABBCCDD"}""";

    async Task<(Guid Id, string Code, string Key)> RoomStation(ApiClient admin)
    {
        var room = await Seed.Room(admin);
        var (id, code) = await Seed.Station(admin, room, "ROOM_STATION");
        var key = (await admin.PostEmptyAsync($"/api/stations/{id}/rotate-key").Ok()).GetProperty("key").GetString()!;
        return (id, code, key);
    }

    Task<HttpResponseMessage> Post(string code, string path, string? key, string body = Scan) =>
        api.NoAuth().SendAsync(HttpMethod.Post, $"/api/stations/{code}/{path}", body,
            key is null ? [] : [(StationAuth.StationKeyHeader, key)]);

    [Fact]
    public async Task Creating_a_room_station_returns_the_key_once()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin);
        var code = "RS-" + Seed.Tag();
        var created = await admin.PostRawAsync("/api/stations",
            $$"""{"stationCode":"{{code}}","type":"ROOM_STATION","roomId":"{{room}}","name":"Reader"}""").Ok(201);
        var key = created.GetProperty("key").GetString()!;
        Assert.Equal(64, key.Length);
        Assert.True(created.GetProperty("hasKey").GetBoolean());

        var again = await admin.GetAsync($"/api/stations/{created.Id()}").Ok();
        Assert.Equal(JsonValueKind.Null, again.GetProperty("key").ValueKind);
        Assert.True(again.GetProperty("hasKey").GetBoolean());

        await Post(code, "scans", key).Expect(200);

        // Stored hashed, never as the secret, and the audit rows do not carry it either.
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var station = await db.Stations.AsNoTracking().SingleAsync(s => s.Id == created.Id());
        Assert.Equal(StationAuth.HashKey(key), station.KeyHash);
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityId == station.Id).ToListAsync();
        Assert.Contains(audit, a => a.Action == "STATION_KEY_CREATED");
        Assert.DoesNotContain(audit, a => (a.After ?? "").Contains(key));
    }

    [Fact]
    public async Task A_counter_kiosk_gets_no_key()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (id, _) = await Seed.Station(admin, room);
        var kiosk = await admin.GetAsync($"/api/stations/{id}").Ok();
        Assert.False(kiosk.GetProperty("hasKey").GetBoolean());
        await admin.PostEmptyAsync($"/api/stations/{id}/rotate-key").Expect(400, "STATION_KEY_NOT_APPLICABLE");
    }

    [Fact]
    public async Task Valid_wrong_and_missing_keys()
    {
        var admin = await api.As("ADMIN");
        var (_, code, key) = await RoomStation(admin);

        await Post(code, "scans", key).Expect(200);
        await Post(code, "heartbeat", key, "{}").Expect(200);

        await Post(code, "scans", new string('0', 64)).Expect(403, "STATION_NOT_AUTHENTICATED");
        await Post(code, "heartbeat", "nope", "{}").Expect(403, "STATION_NOT_AUTHENTICATED");

        // Missing key: allowed during the grace period (default), refused once keys are required.
        await Post(code, "scans", null).Expect(200);
        await admin.PutRawAsync("/api/settings/station_key_required", """{"value":1}""").Expect(200);
        try
        {
            await Post(code, "scans", null).Expect(403, "STATION_NOT_AUTHENTICATED");
            await Post(code, "heartbeat", null, "{}").Expect(403, "STATION_NOT_AUTHENTICATED");
            await Post(code, "scans", key).Expect(200);
        }
        finally { await admin.PutRawAsync("/api/settings/station_key_required", """{"value":0}""").Expect(200); }
    }

    [Fact]
    public async Task A_rotated_key_replaces_the_old_one_and_is_audited()
    {
        var admin = await api.As("ADMIN");
        var (id, code, oldKey) = await RoomStation(admin);

        var next = (await admin.PostEmptyAsync($"/api/stations/{id}/rotate-key").Ok()).GetProperty("key").GetString()!;
        Assert.NotEqual(oldKey, next);

        await Post(code, "scans", oldKey).Expect(403, "STATION_NOT_AUTHENTICATED");
        await Post(code, "scans", next).Expect(200);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityId == id).ToListAsync();
        Assert.Equal(2, audit.Count(a => a.Action == "STATION_KEY_ROTATED"));
    }

    [Fact]
    public async Task A_deleted_station_is_refused_even_with_its_key()
    {
        var admin = await api.As("ADMIN");
        var (id, code, key) = await RoomStation(admin);
        await admin.DeleteAsync($"/api/stations/{id}").Expect(204);
        await Post(code, "scans", key).Expect(403, "STATION_NOT_AUTHENTICATED");
        await admin.PostEmptyAsync($"/api/stations/{id}/rotate-key").Expect(404, "STATION_NOT_FOUND");
    }

    [Fact]
    public async Task Only_admin_and_tech_staff_rotate_keys()
    {
        var (id, _, _) = await RoomStation(await api.As("ADMIN"));
        await (await api.As("TECH_STAFF")).PostEmptyAsync($"/api/stations/{id}/rotate-key").Expect(200);
        await (await api.As("CUSTODIAN")).PostEmptyAsync($"/api/stations/{id}/rotate-key").Expect(403);
        await api.NoAuth().PostEmptyAsync($"/api/stations/{id}/rotate-key").Expect(401);
    }
}
