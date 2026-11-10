using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Students;

public sealed record RowError(int Line, string Code, string? Field, string Message);

public sealed record ImportReport(
    Guid ImportId, bool DryRun, bool Strict, bool Committed,
    int RowsRead, int Created, int Updated, int Unchanged, int Skipped, int SectionsCreated,
    List<RowError> Errors);

/// <summary>Validated roster row.</summary>
public sealed record RosterRow(
    int Line, string StudentNo, string FirstName, string LastName,
    string SectionCode, string Program, short YearLevel, string SchoolYear);

public static partial class RosterImport
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxRows = 5000;

    // Header names are case-insensitive. All columns are required; no other column is accepted,
    // so address, ID photo and similar data cannot ride in on the file (data minimisation).
    public static readonly string[] Columns =
        ["student_no", "first_name", "last_name", "section_code", "program", "year_level", "school_year"];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_.]*$")] private static partial Regex StudentNoPattern();
    [GeneratedRegex(@"^(\d{4})-(\d{4})$")] private static partial Regex SchoolYearPattern();

    /// <summary>Checks the header row. Returns null when fine, otherwise (code, message).</summary>
    public static (string Code, string Message)? CheckHeader(List<string> header)
    {
        var names = header.Select(h => h.Trim().ToLowerInvariant()).ToList();
        var dup = names.GroupBy(n => n).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) return ("CSV_DUPLICATE_COLUMN", $"Column '{dup.Key}' appears more than once.");
        var unknown = names.Except(Columns).ToList();
        if (unknown.Count > 0) return ("CSV_UNKNOWN_COLUMN", $"Unsupported column(s): {string.Join(", ", unknown)}. Allowed: {string.Join(", ", Columns)}.");
        var missing = Columns.Except(names).ToList();
        if (missing.Count > 0) return ("CSV_MISSING_COLUMN", $"Missing required column(s): {string.Join(", ", missing)}.");
        return null;
    }

    /// <summary>Validates one data record against the header order. Returns a row or one error.</summary>
    public static (RosterRow? Row, RowError? Error) ValidateRow(CsvReader.Record rec, List<string> header)
    {
        var names = header.Select(h => h.Trim().ToLowerInvariant()).ToList();
        if (rec.Fields.Count != names.Count)
            return (null, new(rec.Line, "ROW_FIELD_COUNT", null,
                $"Expected {names.Count} fields but found {rec.Fields.Count}."));

        string Get(string col) => rec.Fields[names.IndexOf(col)].Trim();
        RowError? Err(string code, string field, string msg) => new(rec.Line, code, field, msg);

        foreach (var col in Columns)
            if (Get(col).Length == 0)
                return (null, Err("ROW_REQUIRED_FIELD_MISSING", col, $"'{col}' is required."));

        var no = Get("student_no");
        if (no.Length > 20 || !StudentNoPattern().IsMatch(no))
            return (null, Err("ROW_INVALID_STUDENT_NO", "student_no",
                "student_no must be 1-20 characters: letters, digits, '-', '_' or '.'."));

        foreach (var (col, max) in new[] { ("first_name", 80), ("last_name", 80), ("section_code", 30), ("program", 20) })
            if (Get(col).Length > max)
                return (null, Err("ROW_FIELD_TOO_LONG", col, $"'{col}' must be at most {max} characters."));

        if (!short.TryParse(Get("year_level"), out var year) || year < 1 || year > 12)
            return (null, Err("ROW_INVALID_YEAR_LEVEL", "year_level", "year_level must be a whole number from 1 to 12."));

        var sy = SchoolYearPattern().Match(Get("school_year"));
        if (!sy.Success || int.Parse(sy.Groups[2].Value) != int.Parse(sy.Groups[1].Value) + 1)
            return (null, Err("ROW_INVALID_SCHOOL_YEAR", "school_year", "school_year must look like 2025-2026."));

        return (new RosterRow(rec.Line, no, Get("first_name"), Get("last_name"),
            Get("section_code"), Get("program"), year, Get("school_year")), null);
    }

    /// <summary>
    /// Upserts validated rows. Caller owns the transaction. Adds to <paramref name="errors"/>
    /// for conflicts found against existing data and returns the counts.
    /// </summary>
    public static async Task<(int Created, int Updated, int Unchanged, int SectionsCreated)> ApplyAsync(
        TamsDbContext db, List<RosterRow> rows, List<RowError> errors, CancellationToken ct)
    {
        var years = rows.Select(r => r.SchoolYear).Distinct().ToList();
        var sections = (await db.Sections.Where(s => years.Contains(s.SchoolYear)).ToListAsync(ct))
            .ToDictionary(s => (s.Code, s.SchoolYear));

        var nos = rows.Select(r => r.StudentNo).ToList();
        // The unique index is partial (live rows only), so a live and a deleted row can share a number:
        // the live row wins; a number with only a deleted row is skipped (ROW_STUDENT_DELETED).
        var students = (await db.Students.Where(s => nos.Contains(s.StudentNo)).ToListAsync(ct))
            .OrderBy(s => s.DeletedAt != null).GroupBy(s => s.StudentNo).ToDictionary(g => g.Key, g => g.First());

        int created = 0, updated = 0, unchanged = 0, sectionsCreated = 0;
        var seen = new HashSet<string>();
        var now = DateTimeOffset.UtcNow;

        foreach (var r in rows)
        {
            if (!seen.Add(r.StudentNo))
            {
                errors.Add(new(r.Line, "ROW_DUPLICATE_STUDENT_NO", "student_no",
                    "student_no appears earlier in this file; only the first row is used."));
                continue;
            }

            if (students.TryGetValue(r.StudentNo, out var existing) && existing.DeletedAt is not null)
            {
                errors.Add(new(r.Line, "ROW_STUDENT_DELETED", "student_no",
                    "A deleted student has this student_no. Restore it first (POST /students/{id}/restore)."));
                continue;
            }

            if (!sections.TryGetValue((r.SectionCode, r.SchoolYear), out var section))
            {
                section = new Section
                {
                    Id = Guid.NewGuid(), Code = r.SectionCode, Program = r.Program,
                    YearLevel = r.YearLevel, SchoolYear = r.SchoolYear,
                };
                db.Sections.Add(section);
                sections[(r.SectionCode, r.SchoolYear)] = section;
                sectionsCreated++;
            }
            else if (section.Program != r.Program || section.YearLevel != r.YearLevel)
            {
                errors.Add(new(r.Line, "ROW_SECTION_MISMATCH", "section_code",
                    $"Section {r.SectionCode} ({r.SchoolYear}) already exists with program {section.Program}, year level {section.YearLevel}."));
                continue;
            }

            if (existing is null)
            {
                db.Students.Add(new Student
                {
                    Id = Guid.NewGuid(), StudentNo = r.StudentNo, FirstName = r.FirstName,
                    LastName = r.LastName, SectionId = section.Id,
                });
                created++;
            }
            else if (existing.FirstName == r.FirstName && existing.LastName == r.LastName && existing.SectionId == section.Id)
            {
                unchanged++;
            }
            else
            {
                existing.FirstName = r.FirstName;
                existing.LastName = r.LastName;
                existing.SectionId = section.Id;
                existing.UpdatedAt = now;
                updated++;
            }
        }

        return (created, updated, unchanged, sectionsCreated);
    }
}
