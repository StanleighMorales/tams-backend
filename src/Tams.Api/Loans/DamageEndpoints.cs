using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Facilities;
using Tams.Api.Http;

namespace Tams.Api.Loans;

/// <param name="LoanId">The loan whose borrower is held liable (usually the asset's last loan).</param>
/// <param name="Amount">Repair cost.</param>
public sealed record DamageLiabilityRequest(Guid? LoanId, decimal? Amount, string? Notes);

/// <summary>
/// Damage is never charged automatically (PRD-GAPS Q1, E5). When staff mark an asset DAMAGED the response shows
/// its last borrower; staff then decide whether to record a liability against that loan's borrower.
/// </summary>
public static class DamageEndpoints
{
    public static IEndpointRouteBuilder MapDamageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/assets/{id:guid}/damage-liability", Assign).WithTags("Assets").RequireRoles(Roles.Admin, Roles.TechStaff);
        return app;
    }

    /// <summary>The asset's most recent loan and who held it: a student (also a student linked to an event loan), the typed event borrower, or the coordinator. Null if it was never lent.</summary>
    internal static async Task<object?> LastBorrowerAsync(TamsDbContext db, Guid assetId, CancellationToken ct)
    {
        var loan = await db.Loans.AsNoTracking().Where(l => l.AssetId == assetId).OrderByDescending(l => l.CheckedOutAt).FirstOrDefaultAsync(ct);
        if (loan is null) return null;
        var (studentId, liableName, userId) = LoanRules.Liable(loan);
        object borrower;
        if (studentId is Guid sid)
        {
            var s = await db.Students.AsNoTracking().FirstAsync(x => x.Id == sid, ct);
            borrower = new { kind = "STUDENT", studentId = sid, name = $"{s.FirstName} {s.LastName}", idNo = s.StudentNo };
        }
        else if (liableName is not null)
            borrower = new { kind = "EVENT_BORROWER", name = liableName, idNo = loan.BorrowerIdNo };
        else
        {
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == userId, ct);
            borrower = new { kind = "COORDINATOR", userId, name = $"{u.FirstName} {u.LastName}", idNo = u.EmployeeNo };
        }
        return new
        {
            loan = new { loan.Id, loan.LoanNo, type = Support.Label(loan.Type), status = Support.Label(loan.Status), loan.CheckedOutAt, loan.ReturnedAt },
            borrower,
            hasLiability = await db.Liabilities.AnyAsync(l => l.LoanId == loan.Id, ct),
        };
    }

    static async Task<IResult> Assign(Guid id, DamageLiabilityRequest req, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (req.LoanId is null) errors["loanId"] = ["Required."];
        if (req.Amount is null or <= 0 or >= 10_000_000_000m) errors["amount"] = ["Required: the repair cost, more than 0."];
        var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        if (notes is { Length: > 2000 }) errors["notes"] = ["Up to 2000 characters."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (asset is null) return Problems.NotFound("ASSET_NOT_FOUND", "Asset not found.");
        if (asset.Condition != AssetCondition.Damaged)
            return Problems.Conflict("ASSET_NOT_DAMAGED", "Mark the asset DAMAGED before assigning damage liability.");
        var loan = await db.Loans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == req.LoanId && l.AssetId == id, ct);
        if (loan is null) return Problems.NotFound("LOAN_NOT_FOUND", "That asset has no such loan.");
        if (await db.Liabilities.AnyAsync(l => l.LoanId == loan.Id, ct))
            return Problems.Conflict("LIABILITY_EXISTS", "That loan already has a liability.");

        var liability = new Liability
        {
            Id = Guid.NewGuid(), LoanId = loan.Id, AssetId = id, Amount = req.Amount!.Value, Notes = notes, Reason = LiabilityReason.Damage,
        };
        (liability.StudentId, liability.LiableName, liability.LiableUserId) = LoanRules.Liable(loan);
        db.Liabilities.Add(liability);
        LoanRules.Audit(db, "LIABILITY_CREATED", "liability", liability.Id,
            new { loanId = loan.Id, assetId = id, reason = "DAMAGE", liability.StudentId, liability.LiableName, liability.LiableUserId, liability.Amount, notes },
            AssetAudit.ActorId(http.User), ip: http.Connection.RemoteIpAddress?.ToString());
        await db.SaveChangesAsync(ct);
        return Results.Created($"/liabilities/{liability.Id}", new
        {
            liability.Id, liability.LoanId, loanNo = loan.LoanNo, liability.AssetId, reason = Support.Label(liability.Reason),
            liability.Amount, status = Support.Label(liability.Status), liability.StudentId, liability.LiableUserId, liability.LiableName, liability.Notes,
        });
    }
}
