using Tams.Api.Common;

namespace Tams.Api.Tests;

public class BackupStatusTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);

    static string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "tams-backup-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Not_configured_reports_nothing() => Assert.Null(BackupStatus.Read(null, Now));

    [Fact]
    public void Missing_file_is_reported() => Assert.Equal("missing", BackupStatus.Read("/no/such/backup.json", Now));

    [Fact]
    public void Recent_success_is_ok() =>
        Assert.Equal("ok", BackupStatus.Read(Write("""{"ok":true,"at":"2026-11-16T02:00:05Z"}"""), Now));

    [Fact]
    public void Old_success_is_stale() =>
        Assert.Equal("stale", BackupStatus.Read(Write("""{"ok":true,"at":"2026-11-14T02:00:05Z"}"""), Now));

    [Fact]
    public void Recorded_failure_is_failed() =>
        Assert.Equal("failed", BackupStatus.Read(Write("""{"ok":false,"at":"2026-11-16T02:00:05Z","error":"x"}"""), Now));

    [Fact]
    public void Garbage_is_failed() => Assert.Equal("failed", BackupStatus.Read(Write("not json"), Now));
}
