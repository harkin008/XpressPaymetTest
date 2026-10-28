namespace XpressTest.OrderService.Models;

public enum OutboxStatus
{
    Pending,
    Sent,
    Failed
}

/// <summary>
/// A notification waiting to be delivered to NotificationService. It is saved together with the
/// order change (same SaveChanges, or same transaction on relational databases), so an order is
/// never stored without its notification, and NotificationService being down never fails an order request.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Relative NotificationService route, e.g. <c>api/notifications/order-created</c>.</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>JSON body; its MessageId equals <see cref="Id"/> so redelivery is idempotent.</summary>
    public string Payload { get; set; } = string.Empty;

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? LastError { get; set; }
}
