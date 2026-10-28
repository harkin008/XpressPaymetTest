using XpressTest.Shared.Models;

namespace XpressTest.AuthService.Models;

/// <summary>
/// A one-time refresh token (only its hash is stored). Each use rotates it: the old token is
/// revoked and a new one in the same <see cref="FamilyId"/> is issued. Presenting an
/// already-rotated token signals theft, so the whole family is revoked.
/// </summary>
public class RefreshToken : BaseEntity
{
    public long UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }

    /// <summary>
    /// The user's security stamp at issue time. Deactivation, role changes and password changes
    /// rotate the stamp, which invalidates every refresh token issued before them.
    /// </summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}
