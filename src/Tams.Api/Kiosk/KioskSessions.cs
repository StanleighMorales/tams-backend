using System.Linq.Expressions;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Enrollment;
using Tams.Api.Facilities;
using Tams.Api.Live;
using Tams.Api.Loans;
using Tams.Api.Settings;
using static Tams.Api.Enrollment.EnrollmentSessions;

namespace Tams.Api.Kiosk;

/// <summary>
/// Shared rules for CHECKOUT, RETURN, EVENT_BATCH and EVENT_RETURN sessions (PRD 6.2). Enrollment
/// sessions keep their own rules in <see cref="EnrollmentSessions"/>.
///
/// Every mutation locks the session row (SELECT ... FOR UPDATE) inside a transaction, so the kiosk,
/// the instructor's phone and the sweeper never race on one session. A session past expires_at is
/// expired by whoever touches it first. Any end other than COMPLETED releases an item held AT_COUNTER.
/// </summary>
static class KioskSessions
{
    public static readonly string[] IdleLines = ["TAMS Counter", "Tap auth card", "or scan finger to", "return an item"];

    public static readonly Expression<Func<CheckoutSession, bool>> IsOpenExpr = s =>
        s.Status == SessionStatus.AwaitingFingerprint || s.Status == SessionStatus.AwaitingAsset
        || s.Status == SessionStatus.AwaitingPhoto || s.Status == SessionStatus.AwaitingApproval
        || s.Status == SessionStatus.AwaitingConfirm;

    public static async Task<CheckoutSession?> LockAsync(TamsDbContext db, Guid id, CancellationToken ct) =>
        (await db.CheckoutSessions.FromSqlInterpolated($"SELECT * FROM checkout_sessions WHERE id = {id} FOR UPDATE")
            .ToListAsync(ct)).FirstOrDefault();

    /// <summary>Ends an open session. The caller saves and commits.</summary>
    public static async Task EndAsync(TamsDbContext db, CheckoutSession s, SessionStatus status, string? failureCode,
        string[] lcd, Outbox outbox, DateTimeOffset now, CancellationToken ct)
    {
        s.Status = status;
        s.FailureCode = failureCode;
        s.CompletedAt = now;
        s.LcdLines = LcdJson.Write(lcd);

        // Items collected for a checkout go back to storage unless the loans were created (TAMS-121).
        if (status != SessionStatus.Completed && s.Type == SessionType.Checkout)
        {
            var held = await (from i in db.CheckoutSessionItems where i.SessionId == s.Id
                              join a in db.Assets on i.AssetId equals a.Id where a.Status == AssetStatus.AtCounter select a.Id).ToListAsync(ct);
            foreach (var assetId in held)
            {
                await db.Assets.Where(a => a.Id == assetId).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, AssetStatus.InStorage).SetProperty(a => a.UpdatedAt, now), ct);
                outbox.Staff("asset.statusChanged", LoanRules.StatusChanged(assetId, AssetStatus.AtCounter, AssetStatus.InStorage, null));
            }
        }

        var pending = await db.Approvals.FirstOrDefaultAsync(a => a.SessionId == s.Id && a.Decision == ApprovalDecision.Pending, ct);
        if (pending is not null) pending.Decision = ApprovalDecision.Expired;
    }

    /// <summary>Expires the locked session if its deadline passed. True when it did.</summary>
    public static async Task<bool> ExpireIfDueAsync(TamsDbContext db, CheckoutSession s, Outbox outbox, DateTimeOffset now, CancellationToken ct)
    {
        if (!IsOpen(s.Status) || s.ExpiresAt > now) return false;
        var code = s.Status == SessionStatus.AwaitingApproval ? "APPROVAL_TIMEOUT" : "SESSION_EXPIRED";
        await EndAsync(db, s, SessionStatus.Expired, code, Denied(s, code), outbox, now, ct);
        return true;
    }

    /// <summary>Sweeper: expire every open kiosk session past its deadline (FR-1.7).</summary>
    public static async Task ExpireStaleAsync(TamsDbContext db, Outbox outbox, DateTimeOffset now, CancellationToken ct)
    {
        var ids = await db.CheckoutSessions.AsNoTracking().Where(IsOpenExpr)
            .Where(s => s.Type != SessionType.Enrollment && s.ExpiresAt <= now).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var s = await LockAsync(db, id, ct);
            if (s is not null && await ExpireIfDueAsync(db, s, outbox, now, ct))
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>The last tapped item with its condition, so the kiosk can show both (F1 step 4). Null before any tap.</summary>
    public static async Task<object?> ItemAsync(TamsDbContext db, CheckoutSession s, CancellationToken ct)
    {
        if (s.AssetId is not Guid id) return null;
        var a = await db.Assets.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.AssetCode, x.Name, x.Condition }).FirstOrDefaultAsync(ct);
        return a is null ? null : new { a.AssetCode, a.Name, condition = EnumText.ToApi(a.Condition) };
    }

    /// <summary>The items a checkout session has collected, in tap order (E8). Null for other session types.</summary>
    public static async Task<object?> ItemsAsync(TamsDbContext db, CheckoutSession s, CancellationToken ct)
    {
        if (s.Type != SessionType.Checkout) return null;
        var rows = await (from i in db.CheckoutSessionItems.AsNoTracking() where i.SessionId == s.Id
                          join a in db.Assets.AsNoTracking() on i.AssetId equals a.Id orderby i.CreatedAt
                          select new { a.AssetCode, a.Name, a.Condition }).ToListAsync(ct);
        return rows.Select(a => new { a.AssetCode, a.Name, condition = EnumText.ToApi(a.Condition) }).ToList();
    }

    /// <summary>
    /// E8: how many items this student may take in one checkout. The smaller of max_items_per_transaction and what is left of
    /// max_active_loans_per_student (FR-1.9); ByLoanCap says the loan cap is the one that binds.
    /// </summary>
    public static async Task<(int Limit, bool ByLoanCap)> LimitAsync(TamsDbContext db, ISettings settings, Guid studentId, CancellationToken ct)
    {
        var perTransaction = await settings.GetIntAsync(SettingKeys.MaxItems, ct);
        var cap = await settings.GetIntAsync(SettingKeys.MaxActiveLoans, ct);
        var active = await db.Loans.CountAsync(l => l.StudentId == studentId && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue), ct);
        var left = Math.Max(0, cap - active);
        return (Math.Min(perTransaction, left), left < perTransaction);
    }

    public static object View(CheckoutSession s, DateTimeOffset now, int pollSeconds, object? item = null, object? items = null)
    {
        var lines = LcdJson.Read(s);
        if (s.Status == SessionStatus.AwaitingApproval)
            lines[2] = $"{Math.Max(0, (int)(s.ExpiresAt - now).TotalSeconds)}s left";
        return new
        {
            sessionId = s.Id, type = Support.Label(s.Type), status = Support.Label(s.Status), nextStep = NextStep(s.Status),
            lcdLines = lines, finished = !IsOpen(s.Status), failureCode = s.FailureCode, s.ExpiresAt,
            pollIntervalSeconds = pollSeconds, item, items,
        };
    }

    static string NextStep(SessionStatus s) => s switch
    {
        SessionStatus.AwaitingFingerprint => "FINGERPRINT",
        SessionStatus.AwaitingAsset => "ASSET",
        SessionStatus.AwaitingPhoto => "PHOTO",
        SessionStatus.AwaitingApproval => "WAIT",
        SessionStatus.AwaitingConfirm => "CONFIRM",
        _ => "DONE",
    };

    // ---------------------------------------------------------------- LCD text (PRD 6.7, 20x4)

    public static string Instr(User u) => $"Instr: {(u.FirstName.Length > 0 ? $"{u.FirstName[0]}. " : "")}{u.LastName}";

    public static string Hi(Student s) => $"Hi, {s.FirstName} {(s.LastName.Length > 0 ? $"{s.LastName[0]}." : "")}";

    public static string Short(Student s) => $"{s.FirstName} {(s.LastName.Length > 0 ? $"{s.LastName[0]}." : "")}";

    /// <summary>"Laptop LP-014": the name cut so the asset code always fits on the line.</summary>
    public static string ItemLine(Asset a)
    {
        var room = Math.Max(0, 20 - a.AssetCode.Length - 1);
        return $"{(a.Name.Length > room ? a.Name[..room] : a.Name)} {a.AssetCode}".Trim();
    }

    /// <summary>"Laptop - GOOD", the name cut to fit 20 columns.</summary>
    public static string NameAndCondition(Asset a)
    {
        var cond = EnumText.ToApi(a.Condition);
        var room = 20 - cond.Length - 3;
        return $"{(a.Name.Length > room ? a.Name[..room] : a.Name)} - {cond}";
    }

    /// <summary>"3 items: LP-014 +2" for the confirm screen of a multi-item checkout.</summary>
    public static string Several(IReadOnlyList<Asset> a)
    {
        var line = $"{a.Count} items: {a[0].AssetCode} +{a.Count - 1}";
        return line.Length <= 20 ? line : $"{a.Count} items +{a.Count - 1} more";
    }

    public static string Reason(string code) => code switch
    {
        "CARD_UNKNOWN" => "Card not recognized",
        "CARD_INACTIVE" => "Card not active",
        "INSTRUCTOR_INACTIVE" => "Account inactive",
        "INSTRUCTOR_FROZEN" => "Instructor frozen",
        "NO_ACTIVE_EVENT" => "No active event",
        "NO_EVENT_LOANS" => "No open event loans",
        "FINGERPRINT_NOT_RECOGNIZED" => "Unknown fingerprint",
        "STUDENT_INACTIVE" => "Student inactive",
        "STUDENT_BLOCKED" => "Borrowing blocked",
        "STUDENT_ON_HOLD" => "Borrowing on hold",
        "LOAN_LIMIT_REACHED" => "Loan limit reached",
        "TAG_UNKNOWN" => "Unknown item tag",
        "ASSET_NOT_BORROWABLE" => "Item not borrowable",
        "ASSET_NOT_AVAILABLE" => "Item not available",
        "ASSET_DAMAGED" => "Item damaged",
        "ALREADY_ADDED" => "Already added",
        "PHOTO_FAILED" => "Camera error",
        "APPROVAL_DECLINED" => "Declined by instr.",
        "APPROVAL_TIMEOUT" => "No approval in time",
        "NOT_ON_LOAN" => "Item not on loan",
        "CANCELLED" => "Cancelled",
        _ => "Session timed out",
    };

    public static string[] Denied(CheckoutSession s, string code) =>
        code == "FINGERPRINT_NOT_RECOGNIZED" ? ["Fingerprint not", "recognized", "See lab staff", ""] // 20-col LCD
        : code == "ASSET_DAMAGED" ? ["Item damaged", "See lab staff", "", ""]
        : [Reason(code), s.Type == SessionType.Return || s.Type == SessionType.EventReturn ? "Return failed" : "Borrow denied", "See lab staff", ""];

    // ---------------------------------------------------------------- fingerprint 1:N (FR-1.3)

    /// <summary>
    /// Legacy path: matches raw template bytes against RAW_V1 rows (see <see cref="FingerprintMatcher"/>).
    /// Threshold: Kiosk:MatchThreshold, default <see cref="FingerprintMatcher.DefaultThreshold"/>.
    /// </summary>
    public static async Task<Guid?> MatchAsync(TamsDbContext db, IServiceProvider sp, byte[] probe, CancellationToken ct)
    {
        var threshold = sp.GetRequiredService<IConfiguration>().GetValue("Kiosk:MatchThreshold", FingerprintMatcher.DefaultThreshold);
        return FingerprintMatcher.Match(probe, (await TemplateCache.GetAsync(db, sp, ct)).Raw, threshold);
    }

    /// <summary>
    /// Real path (TAMS-128): matches a SourceAFIS template built from the uploaded image against SOURCEAFIS_V1 rows.
    /// Threshold: Kiosk:SourceAfisThreshold, default <see cref="SourceAfisMatcher.DefaultThreshold"/> (provisional).
    /// </summary>
    public static async Task<Guid?> MatchAsync(TamsDbContext db, IServiceProvider sp, SourceAFIS.FingerprintTemplate probe, CancellationToken ct)
    {
        var threshold = sp.GetRequiredService<IConfiguration>().GetValue("Kiosk:SourceAfisThreshold", SourceAfisMatcher.DefaultThreshold);
        return SourceAfisMatcher.Match(probe, (await TemplateCache.GetAsync(db, sp, ct)).Afis, threshold);
    }

    /// <summary>
    /// Next loan number, L-2026-000123. The advisory lock serialises numbering across concurrent
    /// checkouts until the caller's transaction ends.
    /// </summary>
    public static async Task<string> NextLoanNoAsync(TamsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(727001)", ct);
        var prefix = $"L-{LocalTime.ToLocal(now).Year}-";
        var last = await db.Loans.Where(l => l.LoanNo.StartsWith(prefix)).OrderByDescending(l => l.LoanNo)
            .Select(l => l.LoanNo).FirstOrDefaultAsync(ct);
        var n = last is null ? 1 : int.Parse(last[prefix.Length..]) + 1;
        return $"{prefix}{n:D6}";
    }
}
