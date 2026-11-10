using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Push;

/// <param name="Token">The FCM registration token.</param>
/// <param name="Platform">ANDROID, IOS or WEB.</param>
public sealed record DeviceTokenRequest(string? Token, string? Platform);

public sealed record DeviceTokenUnregisterRequest(string? Token);

/// <summary>Lets the signed-in user's phone register its FCM token so approval requests can be pushed (TAMS-62).</summary>
public static class DeviceTokenEndpoints
{
    const int MaxTokenLength = 4096;

    public static IEndpointRouteBuilder MapDeviceTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/device-tokens").WithTags("Device tokens").RequireAuthorization(Policies.AnyUser);
        g.MapPost("", Register);
        g.MapPost("/unregister", Unregister);
        return app;
    }

    /// <summary>Upsert by token: a token seen again (or on another account after sign-in) moves to the caller.</summary>
    static async Task<IResult> Register(DeviceTokenRequest req, ClaimsPrincipal user, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        if (AssetAudit.ActorId(user) is not Guid me) return Problems.Unauthorized("UNAUTHORIZED", "Missing user identity.");
        var errors = new Dictionary<string, string[]>();
        var token = req.Token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) errors["token"] = [$"Required, up to {MaxTokenLength} characters."];
        if (!Enum.TryParse<DevicePlatform>(req.Platform, true, out var platform) || !Enum.IsDefined(platform))
            errors["platform"] = ["Must be ANDROID, IOS or WEB."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var now = DateTimeOffset.UtcNow;
        var row = await db.DeviceTokens.FirstOrDefaultAsync(t => t.Token == token, ct);
        if (row is null) db.DeviceTokens.Add(row = new DeviceToken { Id = Guid.NewGuid(), Token = token! });
        row.UserId = me;
        row.Platform = platform;
        row.LastSeenAt = now;
        // The token is a push credential, so it never goes into the audit log.
        AssetAudit.Add(db, http, "DEVICE_TOKEN_REGISTER", "device_token", row.Id, null, new { platform = platform.ToString().ToUpperInvariant() });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AssetAudit.IsUniqueViolation(ex))
        {
            return Problems.Conflict("DEVICE_TOKEN_CONFLICT", "The token was registered concurrently. Retry.");
        }
        return Results.Ok(new { id = row.Id, platform = platform.ToString().ToUpperInvariant(), lastSeenAt = now });
    }

    /// <summary>Removes the caller's own token (sign-out). Idempotent; another user's token is left alone.</summary>
    static async Task<IResult> Unregister(DeviceTokenUnregisterRequest req, ClaimsPrincipal user, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        if (AssetAudit.ActorId(user) is not Guid me) return Problems.Unauthorized("UNAUTHORIZED", "Missing user identity.");
        var token = req.Token?.Trim();
        if (string.IsNullOrEmpty(token)) return Problems.Validation(new Dictionary<string, string[]> { ["token"] = ["Required."] });
        var row = await db.DeviceTokens.FirstOrDefaultAsync(t => t.Token == token && t.UserId == me, ct);
        if (row is not null)
        {
            db.DeviceTokens.Remove(row);
            AssetAudit.Add(db, http, "DEVICE_TOKEN_UNREGISTER", "device_token", row.Id, new { platform = row.Platform.ToString().ToUpperInvariant() }, null);
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { removed = row is not null });
    }
}
