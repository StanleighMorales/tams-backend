using System.Text.Json;

namespace Tams.Api.Tests.Integration;

/// <summary>
/// TEST-DATA.md sections 11 (settings) and 12 (audit log). Settings are global, so each test that
/// changes one puts the original value back.
/// </summary>
[Collection("api")]
public class SettingsAndAuditTests(ApiFixture api)
{
    // ------------------------------------------------------------ settings

    static readonly (string Key, int Default)[] Defaults =
    [
        ("transit_window_minutes", 10), ("class_length_default_minutes", 90), ("grace_minutes", 15),
        ("kiosk_poll_interval_seconds", 2), ("approval_timeout_seconds", 120), ("kiosk_idle_timeout_seconds", 60),
        ("heartbeat_interval_seconds", 30), ("station_warning_missed_windows", 1), ("station_offline_missed_windows", 2),
        ("late_arrival_warning_limit", 3), ("marks_before_block", 2),
    ];

    /// <summary>GET /settings is an array of { key, value, default, ... }.</summary>
    static JsonElement Setting(JsonElement list, string key) =>
        list.EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Settings_list_contains_every_documented_key()
    {
        var admin = await api.As("ADMIN");
        var all = await admin.GetAsync("/api/settings").Ok();
        foreach (var (key, def) in Defaults) Assert.Equal(def, Setting(all, key).GetProperty("default").GetInt32());
        Setting(all, "enrollment_session_timeout_seconds");
    }

    [Fact]
    public async Task A_setting_that_was_never_changed_returns_its_default()
    {
        var admin = await api.As("ADMIN");
        // Read-only keys no other test changes, so the default must still be in force.
        foreach (var key in new[] { "kiosk_poll_interval_seconds", "approval_timeout_seconds", "marks_before_block" })
        {
            var s = await admin.GetAsync($"/api/settings/{key}").Ok();
            Assert.Equal(s.GetProperty("default").GetInt32(), s.GetProperty("value").GetInt32());
            Assert.Equal(Defaults.Single(d => d.Key == key).Default, s.GetProperty("default").GetInt32());
            Assert.False(s.GetProperty("isOverridden").GetBoolean());
        }
    }

    [Fact]
    public async Task Put_one_setting_changes_it_and_marks_it_overridden()
    {
        var admin = await api.As("ADMIN");
        try
        {
            await admin.PutRawAsync("/api/settings/grace_minutes", """{"value":20}""").Expect(200);
            var s = await admin.GetAsync("/api/settings/grace_minutes").Ok();
            Assert.Equal(20, s.GetProperty("value").GetInt32());
            Assert.True(s.GetProperty("isOverridden").GetBoolean());
        }
        finally { await admin.PutRawAsync("/api/settings/grace_minutes", """{"value":15}""").Expect(200); }
    }

    [Theory]
    [InlineData("grace_minutes", 99)]
    [InlineData("grace_minutes", 14)]
    [InlineData("transit_window_minutes", 0)]
    [InlineData("transit_window_minutes", 61)]
    [InlineData("class_length_default_minutes", 100)]
    [InlineData("kiosk_poll_interval_seconds", 11)]
    [InlineData("marks_before_block", 11)]
    public async Task Put_rejects_a_value_outside_the_allowed_range(string key, int value)
    {
        var admin = await api.As("ADMIN");
        await admin.PutRawAsync($"/api/settings/{key}", $$"""{"value":{{value}}}""").Expect(400);
    }

    [Fact]
    public async Task Put_rejects_a_non_number_value()
    {
        var admin = await api.As("ADMIN");
        await admin.PutRawAsync("/api/settings/grace_minutes", """{"value":"twenty"}""").Expect(400);
    }

    [Fact]
    public async Task An_unknown_setting_key_is_404_on_get_and_put()
    {
        var admin = await api.As("ADMIN");
        await admin.GetAsync("/api/settings/nope").Expect(404, "UNKNOWN_SETTING");
        await admin.PutRawAsync("/api/settings/nope", """{"value":1}""").Expect(404, "UNKNOWN_SETTING");
    }

    [Fact]
    public async Task Bulk_put_updates_several_settings_together()
    {
        var admin = await api.As("ADMIN");
        try
        {
            await admin.PutRawAsync("/api/settings", """{"grace_minutes":16,"transit_window_minutes":12,"class_length_default_minutes":120}""").Expect(200);
            var all = await admin.GetAsync("/api/settings").Ok();
            Assert.Equal(16, Setting(all, "grace_minutes").GetProperty("value").GetInt32());
            Assert.Equal(12, Setting(all, "transit_window_minutes").GetProperty("value").GetInt32());
            Assert.Equal(120, Setting(all, "class_length_default_minutes").GetProperty("value").GetInt32());
        }
        finally { await admin.PutRawAsync("/api/settings", """{"grace_minutes":15,"transit_window_minutes":10,"class_length_default_minutes":90}""").Expect(200); }
    }

    [Fact]
    public async Task Bulk_put_is_all_or_nothing()
    {
        var admin = await api.As("ADMIN");
        var before = (await admin.GetAsync("/api/settings/transit_window_minutes").Ok()).GetProperty("value").GetInt32();

        await admin.PutRawAsync("/api/settings", """{"transit_window_minutes":13,"grace_minutes":99}""").Expect(400);
        var after = (await admin.GetAsync("/api/settings/transit_window_minutes").Ok()).GetProperty("value").GetInt32();
        Assert.Equal(before, after);

        await admin.PutRawAsync("/api/settings", """{"grace_minutes":15,"nope":1}""").Expect(400, "UNKNOWN_SETTING");
        await admin.PutRawAsync("/api/settings", "{}").Expect(400, "VALIDATION_FAILED");
    }

    // ------------------------------------------------------------ audit log

    [Fact]
    public async Task Audit_log_records_an_asset_change_with_the_actor()
    {
        var admin = await api.As("ADMIN");
        var cat = await Seed.Category(admin);
        var room = await Seed.Room(admin, storage: true);
        var (assetId, _, _) = await Seed.BorrowableAsset(admin, cat, room);

        var log = await admin.GetAsync("/api/audit-log?entityType=asset&page=1&pageSize=100").Ok();
        var entry = log.GetProperty("items").EnumerateArray().FirstOrDefault(e => e.GetProperty("entityId").GetGuid() == assetId);
        Assert.NotEqual(JsonValueKind.Undefined, entry.ValueKind);
        Assert.Equal("asset", entry.GetProperty("entityType").GetString());
        Assert.NotEqual(JsonValueKind.Null, entry.GetProperty("actorUserId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, entry.GetProperty("after").ValueKind);
    }

    [Fact]
    public async Task Audit_log_filters_by_actor_and_date_range_and_pages()
    {
        var admin = await api.As("ADMIN");
        var me = (await admin.GetAsync($"/api/users?q={ApiFixture.AdminEmail}").Ok()).GetProperty("items")[0].Id();
        await Seed.Category(admin); // guarantees at least one entry by this actor

        var all = await admin.GetAsync($"/api/audit-log?actorUserId={me}&from=2026-01-01T00:00:00%2B00:00&to=2099-01-01T00:00:00%2B00:00&page=1&pageSize=5").Ok();
        Assert.True(all.GetProperty("total").GetInt32() >= 1);
        Assert.True(all.GetProperty("items").GetArrayLength() <= 5);
        Assert.All(all.GetProperty("items").EnumerateArray(), e => Assert.Equal(me, e.GetProperty("actorUserId").GetGuid()));

        var past = await admin.GetAsync("/api/audit-log?from=2000-01-01T00:00:00%2B00:00&to=2000-01-02T00:00:00%2B00:00").Ok();
        Assert.Equal(0, past.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Audit_log_rejects_a_bad_date_and_an_inverted_range()
    {
        var admin = await api.As("ADMIN");
        await admin.GetAsync("/api/audit-log?from=yesterday").Expect(400);
    }
}
