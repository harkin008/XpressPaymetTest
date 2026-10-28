using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Data.Queries;
using XpressTest.AuthService.Models;

namespace XpressTest.AuthService.Services;

public enum UserOperationStatus
{
    Succeeded,
    NotFound,
    Forbidden,
    Invalid
}

public record UserOperationResult(UserOperationStatus Status, IReadOnlyList<string> Errors, ApplicationUser? User = null)
{
    public static UserOperationResult Ok(ApplicationUser user) => new(UserOperationStatus.Succeeded, [], user);
    public static readonly UserOperationResult NotFound = new(UserOperationStatus.NotFound, []);
    public static readonly UserOperationResult Forbidden = new(UserOperationStatus.Forbidden, []);
    public static UserOperationResult Invalid(IdentityResult result) =>
        new(UserOperationStatus.Invalid, result.Errors.Select(e => e.Description).ToList());
}

/// <summary>
/// The user-management rules, shared by the portal pages and the REST API so both enforce the
/// same hierarchy: Admins manage Users; SuperAdmins manage Users and Admins and can delete;
/// nobody manages peers or superiors, and nobody grants a role above what they may assign.
/// </summary>
public class UserManagementService(UserManager<ApplicationUser> userManager, UserQueries queries)
{
    public async Task<UserOperationResult> CreateAsync(ClaimsPrincipal actor, string fullName, string email, string password, string role)
    {
        if (!AppRoles.AssignableBy(actor).Contains(role)) return UserOperationResult.Forbidden;

        var user = new ApplicationUser { UserName = email, Email = email, FullName = fullName.Trim(), EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded) return UserOperationResult.Invalid(result);

        await userManager.AddToRoleAsync(user, role);
        return UserOperationResult.Ok(user);
    }

    public async Task<UserOperationResult> UpdateAsync(ClaimsPrincipal actor, long id, string fullName, string email, string role)
    {
        var found = await queries.GetUserWithRolesAsync(id);
        if (found is null) return UserOperationResult.NotFound;
        if (!AppRoles.CanManage(actor, found.Roles) || !AppRoles.AssignableBy(actor).Contains(role)) return UserOperationResult.Forbidden;

        var user = found.User;
        user.FullName = fullName.Trim();
        user.Email = email;
        user.UserName = email;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded) return UserOperationResult.Invalid(result);

        if (!found.Roles.SequenceEqual([role]))
        {
            await userManager.RemoveFromRolesAsync(user, found.Roles);
            await userManager.AddToRoleAsync(user, role);
            // Ends portal sessions and invalidates refresh tokens, so the new role takes effect.
            await userManager.UpdateSecurityStampAsync(user);
        }
        return UserOperationResult.Ok(user);
    }

    public async Task<UserOperationResult> SetActiveAsync(ClaimsPrincipal actor, long id, bool active)
    {
        var found = await queries.GetUserWithRolesAsync(id);
        if (found is null) return UserOperationResult.NotFound;
        if (!AppRoles.CanManage(actor, found.Roles)) return UserOperationResult.Forbidden;

        found.User.IsActive = active;
        // Saves IsActive together with a new security stamp in one UPDATE; the new stamp ends
        // portal sessions and invalidates the user's refresh tokens.
        await userManager.UpdateSecurityStampAsync(found.User);
        return UserOperationResult.Ok(found.User);
    }

    public async Task<UserOperationResult> DeleteAsync(ClaimsPrincipal actor, long id)
    {
        var found = await queries.GetUserWithRolesAsync(id);
        if (found is null) return UserOperationResult.NotFound;
        if (!AppRoles.CanDelete(actor, found.Roles)) return UserOperationResult.Forbidden;

        await userManager.DeleteAsync(found.User);
        return UserOperationResult.Ok(found.User);
    }

    /// <summary>Deactivates the given users the actor may manage; others are skipped. Returns the count.</summary>
    public Task<int> DeactivateManyAsync(ClaimsPrincipal actor, IReadOnlyCollection<long> ids, CancellationToken ct) =>
        queries.DeactivateUsersAsync(ids, AppRoles.AssignableBy(actor), ct);
}
