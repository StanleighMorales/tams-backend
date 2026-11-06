namespace Tams.Api.Common;

// One rule set for every place a user picks a password (admin create, self-service change).
public static class PasswordRules
{
    public const int MinLength = 8, MaxLength = 128;

    /// <summary>Returns the field error message, or null when the password is acceptable.</summary>
    public static string? Check(string? password, string? current = null)
    {
        if (string.IsNullOrEmpty(password)) return "Password is required.";
        if (password.Length is < MinLength or > MaxLength) return $"Password must be {MinLength} to {MaxLength} characters.";
        if (current is not null && password == current) return "New password must differ from the current password.";
        return null;
    }
}
