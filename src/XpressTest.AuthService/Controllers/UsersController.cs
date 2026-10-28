using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Data.Queries;
using XpressTest.AuthService.Services;
using XpressTest.AuthService.ViewModels;

namespace XpressTest.AuthService.Controllers;

/// <summary>
/// User management pages. The rules live in <see cref="UserManagementService"/> (shared with
/// the REST API); hidden buttons are not the security boundary.
/// </summary>
[Authorize(Policy = AppPolicies.RequireAdmin)]
public class UsersController(UserManagementService users, UserQueries queries) : Controller
{
    public async Task<IActionResult> Index([FromQuery] UserFilter filter, CancellationToken ct)
    {
        var result = await queries.GetUserPageAsync(filter, ct);
        var items = result.Items.Select(u => new UserListItemViewModel
        {
            Id = u.Id,
            FullName = u.FullName,
            Email = u.Email,
            Role = AppRoles.HighestRole(u.Roles),
            IsActive = u.IsActive,
            CanManage = AppRoles.CanManage(User, u.Roles),
            CanDelete = AppRoles.CanDelete(User, u.Roles)
        }).ToList();

        return View(new UserListViewModel
        {
            Items = items,
            Filter = filter,
            Page = result.Page,
            TotalPages = result.TotalPages,
            TotalCount = result.TotalCount
        });
    }

    [HttpPost]
    public async Task<IActionResult> BulkDeactivate(List<long> ids, CancellationToken ct)
    {
        var count = await users.DeactivateManyAsync(User, ids, ct);
        TempData["Message"] = $"Deactivated {count} user(s).";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public IActionResult Create() =>
        View(new CreateUserViewModel { Role = AppRoles.User, AssignableRoles = AppRoles.AssignableBy(User) });

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserViewModel model)
    {
        model.AssignableRoles = AppRoles.AssignableBy(User);
        if (!model.AssignableRoles.Contains(model.Role))
            return Forbid(); // e.g. an Admin posting Role=Admin
        if (!ModelState.IsValid) return View(model);

        var result = await users.CreateAsync(User, model.FullName, model.Email, model.Password, model.Role);
        if (Handle(result) is { } failure) return failure;
        if (result.Status == UserOperationStatus.Invalid) return View(model);

        TempData["Message"] = $"Created {model.Role} '{model.Email}'.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(long id)
    {
        var found = await queries.GetUserWithRolesAsync(id);
        if (found is null) return NotFound();
        if (!AppRoles.CanManage(User, found.Roles)) return Forbid();

        return View(new EditUserViewModel
        {
            Id = found.User.Id,
            FullName = found.User.FullName,
            Email = found.User.Email ?? string.Empty,
            Role = AppRoles.HighestRole(found.Roles),
            AssignableRoles = AppRoles.AssignableBy(User)
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(EditUserViewModel model)
    {
        model.AssignableRoles = AppRoles.AssignableBy(User);
        if (!ModelState.IsValid)
        {
            // Still enforce access before echoing the form back.
            var found = await queries.GetUserWithRolesAsync(model.Id);
            if (found is null) return NotFound();
            return AppRoles.CanManage(User, found.Roles) ? View(model) : Forbid();
        }

        var result = await users.UpdateAsync(User, model.Id, model.FullName, model.Email, model.Role);
        if (Handle(result) is { } failure) return failure;
        if (result.Status == UserOperationStatus.Invalid) return View(model);

        TempData["Message"] = $"Updated '{model.Email}'.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public Task<IActionResult> Deactivate(long id) => SetActiveAsync(id, false);

    [HttpPost]
    public Task<IActionResult> Activate(long id) => SetActiveAsync(id, true);

    [HttpPost]
    [Authorize(Policy = AppPolicies.RequireSuperAdmin)]
    public async Task<IActionResult> Delete(long id)
    {
        var result = await users.DeleteAsync(User, id);
        if (Handle(result) is { } failure) return failure;

        TempData["Message"] = $"Deleted '{result.User!.Email}'.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> SetActiveAsync(long id, bool active)
    {
        var result = await users.SetActiveAsync(User, id, active);
        if (Handle(result) is { } failure) return failure;

        TempData["Message"] = $"{(active ? "Activated" : "Deactivated")} '{result.User!.Email}'.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>NotFound/Forbidden become responses; validation errors go into ModelState.</summary>
    private IActionResult? Handle(UserOperationResult result)
    {
        switch (result.Status)
        {
            case UserOperationStatus.NotFound: return NotFound();
            case UserOperationStatus.Forbidden: return Forbid();
            case UserOperationStatus.Invalid:
                foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
                return null;
            default: return null;
        }
    }
}
