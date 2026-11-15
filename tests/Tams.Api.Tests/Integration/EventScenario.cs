using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Tests.Integration;

/// <summary>A running event with a coordinator (card and counter kiosk) and open event loans, inserted directly (the kiosk flow is covered elsewhere).</summary>
public sealed record EventScenario(Guid CoordinatorId, string CardUid, Guid StationId, string StationCode, Guid EventId, List<Guid> LoanIds, List<Guid> AssetIds)
{

    

    /// <summary>A running event with a coordinator and the given number of open event loans, inserted directly (the kiosk flow is covered elsewhere).</summary>
    public static async Task<EventScenario> Create(ApiFixture api, int loans = 1)
    {
        var admin = await api.As("ADMIN");
        var (coordinator, _, _) = await Seed.User(admin, "INSTRUCTOR", coordinator: true);
        var uid = Seed.Hex(4);
        await admin.PostRawAsync("/api/auth-cards", $$"""{"cardUid":"{{uid}}","userId":"{{coordinator}}"}""").Ok(201);
        var room = await Seed.Room(admin, storage: true);
        var (stationId, stationCode) = await Seed.Station(admin, room);
        var category = await Seed.Category(admin);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var evt = new Event
        {
            Id = Guid.NewGuid(), Title = "Fair " + Seed.Tag(), VenueText = "Gym", StartsAt = now.AddHours(-1), EndsAt = now.AddHours(3),
            CoordinatorUserId = coordinator, Status = EventStatus.Active, CreatedByUserId = coordinator, CreatedAt = now, UpdatedAt = now,
        };
        db.Events.Add(evt);
        var session = new CheckoutSession
        {
            Id = Guid.NewGuid(), StationId = stationId, Type = SessionType.EventBatch, Status = SessionStatus.Completed,
            InstructorUserId = coordinator, EventId = evt.Id, ExpiresAt = now, CreatedAt = now, CompletedAt = now,
        };
        db.CheckoutSessions.Add(session);
        await db.SaveChangesAsync();

        var ids = new List<Guid>();
        var assets = new List<Guid>();
        for (var i = 0; i < loans; i++)
        {
            var (assetId, _, _) = await Seed.BorrowableAsset(admin, category, room);
            var loan = new Loan
            {
                Id = Guid.NewGuid(), LoanNo = "L" + Seed.Tag() + i, Type = LoanType.Event, AssetId = assetId, CoordinatorUserId = coordinator,
                EventId = evt.Id, SessionId = session.Id, CheckoutStationId = stationId, Status = LoanStatus.Active,
                CustodyState = CustodyState.NotRequired, CheckedOutAt = now, ClassEndsAt = evt.EndsAt, GraceMinutes = 15,
                DueAt = evt.EndsAt.AddMinutes(15),
            };
            db.Loans.Add(loan);
            ids.Add(loan.Id);
            assets.Add(assetId);
        }
        await db.SaveChangesAsync();
        return new(coordinator, uid, stationId, stationCode, evt.Id, ids, assets);
    }
}

