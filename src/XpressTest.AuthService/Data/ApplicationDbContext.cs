using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using XpressTest.AuthService.Models;
using XpressTest.Shared.Data;

namespace XpressTest.AuthService.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<long>, long>(options)
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.FullName).HasMaxLength(100);
            // Supports the prefix search in UserQueries. Identity already indexes
            // NormalizedEmail, NormalizedUserName, UserRoles.RoleId and Roles.NormalizedName.
            user.HasIndex(u => u.FullName);
        });

        builder.Entity<RefreshToken>(token =>
        {
            token.Property(t => t.TokenHash).HasMaxLength(64);
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasIndex(t => t.FamilyId);
            token.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ApiKey>(key =>
        {
            key.Property(k => k.Name).HasMaxLength(100);
            key.Property(k => k.Prefix).HasMaxLength(16);
            key.Property(k => k.KeyHash).HasMaxLength(64);
            key.HasIndex(k => k.KeyHash).IsUnique();
            key.HasOne(k => k.User).WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.UseApplicationIds();
    }
}
