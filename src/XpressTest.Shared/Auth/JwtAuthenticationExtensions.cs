using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace XpressTest.Shared.Auth;

public static class JwtAuthenticationExtensions
{
    /// <summary>
    /// Validates access tokens issued by AuthService. Signing keys are fetched (and refreshed on
    /// rotation) from the authority's discovery document, so services share no secrets.
    /// Configuration: <c>Auth:Authority</c> (AuthService base URL, also the token issuer).
    /// </summary>
    public static AuthenticationBuilder AddXpressJwtBearer(this IServiceCollection services, IConfiguration configuration, string audience)
    {
        var authority = configuration["Auth:Authority"]
                        ?? throw new InvalidOperationException("Auth:Authority (the AuthService URL) is not configured.");

        return services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = audience;
                options.RequireHttpsMetadata = configuration.GetValue("Auth:RequireHttpsMetadata", true);
                // Keep claim names as issued ("sub", "role") instead of mapping to long URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = authority,
                    ValidAudience = audience,
                    ValidTypes = [XpressTokenTypes.AccessToken],
                    NameClaimType = XpressClaims.Name,
                    RoleClaimType = XpressClaims.Role,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });
    }

    /// <summary>Requires a space-separated "scope" claim to contain <paramref name="scope"/>.</summary>
    public static AuthorizationPolicyBuilder RequireScope(this AuthorizationPolicyBuilder policy, string scope) =>
        policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.HasScope(scope));

    public static bool HasScope(this ClaimsPrincipal user, string scope) =>
        user.FindAll(XpressClaims.Scope).Any(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope));

    public static string? GetSubject(this ClaimsPrincipal user) => user.FindFirst(XpressClaims.Subject)?.Value;

    /// <summary>The user id from "sub", or null when the token is not a user's (e.g. a service client).</summary>
    public static long? GetUserId(this ClaimsPrincipal user) => long.TryParse(user.GetSubject(), out var id) ? id : null;
}
