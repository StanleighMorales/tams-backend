using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Facilities;

record CardCreate(string? CardUid, Guid? UserId);
record CardUpdate(string? Status);
record CardRevoke(string? Status);
record CardReplace(string? NewCardUid, string? OldStatus);

record CardDto(Guid Id, string CardUid, Guid UserId, string Status, DateTimeOffset IssuedAt)
{
    public static CardDto From(AuthCard c) => new(c.Id, c.CardUid, c.UserId, Support.Label(c.Status), c.IssuedAt);
}

record CardReplaceResult(CardDto OldCard, CardDto NewCard);

/// <summary>Why a card could not be issued; the web endpoint answers it as a problem, the kiosk tap as LCD text.</summary>
public record CardProblem(string Code, string Detail);

// auth_cards has no deleted_at: a card is never deleted, only moved to LOST or REVOKED, so the
// history of who held which UID stays intact. Hence no DELETE or restore here.
public static class AuthCardEndpoints
{
    public static WebApplication MapAuthCardEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/auth-cards").WithTags("Authorization cards");

        g.MapGet("/", List).RequireRoles(FacilityRoles.Read);
        g.MapGet("/{id:guid}", Get).RequireRoles(FacilityRoles.Read);
        g.MapPost("/", Create).RequireRoles(FacilityRoles.Write);
        g.MapPut("/{id:guid}", Update).RequireRoles(FacilityRoles.Write);
        g.MapPost("/{id:guid}/revoke", Revoke).RequireRoles(FacilityRoles.Write);
        g.MapPost("/{id:guid}/replace", Replace).RequireRoles(FacilityRoles.Write);
        return app;
    }

    static async Task<IResult> List([AsParameters] PageQuery q, Guid? userId, string? status,
        TamsDbContext db, CancellationToken ct)
    {
        var query = db.AuthCards.AsNoTracking().AsQueryable();
        if (userId is not null) query = query.Where(c => c.UserId == userId);
        if (status is not null)
        {
            if (!Support.TryParse<CardStatus>(status, out var st)) return Support.BadEnum<CardStatus>("status");
            query = query.Where(c => c.Status == st);
        }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var like = $"%{q.Q.Trim()}%";
            query = query.Where(c => EF.Functions.ILike(c.CardUid, like));
        }
        query = q.Sort?.StartsWith('-') == false || q.Sort is null
            ? query.OrderBy(c => c.IssuedAt)
            : query.OrderByDescending(c => c.IssuedAt);

        var (page, size) = Support.Clamp(q);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new Paged<CardDto>(items.Select(CardDto.From).ToList(), total, page, size));
    }

    static async Task<IResult> Get(Guid id, TamsDbContext db, CancellationToken ct)
    {
        var c = await db.AuthCards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? NotFound() : Results.Ok(CardDto.From(c));
    }

    static async Task<IResult> Create(CardCreate body, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        if (body.UserId is null)
            return Problems.Validation(new Dictionary<string, string[]> { ["userId"] = ["Required."] });
        if (!RfidUid.TryNormalize(body.CardUid, out var uid)) return InvalidUid();

        var (card, problem) = await IssueAsync(db, http, uid, body.UserId.Value, Support.ActorId(http), ct);
        return card is null ? ToResult(problem!) : Results.Created($"/auth-cards/{card.Id}", CardDto.From(card));
    }

    /// <summary>Issues a card to an instructor or coordinator (owner and UID checks included) and writes
    /// AUTH_CARD_ISSUED as <paramref name="actor"/>. Shared by POST /auth-cards and the kiosk "Add card" tap.</summary>
    internal static async Task<(AuthCard? Card, CardProblem? Problem)> IssueAsync(
        TamsDbContext db, HttpContext http, string uid, Guid userId, Guid? actor, CancellationToken ct)
    {
        var ownerProblem = await OwnerProblem(db, userId, ct);
        if (ownerProblem is not null) return (null, ownerProblem);
        if (await UidInUse(db, uid, ct)) return (null, TakenProblem(uid));

        var card = new AuthCard
        {
            Id = Guid.NewGuid(),
            CardUid = uid,
            UserId = userId,
            Status = CardStatus.Active,
            IssuedAt = DateTimeOffset.UtcNow,
        };
        db.AuthCards.Add(card);
        Support.Audit(db, http, "AUTH_CARD_ISSUED", "auth_card", card.Id, null, CardDto.From(card), actor);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex)) { return (null, TakenProblem(uid)); }
        return (card, null);
    }

    static async Task<IResult> Update(Guid id, CardUpdate body, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var card = await db.AuthCards.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (card is null) return NotFound();
        if (!Support.TryParse<CardStatus>(body.Status, out var status)) return Support.BadEnum<CardStatus>("status");

        var before = CardDto.From(card);
        card.Status = status;
        Support.Audit(db, http, "AUTH_CARD_UPDATED", "auth_card", card.Id, before, CardDto.From(card));
        // Re-activating an old row whose UID is active elsewhere breaks the active-only unique index.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex)) { return Taken(card.CardUid); }
        return Results.Ok(CardDto.From(card));
    }

    static async Task<IResult> Revoke(Guid id, CardRevoke? body, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var card = await db.AuthCards.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (card is null) return NotFound();

        var target = CardStatus.Revoked;
        if (body?.Status is not null && !TryInactive(body.Status, out target)) return BadInactive("status");
        if (card.Status == target) return Results.Ok(CardDto.From(card));

        var before = CardDto.From(card);
        card.Status = target;
        Support.Audit(db, http, target == CardStatus.Lost ? "AUTH_CARD_LOST" : "AUTH_CARD_REVOKED",
            "auth_card", card.Id, before, CardDto.From(card));
        await db.SaveChangesAsync(ct);
        return Results.Ok(CardDto.From(card));
    }

    // Lost or damaged card: retire the old UID and issue a new one to the same person, in one commit.
    static async Task<IResult> Replace(Guid id, CardReplace body, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var old = await db.AuthCards.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (old is null) return NotFound();
        if (!RfidUid.TryNormalize(body.NewCardUid, out var uid)) return InvalidUid();

        var oldStatus = CardStatus.Lost;
        if (body.OldStatus is not null && !TryInactive(body.OldStatus, out oldStatus)) return BadInactive("oldStatus");

        if (await UidInUse(db, uid, ct)) return Taken(uid);
        var ownerProblem = await OwnerProblem(db, old.UserId, ct);
        if (ownerProblem is not null) return ToResult(ownerProblem);

        var before = CardDto.From(old);
        old.Status = oldStatus;
        var card = new AuthCard
        {
            Id = Guid.NewGuid(),
            CardUid = uid,
            UserId = old.UserId,
            Status = CardStatus.Active,
            IssuedAt = DateTimeOffset.UtcNow,
        };
        db.AuthCards.Add(card);
        Support.Audit(db, http, "AUTH_CARD_REPLACED", "auth_card", old.Id, before, CardDto.From(old));
        Support.Audit(db, http, "AUTH_CARD_ISSUED", "auth_card", card.Id, null, CardDto.From(card));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex)) { return Taken(uid); }
        return Results.Ok(new CardReplaceResult(CardDto.From(old), CardDto.From(card)));
    }

    // The same UID must not be both an asset tag and an instructor card. Only ACTIVE cards count: a
    // revoked or lost card keeps its row as history and its UID can be issued again.
    static async Task<bool> UidInUse(TamsDbContext db, string uid, CancellationToken ct) =>
        await db.AuthCards.AnyAsync(c => c.CardUid == uid && c.Status == CardStatus.Active, ct)
        || await db.Assets.AnyAsync(a => a.DeletedAt == null && a.RfidTagUid == uid, ct);

    // PRD 7.3: card owner is an instructor or coordinator.
    internal static async Task<CardProblem?> OwnerProblem(TamsDbContext db, Guid userId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId && x.DeletedAt == null)
            .Select(x => new { x.Role, x.IsEventCoordinator, x.Status }).FirstOrDefaultAsync(ct);
        if (u is null) return new("USER_NOT_FOUND", "userId does not match an existing user.");
        if (u.Status != RecordStatus.Active) return new("USER_INACTIVE", "The user is inactive.");
        if (u.Role != UserRole.Instructor && !u.IsEventCoordinator)
            return new("CARD_OWNER_NOT_INSTRUCTOR", "Authorization cards are issued to instructors or event coordinators.");
        return null;
    }

    static IResult ToResult(CardProblem p) => p.Code == "TAG_ALREADY_ASSIGNED"
        ? Problems.Conflict(p.Code, p.Detail)
        : Problems.BadRequest(p.Code, p.Detail);

    static bool TryInactive(string text, out CardStatus status) =>
        Support.TryParse(text, out status) && status != CardStatus.Active;

    static IResult BadInactive(string field) => Problems.Validation(
        new Dictionary<string, string[]> { [field] = ["Must be LOST or REVOKED."] });

    static IResult NotFound() => Problems.NotFound("AUTH_CARD_NOT_FOUND", "Authorization card not found.");

    static IResult InvalidUid() => Problems.BadRequest("INVALID_RFID_UID",
        $"UID must be hexadecimal, {RfidUid.MinLength} to {RfidUid.MaxLength} characters, even length (separators allowed).");

    static IResult Taken(string uid) => ToResult(TakenProblem(uid));

    internal static CardProblem TakenProblem(string uid) => new("TAG_ALREADY_ASSIGNED", $"UID '{uid}' is already assigned.");
}
