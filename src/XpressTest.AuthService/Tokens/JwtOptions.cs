namespace XpressTest.AuthService.Tokens;

/// <summary>Bound from the "Jwt" configuration section.</summary>
public sealed class JwtOptions
{
    /// <summary>Token "iss"; must equal the AuthService base URL that other services use as Auth:Authority.</summary>
    public string Issuer { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);
    public TimeSpan ClientTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Audiences of user access tokens: the services users call directly.</summary>
    public List<string> UserAudiences { get; set; } = [];

    /// <summary>Scope name → audience (the service that owns the scope).</summary>
    public Dictionary<string, string> ApiScopes { get; set; } = new();

    /// <summary>Services allowed to use client_credentials.</summary>
    public List<ClientDefinition> Clients { get; set; } = [];

    /// <summary>Optional PEM file with the RSA private key. Without it a key is generated per process.</summary>
    public string? SigningKeyPath { get; set; }
}

public sealed class ClientDefinition
{
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Hex SHA-256 of the client secret; the secret itself is never stored here.</summary>
    public string SecretSha256 { get; set; } = string.Empty;

    public List<string> AllowedScopes { get; set; } = [];
}
