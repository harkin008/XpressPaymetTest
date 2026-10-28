using XpressTest.Shared.Models;

namespace XpressTest.OrderService.Models;

public enum OrderStatus
{
    Pending,
    Confirmed,
    Shipped,
    Cancelled
}

public class Order : BaseEntity
{
    /// <summary>The owner's user id in AuthService (the token's "sub").</summary>
    public long UserId { get; set; }

    // Contact details captured from the owner's token at creation, so notifications
    // (including status changes made by admins) need no call back to AuthService.
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
}
