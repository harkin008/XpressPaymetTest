using System.Globalization;
using System.Net;
using Hangfire;
using XpressTest.NotificationService.Email;
using XpressTest.Shared.Contracts;

namespace XpressTest.NotificationService.Jobs;

/// <summary>
/// Hangfire jobs. Each runs on a Hangfire worker and is retried with backoff if sending
/// fails (e.g. the SMTP server is down); after the last attempt it moves to "Failed" in the dashboard.
/// </summary>
public class OrderEmailJobs(IEmailSender email, ILogger<OrderEmailJobs> logger)
{
    public const int RetryAttempts = 5;

    [AutomaticRetry(Attempts = RetryAttempts)]
    public async Task SendOrderCreatedAsync(OrderCreatedNotification notification, CancellationToken ct)
    {
        await email.SendAsync(OrderEmailTemplates.OrderCreated(notification), ct);
        logger.LogInformation("Sent order-created email for order {OrderId} (message {MessageId})", notification.OrderId, notification.MessageId);
    }

    [AutomaticRetry(Attempts = RetryAttempts)]
    public async Task SendOrderStatusChangedAsync(OrderStatusChangedNotification notification, CancellationToken ct)
    {
        await email.SendAsync(OrderEmailTemplates.OrderStatusChanged(notification), ct);
        logger.LogInformation("Sent status email for order {OrderId}: {Previous} -> {New} (message {MessageId})",
            notification.OrderId, notification.PreviousStatus, notification.NewStatus, notification.MessageId);
    }
}

public static class OrderEmailTemplates
{
    private static readonly CultureInfo Money = CultureInfo.InvariantCulture;

    public static EmailMessage OrderCreated(OrderCreatedNotification n)
    {
        var subject = $"Order #{n.OrderId} received";
        var text = $"""
            Hi {n.CustomerName},

            Thanks for your order #{n.OrderId}.

            {n.Quantity} x {n.ProductName} @ {n.UnitPrice.ToString("0.00", Money)}
            Total: {n.Total.ToString("0.00", Money)}

            We'll email you when its status changes.
            """;
        var html = $"""
            <p>Hi {Encode(n.CustomerName)},</p>
            <p>Thanks for your order <strong>#{n.OrderId}</strong>.</p>
            <table cellpadding="4">
              <tr><td>Product</td><td>{Encode(n.ProductName)}</td></tr>
              <tr><td>Quantity</td><td>{n.Quantity}</td></tr>
              <tr><td>Unit price</td><td>{n.UnitPrice.ToString("0.00", Money)}</td></tr>
              <tr><td><strong>Total</strong></td><td><strong>{n.Total.ToString("0.00", Money)}</strong></td></tr>
            </table>
            <p>We'll email you when its status changes.</p>
            """;
        return new EmailMessage(n.CustomerEmail, n.CustomerName, subject, html, text);
    }

    public static EmailMessage OrderStatusChanged(OrderStatusChangedNotification n)
    {
        var subject = $"Order #{n.OrderId} is now {n.NewStatus}";
        var text = $"""
            Hi {n.CustomerName},

            Your order #{n.OrderId} ({n.ProductName}) changed from {n.PreviousStatus} to {n.NewStatus}.
            """;
        var html = $"""
            <p>Hi {Encode(n.CustomerName)},</p>
            <p>Your order <strong>#{n.OrderId}</strong> ({Encode(n.ProductName)}) changed from
            <strong>{Encode(n.PreviousStatus)}</strong> to <strong>{Encode(n.NewStatus)}</strong>.</p>
            """;
        return new EmailMessage(n.CustomerEmail, n.CustomerName, subject, html, text);
    }

    // Product and customer names come from users, so they must never be rendered as HTML.
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
