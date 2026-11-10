namespace Tams.Api.Http;

// Stable machine-readable `code` values named in PRD 6.3 / NFR-15 and schema-v1-decisions.
// Endpoints pass these to Problems.*; add new codes here so web and mobile have one list.
public static class ErrorCodes
{
    public const string AssetNotAvailable = "ASSET_NOT_AVAILABLE";
    public const string StudentBlocked = "STUDENT_BLOCKED";
    public const string InstructorFrozen = "INSTRUCTOR_FROZEN";
    public const string PhotoFailed = "PHOTO_FAILED";
    public const string TagAlreadyAssigned = "TAG_ALREADY_ASSIGNED";
    public const string RestoreConflict = "RESTORE_CONFLICT";
}
