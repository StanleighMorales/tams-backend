namespace Tams.Api.Auth;

// Role claim values are the user_role labels (Data/Enums.cs UserRole, UPPER_SNAKE).
// Endpoints guard themselves with .RequireRoles(...). JWT issuing and the
// authentication/authorization pipeline are owned by TAMS-31 and TAMS-39.
public static class Roles
{
    public const string Admin = "ADMIN";
    public const string TechStaff = "TECH_STAFF";
    public const string Custodian = "CUSTODIAN";
    public const string Instructor = "INSTRUCTOR";
}

public static class RoleGuards
{
    public static TBuilder RequireRoles<TBuilder>(this TBuilder builder, params string[] roles)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(p => p.RequireRole(roles));
}
