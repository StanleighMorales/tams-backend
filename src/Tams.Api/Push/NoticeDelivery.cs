using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;

namespace Tams.Api.Push;

/// <summary>
/// TAMS-97: pushes stored notifications (warning, overdue, missing, freeze, anomaly) and stamps delivered_at.
/// The sweeper calls it, so every Notify() call site is covered without touching it. A notice older than the
/// window is left to GET /notifications. A user with no device token is skipped (and retried inside the window)
/// unless the sender does not need tokens.
/// </summary>
public static class NoticeDelivery
{
    static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    // ponytail: one API instance assumed; two would both push the same row. Add a claim (UPDATE ... WHERE delivered_at IS NULL) if it scales out.
    public static async Task DeliverAsync(TamsDbContext db, IApprovalPushSender sender, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Notifications.Where(n => n.DeliveredAt == null && n.CreatedAt > now - Window)
            .OrderBy(n => n.CreatedAt).Take(100).ToListAsync(ct);
        foreach (var n in rows)
        {
            var tokens = await db.DeviceTokens.AsNoTracking().Where(t => t.UserId == n.RecipientUserId).Select(t => t.Token).ToListAsync(ct);
            if (tokens.Count == 0 && sender.NeedsTokens) continue;
            JsonElement? payload = n.Payload is null ? null : JsonDocument.Parse(n.Payload).RootElement.Clone();
            var invalid = await sender.SendNoticeAsync(
                new NoticePush(n.RecipientUserId, n.Id, n.Type, n.Title, n.Body, payload, n.LoanId, n.CreatedAt), tokens, ct);
            if (invalid.Count > 0) await db.DeviceTokens.Where(t => invalid.Contains(t.Token)).ExecuteDeleteAsync(ct);
            n.DeliveredAt = now;
        }
        await db.SaveChangesAsync(ct);
    }
}
