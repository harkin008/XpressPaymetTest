using System.Net;
using System.Net.Http.Json;
using XpressTest.Shared.Auth;
using XpressTest.Shared.Contracts;
using XpressTest.Tests.Infrastructure;

namespace XpressTest.Tests.Notifications;

public sealed class NotificationApiTests : IDisposable
{
    private readonly NotificationServiceFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static OrderCreatedNotification Created(Guid? id = null, string email = "alice@example.com", string product = "Widget") =>
        new(id ?? Guid.NewGuid(), 42, email, "Alice", product, 2, 9.99m, 19.98m, DateTime.UtcNow);

    [Fact]
    public async Task Requires_a_token()
    {
        var response = await _factory.CreateApiClient(null).PostAsJsonAsync(NotificationRoutes.OrderCreated, Created());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_user_tokens_even_for_the_right_audience()
    {
        var userToken = TestJwt.User(TestUser.Admin, audience: XpressAudiences.NotificationService);
        var response = await _factory.CreateApiClient(userToken).PostAsJsonAsync(NotificationRoutes.OrderCreated, Created());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_service_tokens_issued_for_another_audience()
    {
        var token = TestJwt.Service(XpressScopes.NotificationsSend, audience: XpressAudiences.OrderService);
        var response = await _factory.CreateApiClient(token).PostAsJsonAsync(NotificationRoutes.OrderCreated, Created());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Order_created_is_queued_in_hangfire_and_emailed_with_user_input_encoded()
    {
        var client = _factory.CreateApiClient(TestJwt.Service(XpressScopes.NotificationsSend));
        var response = await client.PostAsJsonAsync(NotificationRoutes.OrderCreated, Created(product: "<script>alert(1)</script>"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<NotificationAccepted>())!;
        Assert.False(string.IsNullOrEmpty(accepted.JobId));
        Assert.False(accepted.Duplicate);

        var email = await _factory.Emails.WaitForAsync(e => e.Subject == "Order #42 received");
        Assert.Equal("alice@example.com", email.To);
        Assert.Contains("19.98", email.HtmlBody);
        Assert.Contains("&lt;script&gt;", email.HtmlBody);
        Assert.DoesNotContain("<script>", email.HtmlBody);
    }

    [Fact]
    public async Task Status_change_is_emailed()
    {
        var client = _factory.CreateApiClient(TestJwt.Service(XpressScopes.NotificationsSend));
        var response = await client.PostAsJsonAsync(NotificationRoutes.OrderStatusChanged,
            new OrderStatusChangedNotification(Guid.NewGuid(), 7, "bob@example.com", "Bob", "Gadget", "Pending", "Shipped", DateTime.UtcNow));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var email = await _factory.Emails.WaitForAsync(e => e.Subject == "Order #7 is now Shipped");
        Assert.Equal("bob@example.com", email.To);
        Assert.Contains("from Pending to Shipped", email.TextBody);
    }

    [Fact]
    public async Task Redelivered_messages_are_accepted_but_emailed_only_once()
    {
        var client = _factory.CreateApiClient(TestJwt.Service(XpressScopes.NotificationsSend));
        var message = Created();

        var first = (await (await client.PostAsJsonAsync(NotificationRoutes.OrderCreated, message)).Content.ReadFromJsonAsync<NotificationAccepted>())!;
        var second = (await (await client.PostAsJsonAsync(NotificationRoutes.OrderCreated, message)).Content.ReadFromJsonAsync<NotificationAccepted>())!;

        Assert.True(second.Duplicate);
        Assert.Equal(first.JobId, second.JobId);
        await _factory.Emails.WaitForAsync(e => e.Subject == "Order #42 received");
        await Task.Delay(500);
        Assert.Single(_factory.Emails.Sent);
    }

    [Fact]
    public async Task Invalid_messages_are_rejected()
    {
        var client = _factory.CreateApiClient(TestJwt.Service(XpressScopes.NotificationsSend));
        var response = await client.PostAsJsonAsync(NotificationRoutes.OrderCreated, Created(id: Guid.Empty, email: "not-an-email"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("messageId", body);
        Assert.Contains("customerEmail", body);
    }
}
