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

        var ownerError = await CheckOwner(db, body.UserId.Value, ct);
        if (ownerError is not null) return ownerError;
        if (await UidInUse(db, uid, ct)) return Taken(uid);

        var card = new AuthCard
        {
            Id = Guid.NewGuid(),
            CardUid = uid,
            UserId = body.UserId.Value,
            Status = CardStatus.Active,
            IssuedAt = DateTimeOffset.UtcNow,
        };
        db.AuthCards.Add(card);
        Support.Audit(db, http, "AUTH_CARD_ISSUED", "auth_card", card.Id, null, CardDto.From(card));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex)) { return Taken(uid); }
        return Results.Created($"/auth-cards/{card.Id}", CardDto.From(card));
    }

    static async Task<IResult> Update(Guid id, CardUpdate body, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var card = await db.AuthCards.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (card is null) return NotFound();
        if (!Support.TryParse<CardStatus>(body.Status, out var status)) return Support.BadEnum<CardStatus>("status");

        var before = CardDto.From(card);
        card.Status = status;
        Support.Audit(db, http, "AUTH_CARD_UPDATED", "auth_card", card.Id, before, CardDto.From(card));
        await db.SaveChangesAsync(ct);
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
        var ownerError = await CheckOwner(db, old.UserId, ct);
        if (ownerError is not null) return ownerError;

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

    // The same UID must not be both an asset tag and an instructor card.
    static async Task<bool> UidInUse(TamsDbContext db, string uid, CancellationToken ct) =>
        await db.AuthCards.AnyAsync(c => c.CardUid == uid, ct) || await db.Assets.AnyAsync(a => a.RfidTagUid == uid, ct);

    // PRD 7.3: card owner is an instructor or coordinator.
    static async Task<IResult?> CheckOwner(TamsDbContext db, Guid userId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId && x.DeletedAt == null)
            .Select(x => new { x.Role, x.IsEventCoordinator, x.Status }).FirstOrDefaultAsync(ct);
        if (u is null) return Problems.BadRequest("USER_NOT_FOUND", "userId does not match an existing user.");
        if (u.Status != RecordStatus.Active) return Problems.BadRequest("USER_INACTIVE", "The user is inactive.");
        if (u.Role != UserRole.Instructor && !u.IsEventCoordinator)
            return Problems.BadRequest("CARD_OWNER_NOT_INSTRUCTOR", "Authorization cards are issued to instructors or event coordinators.");
        return null;
    }

    static bool TryInactive(string text, out CardStatus status) =>
        Support.TryParse(text, out status) && status != CardStatus.Active;

    static IResult BadInactive(string field) => Problems.Validation(
        new Dictionary<string, string[]> { [field] = ["Must be LOST or REVOKED."] });

    static IResult NotFound() => Problems.NotFound("AUTH_CARD_NOT_FOUND", "Authorization card not found.");

    static IResult InvalidUid() => Problems.BadRequest("INVALID_RFID_UID",
        $"UID must be hexadecimal, {RfidUid.MinLength} to {RfidUid.MaxLength} characters, even length (separators allowed).");

    static IResult Taken(string uid) => Problems.Conflict("TAG_ALREADY_ASSIGNED", $"UID '{uid}' is already assigned.");
}
