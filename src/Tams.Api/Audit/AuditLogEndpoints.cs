using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Auditing;

record AuditLogDto(
    long Id, Guid? ActorUserId, Guid? ActorStationId, string Action, string EntityType, Guid EntityId,
    JsonElement? Before, JsonElement? After, string? IpAddress, DateTimeOffset CreatedAt)
{
    public static AuditLogDto From(AuditLogEntry e) => new(
        e.Id, e.ActorUserId, e.ActorStationId, e.Action, e.EntityType, e.EntityId,
        Parse(e.Before), Parse(e.After), e.IpAddress, e.CreatedAt);

    static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
}

record AuditLogPage(IReadOnlyList<AuditLogDto> Items, int Page, int PageSize, int Total);

// schema-v1-decisions #10: the audit log is append-only and read by ADMIN only. No write routes exist.
static class AuditLogEndpoints
{
    public static IEndpointRouteBuilder MapAuditLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/audit-log", List).WithTags("Audit log").RequireRoles(Roles.Admin);
        return app;
    }

    static async Task<IResult> List(TamsDbContext db, CancellationToken ct,
        Guid? actorUserId, string? entityType, Guid? entityId, DateTimeOffset? from, DateTimeOffset? to,
        int page = 1, int pageSize = 25)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1) errors["page"] = ["Must be 1 or greater."];
        if (pageSize is < 1 or > 100) errors["pageSize"] = ["Must be between 1 and 100."];
        if (from is not null && to is not null && from > to) errors["from"] = ["Must not be after 'to'."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var query = db.AuditLog.AsNoTracking().AsQueryable();
        if (actorUserId is not null) query = query.Where(e => e.ActorUserId == actorUserId);
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(e => e.EntityType == entityType.Trim());
        if (entityId is not null) query = query.Where(e => e.EntityId == entityId);
        if (from is not null) query = query.Where(e => e.CreatedAt >= from);
        if (to is not null) query = query.Where(e => e.CreatedAt <= to);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new AuditLogPage(rows.Select(AuditLogDto.From).ToList(), page, pageSize, total));
    }
}
