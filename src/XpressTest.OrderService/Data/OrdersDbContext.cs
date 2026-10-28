using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XpressTest.OrderService.Models;
using XpressTest.Shared.Data;

namespace XpressTest.OrderService.Data;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Queues a notification; it is persisted by the caller's next SaveChanges.</summary>
    public void EnqueueNotification<T>(string route, T payload, Guid messageId, DateTime nowUtc)
    {
        OutboxMessages.Add(new OutboxMessage
        {
            Id = messageId,
            Route = route,
            Payload = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
            OccurredAtUtc = nowUtc,
            NextAttemptAtUtc = nowUtc
        });
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Order>(order =>
        {
            order.Property(o => o.CustomerEmail).HasMaxLength(256);
            order.Property(o => o.CustomerName).HasMaxLength(100);
            order.Property(o => o.ProductName).HasMaxLength(200);
            order.Property(o => o.UnitPrice).HasPrecision(18, 2);
            order.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            // "My orders, newest first" is the main list query.
            order.HasIndex(o => new { o.UserId, o.CreatedAt });
        });

        builder.Entity<OutboxMessage>(message =>
        {
            message.Property(m => m.Route).HasMaxLength(200);
            message.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            // The dispatcher polls "pending and due, oldest first".
            message.HasIndex(m => new { m.Status, m.NextAttemptAtUtc });
        });

        builder.UseApplicationIds();
    }
}
