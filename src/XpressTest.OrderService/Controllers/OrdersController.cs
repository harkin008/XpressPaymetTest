using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XpressTest.OrderService.Contracts;
using XpressTest.OrderService.Data;
using XpressTest.OrderService.Models;
using XpressTest.OrderService.RateLimiting;
using XpressTest.Shared.Auth;
using XpressTest.Shared.Contracts;

namespace XpressTest.OrderService.Controllers;

/// <summary>
/// Orders REST API, authenticated with AuthService-issued bearer tokens.
/// Users see and manage their own orders; Admins and SuperAdmins see and manage all orders.
/// Create and edit are limited per user (3 per rolling minute each by default); everything else
/// falls under the baseline "api" policy. Creating an order and changing its status queue an
/// email notification in the outbox, in the same save as the order change.
/// </summary>
[ApiController]
[Route("api/orders")]
[Authorize]
[RateLimit(RateLimitPolicies.Api)]
[Produces("application/json")]
public class OrdersController(OrdersDbContext db, TimeProvider clock) : ControllerBase
{
    public const int MaxPageSize = 100;

    private static readonly Expression<Func<Order, OrderResponse>> ToResponse = o => new OrderResponse
    {
        Id = o.Id,
        UserId = o.UserId,
        CustomerEmail = o.CustomerEmail,
        CustomerName = o.CustomerName,
        ProductName = o.ProductName,
        Quantity = o.Quantity,
        UnitPrice = o.UnitPrice,
        Total = o.Quantity * o.UnitPrice,
        Status = o.Status,
        CreatedAtUtc = o.CreatedAt,
        UpdatedAtUtc = o.UpdatedAt
    };

    private static readonly Func<Order, OrderResponse> MapOrder = ToResponse.Compile();

    // 0 is never issued by IdGenerator, so a non-user token matches no orders.
    private long CurrentUserId => User.GetUserId() ?? 0;
    private bool IsAdmin => User.IsInRole(XpressRoles.Admin) || User.IsInRole(XpressRoles.SuperAdmin);

    [HttpGet]
    public async Task<ActionResult<PagedResponse<OrderResponse>>> List(
        [FromQuery] OrderStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = VisibleOrders();
        if (status is not null) query = query.Where(o => o.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToResponse)
            .ToListAsync(ct);

        return new PagedResponse<OrderResponse>(items, page, pageSize, total);
    }

    /// <summary>Order counts per status, across all orders. One GROUP BY query.</summary>
    [HttpGet("stats")]
    [Authorize(Roles = $"{XpressRoles.Admin},{XpressRoles.SuperAdmin}")]
    public async Task<OrderStatsResponse> Stats(CancellationToken ct)
    {
        var counts = await db.Orders.GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byStatus = Enum.GetValues<OrderStatus>().ToDictionary(
            s => s.ToString(), s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0);
        return new OrderStatsResponse(byStatus.Values.Sum(), byStatus);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<OrderResponse>> GetById(long id, CancellationToken ct)
    {
        var order = await VisibleOrders().Where(o => o.Id == id).Select(ToResponse).FirstOrDefaultAsync(ct);
        return order is null ? NotFound() : order;
    }

    [HttpPost]
    [RateLimit(RateLimitPolicies.OrdersCreate)]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<OrderResponse>> Create(CreateOrderRequest request, CancellationToken ct)
    {
        var email = User.FindFirst(XpressClaims.Email)?.Value;
        if (string.IsNullOrEmpty(email) || User.GetUserId() is null)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Orders can only be placed with a user token (no email or user id claim).");

        var now = clock.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            UserId = CurrentUserId,
            CustomerEmail = email,
            CustomerName = User.Identity?.Name ?? email,
            ProductName = request.ProductName.Trim(),
            Quantity = request.Quantity,
            UnitPrice = request.UnitPrice,
            CreatedAt = now,
            UpdatedAt = now
        };
        // Add assigns the order id, so the order and its notification go out in one save.
        db.Orders.Add(order);

        var messageId = Guid.NewGuid();
        db.EnqueueNotification(NotificationRoutes.OrderCreated, new OrderCreatedNotification(
            messageId, order.Id, order.CustomerEmail, order.CustomerName, order.ProductName,
            order.Quantity, order.UnitPrice, order.Quantity * order.UnitPrice, order.CreatedAt), messageId, now);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = order.Id }, MapOrder(order));
    }

    [HttpPut("{id:long}")]
    [RateLimit(RateLimitPolicies.OrdersEdit)]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<OrderResponse>> Update(long id, UpdateOrderRequest request, CancellationToken ct)
    {
        var order = await VisibleOrders().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return NotFound();

        if (!IsAdmin)
        {
            if (request.Status is not null)
                return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only administrators can change an order's status.");
            if (order.Status != OrderStatus.Pending)
                return Problem(statusCode: StatusCodes.Status409Conflict, title: $"Only pending orders can be edited; this order is {order.Status}.");
        }

        order.ProductName = request.ProductName.Trim();
        order.Quantity = request.Quantity;
        order.UnitPrice = request.UnitPrice;
        if (request.Status is { } status) ChangeStatus(order, status);
        order.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return MapOrder(order);
    }

    /// <summary>Cancels the order (orders are never hard-deleted). Owners can cancel only pending orders.</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        var order = await VisibleOrders().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return NotFound();
        if (order.Status == OrderStatus.Cancelled) return NoContent();

        if (!IsAdmin && order.Status != OrderStatus.Pending)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: $"Only pending orders can be cancelled; this order is {order.Status}.");

        ChangeStatus(order, OrderStatus.Cancelled);
        order.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Sets the status and, if it actually changed, queues the status email in the same save.</summary>
    private void ChangeStatus(Order order, OrderStatus newStatus)
    {
        if (order.Status == newStatus) return;

        var previous = order.Status;
        order.Status = newStatus;
        var now = clock.GetUtcNow().UtcDateTime;
        var messageId = Guid.NewGuid();
        db.EnqueueNotification(NotificationRoutes.OrderStatusChanged, new OrderStatusChangedNotification(
            messageId, order.Id, order.CustomerEmail, order.CustomerName, order.ProductName,
            previous.ToString(), newStatus.ToString(), now), messageId, now);
    }

    // Other users' orders are invisible (404), so their existence is not revealed.
    private IQueryable<Order> VisibleOrders()
    {
        if (IsAdmin) return db.Orders;
        var userId = CurrentUserId;
        return db.Orders.Where(o => o.UserId == userId);
    }
}
