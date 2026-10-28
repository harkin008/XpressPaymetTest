using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Models;

namespace XpressTest.AuthService.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // Creates or upgrades the schema. Skipped for the in-memory provider the tests use.
        if (db.Database.IsRelational()) await db.Database.MigrateAsync();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<long>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var role in AppRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole<long>(role));
        }

        await EnsureUserAsync(userManager, "superadmin@xpress.local", "Super Admin", "SuperAdmin@123", AppRoles.SuperAdmin);
        await EnsureUserAsync(userManager, "admin@xpress.local", "Default Admin", "Admin@123", AppRoles.Admin);
        await EnsureUserAsync(userManager, "user@xpress.local", "Default User", "User@123", AppRoles.User);

        // Well-known keys for local testing; only appsettings.Development.json defines them.
        var demoKeys = scope.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetSection("DemoApiKeys").Get<Dictionary<string, string>>() ?? [];
        foreach (var (email, key) in demoKeys)
        {
            var hash = ApiKeyDefaults.Hash(key);
            if (await db.ApiKeys.AnyAsync(k => k.KeyHash == hash)) continue;
            if (await userManager.FindByEmailAsync(email) is not { } owner) continue;

            db.ApiKeys.Add(new ApiKey
            {
                UserId = owner.Id,
                Name = "Demo key",
                Prefix = ApiKeyDefaults.DisplayPrefix(key),
                KeyHash = hash,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task EnsureUserAsync(
        UserManager<ApplicationUser> userManager, string email, string fullName, string password, string role)
    {
        if (await userManager.FindByEmailAsync(email) is not null) return;

        var user = new ApplicationUser { UserName = email, Email = email, FullName = fullName, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Seeding {email} failed: {string.Join(", ", result.Errors.Select(e => e.Description))}");

        await userManager.AddToRoleAsync(user, role);
    }
}
