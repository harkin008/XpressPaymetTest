using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XpressTest.Shared.Auth;
using XpressTest.Web.Models;
using XpressTest.Web.Services;

namespace XpressTest.Web.Controllers;

/// <summary>
/// Orders pages. Users see and manage their own orders; Admins and SuperAdmins see every order
/// and can change statuses. OrderService enforces all of this; the pages only mirror it.
/// </summary>
[Route("orders")]
public class OrdersController(OrdersApiClient orders) : Controller
{
    public const int PageSize = 10;

    private bool IsAdmin => User.IsInRole(XpressRoles.Admin) || User.IsInRole(XpressRoles.SuperAdmin);

    [HttpGet("")]
    public async Task<IActionResult> Index(string? status, int page = 1, CancellationToken ct = default)
    {
        var result = await orders.ListAsync(status, Math.Max(1, page), PageSize, ct);
        if (!result.Succeeded) return ErrorPage(result);

        return View(new OrderListViewModel { Orders = result.Value!, Status = status, ShowCustomer = IsAdmin });
    }

    [HttpGet("new")]
    public IActionResult Create() => View(new OrderFormViewModel());

    [HttpPost("new")]
    public async Task<IActionResult> Create(OrderFormViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await orders.CreateAsync(model.ProductName, model.Quantity, model.UnitPrice, ct);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorText);
            return View(model);
        }

        TempData["Message"] = $"Order #{result.Value!.Id} placed. A confirmation email is on its way.";
        return RedirectToAction(nameof(Details), new { id = result.Value.Id });
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var result = await orders.GetAsync(id, ct);
        if (!result.Succeeded) return ErrorPage(result);

        return View(new OrderDetailsViewModel { Order = result.Value!, IsAdmin = IsAdmin });
    }

    [HttpGet("{id:long}/edit")]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var result = await orders.GetAsync(id, ct);
        if (!result.Succeeded) return ErrorPage(result);

        var order = result.Value!;
        ViewData["Order"] = order;
        return View(new OrderFormViewModel { ProductName = order.ProductName, Quantity = order.Quantity, UnitPrice = order.UnitPrice });
    }

    [HttpPost("{id:long}/edit")]
    public async Task<IActionResult> Edit(long id, OrderFormViewModel model, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await orders.UpdateAsync(id, model.ProductName, model.Quantity, model.UnitPrice, status: null, ct);
            if (result.Succeeded)
            {
                TempData["Message"] = $"Order #{id} updated.";
                return RedirectToAction(nameof(Details), new { id });
            }
            if (result.Status == HttpStatusCode.NotFound) return ErrorPage(result);
            ModelState.AddModelError(string.Empty, result.ErrorText);
        }

        var current = await orders.GetAsync(id, ct);
        if (!current.Succeeded) return ErrorPage(current);
        ViewData["Order"] = current.Value;
        return View(model);
    }

    /// <summary>Admins and SuperAdmins: move an order to another status (the customer is emailed).</summary>
    [HttpPost("{id:long}/status")]
    [Authorize(Policy = WebPolicies.Admin)]
    public async Task<IActionResult> ChangeStatus(long id, string status, CancellationToken ct)
    {
        var current = await orders.GetAsync(id, ct);
        if (!current.Succeeded) return ErrorPage(current);

        var order = current.Value!;
        var result = await orders.UpdateAsync(id, order.ProductName, order.Quantity, order.UnitPrice, status, ct);
        SetFlash(result, $"Order #{id} is now {status}. The customer has been notified.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        var result = await orders.CancelAsync(id, ct);
        if (result.Status == HttpStatusCode.NotFound) return ErrorPage(result);
        SetFlash(result, $"Order #{id} cancelled.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private void SetFlash<T>(ApiResult<T> result, string success)
    {
        if (result.Succeeded) TempData["Message"] = success;
        else TempData["Error"] = result.ErrorText;
    }

    private IActionResult ErrorPage<T>(ApiResult<T> result) => result.Status switch
    {
        HttpStatusCode.NotFound => NotFound(),
        HttpStatusCode.Forbidden => RedirectToAction("AccessDenied", "Account"),
        _ => View("ApiError", result.ErrorText)
    };
}
