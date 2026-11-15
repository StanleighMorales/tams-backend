using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Settings;

public static class SettingsEndpoints
{
    public static IServiceCollection AddTamsSettings(this IServiceCollection services) =>
        services.AddSingleton(TimeProvider.System).AddSingleton<ISettings, SettingsService>();

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/settings").WithTags("Settings");

        g.MapGet("/", async (ISettings settings, CancellationToken ct) =>
                Results.Ok((await Effective(settings, ct)).Values))
            .RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);

        g.MapGet("/{key}", async (string key, ISettings settings, CancellationToken ct) =>
                SettingDefinitions.ByKey.ContainsKey(key)
                    ? Results.Ok((await Effective(settings, ct))[key])
                    : Unknown(key, 404))
            .RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);

        g.MapPut("/{key}", async (string key, UpdateSettingRequest body, HttpContext http, TamsDbContext db, ISettings settings, CancellationToken ct) =>
            {
                if (!SettingDefinitions.ByKey.ContainsKey(key)) return Unknown(key, 404);
                return AdminOnly([key], http) ?? await Apply(new() { [key] = body.Value }, http, db, settings, ct);
            })
            .RequireRoles(Roles.Admin, Roles.TechStaff);

        // Flat object: { "grace_minutes": 20, "transit_window_minutes": 12 }. All or nothing.
        g.MapPut("/", async (Dictionary<string, JsonElement> body, HttpContext http, TamsDbContext db, ISettings settings, CancellationToken ct) =>
            {
                if (body.Count == 0)
                    return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Provide at least one setting."] });
                var unknown = body.Keys.Where(k => !SettingDefinitions.ByKey.ContainsKey(k)).ToArray();
                if (unknown.Length > 0) return Unknown(string.Join(", ", unknown), 400);
                return AdminOnly(body.Keys, http) ?? await Apply(body, http, db, settings, ct);
            })
            .RequireRoles(Roles.Admin, Roles.TechStaff);

        return app;
    }

    /// <summary>Settings are admin only, except the ones marked TechStaffCanEdit (max_items_per_transaction).</summary>
    static IResult? AdminOnly(IEnumerable<string> keys, HttpContext http) =>
        http.User.IsInRole(Roles.Admin) || keys.All(k => SettingDefinitions.ByKey[k].TechStaffCanEdit) ? null
            : Problems.Forbidden("SETTING_ADMIN_ONLY", "Only an administrator can change this setting.");

    public sealed record UpdateSettingRequest(JsonElement Value);

    public sealed record SettingView(string Key, string Type, int Value, int Default, int? Min, int? Max, int[]? AllowedValues,
        string Description, string Source, bool IsOverridden, DateTimeOffset? UpdatedAt, Guid? UpdatedByUserId,
        bool TechStaffCanEdit);

    static IResult Unknown(string key, int status) => status == 404
        ? Problems.NotFound("UNKNOWN_SETTING", $"No setting named '{key}'.")
        : Problems.BadRequest("UNKNOWN_SETTING", $"Unknown setting(s): {key}.");

    static async Task<IResult> Apply(Dictionary<string, JsonElement> changes, HttpContext http, TamsDbContext db, ISettings settings, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var parsed = new Dictionary<string, int>();
        foreach (var (key, el) in changes)
        {
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
                errors[key] = ["Must be a whole number."];
            else if (SettingDefinitions.ByKey[key].Validate(v) is { } msg)
                errors[key] = [msg];
            else parsed[key] = v;
        }
        if (errors.Count > 0) return Problems.Validation(errors);

        var actor = ActorId(http.User);
        if (actor is null) return Problems.Unauthorized("UNAUTHORIZED", "Token has no user id.");

        // Read straight from the database, not the cache, so audit "before" values are exact.
        var rows = await db.SystemSettings.ToDictionaryAsync(s => s.Key, ct);
        int Current(SettingDefinition d) =>
            rows.TryGetValue(d.Key, out var r) && int.TryParse(r.Value, out var x) ? x : d.Default;

        var effective = SettingDefinitions.All.ToDictionary(d => d.Key, Current);
        foreach (var (k, v) in parsed) effective[k] = v;
        if (SettingDefinitions.ValidateCombination(effective) is { } combo)
            return Problems.Validation(new Dictionary<string, string[]> { ["combination"] = [combo] });

        var now = DateTimeOffset.UtcNow;
        var ip = http.Connection.RemoteIpAddress?.ToString();
        foreach (var (key, v) in parsed)
        {
            var before = Current(SettingDefinitions.ByKey[key]);
            if (before == v) continue; // no-op, nothing to record

            if (!rows.TryGetValue(key, out var row))
                db.SystemSettings.Add(row = new SystemSetting { Key = key });
            row.Value = v.ToString();
            row.UpdatedByUserId = actor.Value;
            row.UpdatedAt = now;
            db.AuditLog.Add(new AuditLogEntry
            {
                ActorUserId = actor,
                Action = "SETTING_UPDATED",
                EntityType = "system_setting",
                EntityId = KeyGuid(key),
                Before = JsonSerializer.Serialize(new { key, value = before }),
                After = JsonSerializer.Serialize(new { key, value = v }),
                IpAddress = ip,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct); // one transaction: every setting and audit row, or none
        settings.Invalidate();
        var result = await Effective(settings, ct);
        return Results.Ok(changes.Count == 1 ? result[changes.Keys.First()] : result.Values);
    }

    static async Task<Dictionary<string, SettingView>> Effective(ISettings settings, CancellationToken ct)
    {
        var o = await settings.GetOverridesAsync(ct);
        return SettingDefinitions.All.ToDictionary(d => d.Key, d =>
        {
            o.TryGetValue(d.Key, out var s);
            return new SettingView(d.Key, "integer", s?.Value ?? d.Default, d.Default, d.Min, d.Max, d.AllowedValues,
                d.Description, d.Source, s is not null, s?.UpdatedAt, s?.UpdatedByUserId, d.TechStaffCanEdit);
        });
    }

    // audit_log.entity_id is a uuid but settings are keyed by string: derive a stable uuid from the key.
    static Guid KeyGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes("system_settings:" + key)));

    static Guid? ActorId(ClaimsPrincipal u) =>
        Guid.TryParse(u.FindFirstValue(ClaimTypes.NameIdentifier) ?? u.FindFirstValue("sub"), out var id) ? id : null;
}
