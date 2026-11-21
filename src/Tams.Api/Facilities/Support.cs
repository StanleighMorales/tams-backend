using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Facilities;

/// <summary>Rooms, stations and authorization cards (TAMS-32). One call wires all three.</summary>
public static class FacilityEndpoints
{
    public static WebApplication MapFacilityEndpoints(this WebApplication app)
    {
        app.MapRoomEndpoints();
        app.MapStationEndpoints();
        app.MapAuthCardEndpoints();
        return app;
    }
}

/// <summary>Role sets shared by the three resources (read: staff; write: admin and technical staff).</summary>
static class FacilityRoles
{
    public static readonly string[] Read = [Roles.Admin, Roles.TechStaff, Roles.Custodian];
    public static readonly string[] Write = [Roles.Admin, Roles.TechStaff];
}

record PageQuery(int Page = 1, int PageSize = 25, string? Q = null, string? Sort = null, bool IncludeDeleted = false);

record Paged<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

static class Support
{
    public const int MaxPageSize = 100;

    public static (int Page, int Size) Clamp(PageQuery q) =>
        (Math.Max(q.Page, 1), Math.Clamp(q.PageSize, 1, MaxPageSize));

    /// <summary>?includeDeleted=true is for ADMIN and TECH_STAFF only.</summary>
    public static bool MayIncludeDeleted(ClaimsPrincipal user) =>
        user.IsInRole(Roles.Admin) || user.IsInRole(Roles.TechStaff);

    /// <summary>Wire format of an enum: the PostgreSQL label (TechStaff -> TECH_STAFF).</summary>
    public static string Label<T>(T value) where T : struct, Enum =>
        PgEnums.ToSnake(value.ToString()).ToUpperInvariant();

    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        foreach (var v in Enum.GetValues<T>())
        {
            if (string.Equals(Label(v), text?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }
        value = default;
        return false;
    }

    public static IResult BadEnum<T>(string field) where T : struct, Enum =>
        Problems.Validation(new Dictionary<string, string[]>
        {
            [field] = [$"Must be one of: {string.Join(", ", Enum.GetValues<T>().Select(Label))}."],
        });

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Adds an audit_log row to the current unit of work; the caller's SaveChanges commits it.</summary>
    public static void Audit(TamsDbContext db, HttpContext http, string action, string entityType, Guid entityId,
        object? before, object? after)
    {
        var actor = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = actor,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Before = before is null ? null : JsonSerializer.Serialize(before, Json),
            After = after is null ? null : JsonSerializer.Serialize(after, Json),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }
}
