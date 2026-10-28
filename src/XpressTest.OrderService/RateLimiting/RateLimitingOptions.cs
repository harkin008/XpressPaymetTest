namespace XpressTest.OrderService.RateLimiting;

/// <summary>Allows at most <see cref="PermitLimit"/> requests in any rolling <see cref="Window"/>.</summary>
public sealed class RateLimitRule
{
    public int PermitLimit { get; set; }
    public TimeSpan Window { get; set; }
}

/// <summary>Bound from the "RateLimiting" configuration section.</summary>
public sealed class RateLimitingOptions
{
    public Dictionary<string, RateLimitRule> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class RateLimitPolicies
{
    /// <summary>Baseline for every /api endpoint.</summary>
    public const string Api = "api";
    public const string OrdersCreate = "orders-create";
    public const string OrdersEdit = "orders-edit";
}

/// <summary>
/// Applies a named rate-limit policy to a controller or action. An action-level attribute
/// replaces the controller-level one, so each request is counted against exactly one policy.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RateLimitAttribute(string policy) : Attribute
{
    public string Policy { get; } = policy;
}
