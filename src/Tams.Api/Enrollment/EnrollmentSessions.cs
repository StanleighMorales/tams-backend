using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Settings;

namespace Tams.Api.Enrollment;

/// <summary>
/// Enrollment session rules. The finger being enrolled lives in checkout_sessions.finger_index;
/// lcd_lines is the plain array of 4 display strings (see <see cref="LcdJson"/>).
///
/// State machine (anything else is 409 INVALID_SESSION_STATE):
///   AWAITING_FINGERPRINT -> AWAITING_CONFIRM  template submitted and stored
///   AWAITING_CONFIRM     -> COMPLETED         confirm
///   AWAITING_FINGERPRINT | AWAITING_CONFIRM -> CANCELLED   cancel (staff or kiosk)
///   AWAITING_FINGERPRINT | AWAITING_CONFIRM -> EXPIRED     expires_at passed (EnrollmentSweeper every
///                                                          30 s, and lazily on every access)
/// A template stored for an AWAITING_CONFIRM session is deleted again on cancel or expiry.
/// </summary>
public static class EnrollmentSessions
{
    public const string TimeoutSettingKey = "enrollment_session_timeout_seconds";

    static readonly SessionStatus[] Open =
    [
        SessionStatus.AwaitingFingerprint, SessionStatus.AwaitingAsset, SessionStatus.AwaitingPhoto,
        SessionStatus.AwaitingApproval, SessionStatus.AwaitingConfirm,
    ];

    public static bool IsOpen(SessionStatus s) => Array.IndexOf(Open, s) >= 0;

    /// <summary>The finger index of an enrollment session (-1 if somehow unset).</summary>
    public static short FingerOf(CheckoutSession s) => s.FingerIndex ?? -1;

    public static class LcdJson
    {
        public static string Write(params string[] lines) => JsonSerializer.Serialize(Pad(lines));

        public static string[] Read(CheckoutSession s)
        {
            try { return Pad(JsonSerializer.Deserialize<string[]>(s.LcdLines) ?? []); }
            catch (JsonException) { return Pad([]); }
        }

        static string[] Pad(string[] lines) =>
            Enumerable.Range(0, 4).Select(i => i < lines.Length ? Trunc(lines[i] ?? "") : "").ToArray();

        static string Trunc(string s) => s.Length <= 20 ? s : s[..20];
    }

    public static string[] Lcd(SessionStatus status, Student student, short fingerIndex) => status switch
    {
        SessionStatus.AwaitingFingerprint =>
            [$"Enroll {student.FirstName} {student.LastName[..1]}.", $"Finger {fingerIndex}", "Place finger on", "sensor"],
        SessionStatus.AwaitingConfirm => ["Fingerprint captured", "OK to save", "X to cancel", ""],
        SessionStatus.Completed => ["Enrolled!", "", "", ""],
        SessionStatus.Cancelled => ["Enrollment cancelled", "See lab staff", "", ""],
        SessionStatus.Expired => ["Session expired", "See lab staff", "", ""],
        _ => ["See lab staff", "", "", ""],
    };

    public static async Task<TimeSpan> TimeoutAsync(ISettings settings, CancellationToken ct) =>
        TimeSpan.FromSeconds(await settings.GetIntAsync(TimeoutSettingKey, ct));

    /// <summary>
    /// Expire open enrollment sessions past expires_at (optionally only one student/session) and
    /// remove templates they had stored. Cheap enough to run on every start and every access, and
    /// <see cref="EnrollmentSweeper"/> runs it every 30 s so abandoned sessions free their station and
    /// templates even when nobody calls.
    /// </summary>
    public static async Task ExpireStaleAsync(TamsDbContext db, CancellationToken ct, Guid? sessionId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var q = db.CheckoutSessions.Where(s => s.Type == SessionType.Enrollment
            && s.ExpiresAt <= now && (s.Status == SessionStatus.AwaitingFingerprint || s.Status == SessionStatus.AwaitingConfirm));
        if (sessionId is not null) q = q.Where(s => s.Id == sessionId);

        foreach (var s in await q.ToListAsync(ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var wasConfirm = s.Status == SessionStatus.AwaitingConfirm;
            var claimed = await db.CheckoutSessions
                .Where(x => x.Id == s.Id && x.Status == s.Status)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.Status, SessionStatus.Expired)
                    .SetProperty(x => x.FailureCode, "SESSION_EXPIRED")
                    .SetProperty(x => x.CompletedAt, now), ct);
            if (claimed == 1 && wasConfirm && s.StudentId is Guid sid)
                await RemoveStoredTemplateAsync(db, sid, FingerOf(s), ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
    }

    public static async Task<int> RemoveStoredTemplateAsync(TamsDbContext db, Guid studentId, short fingerIndex, CancellationToken ct)
    {
        var n = await db.FingerprintTemplates.Where(t => t.StudentId == studentId && t.FingerIndex == fingerIndex).ExecuteDeleteAsync(ct);
        Kiosk.TemplateCache.Invalidate();
        return n;
    }

    public static async Task WriteAuditAsync(TamsDbContext db, HttpContext http, Guid? actorUserId, Guid? actorStationId,
        string action, Guid studentId, string? beforeJson, string? afterJson, CancellationToken ct)
    {
        // Never put template bytes in here: only finger index, sensor model, quality, station.
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = actorUserId,
            ActorStationId = actorStationId,
            Action = action,
            EntityType = "student",
            EntityId = studentId,
            Before = beforeJson,
            After = afterJson,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
