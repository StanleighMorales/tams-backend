using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Facilities;
using Tams.Api.Http;

namespace Tams.Api.Reports;

public sealed record ReportStat(string Label, double Value);
public sealed record ReportCount(string Label, int Count);
public sealed record ReportCard(string Key, string Title, string Description, ReportStat Headline);
public sealed record ReportList(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ReportCard> Reports);
public sealed record ReportDetail(
    string Key, string Title, string Description, DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<ReportStat> Stats, IReadOnlyDictionary<string, IReadOnlyList<ReportCount>> Breakdowns);

/// <summary>Admin report views: GET /reports is the card list (one headline each), GET /reports/{key} is the detail view.</summary>
public static class ReportEndpoints
{
    // key -> (title, description, headline stat label, builder). The headline is the first stat the builder returns.
    static readonly (string Key, string Title, string Description)[] Catalog =
    [
        ("loans", "Loans", "Checkout to return: volume, status mix, transit time and rooms."),
        ("anomalies", "Anomalies", "Wrong-room scans, unborrowed taps and transit timeouts, and how fast they are resolved."),
        ("violations", "Penalties", "Warnings, marks, holds, freezes and liabilities."),
        ("kiosk", "Kiosk and approvals", "Checkout sessions, failures and instructor approval decisions."),
        ("stations", "Stations", "Current station health and heartbeat freshness."),
        ("audits", "Inventory audits", "Completed audits with matched, missing, misplaced and unknown counts."),
        ("events", "Events", "Events in the period and the loans they generated."),
    ];

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/reports").WithTags("Reports");
        g.MapGet("", List).RequireRoles(Roles.Admin);
        g.MapGet("{key}", Get).RequireRoles(Roles.Admin);
        return app;
    }

    static bool TryPeriod(DateTimeOffset? from, DateTimeOffset? to, out DateTimeOffset f, out DateTimeOffset t, out IResult? error)
    {
        t = to ?? DateTimeOffset.UtcNow;
        f = from ?? t.AddDays(-30);
        error = f > t ? Problems.Validation(new Dictionary<string, string[]> { ["from"] = ["Must not be after 'to'."] }) : null;
        return error is null;
    }

    static async Task<IResult> List(TamsDbContext db, CancellationToken ct, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!TryPeriod(from, to, out var f, out var t, out var error)) return error!;
        // ponytail: builds every report to read its headline; cache or per-report count queries if this gets slow.
        var cards = new List<ReportCard>();
        foreach (var (key, title, description) in Catalog)
        {
            var d = await Build(key, db, f, t, ct);
            cards.Add(new ReportCard(key, title, description, d!.Stats[0]));
        }
        return Results.Ok(new ReportList(f, t, cards));
    }

    static async Task<IResult> Get(string key, TamsDbContext db, CancellationToken ct, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!TryPeriod(from, to, out var f, out var t, out var error)) return error!;
        var detail = await Build(key, db, f, t, ct);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    static Task<ReportDetail?> Build(string key, TamsDbContext db, DateTimeOffset f, DateTimeOffset t, CancellationToken ct) => key switch
    {
        "loans" => Loans(db, f, t, ct),
        "anomalies" => Anomalies(db, f, t, ct),
        "violations" => Violations(db, f, t, ct),
        "kiosk" => Kiosk(db, f, t, ct),
        "stations" => Stations(db, f, t, ct),
        "audits" => Audits(db, f, t, ct),
        "events" => Events(db, f, t, ct),
        _ => Task.FromResult<ReportDetail?>(null),
    };

    static ReportDetail Detail(string key, DateTimeOffset f, DateTimeOffset t, List<ReportStat> stats,
        params (string Name, IReadOnlyList<ReportCount> Rows)[] breakdowns)
    {
        var (_, title, description) = Catalog.First(c => c.Key == key);
        return new ReportDetail(key, title, description, f, t, stats,
            breakdowns.ToDictionary(b => b.Name, b => b.Rows));
    }

    // GroupBy runs in SQL on the raw key; labels are applied in memory.
    static async Task<IReadOnlyList<ReportCount>> Counts<TKey>(IQueryable<TKey> keys, Func<TKey, string> label, CancellationToken ct)
        where TKey : notnull
    {
        var rows = await keys.GroupBy(k => k).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        return rows.Select(r => new ReportCount(label(r.Key), r.N)).OrderByDescending(r => r.Count).ThenBy(r => r.Label).ToList();
    }

    static double Minutes(IEnumerable<TimeSpan> spans) => spans.Any() ? Math.Round(spans.Average(s => s.TotalMinutes), 1) : 0;

    static async Task<ReportDetail?> Loans(TamsDbContext db, DateTimeOffset f, DateTimeOffset t, CancellationToken ct)
    {
        var q = db.Loans.AsNoTracking().Where(l => l.CheckedOutAt >= f && l.CheckedOutAt <= t);
        var timings = await q.Select(l => new { l.CheckedOutAt, l.VerifiedAt, l.ReturnedAt }).ToListAsync(ct);
        var rooms = await q.Where(l => l.DestinationRoomId != null)
            .Join(db.Rooms, l => l.DestinationRoomId, r => r.Id, (l, r) => r.Name).ToListAsync(ct);
        var byStatus = await Counts(q.Select(l => l.Status), Support.Label, ct);
        var count = (string label) => byStatus.FirstOrDefault(c => c.Label == label)?.Count ?? 0;
        return Detail("loans", f, t,
        [
            new("Loans", timings.Count),
            new("Active", count("ACTIVE")), new("Overdue", count("OVERDUE")),
            new("Missing", count("MISSING")), new("Returned", count("RETURNED")), new("Cancelled", count("CANCELLED")),
            new("Avg minutes to verify in room", Minutes(timings.Where(x => x.VerifiedAt != null).Select(x => x.VerifiedAt!.Value - x.CheckedOutAt))),
            new("Avg minutes to return", Minutes(timings.Where(x => x.ReturnedAt != null).Select(x => x.ReturnedAt!.Value - x.CheckedOutAt))),
        ],
        ("byStatus", byStatus),
        ("byType", await Counts(q.Select(l => l.Type), Support.Label, ct)),
        ("byCustody", await Counts(q.Select(l => l.CustodyState), Support.Label, ct)),
        ("byRoom", rooms.GroupBy(r => r).Select(g => new ReportCount(g.Key, g.Count())).OrderByDescending(c => c.Count).Take(10).ToList()));
    }

    static async Task<ReportDetail?> Anomalies(TamsDbContext db, DateTimeOffset f, DateTimeOffset t, CancellationToken ct)
    {
        var q = db.Anomalies.AsNoTracking().Where(a => a.DetectedAt >= f && a.DetectedAt <= t);
        var byStatus = await Counts(q.Select(a => a.Status), Support.Label, ct);
        var count = (string label) => byStatus.FirstOrDefault(c => c.Label == label)?.Count ?? 0;
        var resolved = await q.Where(a => a.ResolvedAt != null).Select(a => new { a.DetectedAt, a.ResolvedAt }).ToListAsync(ct);
        return Detail("anomalies", f, t,
        [
            new("Anomalies", byStatus.Sum(c => c.Count)),
            new("Open", count("OPEN")), new("Acknowledged", count("ACKNOWLEDGED")), new("Resolved", count("RESOLVED")),
            new("Avg minutes to resolve", Minutes(resolved.Select(r => r.ResolvedAt!.Value - r.DetectedAt))),
        ],
        ("byStatus", byStatus),
        ("byType", await Counts(q.Select(a => a.Type), Support.Label, ct)));
    }

    static async Task<ReportDetail?> Violations(TamsDbContext db, DateTimeOffset f, DateTimeOffset t, CancellationToken ct)
    {
        var warnings = db.LateArrivalWarnings.AsNoTracking().Where(w => w.IssuedAt >= f && w.IssuedAt <= t);
        var marks = db.NonComplianceMarks.AsNoTracking().Where(m => m.IssuedAt >= f && m.IssuedAt <= t);
        var holds = db.ProfileHolds.AsNoTracking().Where(h => h.PlacedAt >= f && h.PlacedAt <= t);
        var freezes = db.ApprovalFreezes.AsNoTracking().Where(z => z.FrozenAt >= f && z.FrozenAt <= t);
        // Liabilities have no issued-at column; the loan's checkout time dates them.
        var liabilities = db.Liabilities.AsNoTracking().Join(db.Loans, l => l.LoanId, x => x.Id, (l, x) => new { l, x.CheckedOutAt })
            .Where(r => r.CheckedOutAt >= f && r.CheckedOutAt <= t).Select(r => r.l);
        var outstanding = await liabilities.Where(l => l.Status == Data.LiabilityStatus.Outstanding).SumAsync(l => (decimal?)l.Amount, ct) ?? 0;
        return Detail("violations", f, t,
        [
            new("Warnings issued", await warnings.CountAsync(ct)),
            new("Marks issued", await marks.CountAsync(ct)),
            new("Active holds", await holds.CountAsync(h => h.Status == Data.PenaltyStatus.Active, ct)),
            new("Active freezes", await freezes.CountAsync(z => z.Status == Data.PenaltyStatus.Active, ct)),
            new("Outstanding liability amount", (double)outstanding),
        ],
        ("marksByReason", await Counts(marks.Select(m => m.Reason), Support.Label, ct)),
        ("freezesByReason", await Counts(freezes.Select(z => z.Reason), Support.Label, ct)),
        ("liabilitiesByStatus", await Counts(liabilities.Select(l => l.Status), Support.Label, ct)),
        ("liabilitiesByReason", await Counts(liabilities.Select(l => l.Reason), Support.Label, ct)));
    }

    static async Task<ReportDetail?> Kiosk(TamsDbContext db, DateTimeOffset f, DateTimeOffset t, CancellationToken ct)
    {
        var sessions = db.CheckoutSessions.AsNoTracking()
            .Where(s => s.CreatedAt >= f && s.CreatedAt <= t && s.Type != Data.SessionType.Enrollment);
        var approvals = db.Approvals.AsNoTracking().Where(a => a.RequestedAt >= f && a.RequestedAt <= t);
        var byStatus = await Counts(sessions.Select(s => s.Status), Support.Label, ct);
        var count = (string label) => byStatus.FirstOrDefault(c => c.Label == label)?.Count ?? 0;
        var decided = await approvals.Where(a => a.DecidedAt != null).Select(a => new { a.RequestedAt, a.DecidedAt }).ToListAsync(ct);
        return Detail("kiosk", f, t,
        [
            new("Sessions", byStatus.Sum(c => c.Count)),
            new("Completed", count("COMPLETED")), new("Declined", count("DECLINED")),
            new("Expired", count("EXPIRED")), new("Failed", count("FAILED")), new("Cancelled", count("CANCELLED")),
            new("Avg minutes to approve", Minutes(decided.Select(d => d.DecidedAt!.Value - d.RequestedAt))),
        ],
        ("sessionsByStatus", byStatus),
        ("sessionsByType", await Counts(sessions.Select(s => s.Type), Support.Label, ct)),
        ("failureCodes", await Counts(sessions.Where(s => s.FailureCode != null).Select(s => s.FailureCode!), c => c, ct)),
        ("approvalsByDecision", await Counts(approvals.Select(a => a.Decision), Support.Label, ct)));
    }

    // Snapshot, not period-bound: the period params are echoed but health is "now".
    static async Task<ReportDetail?> Stations(TamsDbContext db, DateTimeOffset f, DateTimeOffset t, CancellationToken ct)
    {
        var stations = db.Stations.AsNoTracking();
        var byHealth = await Counts(stations.Select(s => s.Health), Support.Label, ct);
        var count = (string label) => byHealth.FirstOrDefault(c => c.Label == label)?.Count ?? 0;
        var stale = await stations.Where(s => s.LastHeartbeatAt == null || s.LastHeartbeatAt < DateTimeOffset.UtcNow.AddHours(-1)).CountAsync(ct);
        return Detail("stations", f, t,
        [
            new("Offline stations", count("OFFLINE")),
            new("Online", count("ONLINE")), new("Warning", count("WARNING")),
            new("No heartbeat in the last hour", stale),
        ],
        ("byHealth", byHealth),
        ("byType", await Counts(stations.Select(s => s.Type), Support.Label, ct)));
    }

    static async Task<ReportDetail?> Audits(TamsDbContext db, DateTimeOffset f, DateTimeOffset t, CancellationToken ct)
    {
        var q = db.Audits.AsNoTracking().Where(a => a.StartedAt >= f && a.StartedAt <= t);
        var done = q.Where(a => a.Status != Data.AuditStatus.InProgress);
        var totals = await done.GroupBy(_ => 1).Select(g => new
        {
            Expected = g.Sum(a => a.ExpectedCount), Matched = g.Sum(a => a.MatchedCount), Missing = g.Sum(a => a.MissingCount),
            Misplaced = g.Sum(a => a.MisplacedCount), Unknown = g.Sum(a => a.UnknownCount),
        }).FirstOrDefaultAsync(ct);
        var rooms = await done.Join(db.Rooms, a => a.RoomId, r => r.Id, (a, r) => new { r.Name, a.ExpectedCount, a.MatchedCount }).ToListAsync(ct);
        return Detail("audits", f, t,
        [
            new("Audits", await q.CountAsync(ct)),
            new("Completed", await done.CountAsync(ct)),
            new("Signed off", await q.CountAsync(a => a.Status == Data.AuditStatus.SignedOff, ct)),
            new("Expected items", totals?.Expected ?? 0), new("Matched", totals?.Matched ?? 0), new("Missing", totals?.Missing ?? 0),
            new("Misplaced", totals?.Misplaced ?? 0), new("Unknown", totals?.Unknown ?? 0),
        ],
        ("byStatus", await Counts(q.Select(a => a.Status), Support.Label, ct)),
        ("bySource", await Counts(q.Select(a => a.Source), Support.Label, ct)),
        // Unmatched = expected minus matched, summed per room.
        ("unmatchedByRoom", rooms.GroupBy(r => r.Name).Select(g => new ReportCount(g.Key, g.Sum(r => r.ExpectedCount - r.MatchedCount)))
            .Where(c => c.Count > 0).OrderByDescending(c => c.Count).Take(10).ToList()));
    }

    static async Task<ReportDetail?> Events(TamsDbContext db, DateTimeOffset f, DateTimeOffset t, CancellationToken ct)
    {
        var q = db.Events.AsNoTracking().Where(e => e.StartsAt >= f && e.StartsAt <= t);
        var byStatus = await Counts(q.Select(e => e.Status), Support.Label, ct);
        var count = (string label) => byStatus.FirstOrDefault(c => c.Label == label)?.Count ?? 0;
        var loans = await db.Loans.AsNoTracking().Where(l => l.EventId != null && q.Select(e => e.Id).Contains(l.EventId.Value)).CountAsync(ct);
        return Detail("events", f, t,
        [
            new("Events", byStatus.Sum(c => c.Count)),
            new("Scheduled", count("SCHEDULED")), new("Active", count("ACTIVE")),
            new("Closed", count("CLOSED")), new("Cancelled", count("CANCELLED")),
            new("Event loans", loans),
        ],
        ("byStatus", byStatus));
    }
}
