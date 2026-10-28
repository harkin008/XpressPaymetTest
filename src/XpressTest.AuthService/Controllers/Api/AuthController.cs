using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Models;
using XpressTest.AuthService.Tokens;
using XpressTest.Shared.Auth;

namespace XpressTest.AuthService.Controllers.Api;

/// <summary>JSON account API for API clients; the browser portal uses /Account instead.</summary>
[ApiController]
[Route("api/auth")]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public class AuthController(UserManager<ApplicationUser> userManager, TokenService tokens) : ControllerBase
{
    /// <summary>Self-registration (always the User role). Returns tokens like the password grant.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var user = new ApplicationUser { UserName = request.Email, Email = request.Email, FullName = request.FullName.Trim() };
        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(error.Code, error.Description);
            return ValidationProblem(ModelState);
        }

        await userManager.AddToRoleAsync(user, AppRoles.User);
        var issued = await tokens.IssueForUserAsync(user, [AppRoles.User], withRefreshToken: true, ct: ct);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, new Dictionary<string, object>
        {
            ["access_token"] = issued.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = issued.ExpiresIn,
            ["refresh_token"] = issued.RefreshToken!
        });
    }

    /// <summary>The caller's identity, as read from their access token.</summary>
    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult Me() => Ok(new
    {
        id = User.GetSubject(),
        email = User.FindFirst(XpressClaims.Email)?.Value,
        name = User.Identity?.Name,
        roles = User.FindAll(XpressClaims.Role).Select(c => c.Value)
    });
}

public class RegisterRequest
{
    [Required, StringLength(100)] public string FullName { get; set; } = string.Empty;
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, StringLength(100, MinimumLength = 6)] public string Password { get; set; } = string.Empty;
}
