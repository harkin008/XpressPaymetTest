using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Data.Queries;

namespace XpressTest.AuthService.Controllers;

/// <summary>Sample protected resources, one per access level.</summary>
[Authorize]
public class ResourcesController : Controller
{
    // Any signed-in user (User, Admin, SuperAdmin).
    public IActionResult Index() => View();

    [Authorize(Policy = AppPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminReports([FromServices] UserQueries queries, CancellationToken ct) =>
        View(await queries.GetDashboardStatsAsync(ct));

    [Authorize(Policy = AppPolicies.RequireSuperAdmin)]
    public IActionResult SystemSettings() => View();
}
