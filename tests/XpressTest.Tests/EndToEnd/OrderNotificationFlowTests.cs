using System.Net;
using XpressTest.OrderService.Models;
using XpressTest.Tests.Infrastructure;

namespace XpressTest.Tests.EndToEnd;

/// <summary>
/// All three services in-process, talking to each other over (in-memory) HTTP exactly as deployed:
/// user signs in at AuthService → creates an order at OrderService (token validated via AuthService JWKS)
/// → outbox dispatcher gets a client-credentials token from AuthService → NotificationService validates
/// it via JWKS and queues a Hangfire job → the job sends the email.
/// </summary>
public sealed class OrderNotificationFlowTests : IDisposable
{
    private readonly AuthServiceFactory _auth = new();
    private readonly NotificationServiceFactory _notifications;
    private readonly OrderServiceFactory _orders;

    public OrderNotificationFlowTests()
    {
        _notifications = new NotificationServiceFactory(_auth);
        _orders = new OrderServiceFactory(_auth, _notifications);
    }

    public void Dispose()
    {
        _orders.Dispose();
        _notifications.Dispose();
        _auth.Dispose();
    }

    [Fact]
    public async Task Order_creation_and_status_update_send_emails_through_all_services()
    {
        var userToken = (await _auth.PasswordGrantAsync("user@xpress.local", "User@123")).GetProperty("access_token").GetString()!;
        var order = await (await _orders.CreateApiClient(userToken).CreateOrderAsync("Espresso machine")).ReadOrderAsync();

        var created = await _notifications.Emails.WaitForAsync(e => e.Subject == $"Order #{order.Id} received");
        Assert.Equal("user@xpress.local", created.To);
        Assert.Equal("Default User", created.ToName);
        Assert.Contains("Espresso machine", created.HtmlBody);

        var adminToken = (await _auth.PasswordGrantAsync("admin@xpress.local", "Admin@123")).GetProperty("access_token").GetString()!;
        var updated = await _orders.CreateApiClient(adminToken).UpdateOrderAsync(order.Id,
            new { productName = "Espresso machine", quantity = 2, unitPrice = 9.99m, status = "Shipped" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var shipped = await _notifications.Emails.WaitForAsync(e => e.Subject == $"Order #{order.Id} is now Shipped");
        Assert.Equal("user@xpress.local", shipped.To); // the owner, not the admin
        Assert.Contains("from Pending to Shipped", shipped.TextBody);

        var outbox = await _orders.OutboxAsync();
        Assert.All(outbox, m => Assert.Equal(OutboxStatus.Sent, m.Status));
        Assert.Equal(2, outbox.Count);
    }

    [Fact]
    public async Task Api_key_tokens_from_auth_service_work_at_the_order_service()
    {
        var response = await _auth.CreateApiClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:xpress:grant-type:api-key", ["api_key"] = "xpk_demo_admin_key"
        }));
        var token = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<System.Text.Json.JsonElement>(response.Content))
            .GetProperty("access_token").GetString()!;

        var list = await _orders.CreateApiClient(token).GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }
}
