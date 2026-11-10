using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Facilities;
using Tams.Api.Http;
using Tams.Api.Live;
using Tams.Api.Settings;

namespace Tams.Api.Loans;

public sealed record StaffReturnRequest(string? Note);

/// <summary>Loans: list, detail, photo and staff return (PRD 6.3).</summary>
public static class LoanEndpoints
{
    static readonly string[] Readers = [Roles.Admin, Roles.TechStaff, Roles.Custodian, Roles.Instructor];
    static readonly string[] Staff = [Roles.Admin, Roles.TechStaff];

    public static IEndpointRouteBuilder MapLoanEndpoints(this IEndpointRouteBuilder app)
    {
        var loans = app.MapGroup("/loans").WithTags("Loans");
        loans.MapGet("", List).RequireRoles(Readers);
        loans.MapGet("{id:guid}", Get).RequireRoles(Readers);
        loans.MapGet("{id:guid}/photo", Photo).RequireRoles(Readers);
        loans.MapPost("{id:guid}/return", StaffReturn).RequireRoles(Staff);
        return app;
    }

    static bool IsWatcher(ClaimsPrincipal u) => u.IsInRole(Roles.Admin) || u.IsInRole(Roles.TechStaff) || u.IsInRole(Roles.Custodian);

    static IResult NotFound() => Problems.NotFound("LOAN_NOT_FOUND", "Loan not found.");

    static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    // ---------------------------------------------------------------- loans

    static async Task<IResult> List(HttpContext http, TamsDbContext db, CancellationToken ct,
        string? status, Guid? roomId, string? approvedBy, Guid? studentId, Guid? assetId, Guid? eventId, string? type,
        string? q, string? sort, int page = 1, int pageSize = 25)
    {
        var me = AssetAudit.ActorId(http.User);
        var errors = new Dictionary<string, string[]>();
        var query = db.Loans.AsNoTracking().AsQueryable();
        if (status is not null)
        {
            if (status.Equals("open", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue);
            else if (Support.TryParse<LoanStatus>(status, out var st)) query = query.Where(l => l.Status == st);
            else errors["status"] = ["ACTIVE, OVERDUE, RETURNED, MISSING or open."];
        }
        if (type is not null)
        {
            if (Support.TryParse<LoanType>(type, out var t)) query = query.Where(l => l.Type == t);
            else errors["type"] = ["STANDARD or EVENT."];
        }
        if (approvedBy is not null)
        {
            Guid? who = approvedBy.Equals("me", StringComparison.OrdinalIgnoreCase) ? me : Guid.TryParse(approvedBy, out var g) ? g : null;
            if (who is null) errors["approvedBy"] = ["'me' or a user id."];
            else query = query.Where(l => l.ApprovedByUserId == who || l.CoordinatorUserId == who);
        }
        var sortKey = sort?.TrimStart('-').ToLowerInvariant();
        if (sortKey is not (null or "" or "checkedoutat" or "dueat")) errors["sort"] = ["checkedOutAt or dueAt, optionally prefixed with '-'."];
        if (errors.Count > 0) return Problems.Validation(errors);

        // PERMISSIONS.md row scoping: an instructor sees only loans they approved or coordinate (FR-2.5).
        if (!IsWatcher(http.User)) query = query.Where(l => l.ApprovedByUserId == me || l.CoordinatorUserId == me);
        if (roomId is not null) query = query.Where(l => l.DestinationRoomId == roomId);
        if (studentId is not null) query = query.Where(l => l.StudentId == studentId || l.ReturnedByStudentId == studentId);
        if (assetId is not null) query = query.Where(l => l.AssetId == assetId);
        if (eventId is not null) query = query.Where(l => l.EventId == eventId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(l => EF.Functions.ILike(l.LoanNo, like)
                || db.Assets.Any(a => a.Id == l.AssetId && (EF.Functions.ILike(a.AssetCode, like) || EF.Functions.ILike(a.Name, like)))
                || db.Students.Any(s => s.Id == l.StudentId && (EF.Functions.ILike(s.StudentNo, like) || EF.Functions.ILike(s.LastName, like))));
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var asc = sort is not null && !sort.StartsWith('-');
        query = (sortKey, asc) switch
        {
            ("dueat", true) => query.OrderBy(l => l.DueAt),
            ("dueat", false) => query.OrderByDescending(l => l.DueAt),
            ("checkedoutat", true) => query.OrderBy(l => l.CheckedOutAt),
            _ => query.OrderByDescending(l => l.CheckedOutAt),
        };
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new { items = await DtosAsync(db, rows, ct), page, pageSize, total });
    }

    static async Task<Loan?> VisibleAsync(TamsDbContext db, ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var loan = await db.Loans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        var me = AssetAudit.ActorId(user);
        return loan is null || IsWatcher(user) || loan.ApprovedByUserId == me || loan.CoordinatorUserId == me ? loan : null;
    }

    static async Task<IResult> Get(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var loan = await VisibleAsync(db, http.User, id, ct);
        return loan is null ? NotFound() : Results.Ok(await DetailAsync(db, loan, ct));
    }

    /// <summary>Loan detail: summary, approval ledger (FR-2.4) and every ledger row.</summary>
    static async Task<object> DetailAsync(TamsDbContext db, Loan loan, CancellationToken ct)
    {
        var approval = loan.ApprovalId is null ? null : await (
            from a in db.Approvals.AsNoTracking()
            where a.Id == loan.ApprovalId
            join u in db.Users on a.InstructorUserId equals u.Id
            select new { a, FullName = u.FirstName + " " + u.LastName }).FirstOrDefaultAsync(ct);
        var ledger = await db.TransactionLedger.AsNoTracking().Where(x => x.LoanId == loan.Id)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(ct);
        return new
        {
            loan = (await DtosAsync(db, [loan], ct))[0],
            approval = approval is null ? null : new
            {
                approval.a.Id, instructor = new { id = approval.a.InstructorUserId, fullName = approval.FullName },
                decision = Support.Label(approval.a.Decision), approval.a.DestinationRoomId, approval.a.ClassLengthMinutes,
                approval.a.RequestedAt, approval.a.DecidedAt, approval.a.DecidedDevice,
            },
            ledger = ledger.Select(x => new
            {
                x.Id, @event = Support.Label(x.Event), x.OccurredAt, x.StudentId, x.InstructorUserId, x.ActorUserId,
                x.StationId, x.PhotoFileId, x.ApprovalDecidedAt, details = Parse(x.Details),
            }),
        };
    }

    /// <summary>Loan rows in the shape of the web Loan model (Gap Map), related rows loaded in a few queries.</summary>
    public static async Task<List<object>> DtosAsync(TamsDbContext db, IReadOnlyList<Loan> loans, CancellationToken ct)
    {
        var assetIds = loans.Select(l => l.AssetId).Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AssetCode, a.Name, a.Status }).ToDictionaryAsync(a => a.Id, ct);
        var studentIds = loans.SelectMany(l => new[] { l.StudentId, l.ReturnedByStudentId }).OfType<Guid>().Distinct().ToList();
        var students = await (
            from s in db.Students.AsNoTracking()
            where studentIds.Contains(s.Id)
            join sec in db.Sections on s.SectionId equals sec.Id
            select new { s.Id, s.StudentNo, FullName = s.FirstName + " " + s.LastName, SectionCode = sec.Code }).ToDictionaryAsync(s => s.Id, ct);
        var userIds = loans.SelectMany(l => new[] { l.ApprovedByUserId, l.CoordinatorUserId }).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, FullName = u.FirstName + " " + u.LastName }).ToDictionaryAsync(u => u.Id, ct);
        var roomIds = loans.Select(l => l.DestinationRoomId).OfType<Guid>().Distinct().ToList();
        var rooms = await db.Rooms.AsNoTracking().Where(r => roomIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Code, r.Name }).ToDictionaryAsync(r => r.Id, ct);
        var eventIds = loans.Select(l => l.EventId).OfType<Guid>().Distinct().ToList();
        var events = await db.Events.AsNoTracking().Where(e => eventIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Title }).ToDictionaryAsync(e => e.Id, ct);
        var loanIds = loans.Select(l => l.Id).ToList();
        var warnings = await db.LateArrivalWarnings.AsNoTracking().Where(w => loanIds.Contains(w.LoanId))
            .ToDictionaryAsync(w => w.LoanId, w => w.WarningNo, ct);

        return loans.Select(l => (object)new
        {
            l.Id, l.LoanNo, type = Support.Label(l.Type), status = Support.Label(l.Status), custodyState = Support.Label(l.CustodyState),
            asset = assets.TryGetValue(l.AssetId, out var a) ? new { a.Id, a.AssetCode, a.Name, status = Support.Label(a.Status) } : null,
            student = l.StudentId is Guid sid && students.TryGetValue(sid, out var s) ? s : null,
            approvedBy = l.ApprovedByUserId is Guid ab && users.TryGetValue(ab, out var u1) ? u1 : null,
            coordinator = l.CoordinatorUserId is Guid co && users.TryGetValue(co, out var u2) ? u2 : null,
            @event = l.EventId is Guid ev && events.TryGetValue(ev, out var e) ? e : null,
            destinationRoom = l.DestinationRoomId is Guid rid && rooms.TryGetValue(rid, out var r) ? r : null,
            l.CheckedOutAt, l.TransitDeadlineAt, l.VerifiedAt, l.ClassLengthMinutes, l.ClassEndsAt, l.GraceMinutes, l.DueAt,
            l.OverdueAt, l.MissingAt, l.ReturnedAt,
            returnedBy = l.ReturnedByStudentId is Guid rb && students.TryGetValue(rb, out var s2) ? s2 : null,
            photoUrl = l.PhotoFileId is null ? null : $"/api/loans/{l.Id}/photo",
            lateArrivalWarningNo = warnings.TryGetValue(l.Id, out var w) ? w : (short?)null,
            l.Notes,
        }).ToList();
    }

    static async Task<IResult> Photo(Guid id, HttpContext http, TamsDbContext db, IConfiguration cfg, CancellationToken ct)
    {
        var loan = await VisibleAsync(db, http.User, id, ct);
        if (loan is null) return NotFound();
        return MediaStore.Serve(cfg, await db.MediaFiles.AsNoTracking().FirstOrDefaultAsync(m => m.Id == loan.PhotoFileId, ct));
    }

    /// <summary>FR-6.8 "record late return": staff close a loan when the item came back outside the kiosk.</summary>
    static async Task<IResult> StaffReturn(Guid id, StaffReturnRequest req, HttpContext http, TamsDbContext db,
        IHubContext<LiveHub> hub, CancellationToken ct)
    {
        var actor = AssetAudit.ActorId(http.User);
        var outbox = new Outbox();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var loan = await LoanRules.CloseAsync(db, outbox, id, DateTimeOffset.UtcNow, null, null, actor, null, Clean(req.Note), ct);
        if (loan is null) return await NotOpenAsync(db, id, ct);
        LoanRules.Audit(db, "LOAN_RETURNED_BY_STAFF", "loan", loan.Id, new { loan.LoanNo, note = Clean(req.Note) }, actor,
            ip: http.Connection.RemoteIpAddress?.ToString());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await outbox.FlushAsync(hub);
        return Results.Ok(await DetailAsync(db, (await db.Loans.AsNoTracking().FirstAsync(l => l.Id == id, ct)), ct));
    }

    static async Task<IResult> NotOpenAsync(TamsDbContext db, Guid id, CancellationToken ct) =>
        await db.Loans.AnyAsync(l => l.Id == id, ct)
            ? Problems.Conflict("LOAN_NOT_OPEN", "The loan is not open.")
            : NotFound();

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
