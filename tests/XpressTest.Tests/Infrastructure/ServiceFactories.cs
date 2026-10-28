using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using XpressTest.AuthService;
using XpressTest.AuthService.Data;
using XpressTest.NotificationService;
using XpressTest.NotificationService.Email;
using XpressTest.OrderService;
using XpressTest.OrderService.Contracts;
using XpressTest.OrderService.Data;
using XpressTest.OrderService.Notifications;

namespace XpressTest.Tests.Infrastructure;

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>AuthService in-process, with its own database. Issuer is the TestServer address.</summary>
public sealed class AuthServiceFactory : WebApplicationFactory<AuthServiceEntryPoint>
{
    public const string Issuer = "http://localhost";
    private readonly string _database = $"auth-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // demo API keys + the order-service client
        builder.UseInMemoryDatabaseMode();
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.ConfigureTestServices(services =>
            services.UseInMemoryDatabase<ApplicationDbContext>(_database));
    }

    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<JsonElement> PasswordGrantAsync(string email, string password)
    {
        var response = await CreateApiClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["username"] = email, ["password"] = password
        }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

/// <summary>
/// OrderService in-process with a fake clock. By default it trusts <see cref="TestJwt"/> tokens and
/// records notifications in <see cref="Notifications"/>; pass real services to wire it end to end.
/// </summary>
public sealed class OrderServiceFactory(AuthServiceFactory? auth = null, NotificationServiceFactory? notifications = null)
    : WebApplicationFactory<OrderServiceEntryPoint>
{
    /// <summary>Test-only header that sets the connection's remote IP.</summary>
    public const string ClientIpHeader = "X-Test-Client-IP";

    private readonly string _database = $"orders-{Guid.NewGuid()}";

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public FakeNotificationClient Notifications { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseInMemoryDatabaseMode();
        builder.UseSetting("Auth:Authority", AuthServiceFactory.Issuer);
        builder.UseSetting("Notifications:BaseUrl", "http://localhost");
        // End to end the dispatcher runs on its own; otherwise tests trigger it explicitly.
        builder.UseSetting("Outbox:PollInterval", auth is null ? "01:00:00" : "00:00:00.100");

        builder.ConfigureTestServices(services =>
        {
            services.UseInMemoryDatabase<OrdersDbContext>(_database);
            services.AddTransient<IStartupFilter, ClientIpStartupFilter>();

            if (auth is null)
            {
                services.AddSingleton<TimeProvider>(Clock);
                TestJwt.TrustTestTokens(services);
            }
            else
            {
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                    o => o.BackchannelHttpHandler = auth.Server.CreateHandler());
                services.AddHttpClient(ServiceTokenClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => auth.Server.CreateHandler());
            }

            if (notifications is null)
                services.AddSingleton<INotificationClient>(Notifications);
            else
                services.AddHttpClient<INotificationClient, NotificationClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => notifications.Server.CreateHandler());
        });
    }

    public HttpClient CreateApiClient(string? bearerToken, string clientIp = "203.0.113.10")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (bearerToken is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        client.DefaultRequestHeaders.Add(ClientIpHeader, clientIp);
        return client;
    }

    public HttpClient CreateApiClient(TestUser user, string clientIp = "203.0.113.10") => CreateApiClient(TestJwt.User(user), clientIp);

    public Task<int> DispatchOutboxAsync() => Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync(CancellationToken.None);

    public async Task<List<XpressTest.OrderService.Models.OutboxMessage>> OutboxAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().OutboxMessages.AsNoTracking()
            .OrderBy(m => m.OccurredAtUtc).ToListAsync();
    }

    private sealed class ClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[ClientIpHeader], out var ip))
                    context.Connection.RemoteIpAddress = ip;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>NotificationService in-process; emails are captured instead of sent.</summary>
public sealed class NotificationServiceFactory(AuthServiceFactory? auth = null) : WebApplicationFactory<NotificationServiceEntryPoint>
{
    public CapturingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseInMemoryDatabaseMode();
        builder.UseSetting("Auth:Authority", AuthServiceFactory.Issuer);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailSender>(Emails);
            if (auth is null)
                TestJwt.TrustTestTokens(services);
            else
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                    o => o.BackchannelHttpHandler = auth.Server.CreateHandler());
        });
    }

    public HttpClient CreateApiClient(string? bearerToken)
    {
        var client = CreateClient();
        if (bearerToken is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }
}

public sealed class FakeNotificationClient : INotificationClient
{
    public ConcurrentQueue<(string Route, JsonElement Payload)> Sent { get; } = new();

    /// <summary>When set, every send throws this (simulates NotificationService being down).</summary>
    public Exception? FailWith { get; set; }

    public Task SendAsync(string route, string jsonPayload, CancellationToken ct)
    {
        if (FailWith is not null) throw FailWith;
        Sent.Enqueue((route, JsonDocument.Parse(jsonPayload).RootElement.Clone()));
        return Task.CompletedTask;
    }
}

public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>Waits for Hangfire to run the job and an email matching <paramref name="match"/> to be sent.</summary>
    public async Task<EmailMessage> WaitForAsync(Func<EmailMessage, bool> match, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (Sent.FirstOrDefault(match) is { } email) return email;
            await Task.Delay(50);
        }
        throw new TimeoutException($"No matching email within the timeout. Sent: {string.Join(" | ", Sent.Select(e => e.Subject))}");
    }
}

public static class OrdersApi
{
    public static Task<HttpResponseMessage> CreateOrderAsync(this HttpClient client, string product = "Widget") =>
        client.PostAsJsonAsync("/api/orders", new { productName = product, quantity = 2, unitPrice = 9.99m });

    public static Task<HttpResponseMessage> UpdateOrderAsync(this HttpClient client, long id, object? body = null) =>
        client.PutAsJsonAsync($"/api/orders/{id}", body ?? new { productName = "Edited", quantity = 3, unitPrice = 5m });

    public static async Task<OrderResponse> ReadOrderAsync(this HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderResponse>(TestJson.Options))!;
    }

    public static string Header(this HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
}
