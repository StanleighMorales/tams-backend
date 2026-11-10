namespace Tams.Api.Auth;

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "tams-api";
    public string Audience { get; set; } = "tams-clients";
    public int AccessMinutes { get; set; } = 15;
    public int RefreshDays { get; set; } = 7;
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}
