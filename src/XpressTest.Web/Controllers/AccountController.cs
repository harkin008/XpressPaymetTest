using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XpressTest.Web.Models;
using XpressTest.Web.Services;

namespace XpressTest.Web.Controllers;

[Route("account")]
public class AccountController(TokenClient tokens, UserSession session) : Controller
{
    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToLocal(returnUrl);
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl, CancellationToken ct)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid) return View(model);

        var result = await tokens.PasswordAsync(model.Email, model.Password, ct);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
            return View(model);
        }

        await session.SignInAsync(HttpContext, result.Tokens!, model.RememberMe);
        return RedirectToLocal(returnUrl);
    }

    [AllowAnonymous]
    [HttpGet("register")]
    public IActionResult Register() => User.Identity?.IsAuthenticated == true ? RedirectToLocal(null) : View();

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await tokens.RegisterAsync(model.FullName, model.Email, model.Password, ct);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
            return View(model);
        }

        await session.SignInAsync(HttpContext, result.Tokens!, persistent: false);
        TempData["Message"] = $"Welcome, {model.FullName}! Your account is ready.";
        return RedirectToAction("Index", "Orders");
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await session.SignOutAsync(HttpContext);
        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous]
    [HttpGet("access-denied")]
    public IActionResult AccessDenied() => View();

    private IActionResult RedirectToLocal(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Orders");
}
