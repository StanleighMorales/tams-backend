using System.Text.Json;

namespace Tams.Api.Common;

/// <summary>
/// Reads the file the backup service writes (Backup:StatusPath) so /health can flag a failed or missing
/// backup (TAMS-130). "ok" is a backup finished in the last <see cref="StaleAfter"/>; a monitor that
/// alerts when the body lacks <c>"backup":"ok"</c> covers failures.
/// </summary>
public static class BackupStatus
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(36);

    /// <summary>null when no status path is configured (backups not part of this deployment).</summary>
    public static string? Read(string? path, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (!File.Exists(path)) return "missing";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.GetProperty("ok").GetBoolean()) return "failed";
            var at = doc.RootElement.GetProperty("at").GetDateTimeOffset();
            return now - at > StaleAfter ? "stale" : "ok";
        }
        catch
        {
            return "failed"; // unreadable status counts as a failure
        }
    }
}
