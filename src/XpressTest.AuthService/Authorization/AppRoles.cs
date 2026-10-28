using System.Security.Claims;
using XpressTest.Shared.Auth;

namespace XpressTest.AuthService.Authorization;

public static class AppRoles
{
    public const string User = XpressRoles.User;
    public const string Admin = XpressRoles.Admin;
    public const string SuperAdmin = XpressRoles.SuperAdmin;

    public static readonly string[] All = [User, Admin, SuperAdmin];

    // Higher rank = more privileges. A manager may only act on users ranked strictly below them.
    private static int Rank(string? role) => role switch
    {
        SuperAdmin => 3,
        Admin => 2,
        User => 1,
        _ => 0
    };

    public static int Rank(IEnumerable<string> roles) => roles.Select(Rank).DefaultIfEmpty(0).Max();

    public static int Rank(ClaimsPrincipal principal) => Rank(All.Where(principal.IsInRole));

    public static string HighestRole(IEnumerable<string> roles) =>
        roles.OrderByDescending(Rank).FirstOrDefault() ?? User;

    /// <summary>Admins manage Users; SuperAdmins manage Users and Admins. Nobody manages peers or superiors.</summary>
    public static bool CanManage(ClaimsPrincipal actor, IEnumerable<string> targetRoles)
    {
        var actorRank = Rank(actor);
        return actorRank >= Rank(Admin) && actorRank > Rank(targetRoles);
    }

    /// <summary>Only SuperAdmins may delete, and only users ranked below them.</summary>
    public static bool CanDelete(ClaimsPrincipal actor, IEnumerable<string> targetRoles) =>
        actor.IsInRole(SuperAdmin) && CanManage(actor, targetRoles);

    /// <summary>Roles the actor is allowed to grant. Admins cannot create Admins.</summary>
    public static IReadOnlyList<string> AssignableBy(ClaimsPrincipal actor)
    {
        if (actor.IsInRole(SuperAdmin)) return [User, Admin];
        if (actor.IsInRole(Admin)) return [User];
        return [];
    }
}

public static class AppPolicies
{
    public const string RequireAdmin = nameof(RequireAdmin);
    public const string RequireSuperAdmin = nameof(RequireSuperAdmin);
}
