using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Enrollment;
using Tams.Api.Http;

namespace Tams.Api.Facilities;

record CardEnrollmentStart(Guid? UserId);
record CardEnrollmentTap(string? CardUid);
record CardEnrollmentError(string Code, string Detail);
record TagScanStart(Guid? AssetId, string? Label);

record CardEnrollment(Guid Id, Guid UserId, string UserName, string Email, string Status, DateTimeOffset ExpiresAt,
    CardDto? Card, CardEnrollmentError? LastError);

/// <summary>"Scan tag" request: the kiosk reads an item's tag UID for the asset form. Nothing is saved.</summary>
record TagScan(Guid Id, Guid? AssetId, string Label, string Status, DateTimeOffset ExpiresAt,
    string? TagUid, CardEnrollmentError? LastError);

/// <summary>What a kiosk poll needs to switch to enrollment mode. For ASSET_TAG, UserName carries the asset label.</summary>
record CardEnrollmentPrompt(Guid Id, string Kind, string UserName, string Email, DateTimeOffset ExpiresAt);

/// <summary>
/// "Add card" and "Scan tag" requests, held in memory: staff press the button on the web app, the idle counter kiosk
/// shows the request and the next tap is used (a card is registered, or a tag UID is read back). A request lives 90 s,
/// so losing one to an API restart only means pressing the button again. One API instance only; several would need
/// these rows in the database. One waiting request at a time, across both kinds.
/// </summary>
sealed class CardEnrollmentDesk
{
    public const string CardKind = "CARD", AssetTag = "ASSET_TAG";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(90);
    static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);

    sealed class Entry
    {
        public Guid Id, UserId;
        public Guid? StartedBy, AssetId;
        public string Kind = CardKind, UserName = "", Email = "", Status = "WAITING";
        public DateTimeOffset ExpiresAt;
        public DateTimeOffset? EndedAt;
        public bool Claimed; // a tap is writing the card right now: a second tap must not start another write
        public CardDto? Card;
        public string? TagUid;
        public CardEnrollmentError? LastError;
    }

    public record Claim(string Kind, Guid UserId, Guid? AssetId, Guid? StartedBy, string UserName);

    readonly object gate = new();
    readonly Dictionary<Guid, Entry> entries = [];

    static CardEnrollment View(Entry e) => new(e.Id, e.UserId, e.UserName, e.Email, e.Status, e.ExpiresAt, e.Card, e.LastError);
    static TagScan TagView(Entry e) => new(e.Id, e.AssetId, e.UserName, e.Status, e.ExpiresAt, e.TagUid, e.LastError);

    void Sweep(DateTimeOffset now)
    {
        foreach (var e in entries.Values.ToList())
        {
            if (e.Status == "WAITING" && !e.Claimed && e.ExpiresAt <= now) { e.Status = "EXPIRED"; e.EndedAt = now; }
            if (e.EndedAt is { } ended && ended + KeepFinished < now) entries.Remove(e.Id);
        }
    }

    /// <summary>A new request cancels the one still waiting, of either kind: one at a time. Call inside the lock.</summary>
    Entry Add(Entry e)
    {
        var now = DateTimeOffset.UtcNow;
        Sweep(now);
        foreach (var old in entries.Values.Where(x => x.Status == "WAITING" && !x.Claimed))
        {
            old.Status = "CANCELLED";
            old.EndedAt = now;
        }
        (e.Id, e.ExpiresAt) = (Guid.NewGuid(), now + Lifetime);
        entries[e.Id] = e;
        return e;
    }

    public CardEnrollment Start(Guid userId, string userName, string email, Guid? startedBy)
    {
        lock (gate) return View(Add(new Entry { UserId = userId, UserName = userName, Email = email, StartedBy = startedBy }));
    }

    public TagScan StartTagScan(Guid? assetId, string label, Guid? startedBy)
    {
        lock (gate) return TagView(Add(new Entry { Kind = AssetTag, AssetId = assetId, UserName = label, StartedBy = startedBy }));
    }

    // An id of one kind is not visible as the other: Get filters by kind. Call inside the lock.
    Entry? Get(Guid id, string kind)
    {
        Sweep(DateTimeOffset.UtcNow);
        return entries.TryGetValue(id, out var e) && e.Kind == kind ? e : null;
    }

    public CardEnrollment? Find(Guid id)
    {
        lock (gate) return Get(id, CardKind) is { } e ? View(e) : null;
    }

    public TagScan? FindTagScan(Guid id)
    {
        lock (gate) return Get(id, AssetTag) is { } e ? TagView(e) : null;
    }

    /// <summary>Idempotent: a finished request is returned unchanged.</summary>
    public CardEnrollment? Cancel(Guid id)
    {
        lock (gate) return CancelEntry(id, CardKind) is { } e ? View(e) : null;
    }

    public TagScan? CancelTagScan(Guid id)
    {
        lock (gate) return CancelEntry(id, AssetTag) is { } e ? TagView(e) : null;
    }

    Entry? CancelEntry(Guid id, string kind)
    {
        var e = Get(id, kind);
        if (e is { Status: "WAITING", Claimed: false }) { e.Status = "CANCELLED"; e.EndedAt = DateTimeOffset.UtcNow; }
        return e;
    }

    /// <summary>The request an idle kiosk should show, if any.</summary>
    public CardEnrollmentPrompt? Waiting()
    {
        lock (gate)
        {
            Sweep(DateTimeOffset.UtcNow);
            var e = entries.Values.FirstOrDefault(x => x.Status == "WAITING" && !x.Claimed);
            return e is null ? null : new(e.Id, e.Kind, e.UserName, e.Email, e.ExpiresAt);
        }
    }

    /// <summary>Takes the request for one tap: not found, found but not waiting (no claim), or the claim.</summary>
    public (bool Found, Claim? Claim) TryClaim(Guid id)
    {
        lock (gate)
        {
            Sweep(DateTimeOffset.UtcNow);
            if (!entries.TryGetValue(id, out var e)) return (false, null);
            if (e.Status != "WAITING" || e.Claimed) return (true, null);
            e.Claimed = true;
            return (true, new Claim(e.Kind, e.UserId, e.AssetId, e.StartedBy, e.UserName));
        }
    }

    public CardEnrollment Complete(Guid id, CardDto card)
    {
        lock (gate)
        {
            var e = entries[id];
            (e.Claimed, e.Status, e.Card, e.LastError, e.EndedAt) = (false, "COMPLETED", card, null, DateTimeOffset.UtcNow);
            return View(e);
        }
    }

    public TagScan CompleteTagScan(Guid id, string tagUid)
    {
        lock (gate)
        {
            var e = entries[id];
            (e.Claimed, e.Status, e.TagUid, e.LastError, e.EndedAt) = (false, "COMPLETED", tagUid, null, DateTimeOffset.UtcNow);
            return TagView(e);
        }
    }

    /// <summary>The tap was refused: the request goes on waiting, so the next tap can be a different card or tag.
    /// Returns the status, so both kinds can build their answer from it.</summary>
    public string Release(Guid id, CardEnrollmentError error)
    {
        lock (gate)
        {
            var e = entries[id];
            (e.Claimed, e.LastError) = (false, error);
            return e.Status;
        }
    }
}

/// <summary>"Add card" at the kiosk. Staff routes use the same roles as POST /auth-cards; the tap route is
/// anonymous like the other /kiosk routes and gated by X-Station-Code (FR-1.4: the kiosk holds no credentials).</summary>
public static class CardEnrollmentEndpoints
{
    public static WebApplication MapCardEnrollmentEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/auth-cards/enrollment").WithTags("Authorization cards");
        g.MapPost("/", StartRequest).RequireRoles(FacilityRoles.Write);
        g.MapGet("/{id:guid}", Get).RequireRoles(FacilityRoles.Write);
        g.MapPost("/{id:guid}/cancel", Cancel).RequireRoles(FacilityRoles.Write);

        app.MapGroup("/kiosk").WithTags("Kiosk").AllowAnonymous()
            .MapPost("/{stationCode}/card-enrollment/{id:guid}/tap", Tap);

        // "Scan tag" on the asset form: same roles as POST/PUT /assets.
        var t = app.MapGroup("/assets/tag-scan").WithTags("Assets");
        t.MapPost("/", StartTagScan).RequireRoles(FacilityRoles.Write);
        t.MapGet("/{id:guid}", GetTagScan).RequireRoles(FacilityRoles.Write);
        t.MapPost("/{id:guid}/cancel", CancelTagScan).RequireRoles(FacilityRoles.Write);
        return app;
    }

    static async Task<IResult> StartRequest(CardEnrollmentStart body, HttpContext http, TamsDbContext db, CardEnrollmentDesk desk, CancellationToken ct)
    {
        if (body.UserId is null)
            return Problems.Validation(new Dictionary<string, string[]> { ["userId"] = ["Required."] });

        var problem = await AuthCardEndpoints.OwnerProblem(db, body.UserId.Value, ct);
        if (problem is not null) return Problems.BadRequest(problem.Code, problem.Detail);

        var u = await db.Users.AsNoTracking().Where(x => x.Id == body.UserId)
            .Select(x => new { x.FirstName, x.LastName, x.Email }).FirstAsync(ct);
        var request = desk.Start(body.UserId.Value, $"{u.FirstName} {u.LastName}", u.Email ?? "", Support.ActorId(http));
        return Results.Created($"/auth-cards/enrollment/{request.Id}", request);
    }

    static IResult Get(Guid id, CardEnrollmentDesk desk) =>
        desk.Find(id) is { } r ? Results.Ok(r) : NotFound();

    static IResult Cancel(Guid id, CardEnrollmentDesk desk) =>
        desk.Cancel(id) is { } r ? Results.Ok(r) : NotFound();

    static async Task<IResult> Tap(string stationCode, Guid id, CardEnrollmentTap body, HttpContext http, TamsDbContext db,
        CardEnrollmentDesk desk, CancellationToken ct)
    {
        var code = stationCode.Trim().ToUpperInvariant();
        var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.StationCode == code && s.DeletedAt == null, ct);
        if (station is null || !StationAuth.IsStationAuthenticated(http, station))
            return Problems.Forbidden("STATION_NOT_AUTHENTICATED", "This device is not allowed to use the kiosk API.");
        if (!RfidUid.TryNormalize(body.CardUid, out var uid))
            return Problems.BadRequest("INVALID_RFID_UID",
                $"UID must be hexadecimal, {RfidUid.MinLength} to {RfidUid.MaxLength} characters, even length (separators allowed).");

        var (found, claim) = desk.TryClaim(id);
        if (!found) return NotFound();
        if (claim is null)
            return Problems.Conflict("CARD_ENROLLMENT_NOT_WAITING", "This card enrollment is already completed, cancelled or expired.");

        // Whatever happens below, the claim must not stay held, or the request could never be tapped again.
        try
        {
            if (claim.Kind == CardEnrollmentDesk.AssetTag)
            {
                // Same rule as saving the asset; the asset being edited may keep its own tag.
                if (await AssetEndpoints.TagProblem(db, uid, claim.AssetId, ct) is not { } taken)
                {
                    var read = desk.CompleteTagScan(id, uid);
                    return Results.Ok(TagAnswer(id, read.Status, null, uid, "Tag read", Fit(uid), "Save it in the", "web app"));
                }
                var waiting = desk.Release(id, new CardEnrollmentError(taken.Code, taken.Detail));
                return Results.Ok(TagAnswer(id, waiting, taken.Code, null, "Tag already in use", "Tap a different", "tag", ""));
            }

            var (card, problem) = await AuthCardEndpoints.IssueAsync(db, http, uid, claim.UserId, claim.StartedBy, ct);
            if (card is not null)
            {
                var done = desk.Complete(id, CardDto.From(card));
                return Results.Ok(Answer(id, done.Status, null, done.Card, "Card registered", Fit(claim.UserName), uid));
            }

            var status = desk.Release(id, new CardEnrollmentError(problem!.Code, problem.Detail));
            return Results.Ok(Answer(id, status, problem.Code, null, problem.Code == "TAG_ALREADY_ASSIGNED"
                ? ["Card already in use", "Tap a different", "card", ""]
                : ["Card not added", Fit(problem.Code), "See the web app", ""]));
        }
        catch
        {
            desk.Release(id, new CardEnrollmentError("SERVER_ERROR", "The tap could not be saved."));
            throw;
        }
    }

    static async Task<IResult> StartTagScan(TagScanStart? body, HttpContext http, TamsDbContext db, CardEnrollmentDesk desk, CancellationToken ct)
    {
        var label = body?.Label?.Trim();
        if (body?.AssetId is { } assetId)
        {
            var code = await db.Assets.AsNoTracking().Where(a => a.Id == assetId && a.DeletedAt == null)
                .Select(a => a.AssetCode).FirstOrDefaultAsync(ct);
            if (code is null) return Problems.NotFound("ASSET_NOT_FOUND", "Asset not found.");
            if (string.IsNullOrEmpty(label)) label = code;
        }
        var scan = desk.StartTagScan(body?.AssetId, string.IsNullOrEmpty(label) ? "New item" : label, Support.ActorId(http));
        return Results.Created($"/assets/tag-scan/{scan.Id}", scan);
    }

    static IResult GetTagScan(Guid id, CardEnrollmentDesk desk) =>
        desk.FindTagScan(id) is { } r ? Results.Ok(r) : TagScanNotFound();

    static IResult CancelTagScan(Guid id, CardEnrollmentDesk desk) =>
        desk.CancelTagScan(id) is { } r ? Results.Ok(r) : TagScanNotFound();

    static object Answer(Guid id, string status, string? failureCode, CardDto? card, params string[] lcd) =>
        new { Id = id, Status = status, FailureCode = failureCode, Card = card, LcdLines = lcd };

    // A tag scan answers with tagUid where an Add card answer has card.
    static object TagAnswer(Guid id, string status, string? failureCode, string? tagUid, params string[] lcd) =>
        new { Id = id, Status = status, FailureCode = failureCode, TagUid = tagUid, LcdLines = lcd };

    // The LCD is 20 columns wide.
    internal static string Fit(string text) => text.Length <= 20 ? text : text[..20];

    /// <summary>The prompt an idle kiosk shows while a request is waiting.</summary>
    internal static string[] PromptLines(CardEnrollmentPrompt p) => p.Kind == CardEnrollmentDesk.AssetTag
        ? ["Scan item tag", Fit(p.UserName), "Tap the item tag", ""]
        : ["Add card for", Fit(p.UserName), "Tap the new card", ""];

    static IResult NotFound() => Problems.NotFound("CARD_ENROLLMENT_NOT_FOUND", "Card enrollment not found.");

    static IResult TagScanNotFound() => Problems.NotFound("TAG_SCAN_NOT_FOUND", "Tag scan not found.");
}
