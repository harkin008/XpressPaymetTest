using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XpressTest.Web.Models;
using XpressTest.Web.Services;

namespace XpressTest.Web.Controllers;

/// <summary>
/// Admin and SuperAdmin pages: dashboard and user management. What each admin may do to each
/// user comes from AuthService (CanManage / CanDelete / AssignableRoles), which also enforces it.
/// </summary>
[Route("admin")]
[Authorize(Policy = WebPolicies.Admin)]
public class AdminController(UsersApiClient users, OrdersApiClient orders) : Controller
{
    public const int PageSize = 20;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userStats = users.StatsAsync(ct);
        var orderStats = orders.StatsAsync(ct);
        await Task.WhenAll(userStats, orderStats);
        return View(new DashboardViewModel { Users = userStats.Result.Value, Orders = orderStats.Result.Value });
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users(string? search, string? role, bool? isActive, int page = 1, CancellationToken ct = default)
    {
        var result = await users.ListAsync(search, role, isActive, Math.Max(1, page), PageSize, ct);
        if (!result.Succeeded) return View("ApiError", result.ErrorText);
        return View(new UserListViewModel { Users = result.Value!, Search = search, Role = role, IsActive = isActive });
    }

    [HttpGet("users/new")]
    public async Task<IActionResult> CreateUser(CancellationToken ct) =>
        View(new CreateUserViewModel { AssignableRoles = await AssignableRolesAsync(ct) });

    [HttpPost("users/new")]
    public async Task<IActionResult> CreateUser(CreateUserViewModel model, CancellationToken ct)
    {
        model.AssignableRoles = await AssignableRolesAsync(ct);
        if (!ModelState.IsValid) return View(model);

        var result = await users.CreateAsync(model.FullName, model.Email, model.Password, model.Role, ct);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorText);
            return View(model);
        }

        TempData["Message"] = $"Created {model.Role} {model.Email}.";
        return RedirectToAction(nameof(Users));
    }

    [HttpGet("users/{id:long}/edit")]
    public async Task<IActionResult> EditUser(long id, CancellationToken ct)
    {
        var result = await users.GetAsync(id, ct);
        if (result.Status == HttpStatusCode.NotFound) return NotFound();
        if (!result.Succeeded) return View("ApiError", result.ErrorText);
        if (!result.Value!.CanManage) return RedirectToAction("AccessDenied", "Account");

        var user = result.Value;
        return View(new EditUserViewModel
        {
            Id = user.Id, FullName = user.FullName, Email = user.Email, Role = user.Role,
            AssignableRoles = await AssignableRolesAsync(ct)
        });
    }

    [HttpPost("users/{id:long}/edit")]
    public async Task<IActionResult> EditUser(long id, EditUserViewModel model, CancellationToken ct)
    {
        model.Id = id;
        model.AssignableRoles = await AssignableRolesAsync(ct);
        if (!ModelState.IsValid) return View(model);

        var result = await users.UpdateAsync(id, model.FullName, model.Email, model.Role, ct);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorText);
            return View(model);
        }

        TempData["Message"] = $"Updated {model.Email}.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost("users/{id:long}/activate")]
    public Task<IActionResult> Activate(long id, CancellationToken ct) => SetActiveAsync(id, true, ct);

    [HttpPost("users/{id:long}/deactivate")]
    public Task<IActionResult> Deactivate(long id, CancellationToken ct) => SetActiveAsync(id, false, ct);

    [HttpPost("users/deactivate")]
    public async Task<IActionResult> DeactivateMany(List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            TempData["Error"] = "Select at least one user.";
            return RedirectToAction(nameof(Users));
        }

        var result = await users.DeactivateManyAsync(ids, ct);
        if (result.Succeeded) TempData["Message"] = $"Deactivated {result.Value!.Deactivated} user(s).";
        else TempData["Error"] = result.ErrorText;
        return RedirectToAction(nameof(Users));
    }

    [HttpPost("users/{id:long}/delete")]
    [Authorize(Policy = WebPolicies.SuperAdmin)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var result = await users.DeleteAsync(id, ct);
        if (result.Succeeded) TempData["Message"] = "User deleted.";
        else TempData["Error"] = result.ErrorText;
        return RedirectToAction(nameof(Users));
    }

    private async Task<IActionResult> SetActiveAsync(long id, bool active, CancellationToken ct)
    {
        var result = await users.SetActiveAsync(id, active, ct);
        if (result.Succeeded) TempData["Message"] = active ? "User activated." : "User deactivated. Their sessions end within 15 minutes.";
        else TempData["Error"] = result.ErrorText;
        return RedirectToAction(nameof(Users));
    }

    // The roles this admin may grant, as decided by AuthService.
    private async Task<IReadOnlyList<string>> AssignableRolesAsync(CancellationToken ct) =>
        (await users.ListAsync(null, null, null, 1, 1, ct)).Value?.AssignableRoles ?? [];
}
