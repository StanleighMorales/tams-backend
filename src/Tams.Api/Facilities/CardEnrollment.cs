using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Enrollment;
using Tams.Api.Http;

namespace Tams.Api.Facilities;

record CardEnrollmentStart(Guid? UserId);
record CardEnrollmentTap(string? CardUid);
record CardEnrollmentError(string Code, string Detail);

record CardEnrollment(Guid Id, Guid UserId, string UserName, string Email, string Status, DateTimeOffset ExpiresAt,
    CardDto? Card, CardEnrollmentError? LastError);

/// <summary>What a kiosk poll needs to switch to card enrollment mode.</summary>
record CardEnrollmentPrompt(Guid Id, string UserName, string Email, DateTimeOffset ExpiresAt);

/// <summary>
/// "Add card" requests, held in memory: staff press Add card on an instructor, the idle counter kiosk shows the
/// request and the next card tapped is registered to that instructor. A request lives 90 s, so losing one to an API
/// restart only means pressing Add card again. One API instance only; several would need these rows in the database.
/// </summary>
sealed class CardEnrollmentDesk
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(90);
    static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);

    sealed class Entry
    {
        public Guid Id, UserId;
        public Guid? StartedBy;
        public string UserName = "", Email = "", Status = "WAITING";
        public DateTimeOffset ExpiresAt;
        public DateTimeOffset? EndedAt;
        public bool Claimed; // a tap is writing the card right now: a second tap must not start another write
        public CardDto? Card;
        public CardEnrollmentError? LastError;
    }

    public record Claim(Guid UserId, Guid? StartedBy, string UserName);

    readonly object gate = new();
    readonly Dictionary<Guid, Entry> entries = [];

    static CardEnrollment View(Entry e) => new(e.Id, e.UserId, e.UserName, e.Email, e.Status, e.ExpiresAt, e.Card, e.LastError);

    void Sweep(DateTimeOffset now)
    {
        foreach (var e in entries.Values.ToList())
        {
            if (e.Status == "WAITING" && !e.Claimed && e.ExpiresAt <= now) { e.Status = "EXPIRED"; e.EndedAt = now; }
            if (e.EndedAt is { } ended && ended + KeepFinished < now) entries.Remove(e.Id);
        }
    }

    /// <summary>A new request cancels the one still waiting: one at a time.</summary>
    public CardEnrollment Start(Guid userId, string userName, string email, Guid? startedBy)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            Sweep(now);
            foreach (var old in entries.Values.Where(x => x.Status == "WAITING" && !x.Claimed))
            {
                old.Status = "CANCELLED";
                old.EndedAt = now;
            }
            var e = new Entry { Id = Guid.NewGuid(), UserId = userId, UserName = userName, Email = email, StartedBy = startedBy, ExpiresAt = now + Lifetime };
            entries[e.Id] = e;
            return View(e);
        }
    }

    public CardEnrollment? Find(Guid id)
    {
        lock (gate)
        {
            Sweep(DateTimeOffset.UtcNow);
            return entries.TryGetValue(id, out var e) ? View(e) : null;
        }
    }

    /// <summary>Idempotent: a finished request is returned unchanged.</summary>
    public CardEnrollment? Cancel(Guid id)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            Sweep(now);
            if (!entries.TryGetValue(id, out var e)) return null;
            if (e.Status == "WAITING" && !e.Claimed) { e.Status = "CANCELLED"; e.EndedAt = now; }
            return View(e);
        }
    }

    /// <summary>The request an idle kiosk should show, if any.</summary>
    public CardEnrollmentPrompt? Waiting()
    {
        lock (gate)
        {
            Sweep(DateTimeOffset.UtcNow);
            var e = entries.Values.FirstOrDefault(x => x.Status == "WAITING" && !x.Claimed);
            return e is null ? null : new(e.Id, e.UserName, e.Email, e.ExpiresAt);
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
            return (true, new Claim(e.UserId, e.StartedBy, e.UserName));
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

    /// <summary>The tap was refused: the request goes on waiting, so the next tap can be a different card.</summary>
    public CardEnrollment Release(Guid id, CardEnrollmentError error)
    {
        lock (gate)
        {
            var e = entries[id];
            (e.Claimed, e.LastError) = (false, error);
            return View(e);
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
            var (card, problem) = await AuthCardEndpoints.IssueAsync(db, http, uid, claim.UserId, claim.StartedBy, ct);
            if (card is not null)
            {
                var done = desk.Complete(id, CardDto.From(card));
                return Results.Ok(Answer(done, null, "Card registered", Fit(claim.UserName), uid));
            }

            var request = desk.Release(id, new CardEnrollmentError(problem!.Code, problem.Detail));
            return Results.Ok(Answer(request, problem.Code, problem.Code == "TAG_ALREADY_ASSIGNED"
                ? ["Card already in use", "Tap a different", "card", ""]
                : ["Card not added", Fit(problem.Code), "See the web app", ""]));
        }
        catch
        {
            desk.Release(id, new CardEnrollmentError("SERVER_ERROR", "The card could not be saved."));
            throw;
        }
    }

    static object Answer(CardEnrollment r, string? failureCode, params string[] lcd) =>
        new { r.Id, r.Status, FailureCode = failureCode, r.Card, LcdLines = lcd };

    // The LCD is 20 columns wide.
    internal static string Fit(string text) => text.Length <= 20 ? text : text[..20];

    /// <summary>The prompt an idle kiosk shows while a request is waiting.</summary>
    internal static string[] PromptLines(CardEnrollmentPrompt p) => ["Add card for", Fit(p.UserName), "Tap the new card", ""];

    static IResult NotFound() => Problems.NotFound("CARD_ENROLLMENT_NOT_FOUND", "Card enrollment not found.");
}
