using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Tests.Integration;

/// <summary>GET /reports (admin card list) and GET /reports/{key} (detail). Every report runs against the real database so each query translates.</summary>
[Collection("api")]
public class ReportTests(ApiFixture api)
{
    static readonly string[] Keys = ["loans", "anomalies", "violations", "kiosk", "stations", "audits", "events"];

    [Fact]
    public async Task List_returns_a_card_with_a_headline_for_every_report()
    {
        var admin = await api.As("ADMIN");
        var list = await admin.GetAsync("/api/reports").Ok();
        var cards = list.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal(Keys, cards.Select(c => c.GetProperty("key").GetString()));
        Assert.All(cards, c => Assert.False(string.IsNullOrEmpty(c.GetProperty("headline").GetProperty("label").GetString())));
    }

    [Theory]
    [InlineData("loans")] [InlineData("anomalies")] [InlineData("violations")] [InlineData("kiosk")]
    [InlineData("stations")] [InlineData("audits")] [InlineData("events")]
    public async Task Detail_runs_and_returns_stats(string key)
    {
        var admin = await api.As("ADMIN");
        var d = await admin.GetAsync($"/api/reports/{key}").Ok();
        Assert.Equal(key, d.GetProperty("key").GetString());
        Assert.True(d.GetProperty("stats").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Unknown_report_is_404_and_a_reversed_period_is_400()
    {
        var admin = await api.As("ADMIN");
        await admin.GetAsync("/api/reports/nope").Expect(404);
        await admin.GetAsync("/api/reports/loans?from=2026-12-01T00:00:00Z&to=2026-11-01T00:00:00Z").Expect(400);
    }

    [Fact]
    public async Task Only_admin_can_read_reports()
    {
        foreach (var role in new[] { "TECH_STAFF", "CUSTODIAN", "INSTRUCTOR" })
            await (await api.As(role)).GetAsync("/api/reports").Expect(403);
    }

    // ---- numbers: known rows in a window nothing else touches, then the report must add them up

    static double Stat(JsonElement d, string label) =>
        d.GetProperty("stats").EnumerateArray().Single(x => x.GetProperty("label").GetString() == label).GetProperty("value").GetDouble();

    static int Count(JsonElement d, string breakdown, string label) =>
        d.GetProperty("breakdowns").GetProperty(breakdown).EnumerateArray()
            .Where(x => x.GetProperty("label").GetString() == label).Select(x => x.GetProperty("count").GetInt32()).SingleOrDefault();

    static string Period(DateTimeOffset from, DateTimeOffset to) =>
        $"from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}";

    /// <summary>A one-day window far in the future, random per run so a leftover row from an earlier run cannot collide.</summary>
    static DateTimeOffset Window() => new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(Random.Shared.Next(0, 3000));

    [Fact]
    public async Task Anomaly_figures_add_up_the_rows_in_the_period()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin);
        var asset = await Seed.BorrowableAsset(admin, await Seed.Category(admin), room);
        var start = Window();
        Anomaly Row(AnomalyType t, AnomalyStatus s, int atMinute, int? resolvedAfter) => new()
        {
            Id = Guid.NewGuid(), Type = t, Status = s, AssetId = asset.Id, RoomId = room,
            DetectedAt = start.AddMinutes(atMinute), ResolvedAt = resolvedAfter is null ? null : start.AddMinutes(atMinute + resolvedAfter.Value),
        };
        var rows = new[]
        {
            Row(AnomalyType.WrongRoom, AnomalyStatus.Open, 1, null),
            Row(AnomalyType.WrongRoom, AnomalyStatus.Open, 2, null),
            Row(AnomalyType.TransitTimeout, AnomalyStatus.Resolved, 3, 30),
            Row(AnomalyType.UnborrowedTap, AnomalyStatus.Resolved, 24 * 60 + 5, 10), // a day later, outside the window
        };
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        db.Anomalies.AddRange(rows);
        await db.SaveChangesAsync();
        try
        {
            var d = await admin.GetAsync($"/api/reports/anomalies?{Period(start, start.AddHours(1))}").Ok();
            Assert.Equal(3, Stat(d, "Anomalies"));
            Assert.Equal(2, Stat(d, "Open"));
            Assert.Equal(1, Stat(d, "Resolved"));
            Assert.Equal(30, Stat(d, "Avg minutes to resolve"));
            Assert.Equal(2, Count(d, "byType", "WRONG_ROOM"));
            Assert.Equal(1, Count(d, "byType", "TRANSIT_TIMEOUT"));
            Assert.Equal(0, Count(d, "byType", "UNBORROWED_TAP"));
        }
        finally
        {
            await db.Anomalies.Where(a => a.AssetId == asset.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Event_figures_count_each_status_in_the_period()
    {
        var admin = await api.As("ADMIN");
        var (coordinator, _, _) = await Seed.User(admin, "TECH_STAFF", coordinator: true);
        var start = Window();
        Event Row(EventStatus s, int atMinute) => new()
        {
            Id = Guid.NewGuid(), Title = "Report test " + Seed.Tag(), VenueText = "Hall", StartsAt = start.AddMinutes(atMinute),
            EndsAt = start.AddMinutes(atMinute + 60), CoordinatorUserId = coordinator, CreatedByUserId = coordinator, Status = s,
        };
        var rows = new[] { Row(EventStatus.Scheduled, 1), Row(EventStatus.Scheduled, 2), Row(EventStatus.Closed, 3) };
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        db.Events.AddRange(rows);
        await db.SaveChangesAsync();
        try
        {
            var d = await admin.GetAsync($"/api/reports/events?{Period(start, start.AddHours(1))}").Ok();
            Assert.Equal(3, Stat(d, "Events"));
            Assert.Equal(2, Stat(d, "Scheduled"));
            Assert.Equal(1, Stat(d, "Closed"));
            Assert.Equal(0, Stat(d, "Active"));
            Assert.Equal(0, Stat(d, "Event loans"));
        }
        finally
        {
            await db.Events.Where(e => rows.Select(r => r.Id).Contains(e.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task An_empty_period_returns_zeros_not_errors()
    {
        var admin = await api.As("ADMIN");
        var start = Window();
        foreach (var key in new[] { "loans", "anomalies", "violations", "kiosk", "audits", "events" })
        {
            var d = await admin.GetAsync($"/api/reports/{key}?{Period(start, start.AddHours(1))}").Ok();
            Assert.Equal(0, d.GetProperty("stats")[0].GetProperty("value").GetDouble());
        }
    }

    [Fact]
    public async Task Loan_figures_match_the_database_and_the_status_counts_add_up()
    {
        var admin = await api.As("ADMIN");
        var to = DateTimeOffset.UtcNow;
        var from = to.AddYears(-1);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var expected = await db.Loans.CountAsync(l => l.CheckedOutAt >= from && l.CheckedOutAt <= to);
        var d = await admin.GetAsync($"/api/reports/loans?{Period(from, to)}").Ok();
        Assert.Equal(expected, Stat(d, "Loans"));
        var byStatus = d.GetProperty("breakdowns").GetProperty("byStatus").EnumerateArray().Sum(x => x.GetProperty("count").GetInt32());
        Assert.Equal(expected, byStatus);
    }

    [Fact]
    public async Task Station_health_counts_cover_every_station_and_ignore_the_period()
    {
        var admin = await api.As("ADMIN");
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var total = await db.Stations.CountAsync();
        var a = await admin.GetAsync("/api/reports/stations").Ok();
        var start = Window();
        var b = await admin.GetAsync($"/api/reports/stations?{Period(start, start.AddHours(1))}").Ok();
        Assert.Equal(total, Stat(a, "Online") + Stat(a, "Warning") + Stat(a, "Offline stations"));
        Assert.Equal(Stat(a, "Offline stations"), Stat(b, "Offline stations"));
    }
}
