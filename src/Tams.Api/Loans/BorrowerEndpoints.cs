using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;
using Tams.Api.Live;

namespace Tams.Api.Loans;

/// <param name="Name">Full name as on the ID.</param>
/// <param name="IdNo">ID number as on the ID (a student number for students).</param>
/// <param name="ConfirmPossibleMatch">Staff confirm that the person is the registered student or account the ID number points to.</param>
/// <param name="OverrideReason">Written reason to hand items to a flagged borrower anyway; saved in the audit log.</param>
public sealed record BorrowerRequest(string? Name, string? IdNo, bool? ConfirmPossibleMatch, string? OverrideReason);

/// <summary>Event loan borrower record: staff register who actually holds the items (PRD-GAPS Q1, E1).</summary>
public static class BorrowerEndpoints
{
    static readonly string[] Staff = [Roles.Admin, Roles.TechStaff];

    public static IEndpointRouteBuilder MapBorrowerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/borrowers/check", Check).WithTags("Borrowers").RequireRoles(Staff);
        app.MapPut("/loans/{id:guid}/borrower", SetOnLoan).WithTags("Loans").RequireRoles(Staff);
        app.MapPut("/events/{id:guid}/borrower", SetOnEvent).WithTags("Events").RequireRoles(Staff);
        return app;
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static Dictionary<string, string[]> Validate(BorrowerRequest req)
    {
        var errors = new Dictionary<string, string[]>();
        if (Clean(req.Name) is not { Length: <= 150 }) errors["name"] = ["Required, up to 150 characters."];
        if (Clean(req.IdNo) is not { Length: <= 40 }) errors["idNo"] = ["Required, up to 40 characters."];
        return errors;
    }

    /// <summary>The web warns before submitting. Nothing is saved.</summary>
    static async Task<IResult> Check(TamsDbContext db, CancellationToken ct, string? name, string? idNo)
    {
        var req = new BorrowerRequest(name, idNo, null, null);
        if (Validate(req) is { Count: > 0 } errors) return Problems.Validation(errors);
        return Results.Ok(await BorrowerRules.CheckAsync(db, Clean(name)!, Clean(idNo)!, [], ct));
    }

    static async Task<IResult> SetOnLoan(Guid id, BorrowerRequest req, HttpContext http, TamsDbContext db, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        var loan = await db.Loans.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (loan is null) return Problems.NotFound("LOAN_NOT_FOUND", "Loan not found.");
        if (loan.Type != LoanType.Event) return Problems.Conflict("NOT_AN_EVENT_LOAN", "Only event loans have a borrower record.");
        if (loan.Status is not (LoanStatus.Active or LoanStatus.Overdue)) return Problems.Conflict("LOAN_NOT_OPEN", "The loan is not open.");
        var (refused, applied) = await ApplyAsync(db, http, hub, [loan], req, ct);
        return refused ?? Results.Ok(new { loan = applied!.Loans[0], applied.Check, applied.Overridden });
    }

    /// <summary>The same borrower for every open loan of an event (the usual case: one person collects the lot).</summary>
    static async Task<IResult> SetOnEvent(Guid id, BorrowerRequest req, HttpContext http, TamsDbContext db, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id, ct)) return Problems.NotFound("EVENT_NOT_FOUND", "Event not found.");
        var loans = await db.Loans.Where(l => l.EventId == id && l.Type == LoanType.Event
            && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue)).OrderBy(l => l.LoanNo).ToListAsync(ct);
        if (loans.Count == 0) return Problems.Conflict("NO_OPEN_EVENT_LOANS", "The event has no open loans.");
        var (refused, applied) = await ApplyAsync(db, http, hub, loans, req, ct);
        return refused ?? Results.Ok(new { loans = applied!.Loans, applied.Check, applied.Overridden });
    }

    sealed record Applied(List<object> Loans, BorrowerCheck Check, bool Overridden);

    /// <summary>
    /// Match, flag and save. POSSIBLE_MATCH needs a confirmation; a flagged borrower is refused (and technical
    /// staff are told) unless an override reason comes with the request. Returns an IResult when refused.
    /// </summary>
    static async Task<(IResult? Refused, Applied? Done)> ApplyAsync(TamsDbContext db, HttpContext http, IHubContext<LiveHub> hub,
        List<Loan> loans, BorrowerRequest req, CancellationToken ct)
    {
        if (Validate(req) is { Count: > 0 } errors) return (Problems.Validation(errors), null);
        var (name, idNo) = (Clean(req.Name)!, Clean(req.IdNo)!);
        var reason = Clean(req.OverrideReason);
        if (reason is { Length: > 2000 })
            return (Problems.Validation(new Dictionary<string, string[]> { ["overrideReason"] = ["Up to 2000 characters."] }), null);

        var check = await BorrowerRules.CheckAsync(db, name, idNo, loans.Select(l => l.Id).ToList(), ct);
        var candidate = check.Candidate;
        if (check.Match == "POSSIBLE_MATCH" && req.ConfirmPossibleMatch != true)
            return (Problems.Conflict(ErrorCodes.BorrowerPossibleMatch,
                $"The ID number belongs to {candidate!.FullName}, not {name}. Confirm it is the same person or correct the name.",
                new Dictionary<string, object?> { ["check"] = check }), null);

        var actor = AssetAudit.ActorId(http.User);
        var ip = http.Connection.RemoteIpAddress?.ToString();
        var now = DateTimeOffset.UtcNow;
        var flagged = check.Flags.Count > 0;
        if (flagged && reason is null)
        {
            var summary = string.Join(" ", check.Flags.Select(f => f.Detail));
            var payload = new { name, idNo, loanNos = loans.Select(l => l.LoanNo), flags = check.Flags };
            foreach (var staffId in await db.Users.Where(u => u.Role == UserRole.TechStaff && u.Status == RecordStatus.Active && u.DeletedAt == null)
                         .Select(u => u.Id).ToListAsync(ct))
                LoanRules.Notify(db, staffId, "BORROWER_FLAGGED", "Flagged borrower entered",
                    $"{name} ({idNo}) was entered as an event borrower and has open problems. {summary}", loans[0], payload, now);
            await db.SaveChangesAsync(ct);
            var outbox = new Outbox();
            outbox.Staff("borrower.flagged", payload);
            await outbox.FlushAsync(hub);
            return (Problems.Conflict(ErrorCodes.BorrowerFlagged,
                "The borrower has open problems. Send an overrideReason to hand over the items anyway.",
                new Dictionary<string, object?> { ["check"] = check }), null);
        }

        // Only a roster student is linked; a confirmed staff-account match stays an outside borrower.
        var studentId = candidate is { Kind: "STUDENT" } ? candidate.Id : (Guid?)null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var loan in loans)
        {
            var before = new { loan.BorrowerName, loan.BorrowerIdNo, loan.BorrowerStudentId };
            (loan.BorrowerName, loan.BorrowerIdNo, loan.BorrowerStudentId) = (name, idNo, studentId);
            LoanRules.Audit(db, "LOAN_BORROWER_SET", "loan", loan.Id, new { loan.LoanNo, name, idNo, studentId, match = check.Match }, actor, before, ip);
            if (flagged)
                LoanRules.Audit(db, "BORROWER_FLAG_OVERRIDDEN", "loan", loan.Id,
                    new { loan.LoanNo, name, idNo, studentId, reason, flags = check.Flags }, actor, ip: ip);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (null, new(await LoanEndpoints.DtosAsync(db, loans, ct), check, flagged));
    }
}
