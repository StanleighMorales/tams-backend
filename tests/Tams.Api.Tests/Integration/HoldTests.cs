using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-126: a hold survives the return of the item and only staff lift it (FR-6.5 with FR-6.8).</summary>
[Collection("api")]
public class HoldTests(ApiFixture api)
{
    [Fact]
    public async Task A_hold_stays_after_the_return_until_staff_lift_it()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (stationId, _) = await Seed.Station(admin, room);
        var (assetId, _, _) = await Seed.BorrowableAsset(admin, await Seed.Category(admin), room);
        var (studentId, studentNo, _, _) = await Seed.Student(admin);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var session = new CheckoutSession { Id = Guid.NewGuid(), StationId = stationId, Type = SessionType.Checkout, Status = SessionStatus.Completed, ExpiresAt = now, CreatedAt = now };
        var loan = new Loan
        {
            Id = Guid.NewGuid(), LoanNo = "L-" + Seed.Tag(), Type = LoanType.Event, GraceMinutes = 15, AssetId = assetId, StudentId = studentId,
            SessionId = session.Id, CheckoutStationId = stationId, Status = LoanStatus.Returned, CustodyState = CustodyState.Verified,
            CheckedOutAt = now.AddHours(-3), ClassEndsAt = now.AddHours(-2), DueAt = now.AddHours(-2), OverdueAt = now.AddHours(-2), ReturnedAt = now,
        };
        var hold = new ProfileHold { Id = Guid.NewGuid(), StudentId = studentId, LoanId = loan.Id, PlacedAt = now.AddHours(-2) };
        db.AddRange(session, loan, hold);
        await db.SaveChangesAsync();
        (await db.Students.FindAsync(studentId))!.HasActiveHold = true;
        await db.SaveChangesAsync();

        var staff = await api.As("TECH_STAFF");
        bool Listed(JsonElement b) => b.GetProperty("students").EnumerateArray()
            .Any(s => s.GetProperty("studentNo").GetString() == studentNo && s.GetProperty("hasActiveHold").GetBoolean());

        // The loan is already returned, yet the student is still listed with the hold.
        Assert.True(Listed(await staff.GetAsync("/api/violations").Ok()));

        await staff.PostRawAsync($"/api/holds/{hold.Id}/lift", """{"reason":"Item checked, cleared by staff"}""").Ok();

        Assert.False(Listed(await staff.GetAsync("/api/violations").Ok()));
    }
}
