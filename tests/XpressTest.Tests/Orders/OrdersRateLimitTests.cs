using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XpressTest.Shared.Auth;
using XpressTest.Tests.Infrastructure;

namespace XpressTest.Tests.Orders;

public sealed class OrdersRateLimitTests : IDisposable
{
    private readonly OrderServiceFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Create_allows_three_per_minute_then_returns_429_with_headers()
    {
        var client = _factory.CreateApiClient(TestUser.Alice);

        for (var expectedRemaining = 2; expectedRemaining >= 0; expectedRemaining--)
        {
            var ok = await client.CreateOrderAsync();
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
            Assert.Equal("3", ok.Header("X-RateLimit-Limit"));
            Assert.Equal(expectedRemaining.ToString(), ok.Header("X-RateLimit-Remaining"));
        }

        var limited = await client.CreateOrderAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("0", limited.Header("X-RateLimit-Remaining"));
        Assert.Equal("60", limited.Header("X-RateLimit-Reset"));
        Assert.Equal("60", limited.Header("Retry-After"));
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);

        var problem = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal("orders-create", problem.GetProperty("policy").GetString());
        Assert.Equal(60, problem.GetProperty("retryAfterSeconds").GetInt32());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/orders");
        Assert.Equal(3, list.GetProperty("totalCount").GetInt32()); // the rejected request created nothing
    }

    [Fact]
    public async Task Create_is_allowed_again_once_the_oldest_request_leaves_the_window()
    {
        var client = _factory.CreateApiClient(TestUser.Alice);
        await client.CreateOrderAsync();                                 // t=0
        _factory.Clock.Advance(TimeSpan.FromSeconds(40));
        await client.CreateOrderAsync();                                 // t=40
        await client.CreateOrderAsync();                                 // t=40

        _factory.Clock.Advance(TimeSpan.FromSeconds(19));                // t=59
        var limited = await client.CreateOrderAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("1", limited.Header("Retry-After"));

        _factory.Clock.Advance(TimeSpan.FromSeconds(1));                 // t=60
        Assert.Equal(HttpStatusCode.Created, (await client.CreateOrderAsync()).StatusCode);

        var next = await client.CreateOrderAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, next.StatusCode);
        Assert.Equal("40", next.Header("Retry-After"));                  // t=40 requests expire at t=100
    }

    [Fact]
    public async Task Edit_is_limited_to_three_per_minute_with_its_own_budget()
    {
        var client = _factory.CreateApiClient(TestUser.Alice);
        var order = await (await client.CreateOrderAsync()).ReadOrderAsync();
        await client.CreateOrderAsync();
        await client.CreateOrderAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.CreateOrderAsync()).StatusCode);

        // Exhausting creates does not consume edits.
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.UpdateOrderAsync(order.Id)).StatusCode);

        var limited = await client.UpdateOrderAsync(order.Id);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("orders-edit", await limited.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task All_tokens_of_one_user_share_a_single_budget()
    {
        // e.g. one token from a password login and one from an API key exchange.
        var first = _factory.CreateApiClient(TestJwt.User(TestUser.Alice), "198.51.100.1");
        var second = _factory.CreateApiClient(TestJwt.User(TestUser.Alice, lifetime: TimeSpan.FromMinutes(5)), "198.51.100.2");

        await first.CreateOrderAsync();
        await first.CreateOrderAsync();
        Assert.Equal(HttpStatusCode.Created, (await second.CreateOrderAsync()).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await second.CreateOrderAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await first.CreateOrderAsync()).StatusCode);
    }

    [Fact]
    public async Task Different_users_have_independent_budgets_even_from_the_same_ip()
    {
        var alice = _factory.CreateApiClient(TestUser.Alice, "198.51.100.7");
        var bob = _factory.CreateApiClient(TestUser.Bob, "198.51.100.7");

        for (var i = 0; i < 3; i++) await alice.CreateOrderAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await alice.CreateOrderAsync()).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await bob.CreateOrderAsync()).StatusCode);
    }

    [Fact]
    public async Task Administrators_are_limited_too()
    {
        foreach (var admin in new[] { TestUser.Admin, TestUser.SuperAdmin })
        {
            var client = _factory.CreateApiClient(admin);
            for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await client.CreateOrderAsync()).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.CreateOrderAsync()).StatusCode);
        }
    }

    [Fact]
    public async Task Unauthenticated_callers_are_limited_per_ip()
    {
        var attacker = _factory.CreateApiClient("not-a-real-token", "192.0.2.50");
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.CreateOrderAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await attacker.CreateOrderAsync()).StatusCode);

        var otherIp = _factory.CreateApiClient(bearerToken: null, "192.0.2.51");
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherIp.CreateOrderAsync()).StatusCode);
    }

    [Fact]
    public async Task Reads_use_the_baseline_api_policy()
    {
        var client = _factory.CreateApiClient(TestUser.Alice);
        for (var i = 0; i < 3; i++) await client.CreateOrderAsync();

        var list = await client.GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("100", list.Header("X-RateLimit-Limit"));
        Assert.Equal("99", list.Header("X-RateLimit-Remaining"));
    }
}

public sealed class OrdersAccessTests : IDisposable
{
    private readonly OrderServiceFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Requires_a_bearer_token()
    {
        var response = await _factory.CreateApiClient(bearerToken: null).GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Rejects_tokens_issued_for_another_service()
    {
        var token = TestJwt.User(TestUser.Alice, audience: XpressAudiences.NotificationService);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateApiClient(token).GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task Rejects_expired_tokens()
    {
        var token = TestJwt.User(TestUser.Alice, lifetime: TimeSpan.FromMinutes(-5));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateApiClient(token).GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task Users_cannot_see_or_edit_other_users_orders()
    {
        var bobOrder = await (await _factory.CreateApiClient(TestUser.Bob).CreateOrderAsync()).ReadOrderAsync();
        var alice = _factory.CreateApiClient(TestUser.Alice);

        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/orders/{bobOrder.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.UpdateOrderAsync(bobOrder.Id)).StatusCode);
        Assert.Equal(0, (await alice.GetFromJsonAsync<JsonElement>("/api/orders")).GetProperty("totalCount").GetInt32());

        var admin = _factory.CreateApiClient(TestUser.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/orders/{bobOrder.Id}")).StatusCode);
    }

    [Fact]
    public async Task Only_administrators_change_status_and_users_edit_only_pending_orders()
    {
        var user = _factory.CreateApiClient(TestUser.Alice);
        var order = await (await user.CreateOrderAsync()).ReadOrderAsync();
        var confirm = new { productName = "Widget", quantity = 2, unitPrice = 9.99m, status = "Confirmed" };

        Assert.Equal(HttpStatusCode.Forbidden, (await user.UpdateOrderAsync(order.Id, confirm)).StatusCode);

        var confirmed = await (await _factory.CreateApiClient(TestUser.Admin).UpdateOrderAsync(order.Id, confirm)).ReadOrderAsync();
        Assert.Equal(XpressTest.OrderService.Models.OrderStatus.Confirmed, confirmed.Status);

        Assert.Equal(HttpStatusCode.Conflict, (await user.UpdateOrderAsync(order.Id)).StatusCode);
    }

    [Fact]
    public async Task Create_validates_input_and_returns_location()
    {
        var client = _factory.CreateApiClient(TestUser.Alice);

        var invalid = await client.PostAsJsonAsync("/api/orders", new { productName = "", quantity = 0, unitPrice = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var created = await client.CreateOrderAsync();
        var order = await created.ReadOrderAsync();
        Assert.Equal($"/api/orders/{order.Id}", created.Headers.Location?.AbsolutePath);
        Assert.Equal(19.98m, order.Total);
        Assert.Equal(TestUser.Alice.Id, order.UserId);
    }

    [Fact]
    public async Task Service_tokens_without_an_email_cannot_place_orders()
    {
        var token = TestJwt.Service(XpressScopes.NotificationsSend, audience: XpressAudiences.OrderService);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateApiClient(token).CreateOrderAsync()).StatusCode);
    }
}
