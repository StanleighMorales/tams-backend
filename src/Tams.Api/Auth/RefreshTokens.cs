using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Auth;

/// <summary>
/// Opaque refresh tokens: 32 random bytes (base64url) handed to the client, only their SHA-256
/// stored. Add and revoke helpers only stage changes on the context; the caller saves, so the
/// change commits atomically with whatever else the request did.
/// </summary>
public static class RefreshTokens
{
    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    /// <summary>Stages a new token for the user and returns the raw value to give to the client.</summary>
    public static string Add(TamsDbContext db, Guid userId, HttpContext http, DateTimeOffset now, int days, out RefreshToken row)
    {
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var ua = http.Request.Headers.UserAgent.ToString();
        row = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = now.AddDays(days),
            UserAgent = ua.Length == 0 ? null : ua[..Math.Min(ua.Length, 200)],
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        };
        db.RefreshTokens.Add(row);
        return raw;
    }

    /// <summary>Marks every active refresh token of the user revoked. Call wherever the SecurityStamp rotates.</summary>
    public static async Task RevokeAllAsync(TamsDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var t in await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct))
            t.RevokedAt = now;
    }

    static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
