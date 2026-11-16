using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace Tams.Api.Data.Entities;

// Staff, instructors and admins. Identity user keyed on email (schema-v1-decisions #3):
// whoever creates the account sets UserName to the email.
[Table("users")]
public class User : IdentityUser<Guid>
{
    [MaxLength(30)] public string EmployeeNo { get; set; } = "";
    [MaxLength(80)] public string FirstName { get; set; } = "";
    [MaxLength(80)] public string LastName { get; set; } = "";
    public UserRole Role { get; set; }
    public bool IsEventCoordinator { get; set; }
    public Guid? DefaultRoomId { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

[Table("device_tokens")]
public class DeviceToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DevicePlatform Platform { get; set; }
    public string Token { get; set; } = "";
    public DateTimeOffset? LastSeenAt { get; set; }
}

[Table("auth_cards")]
public class AuthCard
{
    public Guid Id { get; set; }
    [MaxLength(20)] public string CardUid { get; set; } = "";
    public Guid UserId { get; set; }
    public CardStatus Status { get; set; } = CardStatus.Active;
    public DateTimeOffset IssuedAt { get; set; }
}

[Table("sections")]
public class Section
{
    public Guid Id { get; set; }
    [MaxLength(30)] public string Code { get; set; } = "";
    [MaxLength(20)] public string Program { get; set; } = "";
    public short YearLevel { get; set; }
    [MaxLength(9)] public string SchoolYear { get; set; } = "";
    public Guid? AdviserUserId { get; set; }
}

[Table("students")]
public class Student
{
    public Guid Id { get; set; }
    [MaxLength(20)] public string StudentNo { get; set; } = "";
    [MaxLength(80)] public string FirstName { get; set; } = "";
    [MaxLength(80)] public string LastName { get; set; } = "";
    public Guid SectionId { get; set; }
    public StudentStanding Standing { get; set; } = StudentStanding.Good;
    public short ActiveWarningCount { get; set; }
    public short ActiveMarkCount { get; set; }
    public bool HasActiveHold { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

[Table("fingerprint_templates")]
public class FingerprintTemplate
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public short FingerIndex { get; set; }
    public byte[] Template { get; set; } = [];
    /// <summary>RAW_V1 (legacy sensor bytes) or SOURCEAFIS_V1 (template built from the uploaded image).</summary>
    [MaxLength(20)] public string TemplateFormat { get; set; } = "RAW_V1";
    [MaxLength(30)] public string SensorModel { get; set; } = "";
    public short? QualityScore { get; set; }
    public Guid EnrolledByUserId { get; set; }
    public Guid EnrolledStationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
