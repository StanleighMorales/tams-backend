using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Kiosk;
using Tams.Api.Live;
using Tams.Api.Loans;
using Tams.Api.Settings;

namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-124: wrong-room, unborrowed, transit-timeout and station-offline notices reach staff, with full details.</summary>
[Collection("api")]
public class StaffAlertTests(ApiFixture api)
{
    sealed record Setup(Asset Asset, Station Station, Room Dest, Guid InstructorId);

    async Task<Asset> NewAsset(ApiClient admin, Guid room)
    {
        var (id, _, _) = await Seed.BorrowableAsset(admin, await Seed.Category(admin), room);
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TamsDbContext>().Assets.AsNoTracking().FirstAsync(a => a.Id == id);
    }

    async Task<(Setup S, Guid StationRoomId)> Arrange()
    {
        var admin = await api.As("ADMIN");
        var dest = await Seed.Room(admin);
        var other = await Seed.Room(admin);
        var (stationId, _) = await Seed.Station(admin, other, "ROOM_STATION");
        var asset = await NewAsset(admin, dest);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        return (new(asset, await db.Stations.AsNoTracking().FirstAsync(s => s.Id == stationId),
            await db.Rooms.AsNoTracking().FirstAsync(r => r.Id == dest), api.UserIds["INSTRUCTOR"]), other);
    }

    /// <summary>An open loan inserted directly; EVENT loans need no photo or student.</summary>
    static async Task<Loan> AddLoan(TamsDbContext db, Setup s, Asset asset, Guid? eventId, Guid? studentId, DateTimeOffset now,
        CustodyState custody = CustodyState.AwaitingArrival, DateTimeOffset? due = null)
    {
        var session = new CheckoutSession
        {
            Id = Guid.NewGuid(), StationId = s.Station.Id, Type = SessionType.Checkout, Status = SessionStatus.Completed,
            ExpiresAt = now, CreatedAt = now,
        };
        db.CheckoutSessions.Add(session);
        Guid? photo = null;
        if (studentId is not null)
        {
            var m = new MediaFile { Id = Guid.NewGuid(), Kind = MediaKind.CheckoutPhoto, StoragePath = "x", MimeType = "image/png", SizeBytes = 1, Sha256 = new string('a', 64), CreatedAt = now };
            db.MediaFiles.Add(m);
            photo = m.Id;
        }
        var loan = new Loan
        {
            Id = Guid.NewGuid(), LoanNo = "L-" + Seed.Tag(), Type = eventId is null ? LoanType.Standard : LoanType.Event, AssetId = asset.Id,
            StudentId = studentId, ApprovedByUserId = eventId is null ? s.InstructorId : null, CoordinatorUserId = eventId is null ? null : s.InstructorId,
            EventId = eventId, SessionId = session.Id, CheckoutStationId = s.Station.Id, DestinationRoomId = s.Dest.Id, PhotoFileId = photo,
            Status = LoanStatus.Active, CustodyState = custody, CheckedOutAt = now.AddMinutes(-30), TransitDeadlineAt = now.AddMinutes(-1),
            ClassLengthMinutes = 90, ClassEndsAt = now.AddHours(1), GraceMinutes = 15, DueAt = due ?? now.AddHours(1),
        };
        db.Loans.Add(loan);
        await db.SaveChangesAsync();
        return loan;
    }

    static List<Notification> For(TamsDbContext db, Guid loanId) => db.Notifications.AsNoTracking().Where(n => n.LoanId == loanId).ToList();

    [Fact]
    public async Task A_wrong_room_scan_notifies_the_instructor_and_each_staff_user_once_with_details()
    {
        var admin = await api.As("ADMIN");
        var (s, _) = await Arrange();
        var student = await Seed.Student(admin);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var loan = await AddLoan(db, s, s.Asset, null, student.Id, now);

        await DeviceEndpoints.RaiseAsync(db, new Outbox(), AnomalyType.WrongRoom, s.Asset, loan, s.Station, now, default);
        await db.SaveChangesAsync();
        await DeviceEndpoints.RaiseAsync(db, new Outbox(), AnomalyType.WrongRoom, s.Asset, loan, s.Station, now, default); // open anomaly: deduped
        await db.SaveChangesAsync();

        var rows = For(db, loan.Id);
        var recipients = rows.Select(r => r.RecipientUserId).ToList();
        Assert.Contains(api.UserIds["TECH_STAFF"], recipients);
        Assert.Contains(api.UserIds["CUSTODIAN"], recipients);
        Assert.Single(rows, r => r.RecipientUserId == s.InstructorId);
        Assert.Equal(recipients.Count, recipients.Distinct().Count());
        Assert.DoesNotContain(rows, r => r.RecipientUserId == api.UserIds.GetValueOrDefault("ADMIN"));
        var d = JsonDocument.Parse(rows.First(r => r.RecipientUserId == api.UserIds["TECH_STAFF"]).Payload!).RootElement.GetProperty("details");
        Assert.Equal(loan.LoanNo, d.GetProperty("loanNo").GetString());
        Assert.Equal(s.Asset.AssetCode, d.GetProperty("assetCode").GetString());
        Assert.Equal(student.StudentNo, d.GetProperty("studentNo").GetString());
        Assert.Equal(s.Station.StationCode, d.GetProperty("station").GetProperty("stationCode").GetString());
        Assert.Equal($"/api/loans/{loan.Id}/photo", d.GetProperty("photoUrl").GetString());
        // The instructor's notice carries the same details.
        Assert.True(JsonDocument.Parse(rows.Single(r => r.RecipientUserId == s.InstructorId).Payload!).RootElement.TryGetProperty("details", out _));
    }

    [Fact]
    public async Task An_unborrowed_tap_goes_to_staff_only()
    {
        var (s, _) = await Arrange();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var now = DateTimeOffset.UtcNow;

        await DeviceEndpoints.RaiseAsync(db, new Outbox(), AnomalyType.UnborrowedTap, s.Asset, null, s.Station, now, default);
        await db.SaveChangesAsync();

        var rows = db.Notifications.AsNoTracking().Where(n => n.Type == "ANOMALY_RAISED" && n.Body.Contains(s.Asset.AssetCode)).ToList();
        Assert.Contains(rows, r => r.RecipientUserId == api.UserIds["TECH_STAFF"]);
        Assert.Contains(rows, r => r.RecipientUserId == api.UserIds["CUSTODIAN"]);
        Assert.DoesNotContain(rows, r => r.RecipientUserId == s.InstructorId);
    }

    [Fact]
    public async Task A_transit_timeout_on_an_event_loan_carries_event_coordinator_and_other_items_out()
    {
        var admin = await api.As("ADMIN");
        var (s, _) = await Arrange();
        var second = await NewAsset(admin, s.Dest.Id);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var evt = new Event
        {
            Id = Guid.NewGuid(), Title = "Foundation Day " + Seed.Tag(), VenueText = "Gym", StartsAt = now.AddHours(-1), EndsAt = now.AddHours(3),
            CoordinatorUserId = s.InstructorId, Status = EventStatus.Active, CreatedByUserId = s.InstructorId, CreatedAt = now, UpdatedAt = now,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var loan = await AddLoan(db, s, s.Asset, evt.Id, null, now);
        var other = await AddLoan(db, s, second, evt.Id, null, now, CustodyState.Verified);

        Assert.True(await LoanRules.TransitTimeoutAsync(db, scope.ServiceProvider.GetRequiredService<ISettings>(), new Outbox(), loan.Id, now, default));

        var rows = For(db, loan.Id);
        Assert.Contains(rows, r => r.Type == "TRANSIT_TIMEOUT" && r.RecipientUserId == api.UserIds["TECH_STAFF"]);
        Assert.Contains(rows, r => r.Type == "TRANSIT_TIMEOUT" && r.RecipientUserId == api.UserIds["CUSTODIAN"]);
        var mine = rows.Single(r => r.Type == "LATE_ARRIVAL_WARNING");
        Assert.Equal(s.InstructorId, mine.RecipientUserId);
        var staff = rows.First(r => r.Type == "TRANSIT_TIMEOUT");
        Assert.Contains(evt.Title, staff.Body);
        var e = JsonDocument.Parse(staff.Payload!).RootElement.GetProperty("details").GetProperty("event");
        Assert.Equal(evt.Title, e.GetProperty("title").GetString());
        Assert.Equal("INSTRUCTOR Test", e.GetProperty("coordinator").GetString());
        Assert.Equal(other.LoanNo, e.GetProperty("otherItemsOut")[0].GetProperty("loanNo").GetString());
    }

    [Fact]
    public async Task An_overdue_notice_stays_instructor_only_but_gains_details()
    {
        var admin = await api.As("ADMIN");
        var (s, _) = await Arrange();
        var student = await Seed.Student(admin);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var loan = await AddLoan(db, s, s.Asset, null, student.Id, now, CustodyState.Verified, now.AddMinutes(-1));

        Assert.True(await LoanRules.OverdueAsync(db, scope.ServiceProvider.GetRequiredService<ISettings>(), new Outbox(), loan.Id, now, default));

        var row = Assert.Single(For(db, loan.Id), r => r.Type == "LOAN_OVERDUE");
        Assert.Equal(s.InstructorId, row.RecipientUserId);
        Assert.Equal(loan.LoanNo, JsonDocument.Parse(row.Payload!).RootElement.GetProperty("details").GetProperty("loanNo").GetString());
        Assert.DoesNotContain(For(db, loan.Id), r => r.RecipientUserId == api.UserIds["TECH_STAFF"]);
    }

    [Fact]
    public async Task A_station_going_offline_notifies_technical_personnel_and_custodians()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin);
        var (stationId, _) = await Seed.Station(admin, room, "ROOM_STATION");
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        await db.Stations.Where(x => x.Id == stationId).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Health, StationHealth.Online).SetProperty(x => x.LastHeartbeatAt, DateTimeOffset.UtcNow.AddHours(-1)));

        // The sweeper recomputes station health every 5 s.
        List<Notification> rows = [];
        for (var i = 0; i < 30 && rows.Count < 2; i++)
        {
            await Task.Delay(1000);
            rows = db.Notifications.AsNoTracking().Where(n => n.Type == "STATION_OFFLINE").ToList().Where(n => n.Payload!.Contains(stationId.ToString())).ToList();
        }

        Assert.Contains(rows, r => r.RecipientUserId == api.UserIds["TECH_STAFF"]);
        Assert.Contains(rows, r => r.RecipientUserId == api.UserIds["CUSTODIAN"]);
    }
}
