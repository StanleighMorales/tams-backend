using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Facilities;

// Only admin-set fields are writable. firmwareVersion, ipAddress, lastHeartbeatAt and health come
// from the heartbeat endpoint and sweep job (other tickets). Stations carry no API key or secret:
// the PRD defines none (FR-1.4), so there is nothing to return after creation.
record StationWrite(string? StationCode, string? Type, Guid? RoomId, string? Name, string? Status);

record StationDto(Guid Id, string StationCode, string Type, Guid RoomId, string Name, string? FirmwareVersion,
    string? IpAddress, DateTimeOffset? LastHeartbeatAt, string Health, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt)
{
    public static StationDto From(Station s) => new(s.Id, s.StationCode, Support.Label(s.Type), s.RoomId, s.Name,
        s.FirmwareVersion, s.IpAddress, s.LastHeartbeatAt, Support.Label(s.Health), Support.Label(s.Status),
        s.CreatedAt, s.UpdatedAt, s.DeletedAt);
}

record StationHealthItem(Guid Id, string StationCode, string Name, string Type, Guid RoomId, string Health,
    DateTimeOffset? LastHeartbeatAt, double? SecondsSinceHeartbeat, string? FirmwareVersion);

record StationHealthCounts(int Online, int Warning, int Offline, int Total);

record StationHealthSummary(StationHealthCounts Counts, DateTimeOffset AsOf, IReadOnlyList<StationHealthItem> Stations);

public static class StationEndpoints
{
    public static WebApplication MapStationEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/stations").WithTags("Stations");

        g.MapGet("/", List).RequireRoles(FacilityRoles.Read);
        g.MapGet("/health", Health).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);
        g.MapGet("/{id:guid}", Get).RequireRoles(FacilityRoles.Read);
        g.MapPost("/", Create).RequireRoles(FacilityRoles.Write);
        g.MapPut("/{id:guid}", Update).RequireRoles(FacilityRoles.Write);
        g.MapDelete("/{id:guid}", Delete).RequireRoles(FacilityRoles.Write);
        g.MapPost("/{id:guid}/restore", Restore).RequireRoles(Roles.Admin);
        return app;
    }

    static async Task<IResult> List([AsParameters] PageQuery q, Guid? roomId, string? type, string? health,
        HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        if (q.IncludeDeleted && !Support.MayIncludeDeleted(http.User))
            return Problems.Forbidden("INCLUDE_DELETED_FORBIDDEN", "Only ADMIN and TECH_STAFF may list deleted stations.");

        var query = db.Stations.AsNoTracking().AsQueryable();
        if (!q.IncludeDeleted) query = query.Where(s => s.DeletedAt == null);
        if (roomId is not null) query = query.Where(s => s.RoomId == roomId);
        if (type is not null)
        {
            if (!Support.TryParse<StationType>(type, out var t)) return Support.BadEnum<StationType>("type");
            query = query.Where(s => s.Type == t);
        }
        if (health is not null)
        {
            if (!Support.TryParse<StationHealth>(health, out var h)) return Support.BadEnum<StationHealth>("health");
            query = query.Where(s => s.Health == h);
        }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var like = $"%{q.Q.Trim()}%";
            query = query.Where(s => EF.Functions.ILike(s.StationCode, like) || EF.Functions.ILike(s.Name, like));
        }
        var desc = q.Sort?.StartsWith('-') == true;
        query = (q.Sort?.TrimStart('-').ToLowerInvariant(), desc) switch
        {
            ("name", false) => query.OrderBy(s => s.Name),
            ("name", true) => query.OrderByDescending(s => s.Name),
            (_, true) => query.OrderByDescending(s => s.StationCode),
            _ => query.OrderBy(s => s.StationCode),
        };

        var (page, size) = Support.Clamp(q);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new Paged<StationDto>(items.Select(StationDto.From).ToList(), total, page, size));
    }

    // Reads the health the sweep job maintains (PRD 7.3: "updated by sweep job"); it does not recompute it.
    static async Task<IResult> Health(TamsDbContext db, CancellationToken ct)
    {
        var rows = await db.Stations.AsNoTracking()
            .Where(s => s.DeletedAt == null && s.Status == RecordStatus.Active)
            .OrderBy(s => s.StationCode)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var stations = rows.Select(s => new StationHealthItem(s.Id, s.StationCode, s.Name, Support.Label(s.Type),
            s.RoomId, Support.Label(s.Health), s.LastHeartbeatAt,
            s.LastHeartbeatAt is null ? null : Math.Round((now - s.LastHeartbeatAt.Value).TotalSeconds, 1),
            s.FirmwareVersion)).ToList();
        var counts = new StationHealthCounts(
            rows.Count(s => s.Health == StationHealth.Online),
            rows.Count(s => s.Health == StationHealth.Warning),
            rows.Count(s => s.Health == StationHealth.Offline),
            rows.Count);
        return Results.Ok(new StationHealthSummary(counts, now, stations));
    }

    static async Task<IResult> Get(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var s = await db.Stations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || (s.DeletedAt != null && !Support.MayIncludeDeleted(http.User)))
            return Problems.NotFound("STATION_NOT_FOUND", "Station not found.");
        return Results.Ok(StationDto.From(s));
    }

    static async Task<IResult> Create(StationWrite body, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var errors = Validate(body, creating: true);
        if (errors.Count > 0) return Problems.Validation(errors);
        if (!Support.TryParse<StationType>(body.Type, out var type)) return Support.BadEnum<StationType>("type");

        var status = RecordStatus.Active;
        if (body.Status is not null && !Support.TryParse(body.Status, out status))
            return Support.BadEnum<RecordStatus>("status");

        var code = body.StationCode!.Trim().ToUpperInvariant();
        var clash = await db.Stations.Where(s => s.DeletedAt == null && s.StationCode == code).Select(s => new { s.DeletedAt }).FirstOrDefaultAsync(ct);
        if (clash is not null) return CodeTaken(code, clash.DeletedAt != null);

        var roomError = await CheckRoom(db, body.RoomId!.Value, type, ct);
        if (roomError is not null) return roomError;

        var now = DateTimeOffset.UtcNow;
        var station = new Station
        {
            Id = Guid.NewGuid(),
            StationCode = code,
            Type = type,
            RoomId = body.RoomId.Value,
            Name = body.Name!.Trim(),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Stations.Add(station);
        Support.Audit(db, http, "STATION_CREATED", "station", station.Id, null, StationDto.From(station));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex)) { return CodeTaken(code, false); }
        return Results.Created($"/stations/{station.Id}", StationDto.From(station));
    }

    static async Task<IResult> Update(Guid id, StationWrite body, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var s = await db.Stations.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (s is null) return Problems.NotFound("STATION_NOT_FOUND", "Station not found.");

        var errors = Validate(body, creating: false);
        if (errors.Count > 0) return Problems.Validation(errors);

        var type = s.Type;
        if (body.Type is not null && !Support.TryParse(body.Type, out type)) return Support.BadEnum<StationType>("type");
        var status = s.Status;
        if (body.Status is not null && !Support.TryParse(body.Status, out status))
            return Support.BadEnum<RecordStatus>("status");

        var before = StationDto.From(s);
        if (body.StationCode is not null)
        {
            var code = body.StationCode.Trim().ToUpperInvariant();
            if (code != s.StationCode)
            {
                var clash = await db.Stations.Where(x => x.DeletedAt == null && x.StationCode == code).Select(x => new { x.DeletedAt }).FirstOrDefaultAsync(ct);
                if (clash is not null) return CodeTaken(code, clash.DeletedAt != null);
                s.StationCode = code;
            }
        }
        var roomId = body.RoomId ?? s.RoomId;
        if (roomId != s.RoomId || type != s.Type)
        {
            var roomError = await CheckRoom(db, roomId, type, ct);
            if (roomError is not null) return roomError;
        }
        s.RoomId = roomId;
        s.Type = type;
        s.Status = status;
        if (body.Name is not null) s.Name = body.Name.Trim();
        s.UpdatedAt = DateTimeOffset.UtcNow;

        Support.Audit(db, http, "STATION_UPDATED", "station", s.Id, before, StationDto.From(s));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex)) { return CodeTaken(s.StationCode, false); }
        return Results.Ok(StationDto.From(s));
    }

    static async Task<IResult> Delete(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var s = await db.Stations.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (s is null) return Problems.NotFound("STATION_NOT_FOUND", "Station not found.");

        var before = StationDto.From(s);
        s.DeletedAt = DateTimeOffset.UtcNow;
        s.UpdatedAt = s.DeletedAt.Value;
        Support.Audit(db, http, "STATION_DELETED", "station", s.Id, before, StationDto.From(s));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> Restore(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var s = await db.Stations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Problems.NotFound("STATION_NOT_FOUND", "Station not found.");
        if (s.DeletedAt is null) return Problems.Conflict("NOT_DELETED", "Station is not deleted.");

        // station_code is uniquely indexed over live rows only, so a live station may have taken the code.
        if (await db.Stations.AnyAsync(x => x.StationCode == s.StationCode && x.Id != id && x.DeletedAt == null, ct))
            return Problems.Conflict("RESTORE_CONFLICT", $"Another station already uses code '{s.StationCode}'.");
        // A station cannot come back bound to a room that is itself deleted.
        if (!await db.Rooms.AnyAsync(r => r.Id == s.RoomId && r.DeletedAt == null, ct))
            return Problems.Conflict("RESTORE_CONFLICT", "The station's room is deleted; restore the room first.");

        var before = StationDto.From(s);
        s.DeletedAt = null;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        Support.Audit(db, http, "STATION_RESTORED", "station", s.Id, before, StationDto.From(s));
        await db.SaveChangesAsync(ct);
        return Results.Ok(StationDto.From(s));
    }

    // PRD 7.3: room_id is "room it guards (kiosk: counter room)", and a storage room is the equipment
    // counter room, so a COUNTER_KIOSK must sit in a storage room. ROOM_STATION has no extra rule.
    static async Task<IResult?> CheckRoom(TamsDbContext db, Guid roomId, StationType type, CancellationToken ct)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId && r.DeletedAt == null, ct);
        if (room is null) return Problems.BadRequest("ROOM_NOT_FOUND", "roomId does not match an existing room.");
        if (room.Status != RecordStatus.Active) return Problems.BadRequest("ROOM_INACTIVE", "The room is inactive.");
        if (type == StationType.CounterKiosk && !room.IsStorage)
            return Problems.BadRequest("KIOSK_ROOM_MISMATCH", "A COUNTER_KIOSK must be bound to the counter (storage) room.");
        return null;
    }

    static IResult CodeTaken(string code, bool deleted) => Problems.Conflict("STATION_CODE_TAKEN",
        deleted ? $"Station code '{code}' belongs to a deleted station; restore it instead."
                : $"Station code '{code}' is already in use.");

    static Dictionary<string, string[]> Validate(StationWrite b, bool creating)
    {
        var e = new Dictionary<string, string[]>();
        if ((creating || b.StationCode is not null) && (string.IsNullOrWhiteSpace(b.StationCode) || b.StationCode.Trim().Length > 30))
            e["stationCode"] = ["Required, up to 30 characters."];
        if ((creating || b.Name is not null) && (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 80))
            e["name"] = ["Required, up to 80 characters."];
        if (creating && b.Type is null) e["type"] = ["Required."];
        if (creating && b.RoomId is null) e["roomId"] = ["Required."];
        return e;
    }
}
