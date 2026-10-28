using System.Net;
using XpressTest.OrderService.Models;
using XpressTest.Shared.Contracts;
using XpressTest.Tests.Infrastructure;

namespace XpressTest.Tests.Orders;

public sealed class OrderOutboxTests : IDisposable
{
    private readonly OrderServiceFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Creating_an_order_queues_an_order_created_notification()
    {
        var order = await (await _factory.CreateApiClient(TestUser.Alice).CreateOrderAsync("Blue <Widget>")).ReadOrderAsync();

        var message = Assert.Single(await _factory.OutboxAsync());
        Assert.Equal(NotificationRoutes.OrderCreated, message.Route);
        Assert.Equal(OutboxStatus.Pending, message.Status);

        Assert.Equal(1, await _factory.DispatchOutboxAsync());
        var (route, payload) = Assert.Single(_factory.Notifications.Sent);
        Assert.Equal(NotificationRoutes.OrderCreated, route);
        Assert.Equal(message.Id, payload.GetProperty("messageId").GetGuid());
        Assert.Equal(order.Id, payload.GetProperty("orderId").GetInt64());
        Assert.Equal(TestUser.Alice.Email, payload.GetProperty("customerEmail").GetString());
        Assert.Equal("Alice", payload.GetProperty("customerName").GetString());
        Assert.Equal("Blue <Widget>", payload.GetProperty("productName").GetString());
        Assert.Equal(19.98m, payload.GetProperty("total").GetDecimal());

        Assert.Equal(OutboxStatus.Sent, Assert.Single(await _factory.OutboxAsync()).Status);
        Assert.Equal(0, await _factory.DispatchOutboxAsync()); // nothing is sent twice
    }

    [Fact]
    public async Task Status_changes_queue_a_status_notification_to_the_owner_but_plain_edits_do_not()
    {
        var order = await (await _factory.CreateApiClient(TestUser.Alice).CreateOrderAsync()).ReadOrderAsync();
        var admin = _factory.CreateApiClient(TestUser.Admin);

        await admin.UpdateOrderAsync(order.Id, new { productName = "Widget", quantity = 5, unitPrice = 9.99m });          // no status change
        await admin.UpdateOrderAsync(order.Id, new { productName = "Widget", quantity = 5, unitPrice = 9.99m, status = "Pending" }); // same status
        await admin.UpdateOrderAsync(order.Id, new { productName = "Widget", quantity = 5, unitPrice = 9.99m, status = "Shipped" });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/orders/{order.Id}")).StatusCode);

        await _factory.DispatchOutboxAsync();
        var statusMessages = _factory.Notifications.Sent.Where(s => s.Route == NotificationRoutes.OrderStatusChanged).Select(s => s.Payload).ToList();
        Assert.Equal(2, statusMessages.Count);
        Assert.Equal(("Pending", "Shipped"), (statusMessages[0].GetProperty("previousStatus").GetString(), statusMessages[0].GetProperty("newStatus").GetString()));
        Assert.Equal(("Shipped", "Cancelled"), (statusMessages[1].GetProperty("previousStatus").GetString(), statusMessages[1].GetProperty("newStatus").GetString()));
        // The owner is notified, not the admin who made the change.
        Assert.All(statusMessages, m => Assert.Equal(TestUser.Alice.Email, m.GetProperty("customerEmail").GetString()));
    }

    [Fact]
    public async Task Orders_succeed_while_notifications_are_down_and_delivery_is_retried_with_backoff()
    {
        _factory.Notifications.FailWith = new HttpRequestException("connection refused");
        var created = await _factory.CreateApiClient(TestUser.Alice).CreateOrderAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await _factory.DispatchOutboxAsync();
        var failed = Assert.Single(await _factory.OutboxAsync());
        Assert.Equal((OutboxStatus.Pending, 1, "connection refused"), (failed.Status, failed.Attempts, failed.LastError));

        // Not due yet: backoff is 2^attempts seconds.
        Assert.Equal(0, await _factory.DispatchOutboxAsync());

        _factory.Notifications.FailWith = null;
        _factory.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await _factory.DispatchOutboxAsync());
        Assert.Equal(OutboxStatus.Sent, Assert.Single(await _factory.OutboxAsync()).Status);
        Assert.Single(_factory.Notifications.Sent);
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_number_of_attempts()
    {
        _factory.Notifications.FailWith = new HttpRequestException("down");
        await _factory.CreateApiClient(TestUser.Alice).CreateOrderAsync();

        for (var i = 0; i < 10; i++)
        {
            await _factory.DispatchOutboxAsync();
            _factory.Clock.Advance(TimeSpan.FromMinutes(6)); // beyond the maximum backoff
        }

        var message = Assert.Single(await _factory.OutboxAsync());
        Assert.Equal((OutboxStatus.Failed, 10), (message.Status, message.Attempts));
        Assert.Equal(0, await _factory.DispatchOutboxAsync());
    }
}
