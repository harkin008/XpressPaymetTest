using XpressTest.Shared.Models;

namespace XpressTest.AuthService.Models;

/// <summary>An API credential owned by a user. Only the SHA-256 hash of the key is stored.</summary>
public class ApiKey : BaseEntity
{
    public long UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>First characters of the key, so owners can tell keys apart.</summary>
    public string Prefix { get; set; } = string.Empty;

    public string KeyHash { get; set; } = string.Empty;

    public DateTime? RevokedAtUtc { get; set; }
}
