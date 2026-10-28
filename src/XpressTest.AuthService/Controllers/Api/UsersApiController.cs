using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Data.Queries;
using XpressTest.AuthService.Services;

namespace XpressTest.AuthService.Controllers.Api;

/// <summary>
/// User management REST API for front ends (e.g. XpressTest.Web). Bearer tokens only, Admin or
/// SuperAdmin. Same rules as the portal pages via <see cref="UserManagementService"/>. Each user in a
/// response says what the caller may do with it, so clients never re-implement the hierarchy.
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AppPolicies.RequireAdmin)]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public class UsersApiController(UserManagementService users, UserQueries queries) : ControllerBase
{
    [HttpGet]
    public async Task<UserPageResponse> List([FromQuery] UserFilter filter, CancellationToken ct)
    {
        var page = await queries.GetUserPageAsync(filter, ct);
        return new UserPageResponse(
            page.Items.Select(u => ToResponse(u.Id, u.FullName, u.Email, u.IsActive, u.Roles)).ToList(),
            page.Page, page.PageSize, page.TotalCount, page.TotalPages, AppRoles.AssignableBy(User));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<UserResponse>> Get(long id)
    {
        var found = await queries.GetUserWithRolesAsync(id);
        if (found is null) return NotFound();
        return ToResponse(found.User.Id, found.User.FullName, found.User.Email ?? "", found.User.IsActive, found.Roles);
    }

    [HttpGet("stats")]
    public Task<DashboardStats> Stats(CancellationToken ct) => queries.GetDashboardStatsAsync(ct);

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request)
    {
        var result = await users.CreateAsync(User, request.FullName, request.Email, request.Password, request.Role);
        return ToActionResult(result, user => CreatedAtAction(nameof(Get), new { id = user.Id },
            ToResponse(user.Id, user.FullName, user.Email ?? "", user.IsActive, [request.Role])));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, UpdateUserRequest request)
    {
        var result = await users.UpdateAsync(User, id, request.FullName, request.Email, request.Role);
        return ToActionResult(result, _ => NoContent());
    }

    [HttpPost("{id:long}/activate")]
    public async Task<IActionResult> Activate(long id) => ToActionResult(await users.SetActiveAsync(User, id, true), _ => NoContent());

    [HttpPost("{id:long}/deactivate")]
    public async Task<IActionResult> Deactivate(long id) => ToActionResult(await users.SetActiveAsync(User, id, false), _ => NoContent());

    /// <summary>Deactivates the listed users the caller may manage; the rest are skipped.</summary>
    [HttpPost("deactivate")]
    public async Task<BulkDeactivateResponse> DeactivateMany(BulkDeactivateRequest request, CancellationToken ct) =>
        new(await users.DeactivateManyAsync(User, request.Ids, ct));

    [HttpDelete("{id:long}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AppPolicies.RequireSuperAdmin)]
    public async Task<IActionResult> Delete(long id) => ToActionResult(await users.DeleteAsync(User, id), _ => NoContent());

    private UserResponse ToResponse(long id, string fullName, string email, bool isActive, IList<string> roles) =>
        new(id, fullName, email, AppRoles.HighestRole(roles), isActive, AppRoles.CanManage(User, roles), AppRoles.CanDelete(User, roles));

    private IActionResult ToActionResult(UserOperationResult result, Func<Models.ApplicationUser, IActionResult> onSuccess)
    {
        switch (result.Status)
        {
            case UserOperationStatus.NotFound: return NotFound();
            case UserOperationStatus.Forbidden:
                return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role does not allow this action on this user.");
            case UserOperationStatus.Invalid:
                foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
                return ValidationProblem(ModelState);
            default: return onSuccess(result.User!);
        }
    }
}

public record UserResponse(long Id, string FullName, string Email, string Role, bool IsActive, bool CanManage, bool CanDelete);

public record UserPageResponse(
    IReadOnlyList<UserResponse> Items, int Page, int PageSize, int TotalCount, int TotalPages, IReadOnlyList<string> AssignableRoles);

public class CreateUserRequest
{
    [Required, StringLength(100)] public string FullName { get; set; } = string.Empty;
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, StringLength(100, MinimumLength = 6)] public string Password { get; set; } = string.Empty;
    [Required] public string Role { get; set; } = string.Empty;
}

public class UpdateUserRequest
{
    [Required, StringLength(100)] public string FullName { get; set; } = string.Empty;
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required] public string Role { get; set; } = string.Empty;
}

public class BulkDeactivateRequest
{
    [Required, MaxLength(100)] public List<long> Ids { get; set; } = [];
}

public record BulkDeactivateResponse(int Deactivated);
