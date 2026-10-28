using System.ComponentModel.DataAnnotations;
using XpressTest.OrderService.Models;

namespace XpressTest.OrderService.Contracts;

public class CreateOrderRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string ProductName { get; set; } = string.Empty;

    [Range(1, 1000)]
    public int Quantity { get; set; }

    [Range(typeof(decimal), "0.01", "1000000")]
    public decimal UnitPrice { get; set; }
}

public class UpdateOrderRequest : CreateOrderRequest
{
    /// <summary>Only Admins and SuperAdmins may change the status.</summary>
    public OrderStatus? Status { get; set; }
}

public class OrderResponse
{
    public long Id { get; init; }
    public long UserId { get; init; }
    public string CustomerEmail { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Total { get; init; }
    public OrderStatus Status { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

/// <summary>Order counts per status (Admin dashboard).</summary>
public record OrderStatsResponse(int Total, IReadOnlyDictionary<string, int> ByStatus);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
