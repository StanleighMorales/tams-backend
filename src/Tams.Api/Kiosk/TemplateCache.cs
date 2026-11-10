using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Enrollment;

namespace Tams.Api.Kiosk;

/// <summary>
/// Decrypted templates held in process memory so a scan costs a popcount pass, not N decrypts.
/// Plaintext never leaves the process. Reloaded when <see cref="Invalidate"/> is called or when the
/// stamp (count and newest created_at of live templates) changes, which also covers other instances,
/// enrollment, deletes and student soft-deletes. ponytail: one reload under a lock; fine at 2,000.
/// </summary>
public static class TemplateCache
{
    static readonly object Gate = new();
    static (string Stamp, IReadOnlyList<(Guid, byte[])> Items)? _cached;

    public static void Invalidate() { lock (Gate) _cached = null; }

    public static async Task<IReadOnlyList<(Guid StudentId, byte[] Template)>> GetAsync(TamsDbContext db, IServiceProvider sp, CancellationToken ct)
    {
        var live = db.FingerprintTemplates.AsNoTracking().Where(t => db.Students.Any(s => s.Id == t.StudentId && s.DeletedAt == null));
        var count = await live.CountAsync(ct);
        var newest = count == 0 ? null : (DateTimeOffset?)await live.MaxAsync(t => t.CreatedAt, ct);
        var stamp = $"{count}|{newest:O}";
        lock (Gate) if (_cached is { } c && c.Stamp == stamp) return c.Items;

        var rows = await live.Select(t => new { t.StudentId, t.Template }).ToListAsync(ct);
        var items = new List<(Guid, byte[])>(rows.Count);
        foreach (var r in rows)
        {
            try { items.Add((r.StudentId, TemplateProtector.Unprotect(sp, r.Template))); }
            catch (CryptographicException) { } // key ring lost: that template can never match
        }
        lock (Gate) _cached = (stamp, items);
        return items;
    }
}
