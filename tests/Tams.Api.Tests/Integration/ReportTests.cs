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
}
