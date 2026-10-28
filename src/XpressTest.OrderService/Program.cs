using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using XpressTest.OrderService.Data;
using XpressTest.OrderService.Notifications;
using XpressTest.OrderService.RateLimiting;
using XpressTest.Shared.Auth;
using XpressTest.Shared.Data;
using XpressTest.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

IdGenerator.Configure(builder.Configuration.GetValue<int?>("Ids:WorkerId")
                      ?? throw new InvalidOperationException("Ids:WorkerId is not configured."));

builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseConfiguredDatabase(builder.Configuration, "OrdersDb"));

// Accept only AuthService access tokens issued for this service.
builder.Services.AddXpressJwtBearer(builder.Configuration, XpressAudiences.OrderService);
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimitPolicies(builder.Configuration.GetSection("RateLimiting"));

// Outbound: service token from AuthService + notification calls, delivered via the outbox.
builder.Services.Configure<ServiceClientOptions>(builder.Configuration.GetSection("ServiceClient"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));
builder.Services.AddHttpClient(ServiceTokenClient.HttpClientName, http =>
    http.BaseAddress = new Uri(EnsureTrailingSlash(builder.Configuration["Auth:Authority"]!)));
builder.Services.AddSingleton<ServiceTokenClient>();
builder.Services.AddHttpClient<INotificationClient, NotificationClient>(http =>
{
    http.BaseAddress = new Uri(EnsureTrailingSlash(builder.Configuration["Notifications:BaseUrl"]
                                                   ?? throw new InvalidOperationException("Notifications:BaseUrl is not configured.")));
    http.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());

builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Creates or upgrades the schema. Skipped for the in-memory provider the tests use.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    if (db.Database.IsRelational()) await db.Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
// After authentication so limits apply per user; before authorization so rejected callers are throttled too.
app.UseRateLimitPolicies();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "order-service" })).AllowAnonymous();

app.Run();

static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";

namespace XpressTest.OrderService
{
    /// <summary>Entry-point marker for WebApplicationFactory in the tests.</summary>
    public sealed class OrderServiceEntryPoint;
}
