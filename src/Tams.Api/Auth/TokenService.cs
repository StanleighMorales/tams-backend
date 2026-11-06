using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Auth;

/// <summary>
/// Access tokens are short-lived JWTs (sub = user id, role = a Roles constant).
/// Refresh tokens are stateless: userId.expiryUnix.HMAC over those plus the user's
/// SecurityStamp. Schema v1 has no refresh-token table, so rotating the SecurityStamp
/// (logout, change-password) revokes every outstanding refresh token for that user at once.
/// </summary>
public class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    readonly JwtOptions _o = options.Value;

    public static string RoleClaim(UserRole role) => role switch
    {
        UserRole.Admin => Roles.Admin,
        UserRole.TechStaff => Roles.TechStaff,
        UserRole.Custodian => Roles.Custodian,
        UserRole.Instructor => Roles.Instructor,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public TokenPair Issue(User user)
    {
        var now = clock.GetUtcNow();
        var accessExpires = now.AddMinutes(_o.AccessMinutes);
        var access = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = accessExpires.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key)), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(),
                ["email"] = user.Email!,
                ["role"] = RoleClaim(user.Role),
                // String "true"/"false": the EventCoordinator policy matches it as a claim value.
                [TamsClaims.EventCoordinator] = user.IsEventCoordinator ? "true" : "false",
                ["jti"] = Guid.NewGuid().ToString(),
            },
        });

        var payload = $"{user.Id:N}.{now.AddDays(_o.RefreshDays).ToUnixTimeSeconds()}";
        var refresh = $"{payload}.{Base64Url.EncodeToString(Sign(payload, user.SecurityStamp))}";
        return new TokenPair(access, accessExpires, refresh);
    }

    /// <summary>Extracts the user id from an unverified refresh token so the caller can load the user.</summary>
    public static Guid? PeekUserId(string token)
    {
        var parts = token.Split('.');
        return parts.Length == 3 && Guid.TryParseExact(parts[0], "N", out var id) ? id : null;
    }

    /// <summary>True when the token is unexpired, signed by us and matches the user's current stamp.</summary>
    public bool ValidateRefresh(string token, User user)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var id) || id != user.Id) return false;
        if (!long.TryParse(parts[1], out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp) <= clock.GetUtcNow()) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Base64Url.DecodeFromChars(parts[2]), Sign($"{parts[0]}.{parts[1]}", user.SecurityStamp));
        }
        catch (FormatException) { return false; }
    }

    byte[] Sign(string payload, string? stamp) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes("refresh:" + _o.Key), Encoding.UTF8.GetBytes($"{payload}|{stamp}"));
}

public record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);
