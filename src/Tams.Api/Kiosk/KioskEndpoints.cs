using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Enrollment;
using Tams.Api.Facilities;
using Tams.Api.Http;
using Tams.Api.Live;
using Tams.Api.Loans;
using Tams.Api.Settings;
using static Tams.Api.Enrollment.EnrollmentSessions;
using static Tams.Api.Kiosk.KioskSessions;

namespace Tams.Api.Kiosk;

/// <param name="CardUid">Instructor or coordinator card. Not needed for RETURN.</param>
/// <param name="Type">Optional: CHECKOUT, RETURN, EVENT_BATCH or EVENT_RETURN. Without it a card opens
/// CHECKOUT, or EVENT_BATCH when its owner coordinates an event running now (FR-5.2).</param>
public sealed record StartKioskSessionRequest(string? CardUid, string? Type);

/// <param name="Template">Base64 of the sensor template. Never echoed back or logged.</param>
public sealed record KioskFingerprintRequest(string? Template, string? SensorModel, short? QualityScore);

public sealed record KioskAssetRequest(string? TagUid);

/// <summary>
/// PRD 6.2 kiosk protocol. No user credentials: every call carries X-Station-Code (StationAuth) and works
/// through a session id. A denial is a FAILED session with a failure_code and LCD text (FR-1.6), so the
/// kiosk always gets the same shape back; problem responses mean a protocol error (bad input, wrong state).
/// ENROLLMENT sessions answer on the same /kiosk/sessions/{id}/... paths (and still on /kiosk/enrollment/...).
/// </summary>
public static class KioskEndpoints
{
    const long MaxPhotoBytes = 2 * 1024 * 1024;
    const int MaxTemplateBytes = 4096;

    public static IEndpointRouteBuilder MapKioskEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/kiosk").WithTags("Kiosk").AllowAnonymous();
        g.MapGet("/{stationCode}/session", Current);
        g.MapPost("/{stationCode}/sessions", Start);
        g.MapGet("/sessions/{id:guid}", Get);
        g.MapPost("/sessions/{id:guid}/fingerprint", Fingerprint);
        g.MapPost("/sessions/{id:guid}/asset", AssetTap);
        g.MapPost("/sessions/{id:guid}/photo", Photo).Accepts<IFormFile>("multipart/form-data");
        g.MapPost("/sessions/{id:guid}/confirm", Confirm);
        g.MapPost("/sessions/{id:guid}/cancel", Cancel);
        return app;
    }

    // ---------------------------------------------------------------- station-scoped

    /// <summary>The open session on this kiosk, if any. The idle kiosk (or the counter computer's camera
    /// screen) polls this to pick up a staff-started enrollment.</summary>
    static async Task<IResult> Current(string stationCode, HttpContext http, TamsDbContext db, ISettings settings, CancellationToken ct)
    {
        var station = await StationAsync(db, stationCode, ct);
        if (station is null || !StationAuth.IsStationAuthenticated(http, station)) return NotAuthenticated();
        var now = DateTimeOffset.UtcNow;
        var poll = await settings.GetIntAsync(SettingKeys.PollInterval, ct);
        var s = await db.CheckoutSessions.AsNoTracking().Where(IsOpenExpr)
            .Where(x => x.StationId == station.Id && x.ExpiresAt > now)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        return Results.Ok(new
        {
            session = s is null ? null : View(s, now, poll),
            lcdLines = s is null ? IdleLines : LcdJson.Read(s),
            pollIntervalSeconds = poll,
        });
    }

    static async Task<IResult> Start(string stationCode, StartKioskSessionRequest req, HttpContext http, TamsDbContext db,
        ISettings settings, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        var station = await StationAsync(db, stationCode, ct);
        if (station is null || !StationAuth.IsStationAuthenticated(http, station)) return NotAuthenticated();

        SessionType? requested = null;
        if (req.Type is not null)
        {
            if (!Support.TryParse<SessionType>(req.Type, out var t) || t != SessionType.Checkout)
                return Invalid("type", "Must be CHECKOUT.");
            requested = t;
        }
        var uid = "";
        if (requested != SessionType.Return && !RfidUid.TryNormalize(req.CardUid, out uid))
            return Invalid("cardUid", "Required: the card UID in hex.");

        await EnrollmentSessions.ExpireStaleAsync(db, ct); // an expired enrollment must not keep the station busy
        var now = DateTimeOffset.UtcNow;
        var outbox = new Outbox();
        var idle = TimeSpan.FromSeconds(await settings.GetIntAsync(SettingKeys.IdleTimeout, ct));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A new tap means the kiosk abandoned what it had open; a staff-started enrollment owns the kiosk.
        var openIds = await db.CheckoutSessions.Where(IsOpenExpr).Where(s => s.StationId == station.Id).Select(s => s.Id).ToListAsync(ct);
        foreach (var openId in openIds)
        {
            var open = await LockAsync(db, openId, ct);
            if (open is null || !IsOpen(open.Status)) continue;
            if (open.Type == SessionType.Enrollment)
                return Problems.Conflict("STATION_BUSY", "An enrollment session is open on this kiosk.");
            await EndAsync(db, open, SessionStatus.Cancelled, "SUPERSEDED", ["Cancelled", "", "", ""], outbox, now, ct);
        }
        await db.SaveChangesAsync(ct); // close them before the insert: one open session per station is a unique index

        var session = new CheckoutSession
        {
            Id = Guid.NewGuid(), StationId = station.Id, Type = requested ?? SessionType.Checkout, CreatedAt = now, ExpiresAt = now + idle,
        };
        string[] lcd = [];
        string? fail = null;
        {
            var card = await db.AuthCards.AsNoTracking().FirstOrDefaultAsync(c => c.CardUid == uid, ct);
            var user = card is null ? null : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == card.UserId, ct);
            session.AuthCardId = card?.Id;
            session.InstructorUserId = user?.Id;
            fail = card is null ? "CARD_UNKNOWN"
                : card.Status != CardStatus.Active ? "CARD_INACTIVE"
                : user is null || user.DeletedAt != null || user.Status != RecordStatus.Active ? "INSTRUCTOR_INACTIVE"
                : null;

            if (fail is null)
            {
                session.Type = SessionType.Checkout;
                // FR-2.3 / step 2 of F1: a frozen instructor's card is refused at the tap.
                if (await LoanRules.IsFrozenAsync(db, user!.Id, ct)) fail = ErrorCodes.InstructorFrozen;
                else
                {
                    session.Status = SessionStatus.AwaitingFingerprint;
                    lcd = [Instr(user), "Place finger on", "sensor", ""];
                }
            }
        }

        if (fail is not null)
        {
            session.Status = SessionStatus.Failed;
            session.FailureCode = fail;
            session.CompletedAt = now;
            lcd = Denied(session, fail);
        }
        session.LcdLines = LcdJson.Write(lcd);
        db.CheckoutSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex))
        {
            return Problems.Conflict("STATION_BUSY", "The kiosk already has an open session.");
        }
        await outbox.FlushAsync(hub);
        return Results.Ok(View(session, now, await settings.GetIntAsync(SettingKeys.PollInterval, ct)));
    }


    static async Task<IResult> Get(Guid id, HttpContext http, TamsDbContext db, ISettings settings, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (await TypeOfAsync(db, id, ct) == SessionType.Enrollment) return await EnrollmentEndpoints.KioskGet(id, http, db, ct);
        var (now, outbox) = (DateTimeOffset.UtcNow, new Outbox());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (s, _, deny, _) = await OpenAsync(db, http, id, outbox, now, ct);
        return deny ?? await FinishAsync(db, tx, outbox, hub, s!, settings, now, ct);
    }

    static async Task<IResult> Fingerprint(Guid id, KioskFingerprintRequest req, HttpContext http, TamsDbContext db,
        ISettings settings, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (await TypeOfAsync(db, id, ct) == SessionType.Enrollment)
            return await EnrollmentEndpoints.KioskSubmit(id, new SubmitFingerprintRequest(req.Template, req.SensorModel, req.QualityScore),
                http, db, settings, ct);

        var buf = new byte[MaxTemplateBytes];
        if (string.IsNullOrWhiteSpace(req.Template) || !Convert.TryFromBase64String(req.Template, buf, out var n) || n == 0)
            return Invalid("template", $"Required: base64 of 1 to {MaxTemplateBytes} bytes.");
        var probe = buf[..n];

        var (now, outbox) = (DateTimeOffset.UtcNow, new Outbox());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (s, _, deny, expired) = await OpenAsync(db, http, id, outbox, now, ct);
        if (deny is not null) return deny;
        if (s!.Status != SessionStatus.AwaitingFingerprint) return InvalidState(s.Status, "submit a fingerprint");

        var idle = TimeSpan.FromSeconds(await settings.GetIntAsync(SettingKeys.IdleTimeout, ct));
        var studentId = await MatchAsync(db, http.RequestServices, probe, ct);
        if (studentId is null)
        {
            s.FingerprintAttempts++;
            if (s.FingerprintAttempts >= 3) // F1 step 3: denied after 3 failed matches
                await EndAsync(db, s, SessionStatus.Failed, "FINGERPRINT_NOT_RECOGNIZED", Denied(s, "FINGERPRINT_NOT_RECOGNIZED"), outbox, now, ct);
            else
            {
                s.LcdLines = LcdJson.Write("Not recognized", $"Try again ({s.FingerprintAttempts}/3)", "Place finger on", "sensor");
                s.ExpiresAt = now + idle;
            }
            return await FinishAsync(db, tx, outbox, hub, s, settings, now, ct);
        }

        var student = await db.Students.AsNoTracking().FirstAsync(x => x.Id == studentId, ct);
        s.StudentId = student.Id;
        string? fail = null;
        if (s.Type == SessionType.Checkout) // returns are always allowed, even for blocked students
        {
            var block = await settings.GetIntAsync(SettingKeys.MarksBeforeBlock, ct);
            var max = await settings.GetIntAsync(SettingKeys.MaxActiveLoans, ct);
            fail = student.Status != RecordStatus.Active ? "STUDENT_INACTIVE"
                : await db.NonComplianceMarks.CountAsync(m => m.StudentId == student.Id && m.Status == PenaltyStatus.Active, ct) >= block
                    ? ErrorCodes.StudentBlocked
                : await db.ProfileHolds.AnyAsync(h => h.StudentId == student.Id && h.Status == PenaltyStatus.Active, ct)
                    ? "STUDENT_ON_HOLD"
                : await db.Loans.CountAsync(l => l.StudentId == student.Id
                    && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue), ct) >= max
                    ? "LOAN_LIMIT_REACHED" // FR-1.9
                : null;
        }

        if (fail is not null) await EndAsync(db, s, SessionStatus.Failed, fail, Denied(s, fail), outbox, now, ct);
        else
        {
            s.Status = SessionStatus.AwaitingAsset;
            s.ExpiresAt = now + idle;
            s.LcdLines = LcdJson.Write(Hi(student), s.Type == SessionType.Return ? "Tap item to return" : "Tap item tag", "", "");
        }
        return await FinishAsync(db, tx, outbox, hub, s, settings, now, ct);
    }

    static async Task<IResult> AssetTap(Guid id, KioskAssetRequest req, HttpContext http, TamsDbContext db,
        ISettings settings, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (!RfidUid.TryNormalize(req.TagUid, out var uid)) return Invalid("tagUid", "Required: the tag UID in hex.");

        var (now, outbox) = (DateTimeOffset.UtcNow, new Outbox());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (s, station, deny, expired) = await OpenAsync(db, http, id, outbox, now, ct);
        if (deny is not null) return deny;
        if (s!.Status != SessionStatus.AwaitingAsset) return InvalidState(s.Status, "tap an item");

        var idle = TimeSpan.FromSeconds(await settings.GetIntAsync(SettingKeys.IdleTimeout, ct));
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.RfidTagUid == uid && a.DeletedAt == null, ct);
        var borrowable = asset is not null && asset.Type == AssetType.Borrowable
            && await db.AssetCategories.AnyAsync(c => c.Id == asset.CategoryId && c.IsBorrowable, ct);

        switch (s.Type)
        {
            case SessionType.Checkout:
            {
                s.AssetId = asset?.Id;
                var fail = asset is null ? "TAG_UNKNOWN" : !borrowable ? "ASSET_NOT_BORROWABLE"
                    : asset.Status != AssetStatus.InStorage ? ErrorCodes.AssetNotAvailable : null;
                if (fail is not null)
                {
                    await EndAsync(db, s, SessionStatus.Failed, fail, Denied(s, fail), outbox, now, ct);
                    break;
                }
                // Held AT_COUNTER until the loan is created or the session ends (EndAsync releases it).
                asset!.Status = AssetStatus.AtCounter;
                asset.UpdatedAt = now;
                s.Status = SessionStatus.AwaitingPhoto;
                s.ExpiresAt = now + idle;
                s.LcdLines = LcdJson.Write(asset.AssetCode, "Look at the camera", "Taking photo...", "");
                outbox.Staff("asset.statusChanged", LoanRules.StatusChanged(asset.Id, AssetStatus.InStorage, AssetStatus.AtCounter, asset.CurrentRoomId));
                break;
            }
        }
        return await FinishAsync(db, tx, outbox, hub, s, settings, now, ct);
    }

    static async Task<IResult> Photo(Guid id, HttpContext http, TamsDbContext db, ISettings settings, IConfiguration cfg,
        IHubContext<LiveHub> hub, CancellationToken ct)
    {
        // Read the form by hand so a body-less POST (the kiosk reporting a camera failure) is not a 415.
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(ct) : null;
        var bytes = await MediaStore.ReadAllAsync(form?.Files["file"] ?? form?.Files.FirstOrDefault(), MaxPhotoBytes, ct);

        var (now, outbox) = (DateTimeOffset.UtcNow, new Outbox());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (s, _, deny, expired) = await OpenAsync(db, http, id, outbox, now, ct);
        if (deny is not null) return deny;
        if (s!.Status != SessionStatus.AwaitingPhoto) return InvalidState(s.Status, "upload a photo");

        // The kiosk retries a failed capture once itself (F1 step 5); a missing or non-JPEG upload ends the borrow.
        if (bytes is null || !MediaStore.IsJpeg(bytes))
        {
            await EndAsync(db, s, SessionStatus.Failed, ErrorCodes.PhotoFailed, Denied(s, ErrorCodes.PhotoFailed), outbox, now, ct);
            return await FinishAsync(db, tx, outbox, hub, s, settings, now, ct);
        }

        var media = await MediaStore.SaveAsync(db, cfg, MediaKind.CheckoutPhoto, "checkout", bytes, "image/jpeg", "jpg", ct);
        s.PhotoFileId = media.Id;
        s.Status = SessionStatus.AwaitingApproval;
        s.ExpiresAt = now.AddSeconds(await settings.GetIntAsync(SettingKeys.ApprovalTimeout, ct));
        s.LcdLines = LcdJson.Write("Waiting for", "instructor approval", "", "");
        db.Approvals.Add(new Approval { Id = Guid.NewGuid(), SessionId = s.Id, InstructorUserId = s.InstructorUserId!.Value, RequestedAt = now });
        return await FinishAsync(db, tx, outbox, hub, s, settings, now, ct);
    }

    static async Task<IResult> Confirm(Guid id, HttpContext http, TamsDbContext db, ISettings settings, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (await TypeOfAsync(db, id, ct) == SessionType.Enrollment) return await EnrollmentEndpoints.KioskConfirm(id, http, db, ct);

        var (now, outbox) = (DateTimeOffset.UtcNow, new Outbox());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (s, _, deny, expired) = await OpenAsync(db, http, id, outbox, now, ct);
        if (deny is not null) return deny;

        return InvalidState(s!.Status, "confirm");
    }

    static async Task<IResult> Cancel(Guid id, HttpContext http, TamsDbContext db, ISettings settings, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (await TypeOfAsync(db, id, ct) == SessionType.Enrollment) return await EnrollmentEndpoints.KioskCancel(id, http, db, ct);

        var (now, outbox) = (DateTimeOffset.UtcNow, new Outbox());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (s, _, deny, expired) = await OpenAsync(db, http, id, outbox, now, ct);
        if (deny is not null) return deny;
        if (!IsOpen(s!.Status)) return InvalidState(s.Status, "cancel");
        // Batch-mode loans already made stay open; cancelling just leaves batch mode.
        await EndAsync(db, s, SessionStatus.Cancelled, "CANCELLED", ["Cancelled", "", "", ""], outbox, now, ct);
        return await FinishAsync(db, tx, outbox, hub, s, settings, now, ct);
    }

    // ---------------------------------------------------------------- helpers

    static Task<Station?> StationAsync(TamsDbContext db, string code, CancellationToken ct)
    {
        var c = code.Trim().ToUpperInvariant();
        return db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.StationCode == c && s.DeletedAt == null, ct);
    }

    static Task<SessionType?> TypeOfAsync(TamsDbContext db, Guid id, CancellationToken ct) =>
        db.CheckoutSessions.Where(s => s.Id == id).Select(s => (SessionType?)s.Type).FirstOrDefaultAsync(ct);

    /// <summary>Locks the session, checks the device and expires the session if its deadline passed.</summary>
    static async Task<(CheckoutSession? S, Station? Station, IResult? Deny, bool Expired)> OpenAsync(TamsDbContext db,
        HttpContext http, Guid id, Outbox outbox, DateTimeOffset now, CancellationToken ct)
    {
        var s = await LockAsync(db, id, ct);
        if (s is null || s.Type == SessionType.Enrollment) return (null, null, NotFound(), false);
        var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == s.StationId, ct);
        if (station is null || !StationAuth.IsStationAuthenticated(http, station)) return (null, null, NotAuthenticated(), false);
        return (s, station, null, false);
    }

    static async Task<IResult> FinishAsync(TamsDbContext db, IDbContextTransaction tx, Outbox outbox, IHubContext<LiveHub> hub,
        CheckoutSession s, ISettings settings, DateTimeOffset now, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await outbox.FlushAsync(hub);
        return Results.Ok(View(s, now, await settings.GetIntAsync(SettingKeys.PollInterval, ct)));
    }

    static IResult Invalid(string field, string message) =>
        Problems.Validation(new Dictionary<string, string[]> { [field] = [message] });

    static IResult NotAuthenticated() =>
        Problems.Forbidden("STATION_NOT_AUTHENTICATED", "This device is not allowed to use the kiosk API.");

    static IResult NotFound() => Problems.NotFound("SESSION_NOT_FOUND", "Kiosk session not found.");

    static IResult InvalidState(SessionStatus current, string action) =>
        Problems.Conflict("INVALID_SESSION_STATE", $"Cannot {action} while the session is {Support.Label(current)}.");
}
