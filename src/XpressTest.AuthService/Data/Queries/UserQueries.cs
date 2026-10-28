using Microsoft.EntityFrameworkCore;
using XpressTest.AuthService.Models;

namespace XpressTest.AuthService.Data.Queries;

public class UserFilter
{
    public string? Search { get; set; }
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public record UserSummary(long Id, string FullName, string Email, bool IsActive, List<string> Roles);

public record UserWithRoles(ApplicationUser User, List<string> Roles);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public record RoleStatusCount(string? Role, bool IsActive, int Count);

public record DashboardStats(int Total, int Active, int Inactive, IReadOnlyList<RoleStatusCount> Breakdown);

/// <summary>
/// Read-optimised queries for user management. See docs/QUERIES.md for the generated SQL.
/// </summary>
public class UserQueries(ApplicationDbContext db)
{
    public const int MaxPageSize = 100;

    /// <summary>
    /// Q1: one filtered, sorted, paged page of users with their role names.
    /// The page and all of its roles come back in a single query (no N+1); a COUNT
    /// follows only when the page itself cannot prove the total.
    /// </summary>
    public async Task<PagedResult<UserSummary>> GetUserPageAsync(UserFilter filter, CancellationToken ct = default)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, MaxPageSize);
        var query = ApplyFilter(filter);

        var items = await query
            .OrderBy(u => u.NormalizedEmail).ThenBy(u => u.Id) // indexed column + unique tiebreaker = stable paging
            .Skip((page - 1) * size)
            .Take(size)
            .Select(u => new UserSummary(
                u.Id,
                u.FullName,
                u.Email!,
                u.IsActive,
                db.UserRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!)
                    .ToList()))
            .ToListAsync(ct);

        // A short (or empty first) page already tells us the exact total.
        var total = items.Count < size && (items.Count > 0 || page == 1)
            ? (page - 1) * size + items.Count
            : await query.CountAsync(ct);

        return new PagedResult<UserSummary>(items, total, page, size);
    }

    // Q2: user + roles in one round trip (instead of FindByIdAsync + GetRolesAsync).
    // Compiled once per app lifetime, so LINQ expression translation is skipped on every call.
    // The user entity is tracked, so UserManager can update it afterwards.
    private static readonly Func<ApplicationDbContext, long, Task<UserWithRoles?>> UserWithRolesQuery =
        EF.CompileAsyncQuery((ApplicationDbContext db, long id) =>
            db.Users
                .Where(u => u.Id == id)
                .Select(u => new UserWithRoles(
                    u,
                    db.UserRoles
                        .Where(ur => ur.UserId == u.Id)
                        .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!)
                        .ToList()))
                .FirstOrDefault());

    public Task<UserWithRoles?> GetUserWithRolesAsync(long id) => UserWithRolesQuery(db, id);

    /// <summary>
    /// Q3: dashboard numbers in one aggregate query; the server returns one row per (role, status)
    /// instead of every user row.
    /// </summary>
    public async Task<DashboardStats> GetDashboardStatsAsync(CancellationToken ct = default)
    {
        var breakdown = await (
                from u in db.Users
                from ur in db.UserRoles.Where(ur => ur.UserId == u.Id).DefaultIfEmpty()
                from r in db.Roles.Where(r => r.Id == ur!.RoleId).DefaultIfEmpty()
                group u by new { Role = r!.Name, u.IsActive } into g
                orderby g.Key.Role, g.Key.IsActive
                select new RoleStatusCount(g.Key.Role, g.Key.IsActive, g.Count()))
            .ToListAsync(ct);

        // Each user holds exactly one role in this app, so summing the breakdown is exact.
        var active = breakdown.Where(b => b.IsActive).Sum(b => b.Count);
        var inactive = breakdown.Where(b => !b.IsActive).Sum(b => b.Count);
        return new DashboardStats(active + inactive, active, inactive, breakdown);
    }

    /// <summary>
    /// Q4: deactivate many users at once. Only users whose every role is in
    /// <paramref name="manageableRoles"/> are touched; the permission check runs in the WHERE clause.
    /// On relational providers this is a single UPDATE statement with no rows loaded.
    /// Returns the number of users deactivated.
    /// </summary>
    public async Task<int> DeactivateUsersAsync(
        IReadOnlyCollection<long> userIds, IReadOnlyCollection<string> manageableRoles, CancellationToken ct = default)
    {
        if (userIds.Count == 0 || manageableRoles.Count == 0) return 0;

        var ids = userIds.Distinct().ToList();
        var roles = manageableRoles.Select(r => r.ToUpperInvariant()).ToList();

        var targets = db.Users.Where(u =>
            ids.Contains(u.Id)
            && u.IsActive
            && !db.UserRoles.Any(ur => ur.UserId == u.Id
                && !db.Roles.Any(r => r.Id == ur.RoleId && roles.Contains(r.NormalizedName!))));

        if (db.Database.IsRelational())
        {
            // Bypasses the change tracker and UserManager, so the stamps Identity
            // normally rotates are rotated here. A new security stamp ends live sessions.
            // Random batch value + user id keeps each stamp unique and unguessable while
            // staying translatable on every provider (SQLite cannot translate Guid.NewGuid()).
            var batch = Guid.NewGuid().ToString("N");
            return await targets.ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, false)
                .SetProperty(u => u.SecurityStamp, u => batch + u.Id.ToString())
                .SetProperty(u => u.ConcurrencyStamp, u => batch + u.Id.ToString()), ct);
        }

        // The in-memory provider has no ExecuteUpdate: one SELECT, then one SaveChanges.
        var users = await targets.ToListAsync(ct);
        foreach (var user in users)
        {
            user.IsActive = false;
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
        }
        await db.SaveChangesAsync(ct);
        return users.Count;
    }

    private IQueryable<ApplicationUser> ApplyFilter(UserFilter filter)
    {
        var query = db.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Prefix match (LIKE 'x%') keeps the predicate sargable, so indexes can be used.
            var term = filter.Search.Trim();
            var normalized = term.ToUpperInvariant();
            query = query.Where(u => u.NormalizedEmail!.StartsWith(normalized) || u.FullName.StartsWith(term));
        }

        if (!string.IsNullOrWhiteSpace(filter.Role))
        {
            var role = filter.Role.ToUpperInvariant();
            query = query.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id
                && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == role)));
        }

        if (filter.IsActive is { } isActive)
            query = query.Where(u => u.IsActive == isActive);

        return query;
    }
}
