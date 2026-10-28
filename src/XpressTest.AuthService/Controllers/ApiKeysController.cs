using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Data;
using XpressTest.AuthService.Models;
using XpressTest.AuthService.ViewModels;

namespace XpressTest.AuthService.Controllers;

/// <summary>Lets a signed-in user issue and revoke their own API keys for the /api endpoints.</summary>
[Authorize]
public class ApiKeysController(ApplicationDbContext db, TimeProvider clock) : Controller
{
    public const int MaxActiveKeys = 5;

    private long CurrentUserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = CurrentUserId;
        var keys = await db.ApiKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyListItemViewModel(k.Id, k.Name, k.Prefix, k.CreatedAt, k.RevokedAtUtc))
            .ToListAsync(ct);
        return View(keys);
    }

    [HttpPost]
    public async Task<IActionResult> Create(string? name, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (await db.ApiKeys.CountAsync(k => k.UserId == userId && k.RevokedAtUtc == null, ct) >= MaxActiveKeys)
        {
            TempData["Message"] = $"You can have at most {MaxActiveKeys} active keys. Revoke one first.";
            return RedirectToAction(nameof(Index));
        }

        var key = ApiKeyDefaults.Generate();
        var now = clock.GetUtcNow().UtcDateTime;
        db.ApiKeys.Add(new ApiKey
        {
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? "API key" : name.Trim()[..Math.Min(100, name.Trim().Length)],
            Prefix = ApiKeyDefaults.DisplayPrefix(key),
            KeyHash = ApiKeyDefaults.Hash(key),
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(ct);

        // Shown once; only the hash is stored.
        TempData["NewApiKey"] = key;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Revoke(long id, CancellationToken ct)
    {
        var userId = CurrentUserId;
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId, ct);
        if (key is null) return NotFound();

        key.RevokedAtUtc ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Revoked '{key.Name}'.";
        return RedirectToAction(nameof(Index));
    }
}
