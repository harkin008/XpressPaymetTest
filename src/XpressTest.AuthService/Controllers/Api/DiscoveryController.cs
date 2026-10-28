using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Tokens;

namespace XpressTest.AuthService.Controllers.Api;

/// <summary>
/// Discovery metadata and public signing keys. OrderService and NotificationService read these
/// (via JwtBearer's Authority) to validate tokens, and refresh them automatically on key rotation.
/// </summary>
[ApiController]
[AllowAnonymous]
public class DiscoveryController(SigningKeyProvider keys, IOptions<JwtOptions> options) : ControllerBase
{
    [HttpGet(".well-known/openid-configuration")]
    public IActionResult Configuration()
    {
        var issuer = options.Value.Issuer.TrimEnd('/');
        return Ok(new Dictionary<string, object>
        {
            ["issuer"] = options.Value.Issuer,
            ["jwks_uri"] = $"{issuer}/.well-known/jwks.json",
            ["token_endpoint"] = $"{issuer}/connect/token",
            ["revocation_endpoint"] = $"{issuer}/connect/revocation",
            ["grant_types_supported"] = new[] { "password", "refresh_token", "client_credentials", ApiKeyDefaults.GrantType },
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post" },
            ["scopes_supported"] = options.Value.ApiScopes.Keys,
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" }
        });
    }

    [HttpGet(".well-known/jwks.json")]
    [ResponseCache(Duration = 300)]
    public IActionResult Jwks() => Ok(new { keys = new[] { keys.PublicJwk } });
}
