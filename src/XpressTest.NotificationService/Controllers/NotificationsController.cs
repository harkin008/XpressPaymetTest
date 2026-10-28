using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MimeKit;
using XpressTest.NotificationService.Jobs;
using XpressTest.Shared.Contracts;

namespace XpressTest.NotificationService.Controllers;

/// <summary>
/// Accepts notification requests from other services and queues them as Hangfire jobs.
/// Callers need an AuthService token for this service with the "notifications.send" scope;
/// user tokens are rejected.
/// </summary>
[ApiController]
[Authorize(Policy = NotificationPolicies.Send)]
[Produces("application/json")]
public class NotificationsController(IBackgroundJobClient jobs, NotificationDeduplicator deduplicator) : ControllerBase
{
    [HttpPost(NotificationRoutes.OrderCreated)]
    [ProducesResponseType<NotificationAccepted>(StatusCodes.Status202Accepted)]
    public IActionResult OrderCreated(OrderCreatedNotification notification)
    {
        if (Validate(notification.MessageId, notification.CustomerEmail) is { } invalid) return invalid;

        var (jobId, duplicate) = deduplicator.EnqueueOnce(notification.MessageId,
            () => jobs.Enqueue<OrderEmailJobs>(j => j.SendOrderCreatedAsync(notification, CancellationToken.None)));
        return Accepted(new NotificationAccepted(notification.MessageId, jobId, duplicate));
    }

    [HttpPost(NotificationRoutes.OrderStatusChanged)]
    [ProducesResponseType<NotificationAccepted>(StatusCodes.Status202Accepted)]
    public IActionResult OrderStatusChanged(OrderStatusChangedNotification notification)
    {
        if (Validate(notification.MessageId, notification.CustomerEmail) is { } invalid) return invalid;

        var (jobId, duplicate) = deduplicator.EnqueueOnce(notification.MessageId,
            () => jobs.Enqueue<OrderEmailJobs>(j => j.SendOrderStatusChangedAsync(notification, CancellationToken.None)));
        return Accepted(new NotificationAccepted(notification.MessageId, jobId, duplicate));
    }

    private IActionResult? Validate(Guid messageId, string email)
    {
        if (messageId == Guid.Empty) ModelState.AddModelError("messageId", "A MessageId is required for de-duplication.");
        if (!MailboxAddress.TryParse(email, out _) || !email.Contains('@')) ModelState.AddModelError("customerEmail", "Not a valid email address.");
        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }
}

public static class NotificationPolicies
{
    public const string Send = "notifications.send";
}

/// <summary>
/// Makes delivery idempotent: OrderService delivers at least once, so the same MessageId can
/// arrive twice (e.g. a retry after a timeout). Remembered for 24 hours, per process; with several
/// instances, move this to a shared store (e.g. Redis SET NX, or a unique column in the database).
/// </summary>
public sealed class NotificationDeduplicator(IMemoryCache cache)
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private readonly Lock _gate = new();

    public (string JobId, bool Duplicate) EnqueueOnce(Guid messageId, Func<string> enqueue)
    {
        lock (_gate)
        {
            if (cache.TryGetValue(messageId, out string? existing)) return (existing!, true);

            var jobId = enqueue();
            cache.Set(messageId, jobId, Retention);
            return (jobId, false);
        }
    }
}
