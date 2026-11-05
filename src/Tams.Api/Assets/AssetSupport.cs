using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Assets;

/// <summary>API text for enums is UPPER_SNAKE (IN_STORAGE), same as the PostgreSQL labels.</summary>
static class EnumText
{
    public static string ToApi<T>(T value) where T : struct, Enum =>
        PgEnums.ToSnake(value.ToString()).ToUpperInvariant();

    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(ToApi(candidate), text?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }
        value = default;
        return false;
    }
}

static class AssetAudit
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web);

    public static Guid? ActorId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var id) ? id : null;

    /// <summary>Adds an audit_log row to the context; the caller saves it together with the change.</summary>
    public static void Add(TamsDbContext db, HttpContext http, string action, string entityType, Guid entityId,
        object? before, object? after) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = ActorId(http.User),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Before = before is null ? null : JsonSerializer.Serialize(before, Json),
            After = after is null ? null : JsonSerializer.Serialize(after, Json),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}

record CategoryRequest(string? Name, string? Description, bool? IsBorrowable);
record CategoryDto(Guid Id, string Name, string? Description, bool IsBorrowable, int AssetCount);

record AssetRequest(
    string? AssetCode, string? Name, Guid? CategoryId, string? Type, string? Brand, string? Model,
    string? SerialNo, string? RfidTagUid, Guid? HomeRoomId, string? Status, string? Condition,
    decimal? ReplacementCost, DateOnly? AcquiredAt, string? Notes);

record AssetDto(
    Guid Id, string AssetCode, string Name, Guid CategoryId, string Type, string? Brand, string? Model,
    string? SerialNo, string? RfidTagUid, Guid HomeRoomId, Guid? CurrentRoomId, string Status, string Condition,
    decimal ReplacementCost, DateOnly? AcquiredAt, string? Notes, Guid? ImageFileId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt)
{
    public static AssetDto From(Asset a) => new(
        a.Id, a.AssetCode, a.Name, a.CategoryId, EnumText.ToApi(a.Type), a.Brand, a.Model, a.SerialNo,
        a.RfidTagUid, a.HomeRoomId, a.CurrentRoomId, EnumText.ToApi(a.Status), EnumText.ToApi(a.Condition),
        a.ReplacementCost, a.AcquiredAt, a.Notes, a.ImageFileId, a.CreatedAt, a.UpdatedAt, a.DeletedAt);
}

record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
