using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using XpressTest.Shared.Auth;

namespace XpressTest.Tests.Infrastructure;

/// <summary>
/// Mints tokens shaped like AuthService's, signed with a test key, so OrderService and
/// NotificationService can be tested without running AuthService.
/// </summary>
public static class TestJwt
{
    public const string Issuer = "https://auth.test";

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };
    private static readonly JsonWebTokenHandler Handler = new();

    public static string User(TestUser user, string audience = XpressAudiences.OrderService, TimeSpan? lifetime = null) =>
        Create(new Dictionary<string, object>
        {
            [XpressClaims.Subject] = user.Id.ToString(),
            [XpressClaims.Email] = user.Email,
            [XpressClaims.Name] = user.Name,
            [XpressClaims.Role] = user.Roles,
            ["aud"] = audience
        }, lifetime);

    public static string Service(string scope, string audience = XpressAudiences.NotificationService) =>
        Create(new Dictionary<string, object>
        {
            [XpressClaims.Subject] = "order-service",
            [XpressClaims.ClientId] = "order-service",
            [XpressClaims.Scope] = scope,
            ["aud"] = audience
        }, null);

    /// <summary>Makes a service's JwtBearer trust <see cref="Key"/> instead of fetching AuthService metadata.</summary>
    public static void TrustTestTokens(IServiceCollection services) =>
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = null;
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(Key);
            options.Configuration = configuration;
            options.TokenValidationParameters.ValidIssuer = Issuer;
        });

    private static string Create(Dictionary<string, object> claims, TimeSpan? lifetime)
    {
        var now = DateTime.UtcNow;
        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = claims,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now + (lifetime ?? TimeSpan.FromMinutes(15)),
            TokenType = XpressTokenTypes.AccessToken,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)
        });
    }
}

public sealed record TestUser(long Id, string Email, string Name, params string[] Roles)
{
    public static readonly TestUser Alice = new(1001, "alice@example.com", "Alice", XpressRoles.User);
    public static readonly TestUser Bob = new(1002, "bob@example.com", "Bob", XpressRoles.User);
    public static readonly TestUser Admin = new(1003, "admin@example.com", "Ada Admin", XpressRoles.Admin);
    public static readonly TestUser SuperAdmin = new(1004, "super@example.com", "Sue Super", XpressRoles.SuperAdmin);
}
