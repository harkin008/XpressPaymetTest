using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using XpressTest.Shared.Auth;

namespace XpressTest.OrderService.RateLimiting;

/// <summary>
/// Enforces <see cref="RateLimitAttribute"/> policies. Runs after authentication (so the caller's
/// identity is known) and before authorization (so unauthenticated floods are throttled too).
/// </summary>
public sealed class RateLimitingMiddleware(
    RequestDelegate next,
    IRateLimitStore store,
    IOptionsMonitor<RateLimitingOptions> options,
    ILogger<RateLimitingMiddleware> logger)
{
    public const string LimitHeader = "X-RateLimit-Limit";
    public const string RemainingHeader = "X-RateLimit-Remaining";
    public const string ResetHeader = "X-RateLimit-Reset";

    public async Task InvokeAsync(HttpContext context)
    {
        var policyName = context.GetEndpoint()?.Metadata.GetMetadata<RateLimitAttribute>()?.Policy;
        if (policyName is null)
        {
            await next(context);
            return;
        }

        if (!options.CurrentValue.Policies.TryGetValue(policyName, out var rule))
            throw new InvalidOperationException($"Rate limit policy '{policyName}' is not configured under RateLimiting:Policies.");

        var partition = ResolvePartition(context);
        var decision = store.TryAcquire($"{policyName}|{partition}", rule);

        var headers = context.Response.Headers;
        headers[LimitHeader] = decision.Limit.ToString(CultureInfo.InvariantCulture);
        headers[RemainingHeader] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
        headers[ResetHeader] = Seconds(decision.ResetAfter).ToString(CultureInfo.InvariantCulture);

        if (decision.Allowed)
        {
            await next(context);
            return;
        }

        var retryAfter = Math.Max(1, Seconds(decision.ResetAfter));
        logger.LogWarning("Rate limit '{Policy}' exceeded by {Partition}; retry after {RetryAfter}s", policyName, partition, retryAfter);

        headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                detail: $"Limit of {rule.PermitLimit} requests per {rule.Window.TotalSeconds:0} seconds reached for '{policyName}'. Retry after {retryAfter} seconds.",
                extensions: new Dictionary<string, object?>
                {
                    ["policy"] = policyName,
                    ["limit"] = rule.PermitLimit,
                    ["windowSeconds"] = (int)rule.Window.TotalSeconds,
                    ["retryAfterSeconds"] = retryAfter
                })
            .ExecuteAsync(context);
    }

    /// <summary>
    /// Authenticated callers are limited per user (the token's "sub"), so every token a user holds,
    /// whether from a password login or any of their API keys, shares one budget.
    /// Everyone else is limited per client IP.
    /// </summary>
    public static string ResolvePartition(HttpContext context)
    {
        var userId = context.User.Identity?.IsAuthenticated == true
            ? context.User.GetSubject()
            : null;
        if (userId is not null) return $"user:{userId}";

        // Behind a reverse proxy, enable UseForwardedHeaders (with KnownProxies) so this is the real client.
        var ip = context.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        return $"ip:{ip ?? IPAddress.None}";
    }

    private static int Seconds(TimeSpan span) => (int)Math.Ceiling(Math.Max(0, span.TotalSeconds));
}

public static class RateLimitingExtensions
{
    public static IServiceCollection AddRateLimitPolicies(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(section)
            .Validate(o => o.Policies.Values.All(r => r.PermitLimit > 0 && r.Window > TimeSpan.Zero),
                "Every rate limit policy needs PermitLimit > 0 and a positive Window.")
            .Validate(o => new[] { RateLimitPolicies.Api, RateLimitPolicies.OrdersCreate, RateLimitPolicies.OrdersEdit }
                    .All(o.Policies.ContainsKey),
                "RateLimiting:Policies must define 'api', 'orders-create' and 'orders-edit'.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IRateLimitStore, SlidingWindowRateLimitStore>();
        return services;
    }

    public static IApplicationBuilder UseRateLimitPolicies(this IApplicationBuilder app) =>
        app.UseMiddleware<RateLimitingMiddleware>();
}
