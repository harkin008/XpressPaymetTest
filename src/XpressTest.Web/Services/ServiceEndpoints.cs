namespace XpressTest.Web.Services;

/// <summary>Base URLs of the back-end services (configuration section "Services").</summary>
public sealed class ServiceEndpoints
{
    /// <summary>AuthService URL; also the token issuer.</summary>
    public string Auth { get; set; } = string.Empty;
    public string Orders { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; } = true;

    public const string AuthClient = "auth";
    public const string OrdersClient = "orders";
}
