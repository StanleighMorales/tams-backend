using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Tams.Api.Data.Entities;

[Table("checkout_sessions")]
public class CheckoutSession
{
    public Guid Id { get; set; }
    public Guid StationId { get; set; }
    public SessionType Type { get; set; }
    public SessionStatus Status { get; set; }
    public Guid? AuthCardId { get; set; }
    public Guid? InstructorUserId { get; set; }
    public Guid? StudentId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? EventId { get; set; }
    public Guid? PhotoFileId { get; set; }
    public Guid? DestinationRoomId { get; set; }
    public short? ClassLengthMinutes { get; set; }
    // ENROLLMENT sessions only: which finger is being enrolled (0 to 9).
    public short? FingerIndex { get; set; }
    public short FingerprintAttempts { get; set; }
    [Column(TypeName = "jsonb")] public string LcdLines { get; set; } = "[]";
    [MaxLength(50)] public string? FailureCode { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

[Table("approvals")]
public class Approval
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid InstructorUserId { get; set; }
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public Guid? DestinationRoomId { get; set; }
    public short? ClassLengthMinutes { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    [MaxLength(80)] public string? DecidedDevice { get; set; }
}

[Table("loans")]
public class Loan
{
    public Guid Id { get; set; }
    [MaxLength(20)] public string LoanNo { get; set; } = "";
    public LoanType Type { get; set; }
    public Guid AssetId { get; set; }
    public Guid? StudentId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Guid? CoordinatorUserId { get; set; }
    public Guid? EventId { get; set; }
    public Guid SessionId { get; set; }
    public Guid? ApprovalId { get; set; }
    public Guid CheckoutStationId { get; set; }
    public Guid? DestinationRoomId { get; set; }
    // PRD: not null for STANDARD loans. Nullable in the schema; the checkout flow enforces it.
    public Guid? PhotoFileId { get; set; }
    // Set when the retention purge deleted the photo (TAMS-131); satisfies ck_loans_standard_photo.
    public DateTimeOffset? PhotoPurgedAt { get; set; }
    public LoanStatus Status { get; set; }
    public CustodyState CustodyState { get; set; }
    public DateTimeOffset CheckedOutAt { get; set; }
    public DateTimeOffset? TransitDeadlineAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? VerifiedStationId { get; set; }
    public short? ClassLengthMinutes { get; set; }
    public DateTimeOffset ClassEndsAt { get; set; }
    public short GraceMinutes { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? OverdueAt { get; set; }
    public DateTimeOffset? MissingAt { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }
    public Guid? ReturnedByStudentId { get; set; }
    public Guid? ReturnStationId { get; set; }
    public string? Notes { get; set; }
}

[Table("room_scans")]
public class RoomScan
{
    public long Id { get; set; }
    public Guid StationId { get; set; }
    public Guid RoomId { get; set; }
    [MaxLength(20)] public string TagUid { get; set; } = "";
    public Guid? AssetId { get; set; }
    public Guid? LoanId { get; set; }
    public ScanResult Result { get; set; }
    public DateTimeOffset ScannedAt { get; set; }
}

[Table("anomalies")]
public class Anomaly
{
    public Guid Id { get; set; }
    public AnomalyType Type { get; set; }
    public AnomalyStatus Status { get; set; } = AnomalyStatus.Open;
    public Guid AssetId { get; set; }
    public Guid? LoanId { get; set; }
    public Guid? RoomId { get; set; }
    public Guid? StationId { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
}

// Append-only. The migration adds a trigger that rejects UPDATE and DELETE.
[Table("transaction_ledger")]
public class TransactionLedger
{
    public long Id { get; set; }
    public LedgerEvent Event { get; set; }
    public Guid LoanId { get; set; }
    public Guid AssetId { get; set; }
    public Guid? StudentId { get; set; }
    public Guid? InstructorUserId { get; set; }
    public DateTimeOffset? ApprovalDecidedAt { get; set; }
    public Guid? PhotoFileId { get; set; }
    public Guid? StationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    [Column(TypeName = "jsonb")] public string? Details { get; set; }
}
