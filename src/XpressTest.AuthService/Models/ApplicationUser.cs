using Microsoft.AspNetCore.Identity;
using XpressTest.Shared.Models;

namespace XpressTest.AuthService.Models;

/// <summary>Inherits its Id from <see cref="IdentityUser{TKey}"/>, so it implements <see cref="IBaseEntity"/> rather than extending BaseEntity.</summary>
public class ApplicationUser : IdentityUser<long>, IBaseEntity
{
    public string FullName { get; set; } = string.Empty;

    /// <summary>Deactivated users keep their data but cannot sign in.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
