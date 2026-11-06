using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Auth;

public record LoginRequest(string? Email, string? Password);
public record RefreshRequest(string? RefreshToken);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public static class AuthEndpoints
{

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/auth").WithTags("Auth");
        g.MapPost("/login", Login).AllowAnonymous();
        g.MapPost("/refresh", Refresh).AllowAnonymous();
        g.MapPost("/logout", Logout).RequireAuthorization();
        g.MapPost("/change-password", ChangePassword).RequireAuthorization();
        return app;
    }

    static async Task<IResult> Login(LoginRequest req, HttpContext http, TamsDbContext db, TokenService tokens,
        IPasswordHasher<User> hasher, IOptions<JwtOptions> opts, TimeProvider clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(req.Email)) errors["email"] = ["Email is required."];
        if (string.IsNullOrEmpty(req.Password)) errors["password"] = ["Password is required."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var email = req.Email!.Trim();
        var now = clock.GetUtcNow();
        // Login is email only (decision #3). Email is citext, so the match is case-insensitive.
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.DeletedAt == null, ct);

        if (user is null)
        {
            // Spend roughly the same time as a real check so response time does not reveal unknown emails.
            hasher.VerifyHashedPassword(DummyUser, DummyHash, req.Password!);
            Audit(db, http, null, Guid.Empty, "LOGIN_FAILED", new { reason = "UNKNOWN_EMAIL" });
            await db.SaveChangesAsync(ct);
            return InvalidCredentials();
        }

        if (user.Status != RecordStatus.Active)
        {
            Audit(db, http, user.Id, user.Id, "LOGIN_FAILED", new { reason = "INACTIVE" });
            await db.SaveChangesAsync(ct);
            return InvalidCredentials();
        }

        if (IsLocked(user, now))
        {
            Audit(db, http, user.Id, user.Id, "LOGIN_FAILED", new { reason = "LOCKED" });
            await db.SaveChangesAsync(ct);
            return Locked(user, now);
        }

        var result = user.PasswordHash is null
            ? PasswordVerificationResult.Failed
            : hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password!);

        if (result == PasswordVerificationResult.Failed)
        {
            var locked = RegisterFailure(user, opts.Value, now);
            Audit(db, http, user.Id, user.Id, "LOGIN_FAILED", new { reason = locked ? "LOCKED_OUT" : "BAD_PASSWORD" });
            await db.SaveChangesAsync(ct);
            return locked ? Locked(user, now) : InvalidCredentials();
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, req.Password!);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        Audit(db, http, user.Id, user.Id, "LOGIN", null);
        await db.SaveChangesAsync(ct);

        return Results.Ok(Session(tokens.Issue(user), user));
    }

    static async Task<IResult> Refresh(RefreshRequest req, TamsDbContext db, TokenService tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RefreshToken))
            return Problems.Validation(new Dictionary<string, string[]> { ["refreshToken"] = ["Refresh token is required."] });

        var id = TokenService.PeekUserId(req.RefreshToken);
        var user = id is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null, ct);
        if (user is null || user.Status != RecordStatus.Active || !tokens.ValidateRefresh(req.RefreshToken, user))
            return Problems.Unauthorized("INVALID_REFRESH_TOKEN", "The refresh token is invalid or expired. Log in again.");

        // Rotation: a fresh pair is returned and the client discards the old refresh token.
        return Results.Ok(Session(tokens.Issue(user), user));
    }

    static async Task<IResult> Logout(HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var user = await CurrentUser(http, db, ct);
        if (user is null) return Problems.Unauthorized("UNAUTHORIZED", "Authentication is required.");

        RevokeRefreshTokens(user, DateTimeOffset.UtcNow);
        Audit(db, http, user.Id, user.Id, "LOGOUT", null);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> ChangePassword(ChangePasswordRequest req, HttpContext http, TamsDbContext db,
        IPasswordHasher<User> hasher, IOptions<JwtOptions> opts, TimeProvider clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(req.CurrentPassword)) errors["currentPassword"] = ["Current password is required."];
        if (string.IsNullOrEmpty(req.NewPassword)) errors["newPassword"] = ["New password is required."];
        else if (PasswordRules.Check(req.NewPassword, req.CurrentPassword) is { } passwordError)
            errors["newPassword"] = [passwordError];
        if (errors.Count > 0) return Problems.Validation(errors);

        var user = await CurrentUser(http, db, ct);
        if (user is null) return Problems.Unauthorized("UNAUTHORIZED", "Authentication is required.");

        var now = clock.GetUtcNow();
        if (IsLocked(user, now)) return Locked(user, now);

        // A stolen access token must not be able to guess the current password, so failures count toward lockout.
        if (user.PasswordHash is null
            || hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword!) == PasswordVerificationResult.Failed)
        {
            var locked = RegisterFailure(user, opts.Value, now);
            Audit(db, http, user.Id, user.Id, "PASSWORD_CHANGE_FAILED", new { reason = locked ? "LOCKED_OUT" : "BAD_CURRENT_PASSWORD" });
            await db.SaveChangesAsync(ct);
            return locked
                ? Locked(user, now)
                : Problems.BadRequest("INVALID_CURRENT_PASSWORD", "The current password is incorrect.");
        }

        user.PasswordHash = hasher.HashPassword(user, req.NewPassword!);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        RevokeRefreshTokens(user, now);
        Audit(db, http, user.Id, user.Id, "PASSWORD_CHANGE", null);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- helpers ----

    static async Task<User?> CurrentUser(HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirst("sub")?.Value, out var id)) return null;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null, ct);
        return user is { Status: RecordStatus.Active } ? user : null;
    }

    /// <summary>Changing the SecurityStamp invalidates every refresh token issued for the user.</summary>
    static void RevokeRefreshTokens(User user, DateTimeOffset now)
    {
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        user.UpdatedAt = now;
    }

    static bool IsLocked(User user, DateTimeOffset now) => user.LockoutEnd is { } end && end > now;

    /// <summary>Counts a failure against the resolved user; returns true when it triggered a lockout.</summary>
    static bool RegisterFailure(User user, JwtOptions o, DateTimeOffset now)
    {
        user.AccessFailedCount++;
        if (user.AccessFailedCount < o.MaxFailedAttempts) return false;
        user.AccessFailedCount = 0;
        user.LockoutEnd = now.AddMinutes(o.LockoutMinutes);
        return true;
    }

    static IResult InvalidCredentials() =>
        Problems.Unauthorized("INVALID_CREDENTIALS", "Email or password is incorrect.");

    static IResult Locked(User user, DateTimeOffset now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((user.LockoutEnd!.Value - now).TotalMinutes));
        return Results.Problem(statusCode: StatusCodes.Status423Locked,
            detail: $"Too many failed attempts. Try again in about {minutes} minute(s).",
            extensions: new Dictionary<string, object?> { ["code"] = "ACCOUNT_LOCKED" });
    }

    static object Session(TokenPair t, User u) => new
    {
        t.AccessToken,
        t.AccessTokenExpiresAt,
        t.RefreshToken,
        User = new
        {
            u.Id, u.Email, u.FirstName, u.LastName,
            Role = TokenService.RoleClaim(u.Role),
            u.IsEventCoordinator,
        },
    };

    // Never put request bodies or passwords in `after`; reasons only.
    static void Audit(TamsDbContext db, HttpContext http, Guid? actor, Guid entityId, string action, object? after) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = actor,
            Action = action,
            EntityType = "user",
            EntityId = entityId,
            After = after is null ? null : JsonSerializer.Serialize(after),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });

    static readonly User DummyUser = new();
    static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser, "not-a-real-password");
}
