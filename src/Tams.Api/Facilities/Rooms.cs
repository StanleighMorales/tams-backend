using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Facilities;

record RoomWrite(string? Code, string? Name, string? Building, short? Floor, bool? IsStorage, string? Status);

record RoomDto(Guid Id, string Code, string Name, string Building, short Floor, bool IsStorage,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt)
{
    public static RoomDto From(Room r) => new(r.Id, r.Code, r.Name, r.Building, r.Floor, r.IsStorage,
        Support.Label(r.Status), r.CreatedAt, r.UpdatedAt, r.DeletedAt);
}

public static class RoomEndpoints
{
    public static WebApplication MapRoomEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/rooms").WithTags("Rooms");

        g.MapGet("/", List).RequireRoles(FacilityRoles.Read);
        g.MapGet("/{id:guid}", Get).RequireRoles(FacilityRoles.Read);
        g.MapPost("/", Create).RequireRoles(FacilityRoles.Write);
        g.MapPut("/{id:guid}", Update).RequireRoles(FacilityRoles.Write);
        g.MapDelete("/{id:guid}", Delete).RequireRoles(FacilityRoles.Write);
        g.MapPost("/{id:guid}/restore", Restore).RequireRoles(Roles.Admin);
        return app;
    }

    static async Task<IResult> List([AsParameters] PageQuery q, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        if (q.IncludeDeleted && !Support.MayIncludeDeleted(http.User))
            return Problems.Forbidden("INCLUDE_DELETED_FORBIDDEN", "Only ADMIN and TECH_STAFF may list deleted rooms.");

        var query = db.Rooms.AsNoTracking().AsQueryable();
        if (!q.IncludeDeleted) query = query.Where(r => r.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var like = $"%{q.Q.Trim()}%";
            query = query.Where(r => EF.Functions.ILike(r.Code, like) || EF.Functions.ILike(r.Name, like));
        }
        var desc = q.Sort?.StartsWith('-') == true;
        query = (q.Sort?.TrimStart('-').ToLowerInvariant(), desc) switch
        {
            ("name", false) => query.OrderBy(r => r.Name),
            ("name", true) => query.OrderByDescending(r => r.Name),
            (_, true) => query.OrderByDescending(r => r.Code),
            _ => query.OrderBy(r => r.Code),
        };

        var (page, size) = Support.Clamp(q);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new Paged<RoomDto>(items.Select(RoomDto.From).ToList(), total, page, size));
    }

    static async Task<IResult> Get(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        // Deleted rows are only visible to those who may list them.
        if (room is null || (room.DeletedAt != null && !Support.MayIncludeDeleted(http.User)))
            return Problems.NotFound("ROOM_NOT_FOUND", "Room not found.");
        return Results.Ok(RoomDto.From(room));
    }

    static async Task<IResult> Create(RoomWrite body, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var errors = Validate(body, creating: true);
        if (errors.Count > 0) return Problems.Validation(errors);

        var status = RecordStatus.Active;
        if (body.Status is not null && !Support.TryParse(body.Status, out status))
            return Support.BadEnum<RecordStatus>("status");

        var code = body.Code!.Trim().ToUpperInvariant();
        var clash = await db.Rooms.Where(r => r.Code == code).Select(r => new { r.DeletedAt }).FirstOrDefaultAsync(ct);
        if (clash is not null) return CodeTaken(code, clash.DeletedAt != null);

        var now = DateTimeOffset.UtcNow;
        var room = new Room
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = body.Name!.Trim(),
            Building = string.IsNullOrWhiteSpace(body.Building) ? "College Building" : body.Building.Trim(),
            Floor = body.Floor ?? 0,
            IsStorage = body.IsStorage ?? false,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Rooms.Add(room);
        Support.Audit(db, http, "ROOM_CREATED", "room", room.Id, null, RoomDto.From(room));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex)) { return CodeTaken(code, false); }
        return Results.Created($"/rooms/{room.Id}", RoomDto.From(room));
    }

    static async Task<IResult> Update(Guid id, RoomWrite body, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct);
        if (room is null) return Problems.NotFound("ROOM_NOT_FOUND", "Room not found.");

        var errors = Validate(body, creating: false);
        if (errors.Count > 0) return Problems.Validation(errors);

        var status = room.Status;
        if (body.Status is not null && !Support.TryParse(body.Status, out status))
            return Support.BadEnum<RecordStatus>("status");

        var before = RoomDto.From(room);
        if (body.Code is not null)
        {
            var code = body.Code.Trim().ToUpperInvariant();
            if (code != room.Code)
            {
                var clash = await db.Rooms.Where(r => r.Code == code).Select(r => new { r.DeletedAt }).FirstOrDefaultAsync(ct);
                if (clash is not null) return CodeTaken(code, clash.DeletedAt != null);
                room.Code = code;
            }
        }
        if (body.Name is not null) room.Name = body.Name.Trim();
        if (body.Building is not null) room.Building = body.Building.Trim();
        if (body.Floor is not null) room.Floor = body.Floor.Value;
        if (body.IsStorage is not null) room.IsStorage = body.IsStorage.Value;
        room.Status = status;
        room.UpdatedAt = DateTimeOffset.UtcNow;

        Support.Audit(db, http, "ROOM_UPDATED", "room", room.Id, before, RoomDto.From(room));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex)) { return CodeTaken(room.Code, false); }
        return Results.Ok(RoomDto.From(room));
    }

    static async Task<IResult> Delete(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct);
        if (room is null) return Problems.NotFound("ROOM_NOT_FOUND", "Room not found.");

        var stations = await db.Stations.CountAsync(s => s.RoomId == id && s.DeletedAt == null, ct);
        var instructors = await db.Users.CountAsync(u => u.DefaultRoomId == id && u.DeletedAt == null, ct);
        var assets = await db.Assets.CountAsync(a => a.DeletedAt == null && (a.HomeRoomId == id || a.CurrentRoomId == id), ct);
        var openLoans = await db.Loans.CountAsync(l => l.DestinationRoomId == id
            && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue), ct);
        if (stations + instructors + assets + openLoans > 0)
        {
            return Results.Problem(statusCode: 409,
                detail: "Room is still in use. Reassign or remove the references first.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "ROOM_IN_USE",
                    ["references"] = new { stations, instructors, assets, openLoans },
                });
        }

        var before = RoomDto.From(room);
        room.DeletedAt = DateTimeOffset.UtcNow;
        room.UpdatedAt = room.DeletedAt.Value;
        Support.Audit(db, http, "ROOM_DELETED", "room", room.Id, before, RoomDto.From(room));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> Restore(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (room is null) return Problems.NotFound("ROOM_NOT_FOUND", "Room not found.");
        if (room.DeletedAt is null) return Problems.Conflict("NOT_DELETED", "Room is not deleted.");

        // Defensive: rooms.code is uniquely indexed over deleted rows too, so a clash cannot exist today.
        if (await db.Rooms.AnyAsync(r => r.Code == room.Code && r.Id != id && r.DeletedAt == null, ct))
            return Problems.Conflict("RESTORE_CONFLICT", $"Another room already uses code '{room.Code}'.");

        var before = RoomDto.From(room);
        room.DeletedAt = null;
        room.UpdatedAt = DateTimeOffset.UtcNow;
        Support.Audit(db, http, "ROOM_RESTORED", "room", room.Id, before, RoomDto.From(room));
        await db.SaveChangesAsync(ct);
        return Results.Ok(RoomDto.From(room));
    }

    static IResult CodeTaken(string code, bool deleted) => Problems.Conflict("ROOM_CODE_TAKEN",
        deleted ? $"Room code '{code}' belongs to a deleted room; restore it instead."
                : $"Room code '{code}' is already in use.");

    static Dictionary<string, string[]> Validate(RoomWrite b, bool creating)
    {
        var e = new Dictionary<string, string[]>();
        if ((creating || b.Code is not null) && (string.IsNullOrWhiteSpace(b.Code) || b.Code.Trim().Length > 20))
            e["code"] = ["Required, up to 20 characters."];
        if ((creating || b.Name is not null) && (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 120))
            e["name"] = ["Required, up to 120 characters."];
        if (b.Building is { Length: > 80 }) e["building"] = ["Up to 80 characters."];
        if (b.Floor is < 0 or > 100) e["floor"] = ["Must be between 0 and 100."];
        return e;
    }
}
