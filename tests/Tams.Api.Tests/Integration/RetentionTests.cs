using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Retention;
using Tams.Api.Settings;

namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-131 (G5): purge removes templates and photos, keeps ledger and audit rows, is admin-only; clean-up sweeper.</summary>
[Collection("api")]
public class RetentionTests(ApiFixture api)
{
    const string Confirm = """{"confirm":"DELETE"}""";

    [Theory]
    [InlineData("/api/data-retention/purge-biometrics")]
    [InlineData("/api/data-retention/purge-checkout-photos")]
    public async Task Purges_are_admin_only_and_need_confirmation(string url)
    {
        foreach (var role in new[] { "TECH_STAFF", "CUSTODIAN", "INSTRUCTOR" })
            await (await api.As(role)).PostRawAsync(url, Confirm).Expect(403);
        await api.NoAuth().PostRawAsync(url, Confirm).Expect(401);

        var admin = await api.As("ADMIN");
        await admin.PostRawAsync(url, "{}").Expect(400, "PURGE_NOT_CONFIRMED");
        await admin.PostRawAsync(url, """{"confirm":"yes"}""").Expect(400, "PURGE_NOT_CONFIRMED");
    }

    [Fact]
    public async Task Biometrics_purge_removes_templates_and_enrollment_sessions_and_writes_evidence()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (stationId, _) = await Seed.Station(admin, room);
        var student = await Seed.Student(admin);
        var me = api.UserIds["TECH_STAFF"];

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        db.FingerprintTemplates.Add(new FingerprintTemplate
        {
            Id = Guid.NewGuid(), StudentId = student.Id, FingerIndex = 1, Template = [1, 2, 3], SensorModel = "AS608",
            EnrolledByUserId = me, EnrolledStationId = stationId, CreatedAt = DateTimeOffset.UtcNow,
        });
        db.CheckoutSessions.Add(new CheckoutSession
        {
            Id = Guid.NewGuid(), StationId = stationId, Type = SessionType.Enrollment, Status = SessionStatus.Cancelled,
            StudentId = student.Id, FingerIndex = 1, ExpiresAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var auditBefore = await db.AuditLog.CountAsync();

        var res = await admin.PostRawAsync("/api/data-retention/purge-biometrics", Confirm).Ok();

        Assert.True(res.GetProperty("counts").GetProperty("fingerprintTemplates").GetInt32() >= 1);
        Assert.True(res.GetProperty("counts").GetProperty("enrollmentSessions").GetInt32() >= 1);
        Assert.Equal(0, await db.FingerprintTemplates.CountAsync());
        Assert.Equal(0, await db.CheckoutSessions.CountAsync(s => s.Type == SessionType.Enrollment));
        Assert.Equal(auditBefore + 1, await db.AuditLog.CountAsync()); // appended, nothing removed
        Assert.Equal(1, await db.AuditLog.CountAsync(a => a.Action == RetentionPurge.BiometricsAction && a.EntityId == res.GetProperty("purgeId").GetGuid()));

        var html = await (await admin.GetAsync(res.GetProperty("summaryUrl").GetString()!).Expect(200)).Content.ReadAsStringAsync();
        Assert.Contains("Fingerprint templates deleted", html);
        Assert.Contains("fingerprintTemplates", html);
    }

    [Fact]
    public async Task Photo_purge_deletes_files_and_links_but_keeps_loan_ledger_and_audit_rows()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (stationId, _) = await Seed.Station(admin, room);
        var cat = await Seed.Category(admin);
        var asset = await Seed.BorrowableAsset(admin, cat, room);
        var student = await Seed.Student(admin);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var root = scope.ServiceProvider.GetRequiredService<IConfiguration>()["Media:Path"]!;
        MediaFile Media(MediaKind kind)
        {
            var id = Guid.NewGuid();
            var rel = $"retention-test/{id}.jpg";
            Directory.CreateDirectory(Path.Combine(root, "retention-test"));
            File.WriteAllBytes(Path.Combine(root, rel), [0xFF, 0xD8, 0xFF, 0xE0]);
            var m = new MediaFile { Id = id, Kind = kind, StoragePath = rel, MimeType = "image/jpeg", SizeBytes = 4, Sha256 = new string('a', 64), CreatedAt = DateTimeOffset.UtcNow };
            db.MediaFiles.Add(m);
            return m;
        }
        var photo = Media(MediaKind.CheckoutPhoto);
        var assetImage = Media(MediaKind.AssetImage);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var session = new CheckoutSession
        {
            Id = Guid.NewGuid(), StationId = stationId, Type = SessionType.Checkout, Status = SessionStatus.Completed,
            StudentId = student.Id, AssetId = asset.Id, PhotoFileId = photo.Id, ExpiresAt = now, CreatedAt = now,
        };
        var loan = new Loan
        {
            Id = Guid.NewGuid(), LoanNo = "L-" + Seed.Tag(), Type = LoanType.Standard, AssetId = asset.Id, StudentId = student.Id,
            SessionId = session.Id, CheckoutStationId = stationId, PhotoFileId = photo.Id, Status = LoanStatus.Active,
            CustodyState = CustodyState.AwaitingArrival, CheckedOutAt = now, ClassLengthMinutes = 90,
            ClassEndsAt = now.AddMinutes(90), GraceMinutes = 15, DueAt = now.AddMinutes(105),
        };
        db.CheckoutSessions.Add(session);
        await db.SaveChangesAsync();
        db.Loans.Add(loan);
        await db.SaveChangesAsync();
        db.TransactionLedger.Add(new TransactionLedger
        {
            Event = LedgerEvent.Checkout, LoanId = loan.Id, AssetId = asset.Id, StudentId = student.Id,
            PhotoFileId = photo.Id, StationId = stationId, OccurredAt = now,
        });
        await db.SaveChangesAsync();
        var photoPath = Path.Combine(root, photo.StoragePath);
        var imagePath = Path.Combine(root, assetImage.StoragePath);
        Assert.True(File.Exists(photoPath));
        var ledgerBefore = await db.TransactionLedger.CountAsync();
        var auditBefore = await db.AuditLog.CountAsync();

        var res = await admin.PostRawAsync("/api/data-retention/purge-checkout-photos", Confirm).Ok();

        Assert.True(res.GetProperty("counts").GetProperty("filesDeleted").GetInt32() >= 1);
        Assert.False(File.Exists(photoPath));
        Assert.True(File.Exists(imagePath)); // asset images are not checkout photos
        Assert.Equal(0, await db.MediaFiles.CountAsync(m => m.Kind == MediaKind.CheckoutPhoto));
        Assert.Equal(1, await db.MediaFiles.CountAsync(m => m.Id == assetImage.Id));

        db.ChangeTracker.Clear();
        var s = await db.CheckoutSessions.SingleAsync(x => x.Id == session.Id);
        var l = await db.Loans.SingleAsync(x => x.Id == loan.Id);
        Assert.Null(s.PhotoFileId);
        Assert.Null(l.PhotoFileId);
        Assert.NotNull(l.PhotoPurgedAt);
        Assert.Equal(ledgerBefore, await db.TransactionLedger.CountAsync());
        Assert.Equal(0, await db.TransactionLedger.CountAsync(x => x.PhotoFileId != null));
        Assert.Equal(auditBefore + 1, await db.AuditLog.CountAsync());

        // Ledger stays append-only for everything else.
        await Assert.ThrowsAnyAsync<Exception>(() => db.TransactionLedger.ExecuteUpdateAsync(u => u.SetProperty(x => x.Details, "{}")));
    }

    [Fact]
    public async Task Cleanup_removes_old_abandoned_sessions_and_old_read_notices_only()
    {
        var admin = await api.As("ADMIN");
        var room = await Seed.Room(admin, storage: true);
        var (stationId, _) = await Seed.Station(admin, room);
        var me = api.UserIds["TECH_STAFF"];
        var now = DateTimeOffset.UtcNow;

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        CheckoutSession Session(SessionStatus status, DateTimeOffset created) => new()
        {
            Id = Guid.NewGuid(), StationId = stationId, Type = SessionType.Checkout, Status = status,
            ExpiresAt = created, CreatedAt = created, CompletedAt = created,
        };
        var oldFailed = Session(SessionStatus.Failed, now.AddDays(-60));
        var recentFailed = Session(SessionStatus.Failed, now.AddDays(-2));
        var oldOpen = Session(SessionStatus.AwaitingFingerprint, now.AddDays(-60));
        db.CheckoutSessions.AddRange(oldFailed, recentFailed, oldOpen);
        Notification Notice(DateTimeOffset? readAt, int ageDays) => new()
        {
            Id = Guid.NewGuid(), RecipientUserId = me, Type = "X", Title = "t", Body = "b",
            CreatedAt = now.AddDays(-ageDays), ReadAt = readAt,
        };
        var oldRead = Notice(now.AddDays(-200), 200);
        var oldUnread = Notice(null, 200);
        var recentRead = Notice(now.AddDays(-1), 2);
        db.Notifications.AddRange(oldRead, oldUnread, recentRead);
        await db.SaveChangesAsync();

        await RetentionPurge.CleanUpAsync(db, scope.ServiceProvider.GetRequiredService<ISettings>(), now, default);

        Assert.False(await db.CheckoutSessions.AnyAsync(s => s.Id == oldFailed.Id));
        Assert.True(await db.CheckoutSessions.AnyAsync(s => s.Id == recentFailed.Id));
        Assert.True(await db.CheckoutSessions.AnyAsync(s => s.Id == oldOpen.Id));
        Assert.False(await db.Notifications.AnyAsync(n => n.Id == oldRead.Id));
        Assert.True(await db.Notifications.AnyAsync(n => n.Id == oldUnread.Id));
        Assert.True(await db.Notifications.AnyAsync(n => n.Id == recentRead.Id));
    }
}
