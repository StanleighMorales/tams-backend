using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Facilities;
using Tams.Api.Live;
using Tams.Api.Settings;

namespace Tams.Api.Loans;

/// <summary>Wall-clock helpers. The pilot site is in the Philippines (UTC+8, no DST).</summary>
public static class LocalTime
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Manila", out var tz)
        ? tz : TimeZoneInfo.CreateCustomTimeZone("PHT", TimeSpan.FromHours(8), "PHT", "PHT");

    public static DateTimeOffset ToLocal(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Zone);

    /// <summary>"3:15PM", short enough for the 20-character LCD.</summary>
    public static string Clock(DateTimeOffset t) => ToLocal(t).ToString("h:mmtt", CultureInfo.InvariantCulture);

    // ponytail: calendar guess (Aug-Dec 1st semester, Jan-May 2nd, Jun-Jul summer); move the term dates
    // into settings if the registrar's calendar differs.
    /// <summary>("2026-2027", "2026-2027-1") for a date in the first semester of 2026-2027.</summary>
    public static (string SchoolYear, string Term) SchoolTerm(DateTimeOffset t)
    {
        var d = ToLocal(t);
        var start = d.Month >= 8 ? d.Year : d.Year - 1;
        var sem = d.Month >= 8 ? 1 : d.Month <= 5 ? 2 : 3;
        var year = $"{start}-{start + 1}";
        return (year, $"{year}-{sem}");
    }
}

/// <summary>system_settings keys read by the transaction flows (SettingDefinitions has the ranges).</summary>
static class SettingKeys
{
    public const string TransitWindow = "transit_window_minutes";
    public const string ClassLengthDefault = "class_length_default_minutes";
    public const string Grace = "grace_minutes";
    public const string PollInterval = "kiosk_poll_interval_seconds";
    public const string ApprovalTimeout = "approval_timeout_seconds";
    public const string IdleTimeout = "kiosk_idle_timeout_seconds";
    public const string HeartbeatInterval = "heartbeat_interval_seconds";
    public const string WarningWindows = "station_warning_missed_windows";
    public const string OfflineWindows = "station_offline_missed_windows";
    public const string WarningLimit = "late_arrival_warning_limit";
    public const string MarksBeforeBlock = "marks_before_block";
    public const string MaxActiveLoans = "max_active_loans_per_student";
}

/// <summary>
/// Loan state changes shared by the kiosk, room stations, staff actions and the sweeper (PRD 4.1, 4.2, F6).
/// Live events go into the caller's <see cref="Outbox"/> and are sent after commit.
/// Penalties are never lifted automatically except by a liability settlement (US-10); everything else
/// is a staff action on the resolution screen (FR-6.8).
/// </summary>
static class LoanRules
{
    public static readonly short[] ClassLengths = [90, 120, 150, 180];

    public static string? Json(object? o) => o is null ? null : JsonSerializer.Serialize(o, JsonSerializerOptions.Web);

    /// <summary>FR-2.4: append-only ledger row (the database rejects UPDATE and DELETE).</summary>
    public static void Ledger(TamsDbContext db, LedgerEvent evt, Loan loan, DateTimeOffset at, Guid? stationId = null,
        Guid? actor = null, object? details = null, DateTimeOffset? approvalDecidedAt = null) =>
        db.TransactionLedger.Add(new TransactionLedger
        {
            Event = evt, LoanId = loan.Id, AssetId = loan.AssetId, StudentId = loan.StudentId,
            InstructorUserId = loan.ApprovedByUserId ?? loan.CoordinatorUserId, ApprovalDecidedAt = approvalDecidedAt,
            PhotoFileId = loan.PhotoFileId, StationId = stationId, ActorUserId = actor, OccurredAt = at, Details = Json(details),
        });

    public static object StatusChanged(Guid assetId, AssetStatus from, AssetStatus to, Guid? roomId) =>
        new { assetId, oldStatus = Support.Label(from), newStatus = Support.Label(to), roomId };

    /// <summary>
    /// US-03 / PRD 4.2: a return closes the loan from any open state, including OVERDUE and MISSING.
    /// Caller owns the transaction. Holds, freezes and liabilities stay for staff to resolve (FR-6.8).
    /// </summary>
    public static async Task<Loan?> CloseAsync(TamsDbContext db, Outbox outbox, Guid loanId, DateTimeOffset now,
        Guid? returnedByStudentId, Guid? stationId, Guid? actorUserId, Guid? returnRoomId, string? note, CancellationToken ct)
    {
        var claimed = await db.Loans
            .Where(l => l.Id == loanId && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue || l.Status == LoanStatus.Missing))
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, LoanStatus.Returned)
                .SetProperty(l => l.ReturnedAt, now)
                .SetProperty(l => l.ReturnedByStudentId, returnedByStudentId)
                .SetProperty(l => l.ReturnStationId, stationId), ct);
        if (claimed == 0) return null;

        var loan = await db.Loans.AsNoTracking().FirstAsync(l => l.Id == loanId, ct);
        var asset = await db.Assets.FirstAsync(a => a.Id == loan.AssetId, ct);
        var from = asset.Status;
        asset.Status = AssetStatus.InStorage;
        asset.CurrentRoomId = returnRoomId ?? asset.HomeRoomId;
        asset.UpdatedAt = now;
        Ledger(db, LedgerEvent.Return, loan, now, stationId, actorUserId, new { returnedByStudentId, note });
        await db.SaveChangesAsync(ct);

        outbox.Staff("asset.statusChanged", StatusChanged(asset.Id, from, AssetStatus.InStorage, asset.CurrentRoomId));
        return loan;
    }

    public static Task<bool> IsFrozenAsync(TamsDbContext db, Guid instructorId, CancellationToken ct) =>
        db.ApprovalFreezes.AnyAsync(f => f.InstructorUserId == instructorId && f.Status == PenaltyStatus.Active, ct);
}
