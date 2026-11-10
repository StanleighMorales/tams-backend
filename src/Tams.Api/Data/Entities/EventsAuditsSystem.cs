using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Tams.Api.Data.Entities;

[Table("events")]
public class Event
{
    public Guid Id { get; set; }
    [MaxLength(150)] public string Title { get; set; } = "";
    [MaxLength(150)] public string VenueText { get; set; } = "";
    public Guid? VenueRoomId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public Guid CoordinatorUserId { get; set; }
    public EventStatus Status { get; set; } = EventStatus.Scheduled;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("audits")]
public class Audit
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public AuditSource Source { get; set; }
    public AuditStatus Status { get; set; } = AuditStatus.InProgress;
    public Guid StartedByUserId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? SignedOffByUserId { get; set; }
    public DateTimeOffset? SignedOffAt { get; set; }
    public int ExpectedCount { get; set; }
    public int MatchedCount { get; set; }
    public int MissingCount { get; set; }
    public int MisplacedCount { get; set; }
    public int UnknownCount { get; set; }
    public Guid? ImportFileId { get; set; }
    public Guid? ReportFileId { get; set; }
}

[Table("audit_items")]
public class AuditItem
{
    public Guid Id { get; set; }
    public Guid AuditId { get; set; }
    public Guid? AssetId { get; set; }
    [MaxLength(200)] public string ScannedValue { get; set; } = "";
    public AuditResult Result { get; set; }
    public Guid ClientScanId { get; set; }
    public Guid ScannedByUserId { get; set; }
    public DateTimeOffset ScannedAt { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}

[Table("notifications")]
public class Notification
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    [MaxLength(50)] public string Type { get; set; } = "";
    [MaxLength(150)] public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    [Column(TypeName = "jsonb")] public string? Payload { get; set; }
    public Guid? LoanId { get; set; }
    public Guid? StudentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

[Table("media_files")]
public class MediaFile
{
    public Guid Id { get; set; }
    public MediaKind Kind { get; set; }
    public string StoragePath { get; set; } = "";
    [MaxLength(100)] public string MimeType { get; set; } = "";
    public long SizeBytes { get; set; }
    [MaxLength(64)] public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("audit_log")]
public class AuditLogEntry
{
    public long Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? ActorStationId { get; set; }
    [MaxLength(60)] public string Action { get; set; } = "";
    [MaxLength(60)] public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    [Column(TypeName = "jsonb")] public string? Before { get; set; }
    [Column(TypeName = "jsonb")] public string? After { get; set; }
    [MaxLength(45)] public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("system_settings")]
public class SystemSetting
{
    [Key, MaxLength(60)] public string Key { get; set; } = "";
    [Column(TypeName = "jsonb")] public string Value { get; set; } = "null";
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
