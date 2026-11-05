using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Tams.Api.Data.Entities;

[Table("late_arrival_warnings")]
public class LateArrivalWarning
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public Guid LoanId { get; set; }
    public Guid AnomalyId { get; set; }
    public short WarningNo { get; set; }
    [MaxLength(20)] public string SchoolTerm { get; set; } = "";
    // schema-v1-decisions section 3: lets reset-term clear the outgoing year's rows.
    [MaxLength(9)] public string SchoolYear { get; set; } = "";
    public bool TriggeredPenalty { get; set; }
    public Guid? MarkId { get; set; }
    public Guid? FreezeId { get; set; }
    public PenaltyStatus Status { get; set; } = PenaltyStatus.Active;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    public Guid? ClearedByUserId { get; set; }
    public string? ClearReason { get; set; }
}

[Table("non_compliance_marks")]
public class NonComplianceMark
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public Guid LoanId { get; set; }
    public PenaltyReason Reason { get; set; }
    public PenaltyStatus Status { get; set; } = PenaltyStatus.Active;
    public DateTimeOffset IssuedAt { get; set; }
    public Guid? ClearedByUserId { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    public string? ClearReason { get; set; }
}

[Table("profile_holds")]
public class ProfileHold
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public Guid LoanId { get; set; }
    public PenaltyStatus Status { get; set; } = PenaltyStatus.Active;
    public DateTimeOffset PlacedAt { get; set; }
    public Guid? LiftedByUserId { get; set; }
    public DateTimeOffset? LiftedAt { get; set; }
    public string? Reason { get; set; }
}

[Table("approval_freezes")]
public class ApprovalFreeze
{
    public Guid Id { get; set; }
    public Guid InstructorUserId { get; set; }
    public Guid LoanId { get; set; }
    public PenaltyReason Reason { get; set; }
    public PenaltyStatus Status { get; set; } = PenaltyStatus.Active;
    public DateTimeOffset FrozenAt { get; set; }
    public Guid? LiftedByUserId { get; set; }
    public DateTimeOffset? LiftedAt { get; set; }
    public string? LiftNote { get; set; }
}

[Table("liabilities")]
public class Liability
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public Guid LoanId { get; set; }
    public Guid AssetId { get; set; }
    [Column(TypeName = "numeric(12,2)")] public decimal Amount { get; set; }
    public LiabilityStatus Status { get; set; } = LiabilityStatus.Outstanding;
    [MaxLength(40)] public string? ReceiptNo { get; set; }
    public Guid? SettledByUserId { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public string? Notes { get; set; }
}
