using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Tests.Integration;

/// <summary>E5 (TAMS-123): damage is charged only when staff assign it to the asset's last borrower.</summary>
[Collection("api")]
public class DamageLiabilityTests(ApiFixture api)
{
    async Task<T> Db<T>(Func<TamsDbContext, Task<T>> read)
    {
        using var scope = api.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<TamsDbContext>());
    }

    /// <summary>Returns every loan of the scenario (an asset with an open loan cannot be edited) and sets one asset's condition by hand, like the monthly check.</summary>
    async Task<JsonElement> SetCondition(EventScenario e, int index, string condition)
    {
        await Db(async d => { await d.Loans.Where(l => e.LoanIds.Contains(l.Id) && l.Status == LoanStatus.Active)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LoanStatus.Returned)); return 0; });
        var admin = await api.As("TECH_STAFF");
        var a = await admin.GetAsync($"/api/assets/{e.AssetIds[index]}").Ok();
        string S(string p) => a.GetProperty(p).GetString()!;
        var body = $$"""{"assetCode":"{{S("assetCode")}}","name":"{{S("name")}}","categoryId":"{{S("categoryId")}}","type":"BORROWABLE","rfidTagUid":"{{S("rfidTagUid")}}","homeRoomId":"{{S("homeRoomId")}}","condition":"{{condition}}","replacementCost":{{a.GetProperty("replacementCost").GetDecimal()}}}""";
        return await admin.PutRawAsync($"/api/assets/{e.AssetIds[index]}", body).Ok();
    }

    static string Borrower(string name, string idNo) => $$"""{"name":"{{name}}","idNo":"{{idNo}}"}""";

    [Fact]
    public async Task Marking_an_asset_damaged_shows_the_last_borrower_and_charges_nobody()
    {
        var admin = await api.As("ADMIN");
        var s = await Seed.Student(admin);
        var e = await EventScenario.Create(api);
        await admin.PutRawAsync($"/api/loans/{e.LoanIds[0]}/borrower", Borrower($"First{s.StudentNo} Last{s.StudentNo}", s.StudentNo)).Ok();

        var res = await SetCondition(e, 0, "DAMAGED");

        Assert.Equal("DAMAGED", res.GetProperty("condition").GetString());
        var last = res.GetProperty("lastBorrower");
        Assert.Equal(e.LoanIds[0], last.GetProperty("loan").GetProperty("id").GetGuid());
        Assert.Equal("STUDENT", last.GetProperty("borrower").GetProperty("kind").GetString());
        Assert.Equal(s.Id, last.GetProperty("borrower").GetProperty("studentId").GetGuid());
        Assert.False(last.GetProperty("hasLiability").GetBoolean());
        Assert.False(await Db(d => d.Liabilities.AnyAsync(l => l.AssetId == e.AssetIds[0])));
        var updates = await Db(d => d.AuditLog.AsNoTracking().Where(a => a.EntityId == e.AssetIds[0] && a.Action == "ASSET_UPDATED").ToListAsync());
        Assert.Contains(updates, a => a.After!.Contains("DAMAGED"));

        // Other conditions, and the plain asset read, carry no borrower.
        Assert.False((await SetCondition(e, 0, "FAIR")).TryGetProperty("lastBorrower", out _));
    }

    [Fact]
    public async Task An_asset_never_lent_has_no_last_borrower()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin);
        var asset = await Seed.BorrowableAsset(admin, await Seed.Category(admin), room);
        var a = await admin.GetAsync($"/api/assets/{asset.Id}").Ok();
        var body = $$"""{"assetCode":"{{asset.Code}}","name":"{{a.GetProperty("name").GetString()}}","categoryId":"{{a.GetProperty("categoryId").GetString()}}","type":"BORROWABLE","rfidTagUid":"{{asset.RfidTag}}","homeRoomId":"{{room}}","condition":"DAMAGED","replacementCost":45000}""";

        var res = await admin.PutRawAsync($"/api/assets/{asset.Id}", body).Ok();

        Assert.Equal("DAMAGED", res.GetProperty("condition").GetString());
        Assert.False(res.TryGetProperty("lastBorrower", out _));
    }

    [Fact]
    public async Task Staff_can_assign_damage_to_a_student_an_event_borrower_or_the_coordinator()
    {
        var admin = await api.As("ADMIN");
        var s = await Seed.Student(admin);
        var e = await EventScenario.Create(api, loans: 3);
        await admin.PutRawAsync($"/api/loans/{e.LoanIds[0]}/borrower", Borrower($"First{s.StudentNo} Last{s.StudentNo}", s.StudentNo)).Ok();
        var typed = "Dana" + Seed.Tag();
        await admin.PutRawAsync($"/api/loans/{e.LoanIds[1]}/borrower", Borrower(typed, "EXT-" + Seed.Tag())).Ok();

        var staff = await api.As("TECH_STAFF");
        for (var i = 0; i < 3; i++)
        {
            await SetCondition(e, i, "DAMAGED");
            var made = await staff.PostRawAsync($"/api/assets/{e.AssetIds[i]}/damage-liability",
                $$"""{"loanId":"{{e.LoanIds[i]}}","amount":1200.50,"notes":"Cracked screen"}""").Ok(201);
            Assert.Equal("DAMAGE", made.GetProperty("reason").GetString());
            Assert.Equal("OUTSTANDING", made.GetProperty("status").GetString());
            Assert.Equal(1200.50m, made.GetProperty("amount").GetDecimal());
        }

        var rows = await Db(d => d.Liabilities.AsNoTracking().Where(l => e.LoanIds.Contains(l.LoanId)).ToListAsync());
        Assert.All(rows, l => Assert.Equal(LiabilityReason.Damage, l.Reason));
        Assert.Equal(s.Id, rows.Single(l => l.LoanId == e.LoanIds[0]).StudentId);
        Assert.Equal(typed, rows.Single(l => l.LoanId == e.LoanIds[1]).LiableName);
        Assert.Equal(e.CoordinatorId, rows.Single(l => l.LoanId == e.LoanIds[2]).LiableUserId);
        var ids = rows.Select(l => l.Id).ToList();
        var audits = await Db(d => d.AuditLog.AsNoTracking().Where(a => a.Action == "LIABILITY_CREATED" && ids.Contains(a.EntityId)).ToListAsync());
        Assert.Equal(3, audits.Count);
        Assert.All(audits, a => Assert.Contains("DAMAGE", a.After));

        // Visible in the student's standing and, for the others, in the violations summary.
        var standing = await admin.GetAsync($"/api/students/{s.Id}/standing").Ok();
        var owed = standing.GetProperty("liabilities").EnumerateArray().Single();
        Assert.Equal("DAMAGE", owed.GetProperty("reason").GetString());
        Assert.Equal("OUTSTANDING", owed.GetProperty("status").GetString());
        var others = (await admin.GetAsync("/api/violations").Ok()).GetProperty("otherLiabilities").EnumerateArray().ToList();
        Assert.Contains(others, x => x.GetProperty("liableName").GetString() == typed && x.GetProperty("reason").GetString() == "DAMAGE");
        Assert.Contains(others, x => x.GetProperty("liableUserId").ValueKind == JsonValueKind.String
            && x.GetProperty("liableUserId").GetGuid() == e.CoordinatorId);

        // Settling uses the existing endpoint.
        await admin.PostRawAsync($"/api/liabilities/{owed.GetProperty("id").GetGuid()}/settle", """{"status":"WAIVED"}""").Ok();
    }

    [Fact]
    public async Task A_damage_liability_flags_the_student_at_their_next_borrowing()
    {
        var admin = await api.As("ADMIN");
        var s = await Seed.Student(admin);
        var first = await EventScenario.Create(api);
        var name = $"First{s.StudentNo} Last{s.StudentNo}";
        await admin.PutRawAsync($"/api/loans/{first.LoanIds[0]}/borrower", Borrower(name, s.StudentNo)).Ok();
        await SetCondition(first, 0, "DAMAGED");
        await admin.PostRawAsync($"/api/assets/{first.AssetIds[0]}/damage-liability", $$"""{"loanId":"{{first.LoanIds[0]}}","amount":500}""").Ok(201);

        var second = await EventScenario.Create(api);
        var refused = await admin.PutRawAsync($"/api/loans/{second.LoanIds[0]}/borrower", Borrower(name, s.StudentNo)).Ok(409, "BORROWER_FLAGGED");
        Assert.Contains("UNSETTLED_LIABILITY", refused.GetProperty("check").GetProperty("flags").ToString());
    }

    [Fact]
    public async Task Without_an_assignment_nothing_is_charged_and_bad_requests_are_refused()
    {
        var admin = await api.As("ADMIN");
        var e = await EventScenario.Create(api, loans: 2);
        var url = $"/api/assets/{e.AssetIds[0]}/damage-liability";
        string Req(Guid loan, string amount = "300") => $$"""{"loanId":"{{loan}}","amount":{{amount}}}""";

        // Not damaged yet.
        await admin.PostRawAsync(url, Req(e.LoanIds[0])).Expect(409, "ASSET_NOT_DAMAGED");
        await SetCondition(e, 0, "DAMAGED");

        await admin.PostRawAsync(url, """{"amount":300}""").Expect(400, "VALIDATION_FAILED");
        await admin.PostRawAsync(url, Req(e.LoanIds[0], "0")).Expect(400, "VALIDATION_FAILED");
        await admin.PostRawAsync(url, Req(e.LoanIds[1])).Expect(404, "LOAN_NOT_FOUND"); // another asset's loan
        await admin.PostRawAsync(url, Req(Guid.NewGuid())).Expect(404, "LOAN_NOT_FOUND");
        await admin.PostRawAsync($"/api/assets/{Guid.NewGuid()}/damage-liability", Req(e.LoanIds[0])).Expect(404, "ASSET_NOT_FOUND");
        Assert.False(await Db(d => d.Liabilities.AnyAsync(l => l.AssetId == e.AssetIds[0])));

        await admin.PostRawAsync(url, Req(e.LoanIds[0])).Expect(201);
        await admin.PostRawAsync(url, Req(e.LoanIds[0])).Expect(409, "LIABILITY_EXISTS");
    }

    [Fact]
    public async Task Only_admin_and_technical_staff_can_assign_damage()
    {
        var e = await EventScenario.Create(api);
        foreach (var role in new[] { "CUSTODIAN", "INSTRUCTOR" })
            await (await api.As(role)).PostRawAsync($"/api/assets/{e.AssetIds[0]}/damage-liability", $$"""{"loanId":"{{e.LoanIds[0]}}","amount":300}""").Expect(403);
    }
}
