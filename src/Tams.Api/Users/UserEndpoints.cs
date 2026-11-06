using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Users;

// ADMIN-managed staff and instructor accounts (FR-9.2). Reads: ADMIN and TECH_STAFF.
// Writes: ADMIN only. Schema-v1-decisions #4 (reset-password) and #7 (restore).
public static partial class UserEndpoints
{
    const int MinPasswordLength = 8;
    static readonly PasswordHasher<User> Hasher = new();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/users").WithTags("Users");

        g.MapGet("", List).RequireRoles(Roles.Admin, Roles.TechStaff);
        g.MapGet("{id:guid}", Get).RequireRoles(Roles.Admin, Roles.TechStaff);
        g.MapPost("", Create).RequireRoles(Roles.Admin);
        g.MapPut("{id:guid}", Update).RequireRoles(Roles.Admin);
        g.MapPost("{id:guid}/deactivate", (Guid id, TamsDbContext db, HttpContext http, CancellationToken ct) =>
            SetStatus(id, RecordStatus.Inactive, "USER_DEACTIVATE", db, http, ct)).RequireRoles(Roles.Admin);
        g.MapPost("{id:guid}/activate", (Guid id, TamsDbContext db, HttpContext http, CancellationToken ct) =>
            SetStatus(id, RecordStatus.Active, "USER_ACTIVATE", db, http, ct)).RequireRoles(Roles.Admin);
        g.MapDelete("{id:guid}", Delete).RequireRoles(Roles.Admin);
        g.MapPost("{id:guid}/restore", Restore).RequireRoles(Roles.Admin);
        g.MapPost("{id:guid}/reset-password", ResetPassword).RequireRoles(Roles.Admin);
        return app;
    }

    static async Task<IResult> List(TamsDbContext db, CancellationToken ct,
        string? role, string? status, string? q, bool includeDeleted = false, int page = 1, int pageSize = 25)
    {
        var errors = new Dictionary<string, string[]>();
        UserRole? roleFilter = null;
        RecordStatus? statusFilter = null;
        if (role is not null)
        {
            if (ParseRole(role) is { } r) roleFilter = r; else errors["role"] = ["Unknown role."];
        }
        if (status is not null)
        {
            if (ParseStatus(status) is { } s) statusFilter = s; else errors["status"] = ["Must be ACTIVE or INACTIVE."];
        }
        if (page < 1) errors["page"] = ["Must be 1 or greater."];
        if (pageSize is < 1 or > 100) errors["pageSize"] = ["Must be between 1 and 100."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var query = db.Users.AsNoTracking().AsQueryable();
        if (!includeDeleted) query = query.Where(u => u.DeletedAt == null);
        if (roleFilter is { } rf) query = query.Where(u => u.Role == rf);
        if (statusFilter is { } sf) query = query.Where(u => u.Status == sf);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(u => EF.Functions.ILike(u.FirstName, like) || EF.Functions.ILike(u.LastName, like)
                || EF.Functions.ILike(u.Email!, like) || EF.Functions.ILike(u.EmployeeNo, like));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(u => u.LastName).ThenBy(u => u.FirstName).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new UserPage(rows.Select(UserDto.From).ToList(), page, pageSize, total));
    }

    static async Task<IResult> Get(Guid id, TamsDbContext db, CancellationToken ct, bool includeDeleted = false)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == id && (includeDeleted || x.DeletedAt == null), ct);
        return u is null ? NotFound() : Results.Ok(UserDto.From(u));
    }

    static async Task<IResult> Create(CreateUserRequest req, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var errors = Validate(req.Email, req.EmployeeNo, req.FirstName, req.LastName, req.Role, out var role);
        if (string.IsNullOrEmpty(req.Password) || req.Password.Length < MinPasswordLength)
            errors["password"] = [$"Must be at least {MinPasswordLength} characters."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var email = req.Email!.Trim();
        var employeeNo = req.EmployeeNo!.Trim();
        if (await EmailTaken(db, email, null, ct)) return EmailTakenProblem();
        if (await db.Users.AnyAsync(u => u.DeletedAt == null && u.EmployeeNo == employeeNo, ct)) return EmployeeNoTakenProblem();
        if (req.DefaultRoomId is { } roomId && await RoomMissing(db, roomId, ct)) return RoomProblem();

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            EmployeeNo = employeeNo,
            FirstName = req.FirstName!.Trim(),
            LastName = req.LastName!.Trim(),
            Role = role,
            IsEventCoordinator = req.IsEventCoordinator,
            DefaultRoomId = req.DefaultRoomId,
            Status = RecordStatus.Active,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = Hasher.HashPassword(user, req.Password!);
        db.Users.Add(user);
        Audit(db, http, "USER_CREATE", user.Id, null, Snapshot(user));

        return await Save(db, ct, () => Results.Created($"/users/{user.Id}", UserDto.From(user)));
    }

    static async Task<IResult> Update(Guid id, UpdateUserRequest req, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var errors = Validate(req.Email, req.EmployeeNo, req.FirstName, req.LastName, req.Role, out var role);
        if (errors.Count > 0) return Problems.Validation(errors);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null, ct);
        if (user is null) return NotFound();

        var email = req.Email!.Trim();
        var employeeNo = req.EmployeeNo!.Trim();
        if (await EmailTaken(db, email, id, ct)) return EmailTakenProblem();
        if (await db.Users.AnyAsync(u => u.DeletedAt == null && u.EmployeeNo == employeeNo && u.Id != id, ct)) return EmployeeNoTakenProblem();
        if (req.DefaultRoomId is { } roomId && await RoomMissing(db, roomId, ct)) return RoomProblem();

        if (role != user.Role)
        {
            if (user.Id == CurrentUserId(http))
                return Problems.Conflict("CANNOT_CHANGE_OWN_ROLE", "You cannot change your own role.");
            if (user.Role == UserRole.Admin && user.Status == RecordStatus.Active && await IsLastActiveAdmin(db, user, ct))
                return Problems.Conflict("LAST_ADMIN", "At least one active administrator must remain.");
        }

        var before = Snapshot(user);
        user.Email = email;
        user.UserName = email;
        user.NormalizedEmail = email.ToUpperInvariant();
        user.NormalizedUserName = email.ToUpperInvariant();
        user.EmployeeNo = employeeNo;
        user.FirstName = req.FirstName!.Trim();
        user.LastName = req.LastName!.Trim();
        if (role != user.Role)
        {
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await RefreshTokens.RevokeAllAsync(db, user.Id, DateTimeOffset.UtcNow, ct);
        }
        user.Role = role;
        user.IsEventCoordinator = req.IsEventCoordinator;
        user.DefaultRoomId = req.DefaultRoomId;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        Audit(db, http, "USER_UPDATE", id, before, Snapshot(user));

        return await Save(db, ct, () => Results.Ok(UserDto.From(user)));
    }

    static async Task<IResult> SetStatus(Guid id, RecordStatus status, string action,
        TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null, ct);
        if (user is null) return NotFound();

        if (status == RecordStatus.Inactive)
        {
            if (user.Id == CurrentUserId(http))
                return Problems.Conflict("CANNOT_DEACTIVATE_SELF", "You cannot deactivate your own account.");
            if (user.Role == UserRole.Admin && user.Status == RecordStatus.Active && await IsLastActiveAdmin(db, user, ct))
                return Problems.Conflict("LAST_ADMIN", "At least one active administrator must remain.");
        }

        if (user.Status != status)
        {
            var before = Snapshot(user);
            user.Status = status;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await RefreshTokens.RevokeAllAsync(db, user.Id, user.UpdatedAt, ct);
            user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            Audit(db, http, action, id, before, Snapshot(user));
        }
        return await Save(db, ct, () => Results.Ok(UserDto.From(user)));
    }

    static async Task<IResult> Delete(Guid id, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null, ct);
        if (user is null) return NotFound();

        if (user.Id == CurrentUserId(http))
            return Problems.Conflict("CANNOT_DELETE_SELF", "You cannot delete your own account.");
        if (user.Role == UserRole.Admin && user.Status == RecordStatus.Active && await IsLastActiveAdmin(db, user, ct))
            return Problems.Conflict("LAST_ADMIN", "At least one active administrator must remain.");

        var before = Snapshot(user);
        user.DeletedAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = user.DeletedAt.Value;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await RefreshTokens.RevokeAllAsync(db, user.Id, user.UpdatedAt, ct);
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        Audit(db, http, "USER_DELETE", id, before, Snapshot(user));
        return await Save(db, ct, () => Results.NoContent());
    }

    static async Task<IResult> Restore(Guid id, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt != null, ct);
        if (user is null) return NotFound();

        // Decision #7: check before clearing the flag. The unique indexes are partial (live rows only),
        // so a live account may have taken the email or employee number since this one was deleted.
        var conflicts = new List<string>();
        if (await db.Users.AnyAsync(u => u.Id != id && u.DeletedAt == null && u.NormalizedEmail == user.NormalizedEmail, ct))
            conflicts.Add("email");
        if (await db.Users.AnyAsync(u => u.Id != id && u.DeletedAt == null && u.EmployeeNo == user.EmployeeNo, ct))
            conflicts.Add("employeeNo");
        if (conflicts.Count > 0)
            return Problems.Conflict("RESTORE_CONFLICT",
                $"Cannot restore: another account already uses this {string.Join(" and ", conflicts)}.");

        var before = Snapshot(user);
        user.DeletedAt = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        Audit(db, http, "USER_RESTORE", id, before, Snapshot(user));
        return await Save(db, ct, () => Results.Ok(UserDto.From(user)));
    }

    static async Task<IResult> ResetPassword(Guid id, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null, ct);
        if (user is null) return NotFound();

        var temp = GeneratePassword();
        user.PasswordHash = Hasher.HashPassword(user, temp);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await RefreshTokens.RevokeAllAsync(db, user.Id, DateTimeOffset.UtcNow, ct); // sign the user out everywhere
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        Audit(db, http, "USER_RESET_PASSWORD", id, null, null); // never records the password

        return await Save(db, ct, () =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new ResetPasswordResponse(id, temp));
        });
    }

    // ---- helpers ----

    static IResult NotFound() => Problems.NotFound("USER_NOT_FOUND", "No such user.");
    static IResult EmailTakenProblem() => Problems.Conflict("EMAIL_TAKEN", "Another account already uses this email.");
    static IResult EmployeeNoTakenProblem() => Problems.Conflict("EMPLOYEE_NO_TAKEN", "Another account already uses this employee number.");
    static IResult RoomProblem() => Problems.Validation(new Dictionary<string, string[]> { ["defaultRoomId"] = ["Room does not exist."] });

    // Live rows only: the unique indexes are partial (WHERE deleted_at IS NULL).
    static Task<bool> EmailTaken(TamsDbContext db, string email, Guid? exceptId, CancellationToken ct)
    {
        var normalized = email.ToUpperInvariant();
        return db.Users.AnyAsync(u => u.DeletedAt == null && u.NormalizedEmail == normalized && u.Id != exceptId, ct);
    }

    static async Task<bool> RoomMissing(TamsDbContext db, Guid roomId, CancellationToken ct) =>
        !await db.Rooms.AnyAsync(r => r.Id == roomId && r.DeletedAt == null, ct);

    static async Task<bool> IsLastActiveAdmin(TamsDbContext db, User user, CancellationToken ct) =>
        !await db.Users.AnyAsync(u => u.Id != user.Id && u.Role == UserRole.Admin
            && u.Status == RecordStatus.Active && u.DeletedAt == null, ct);

    static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub"), out var id) ? id : null;

    static UserRole? ParseRole(string? s) => s?.Trim().ToUpperInvariant() switch
    {
        "ADMIN" => UserRole.Admin,
        "TECH_STAFF" => UserRole.TechStaff,
        "CUSTODIAN" => UserRole.Custodian,
        "INSTRUCTOR" => UserRole.Instructor,
        _ => null,
    };

    static RecordStatus? ParseStatus(string? s) => s?.Trim().ToUpperInvariant() switch
    {
        "ACTIVE" => RecordStatus.Active,
        "INACTIVE" => RecordStatus.Inactive,
        _ => null,
    };

    static Dictionary<string, string[]> Validate(string? email, string? employeeNo, string? first, string? last,
        string? roleText, out UserRole role)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 256 || !EmailPattern().IsMatch(email.Trim()))
            errors["email"] = ["A valid email is required."];
        if (string.IsNullOrWhiteSpace(employeeNo) || employeeNo.Trim().Length > 30)
            errors["employeeNo"] = ["Required, up to 30 characters."];
        if (string.IsNullOrWhiteSpace(first) || first.Trim().Length > 80)
            errors["firstName"] = ["Required, up to 80 characters."];
        if (string.IsNullOrWhiteSpace(last) || last.Trim().Length > 80)
            errors["lastName"] = ["Required, up to 80 characters."];
        var parsed = ParseRole(roleText);
        if (parsed is null) errors["role"] = ["Must be ADMIN, TECH_STAFF, CUSTODIAN or INSTRUCTOR."];
        role = parsed ?? UserRole.Instructor;
        return errors;
    }

    static string Snapshot(User u) => JsonSerializer.Serialize(new
    {
        u.Email, u.EmployeeNo, u.FirstName, u.LastName, Role = UserDto.RoleLabel(u.Role),
        u.IsEventCoordinator, u.DefaultRoomId, Status = UserDto.StatusLabel(u.Status), u.DeletedAt,
    });

    static void Audit(TamsDbContext db, HttpContext http, string action, Guid entityId, string? before, string? after) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = CurrentUserId(http),
            Action = action,
            EntityType = "user",
            EntityId = entityId,
            Before = before,
            After = after,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });

    // A unique-index race between the pre-check and the insert still surfaces as a stable 409.
    static async Task<IResult> Save(TamsDbContext db, CancellationToken ct, Func<IResult> ok)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problems.Conflict("CONCURRENT_UPDATE", "The account was changed by someone else. Reload and retry.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            return pg.ConstraintName?.Contains("employee_no", StringComparison.OrdinalIgnoreCase) == true
                ? EmployeeNoTakenProblem()
                : EmailTakenProblem();
        }
    }

    // 16 chars, unambiguous alphabet, at least one of each class so Identity's default rules pass.
    static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digit = "23456789", symbol = "!@#$%*?";
        const string all = upper + lower + digit + symbol;
        var chars = new List<char> { Pick(upper), Pick(lower), Pick(digit), Pick(symbol) };
        while (chars.Count < 16) chars.Add(Pick(all));
        RandomNumberGenerator.Shuffle(CollectionsMarshal.AsSpan(chars));
        return new string(chars.ToArray());

        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }
}
