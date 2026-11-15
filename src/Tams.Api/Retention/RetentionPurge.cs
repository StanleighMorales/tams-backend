using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Loans;
using Tams.Api.Settings;

namespace Tams.Api.Retention;

public sealed record PurgeSummary(Guid PurgeId, string Kind, DateTimeOffset PurgedAt, Guid? PurgedByUserId,
    IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// The end-of-project deletion the school letter promises (G5): all fingerprint templates, and all checkout
/// photos. Each run writes one audit_log row (entity_type data_purge) holding the counts; the printable
/// evidence is rendered from that row. Ledger, loan and audit rows are kept.
/// </summary>
public static class RetentionPurge
{
    public const string BiometricsAction = "BIOMETRICS_PURGED";
    public const string PhotosAction = "CHECKOUT_PHOTOS_PURGED";
    public const string EntityType = "data_purge";

    public static async Task<PurgeSummary> PurgeBiometricsAsync(TamsDbContext db, Guid? actor, string? ip, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var students = await db.FingerprintTemplates.Select(t => t.StudentId).Distinct().CountAsync(ct);
        var templates = await db.FingerprintTemplates.ExecuteDeleteAsync(ct);
        var sessions = await db.CheckoutSessions.Where(s => s.Type == SessionType.Enrollment).ExecuteDeleteAsync(ct);
        Kiosk.TemplateCache.Invalidate();

        var summary = Audit(db, BiometricsAction, actor, ip, new Dictionary<string, int>
        {
            ["fingerprintTemplates"] = templates, ["studentsAffected"] = students, ["enrollmentSessions"] = sessions,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return summary;
    }

    public static async Task<PurgeSummary> PurgeCheckoutPhotosAsync(TamsDbContext db, IConfiguration cfg, Guid? actor, string? ip, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var files = await db.MediaFiles.Where(m => m.Kind == MediaKind.CheckoutPhoto).Select(m => m.StoragePath).ToListAsync(ct);

        // Links first (the media rows are the FK targets), then the rows. The ledger's append-only trigger
        // allows exactly this change: photo_file_id set to NULL (migration RetentionPurge).
        var sessions = await db.CheckoutSessions.Where(s => s.PhotoFileId != null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.PhotoFileId, (Guid?)null), ct);
        var loans = await db.Loans.Where(l => l.PhotoFileId != null)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.PhotoFileId, (Guid?)null).SetProperty(l => l.PhotoPurgedAt, now), ct);
        var ledger = await db.TransactionLedger.Where(l => l.PhotoFileId != null)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.PhotoFileId, (Guid?)null), ct);
        var rows = await db.MediaFiles.Where(m => m.Kind == MediaKind.CheckoutPhoto).ExecuteDeleteAsync(ct);

        var root = MediaStore.Root(cfg);
        int deleted = 0, missing = 0, failed = 0;
        foreach (var rel in files)
        {
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal)) { failed++; continue; } // never leave the media root
            try
            {
                if (File.Exists(full)) { File.Delete(full); deleted++; } else missing++;
            }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
        }

        var summary = Audit(db, PhotosAction, actor, ip, new Dictionary<string, int>
        {
            ["photoRecords"] = rows, ["filesDeleted"] = deleted, ["filesAlreadyMissing"] = missing, ["filesFailed"] = failed,
            ["sessionLinksCleared"] = sessions, ["loanLinksCleared"] = loans, ["ledgerLinksCleared"] = ledger,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return summary;
    }

    static PurgeSummary Audit(TamsDbContext db, string action, Guid? actor, string? ip, Dictionary<string, int> counts)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = actor, Action = action, EntityType = EntityType, EntityId = id,
            After = JsonSerializer.Serialize(counts), IpAddress = ip, CreatedAt = now,
        });
        return new PurgeSummary(id, action, now, actor, counts);
    }

    /// <summary>Scheduled clean-up (G5 phase 2): abandoned kiosk sessions and old read notifications.</summary>
    public static async Task<(int Sessions, int Notifications)> CleanUpAsync(TamsDbContext db, ISettings settings, DateTimeOffset now, CancellationToken ct)
    {
        var sessionCutoff = now.AddDays(-await settings.GetIntAsync(SettingKeys.KioskSessionRetentionDays, ct));
        var noticeCutoff = now.AddDays(-await settings.GetIntAsync(SettingKeys.NotificationRetentionDays, ct));

        // Only sessions that never produced a loan, approval or photo: those belong to the ledger and the photo purge.
        var sessions = await db.CheckoutSessions
            .Where(s => (s.Status == SessionStatus.Declined || s.Status == SessionStatus.Cancelled
                         || s.Status == SessionStatus.Expired || s.Status == SessionStatus.Failed)
                        && (s.CompletedAt ?? s.CreatedAt) < sessionCutoff && s.PhotoFileId == null
                        && !db.Loans.Any(l => l.SessionId == s.Id) && !db.Approvals.Any(a => a.SessionId == s.Id))
            .ExecuteDeleteAsync(ct);
        var notices = await db.Notifications.Where(n => n.ReadAt != null && n.ReadAt < noticeCutoff).ExecuteDeleteAsync(ct);
        return (sessions, notices);
    }
}

/// <summary>Daily; the first run is one interval after start.</summary>
public sealed class RetentionSweeper(IServiceScopeFactory scopes, ILogger<RetentionSweeper> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var (sessions, notices) = await RetentionPurge.CleanUpAsync(
                        scope.ServiceProvider.GetRequiredService<TamsDbContext>(),
                        scope.ServiceProvider.GetRequiredService<ISettings>(), DateTimeOffset.UtcNow, stoppingToken);
                    logger.LogInformation("Retention clean-up removed {Sessions} kiosk sessions and {Notices} read notifications", sessions, notices);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Retention clean-up failed; will retry in {Hours}h", Interval.TotalHours);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
