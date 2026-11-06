using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Tams.Api.Data.Entities;

// Opaque refresh token, stored only as SHA-256 of the token. Each refresh rotates it:
// the old row gets RevokedAt and ReplacedById, and presenting a revoked row is treated as reuse.
[Table("refresh_tokens")]
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    [MaxLength(200)] public string? UserAgent { get; set; }
    [MaxLength(45)] public string? IpAddress { get; set; }
}
