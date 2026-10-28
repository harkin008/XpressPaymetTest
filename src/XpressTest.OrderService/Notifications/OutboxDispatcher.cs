using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XpressTest.OrderService.Data;
using XpressTest.OrderService.Models;

namespace XpressTest.OrderService.Notifications;

public sealed class OutboxOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 10;
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Delivers outbox messages to NotificationService, at least once, retrying with exponential
/// backoff. Duplicates are harmless because NotificationService de-duplicates on MessageId.
/// Assumes one OrderService instance; with several, claim rows first (e.g. UPDATE … OUTPUT
/// or SELECT … FOR UPDATE SKIP LOCKED) so instances don't race for the same message.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Drain while there is a full batch; otherwise wait for the next poll.
                while (await DispatchPendingAsync(stoppingToken) == options.Value.BatchSize) { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatch cycle failed");
            }

            try { await Task.Delay(options.Value.PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Sends one batch of due messages. Returns how many were attempted.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<INotificationClient>();
        var o = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;

        var batch = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAtUtc <= now)
            .OrderBy(m => m.OccurredAtUtc)
            .Take(o.BatchSize)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            message.Attempts++;
            try
            {
                await client.SendAsync(message.Route, message.Payload, ct);
                message.Status = OutboxStatus.Sent;
                message.SentAtUtc = clock.GetUtcNow().UtcDateTime;
                message.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.LastError = ex.Message[..Math.Min(1000, ex.Message.Length)];
                if (message.Attempts >= o.MaxAttempts)
                {
                    message.Status = OutboxStatus.Failed;
                    logger.LogError(ex, "Giving up on outbox message {MessageId} ({Route}) after {Attempts} attempts",
                        message.Id, message.Route, message.Attempts);
                }
                else
                {
                    var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, message.Attempts), o.MaxBackoff.TotalSeconds));
                    message.NextAttemptAtUtc = clock.GetUtcNow().UtcDateTime + backoff;
                    logger.LogWarning("Outbox message {MessageId} ({Route}) failed attempt {Attempts}; retrying in {Backoff}: {Error}",
                        message.Id, message.Route, message.Attempts, backoff, ex.Message);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return batch.Count;
    }
}
