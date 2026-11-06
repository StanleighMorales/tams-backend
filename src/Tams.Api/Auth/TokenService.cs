using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Auth;

/// <summary>
/// Access tokens are short-lived JWTs (sub = user id, role = a Roles constant). Refresh tokens
/// are opaque and stored hashed in refresh_tokens; see <see cref="RefreshTokens"/>.
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

    public TokenPair Issue(User user, string refreshToken)
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

        return new TokenPair(access, accessExpires, refreshToken);
    }
}

public record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);
