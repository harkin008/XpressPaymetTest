using Hangfire;
using Hangfire.InMemory;
using Hangfire.SqlServer;
using XpressTest.NotificationService.Controllers;
using XpressTest.NotificationService.Email;
using XpressTest.NotificationService.Jobs;
using XpressTest.Shared.Auth;
using XpressTest.Shared.Data;

var builder = WebApplication.CreateBuilder(args);

// Accept only AuthService access tokens issued for this service, and only with the send scope.
builder.Services.AddXpressJwtBearer(builder.Configuration, XpressAudiences.NotificationService);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(NotificationPolicies.Send, p => p.RequireScope(XpressScopes.NotificationsSend))
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Hangfire job storage follows Database:UseInMemoryDb / Database:UseSqlServer. With SqlServer,
// queued emails survive restarts; in memory they are lost when the service stops.
var databaseMode = builder.Configuration.GetDatabaseMode();
var hangfireConnectionString = databaseMode == DatabaseMode.SqlServer
    ? builder.Configuration.GetRequiredConnectionString("NotificationsDb")
    : null;
builder.Services.AddHangfire(config =>
{
    config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings();
    if (hangfireConnectionString is not null)
        // Hangfire creates and upgrades its own tables (schema "HangFire"), but not the database.
        config.UseSqlServerStorage(hangfireConnectionString, new SqlServerStorageOptions { PrepareSchemaIfNecessary = true });
    else
        config.UseInMemoryStorage(new InMemoryStorageOptions { MaxExpirationTime = TimeSpan.FromHours(24) });
});
builder.Services.AddHangfireServer(options =>
{
    options.ServerName = $"notification-service:{Environment.MachineName}";
    options.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 5);
});

builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
var delivery = builder.Configuration.GetValue("Email:Delivery", EmailDelivery.Pickup);
if (delivery == EmailDelivery.Smtp)
    builder.Services.AddTransient<IEmailSender, SmtpEmailSender>();
else
    builder.Services.AddTransient<IEmailSender, PickupDirectoryEmailSender>();

builder.Services.AddTransient<OrderEmailJobs>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<NotificationDeduplicator>();

builder.Services.AddProblemDetails();
builder.Services.AddControllers();

if (hangfireConnectionString is not null) DatabaseProviderExtensions.EnsureSqlServerDatabaseExists(hangfireConnectionString);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
// Job dashboard. Exempt from the JWT fallback policy (browsers have no bearer token); Hangfire's
// own default filter still allows local requests only. Add an IDashboardAuthorizationFilter
// (e.g. SSO with the Admin role) before exposing it beyond localhost.
app.MapHangfireDashboard("/hangfire").AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "notification-service" })).AllowAnonymous();

app.Run();

namespace XpressTest.NotificationService
{
    /// <summary>Entry-point marker for WebApplicationFactory in the tests.</summary>
    public sealed class NotificationServiceEntryPoint;
}
