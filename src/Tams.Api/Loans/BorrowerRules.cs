using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Loans;

/// <summary>An open problem found on a would-be borrower (Code is stable for the web, Detail is for staff).</summary>
public sealed record BorrowerFlag(string Code, string Detail);

/// <param name="Match">STUDENT (name and ID match a roster student), POSSIBLE_MATCH (ID matches, name differs) or NONE (outside borrower).</param>
/// <param name="Candidate">The student or staff account behind a STUDENT or POSSIBLE_MATCH.</param>
public sealed record BorrowerCheck(string Match, BorrowerCandidate? Candidate, IReadOnlyList<BorrowerFlag> Flags);

public sealed record BorrowerCandidate(string Kind, Guid Id, string IdNo, string FullName);

/// <summary>
/// Event loan borrowers (PRD-GAPS Q1, E1): staff type a name and ID number, the system matches them to a
/// student and looks for open problems before the items are handed over. People without a roster record are
/// matched against earlier event loans and liabilities by ID number, then by name.
/// </summary>
static class BorrowerRules
{
    /// <summary>Case, spacing and "Last, First" order do not matter.</summary>
    public static string Normalize(string s) =>
        string.Join(' ', s.Replace(',', ' ').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Order());

    /// <param name="exceptLoanIds">Loans being updated right now; they must not flag their own borrower.</param>
    public static async Task<BorrowerCheck> CheckAsync(TamsDbContext db, string name, string idNo,
        IReadOnlyCollection<Guid> exceptLoanIds, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking().Where(s => s.StudentNo == idNo && s.DeletedAt == null).FirstOrDefaultAsync(ct);
        if (student is not null)
        {
            var full = $"{student.FirstName} {student.LastName}";
            var exact = Normalize(full) == Normalize(name);
            return new(exact ? "STUDENT" : "POSSIBLE_MATCH", new("STUDENT", student.Id, student.StudentNo, full),
                await StudentFlagsAsync(db, student, exceptLoanIds, ct));
        }

        var user = await db.Users.AsNoTracking().Where(u => u.EmployeeNo == idNo && u.DeletedAt == null).FirstOrDefaultAsync(ct);
        if (user is not null && Normalize($"{user.FirstName} {user.LastName}") != Normalize(name))
            return new("POSSIBLE_MATCH", new("USER", user.Id, user.EmployeeNo, $"{user.FirstName} {user.LastName}"),
                await OutsideFlagsAsync(db, name, idNo, exceptLoanIds, ct));

        return new("NONE", null, await OutsideFlagsAsync(db, name, idNo, exceptLoanIds, ct));
    }

    static async Task<List<BorrowerFlag>> StudentFlagsAsync(TamsDbContext db, Student s, IReadOnlyCollection<Guid> except, CancellationToken ct)
    {
        var flags = new List<BorrowerFlag>();
        if (s.Standing == StudentStanding.Blocked) flags.Add(new("BLOCKED", $"{s.ActiveMarkCount} active marks; the student is blocked."));
        if (s.HasActiveHold) flags.Add(new("ON_HOLD", "The student's profile is on hold."));
        var open = await db.Loans.AsNoTracking()
            .Where(l => (l.StudentId == s.Id || l.BorrowerStudentId == s.Id) && !except.Contains(l.Id)
                && (l.Status == LoanStatus.Overdue || l.Status == LoanStatus.Missing))
            .Select(l => new { l.LoanNo, l.Status }).ToListAsync(ct);
        AddLoanFlags(flags, open.Select(l => (l.LoanNo, l.Status)));
        var owing = await db.Liabilities.AsNoTracking().Where(x => x.StudentId == s.Id && x.Status == LiabilityStatus.Outstanding).SumAsync(x => x.Amount, ct);
        if (owing > 0) flags.Add(new("UNSETTLED_LIABILITY", $"Unsettled liability of {owing:0.00}."));
        return flags;
    }

    static async Task<List<BorrowerFlag>> OutsideFlagsAsync(TamsDbContext db, string name, string idNo,
        IReadOnlyCollection<Guid> except, CancellationToken ct)
    {
        var byId = await db.Loans.AnyAsync(l => l.BorrowerStudentId == null && l.BorrowerIdNo == idNo, ct);
        var lower = name.Trim().ToLower();
        var theirs = db.Loans.AsNoTracking().Where(l => l.BorrowerStudentId == null && !except.Contains(l.Id)
            && (byId ? l.BorrowerIdNo == idNo : l.BorrowerName!.ToLower() == lower));
        var flags = new List<BorrowerFlag>();
        var open = await theirs.Where(l => l.Status == LoanStatus.Overdue || l.Status == LoanStatus.Missing)
            .Select(l => new { l.LoanNo, l.Status }).ToListAsync(ct);
        AddLoanFlags(flags, open.Select(l => (l.LoanNo, l.Status)));
        var owing = await db.Liabilities.AsNoTracking()
            .Where(x => x.Status == LiabilityStatus.Outstanding && x.LiableName != null && theirs.Any(l => l.Id == x.LoanId))
            .SumAsync(x => x.Amount, ct);
        if (owing > 0) flags.Add(new("UNSETTLED_LIABILITY", $"Unsettled liability of {owing:0.00} from an earlier event loan."));
        return flags;
    }

    static void AddLoanFlags(List<BorrowerFlag> flags, IEnumerable<(string LoanNo, LoanStatus Status)> loans)
    {
        var l = loans.ToList();
        var overdue = l.Where(x => x.Status == LoanStatus.Overdue).Select(x => x.LoanNo).ToList();
        var missing = l.Where(x => x.Status == LoanStatus.Missing).Select(x => x.LoanNo).ToList();
        if (overdue.Count > 0) flags.Add(new("OVERDUE_ITEM", $"Overdue loans: {string.Join(", ", overdue)}."));
        if (missing.Count > 0) flags.Add(new("MISSING_ITEM", $"Missing items on loans: {string.Join(", ", missing)}."));
    }
}
