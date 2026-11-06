using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Enrollment;

/// <summary>
/// Enrollment session rules. checkout_sessions has no finger_index column (schema change not
/// allowed in this ticket), so for ENROLLMENT sessions lcd_lines holds
/// {"fingerIndex":n,"lines":[4 strings]} instead of a bare array. All reads and writes of that
/// shape live in <see cref="Meta"/>. Recommended follow-up: add finger_index smallint null.
///
/// State machine (anything else is 409 INVALID_SESSION_STATE):
///   AWAITING_FINGERPRINT -> AWAITING_CONFIRM  template submitted and stored
///   AWAITING_CONFIRM     -> COMPLETED         confirm
///   AWAITING_FINGERPRINT | AWAITING_CONFIRM -> CANCELLED   cancel (staff or kiosk)
///   AWAITING_FINGERPRINT | AWAITING_CONFIRM -> EXPIRED     expires_at passed (applied lazily)
/// A template stored for an AWAITING_CONFIRM session is deleted again on cancel or expiry.
/// </summary>
public static class EnrollmentSessions
{
    public const string TimeoutSettingKey = "enrollment_session_timeout_seconds";
    public const int DefaultTimeoutSeconds = 120;

    static readonly SessionStatus[] Open =
    [
        SessionStatus.AwaitingFingerprint, SessionStatus.AwaitingAsset, SessionStatus.AwaitingPhoto,
        SessionStatus.AwaitingApproval, SessionStatus.AwaitingConfirm,
    ];

    public static bool IsOpen(SessionStatus s) => Array.IndexOf(Open, s) >= 0;

    public sealed record MetaData(short FingerIndex, string[] Lines);

    public static class Meta
    {
        public static string Write(short fingerIndex, params string[] lines) =>
            JsonSerializer.Serialize(new { fingerIndex, lines = Pad(lines) });

        public static MetaData Read(CheckoutSession s)
        {
            try
            {
                using var doc = JsonDocument.Parse(s.LcdLines);
                var idx = doc.RootElement.GetProperty("fingerIndex").GetInt16();
                var lines = doc.RootElement.GetProperty("lines").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
                return new MetaData(idx, Pad(lines));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return new MetaData(-1, Pad([]));
            }
        }

        static string[] Pad(string[] lines) =>
            Enumerable.Range(0, 4).Select(i => i < lines.Length ? Trunc(lines[i]) : "").ToArray();

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

    public static async Task<TimeSpan> TimeoutAsync(TamsDbContext db, CancellationToken ct)
    {
        var raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == TimeoutSettingKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (raw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Number
                    && doc.RootElement.TryGetInt32(out var secs) && secs is >= 15 and <= 3600)
                    return TimeSpan.FromSeconds(secs);
            }
            catch (JsonException) { /* fall through to default */ }
        }
        return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    /// <summary>
    /// Expire open enrollment sessions past expires_at (optionally only one student/session) and
    /// remove templates they had stored. Cheap enough to run on every start and every access;
    /// there is no background sweep, so nothing relies on one.
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
                await RemoveStoredTemplateAsync(db, sid, Meta.Read(s).FingerIndex, ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
    }

    public static Task<int> RemoveStoredTemplateAsync(TamsDbContext db, Guid studentId, short fingerIndex, CancellationToken ct) =>
        db.FingerprintTemplates.Where(t => t.StudentId == studentId && t.FingerIndex == fingerIndex).ExecuteDeleteAsync(ct);

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
