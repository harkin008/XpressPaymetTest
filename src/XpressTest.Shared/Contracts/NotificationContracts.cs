namespace XpressTest.Shared.Contracts;

/// <summary>Messages OrderService sends to NotificationService (POST {Route}).</summary>
public static class NotificationRoutes
{
    public const string OrderCreated = "api/notifications/order-created";
    public const string OrderStatusChanged = "api/notifications/order-status-changed";
}

/// <param name="MessageId">Idempotency key: NotificationService ignores a MessageId it has already accepted.</param>
public record OrderCreatedNotification(
    Guid MessageId,
    long OrderId,
    string CustomerEmail,
    string CustomerName,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal Total,
    DateTime CreatedAtUtc);

public record OrderStatusChangedNotification(
    Guid MessageId,
    long OrderId,
    string CustomerEmail,
    string CustomerName,
    string ProductName,
    string PreviousStatus,
    string NewStatus,
    DateTime ChangedAtUtc);

public record NotificationAccepted(Guid MessageId, string JobId, bool Duplicate);
