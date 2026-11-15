namespace Tams.Api.Data;

// PRD 7.2. Each enum maps to a native PostgreSQL enum named after the C# type in
// snake_case (UserRole -> user_role) with UPPER_SNAKE labels (TechStaff -> TECH_STAFF).
// ASSET_IMAGE on MediaKind is the schema-v1-decisions #5 addition.

public enum UserRole { Admin, TechStaff, Custodian, Instructor }
public enum RecordStatus { Active, Inactive }
public enum CardStatus { Active, Lost, Revoked }
public enum StudentStanding { Good, Warned, Blocked }
public enum AssetType { Borrowable, Fixed }
public enum AssetStatus { InStorage, AtCounter, InTransit, InRoomVerified, LocationAnomaly, Overdue, Missing, Maintenance, Retired }
public enum AssetCondition { Good, Fair, Damaged }
public enum LoanType { Standard, Event }
public enum LoanStatus { Active, Overdue, Returned, Missing, Cancelled }
public enum CustodyState { AwaitingArrival, Verified, Anomaly, NotRequired }
public enum SessionType { Checkout, Return, EventBatch, EventReturn, Enrollment }
public enum SessionStatus { AwaitingFingerprint, AwaitingAsset, AwaitingPhoto, AwaitingApproval, AwaitingConfirm, Completed, Declined, Cancelled, Expired, Failed }
public enum ApprovalDecision { Pending, Approved, Declined, Expired }
public enum StationType { CounterKiosk, RoomStation }
public enum StationHealth { Online, Warning, Offline }
public enum ScanResult { Verified, VerifiedLate, WrongRoom, NotBorrowed, UnknownTag }
public enum AnomalyType { TransitTimeout, WrongRoom, UnborrowedTap }
public enum AnomalyStatus { Open, Acknowledged, Resolved }
public enum PenaltyStatus { Active, Cleared }
public enum PenaltyReason { LateArrivalLimit, Overdue, Missing }
public enum LiabilityStatus { Outstanding, SettledPayment, SettledReplacement, Waived }
public enum EventStatus { Scheduled, Active, Closed, Cancelled }
public enum AuditSource { Mobile, ExternalImport }
public enum AuditStatus { InProgress, Completed, SignedOff }
public enum AuditResult { Matched, Missing, Misplaced, Unknown }
public enum LedgerEvent { Checkout, RoomVerified, LateArrivalWarning, Return, Overdue, MarkedMissing, Settled, Voided }
// PRD 7.3 device_tokens.platform and media_files.kind are enumerated inline, not in 7.2.
public enum DevicePlatform { Android, Ios, Web }
public enum MediaKind { CheckoutPhoto, AssetImage, AuditReport, AuditImport, QrSheet }
