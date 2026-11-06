using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Users;

public record UserDto(
    Guid Id, string Email, string EmployeeNo, string FirstName, string LastName,
    string Role, bool IsEventCoordinator, Guid? DefaultRoomId, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt)
{
    public static UserDto From(User u) => new(
        u.Id, u.Email ?? "", u.EmployeeNo, u.FirstName, u.LastName, RoleLabel(u.Role),
        u.IsEventCoordinator, u.DefaultRoomId, StatusLabel(u.Status),
        u.CreatedAt, u.UpdatedAt, u.DeletedAt);

    // Same UPPER_SNAKE labels as the user_role / record_status PostgreSQL enums.
    public static string RoleLabel(UserRole r) => r switch
    {
        UserRole.Admin => "ADMIN",
        UserRole.TechStaff => "TECH_STAFF",
        UserRole.Custodian => "CUSTODIAN",
        _ => "INSTRUCTOR",
    };

    public static string StatusLabel(RecordStatus s) => s == RecordStatus.Active ? "ACTIVE" : "INACTIVE";
}

public record UserPage(IReadOnlyList<UserDto> Items, int Page, int PageSize, int Total);

public record CreateUserRequest(
    string? Email, string? Password, string? EmployeeNo, string? FirstName, string? LastName,
    string? Role, bool IsEventCoordinator = false, Guid? DefaultRoomId = null);

// Full replacement of the editable fields; the password is never changed here.
public record UpdateUserRequest(
    string? Email, string? EmployeeNo, string? FirstName, string? LastName,
    string? Role, bool IsEventCoordinator = false, Guid? DefaultRoomId = null);

public record ResetPasswordResponse(Guid UserId, string TemporaryPassword);
