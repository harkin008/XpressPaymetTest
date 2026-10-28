using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Data;
using XpressTest.AuthService.Models;
using XpressTest.AuthService.Tokens;

namespace XpressTest.AuthService.Controllers.Api;

/// <summary>
/// OAuth 2.0 token endpoint (RFC 6749), form-encoded. Supported grants:
/// <list type="bullet">
/// <item><c>password</c>: first-party clients sign users in (username = email); returns access + refresh token.</item>
/// <item><c>refresh_token</c>: rotates the refresh token and returns a new pair.</item>
/// <item><c>urn:xpress:grant-type:api-key</c>: exchanges a user's API key for an access token (no refresh token).</item>
/// <item><c>client_credentials</c>: service-to-service tokens, scoped (e.g. notifications.send).</item>
/// </list>
/// </summary>
[ApiController]
[AllowAnonymous]
// Credentials arrive in the request body or Authorization header, never via the portal cookie.
[IgnoreAntiforgeryToken]
public class TokenController(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    TokenService tokens,
    IOptions<JwtOptions> options) : ControllerBase
{
    [HttpPost("connect/token")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token([FromForm] TokenRequest request, CancellationToken ct)
    {
        // Tokens must never be cached (RFC 6749 §5.1).
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        return request.GrantType switch
        {
            "password" => await PasswordAsync(request, ct),
            "refresh_token" => await RefreshAsync(request, ct),
            ApiKeyDefaults.GrantType => await ApiKeyAsync(request, ct),
            "client_credentials" => ClientCredentials(request),
            null or "" => Error("invalid_request", "grant_type is required."),
            _ => Error("unsupported_grant_type", $"Grant type '{request.GrantType}' is not supported.")
        };
    }

    /// <summary>RFC 7009: revokes a refresh token (and its rotation family). Always 200, even for unknown tokens.</summary>
    [HttpPost("connect/revocation")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Revoke([FromForm(Name = "token")] string? token, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(token)) await tokens.RevokeAsync(token, ct);
        return Ok();
    }

    private async Task<IActionResult> PasswordAsync(TokenRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
            return Error("invalid_request", "username and password are required.");

        var user = await userManager.FindByEmailAsync(request.Username);
        if (user is null) return Error("invalid_grant", "Invalid username or password.");

        // Same checks as the portal login: lockout counting and IsActive (via ApplicationSignInManager).
        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut) return Error("invalid_grant", "Account locked after too many failed attempts. Try again later.");
        if (result.IsNotAllowed) return Error("invalid_grant", "This account has been deactivated.");
        if (!result.Succeeded) return Error("invalid_grant", "Invalid username or password.");

        return Success(await tokens.IssueForUserAsync(user, await userManager.GetRolesAsync(user), withRefreshToken: true, ct: ct));
    }

    private async Task<IActionResult> RefreshAsync(TokenRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.RefreshToken)) return Error("invalid_request", "refresh_token is required.");

        var (failure, user, newRefreshToken) = await tokens.RotateRefreshTokenAsync(request.RefreshToken, ct);
        if (failure != RefreshFailure.None || user is null)
            return Error("invalid_grant", failure switch
            {
                RefreshFailure.Reused => "Refresh token was already used; the session has been revoked.",
                RefreshFailure.Expired => "Refresh token has expired.",
                RefreshFailure.UserInvalid => "The account changed or was deactivated; sign in again.",
                _ => "Invalid refresh token."
            });

        var issued = await tokens.IssueForUserAsync(user, await userManager.GetRolesAsync(user), withRefreshToken: false, ct: ct);
        return Success(issued with { RefreshToken = newRefreshToken });
    }

    private async Task<IActionResult> ApiKeyAsync(TokenRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.ApiKey)) return Error("invalid_request", "api_key is required.");

        var hash = ApiKeyDefaults.Hash(request.ApiKey);
        var match = await db.ApiKeys
            .Where(k => k.KeyHash == hash && k.RevokedAtUtc == null)
            .Select(k => new { k.Id, k.User })
            .FirstOrDefaultAsync(ct);

        if (match?.User is not { } user || !await signInManager.CanSignInAsync(user) || await userManager.IsLockedOutAsync(user))
            return Error("invalid_grant", "Invalid API key.");

        return Success(await tokens.IssueForUserAsync(user, await userManager.GetRolesAsync(user),
            withRefreshToken: false, apiKeyId: match.Id, ct: ct));
    }

    private IActionResult ClientCredentials(TokenRequest request)
    {
        var (clientId, secret) = ReadClientCredentials(request);
        var client = options.Value.Clients.FirstOrDefault(c => c.ClientId == clientId);
        if (client is null || secret is null || !SecretMatches(secret, client.SecretSha256))
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"xpress\"";
            return Error("invalid_client", "Client authentication failed.", StatusCodes.Status401Unauthorized);
        }

        var requested = (request.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        var scopes = requested.Count == 0 ? client.AllowedScopes : requested;
        if (scopes.Count == 0 || scopes.Any(s => !client.AllowedScopes.Contains(s) || !options.Value.ApiScopes.ContainsKey(s)))
            return Error("invalid_scope", "One or more requested scopes are not allowed for this client.");

        return Success(tokens.IssueForClient(client, scopes));
    }

    // client_secret_basic, falling back to client_secret_post.
    private (string? ClientId, string? Secret) ReadClientCredentials(TokenRequest request)
    {
        if (AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header)
            && header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) && header.Parameter is not null)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
                var colon = decoded.IndexOf(':');
                if (colon > 0) return (Uri.UnescapeDataString(decoded[..colon]), Uri.UnescapeDataString(decoded[(colon + 1)..]));
            }
            catch (FormatException) { }
            return (null, null);
        }
        return (request.ClientId, request.ClientSecret);
    }

    private static bool SecretMatches(string secret, string expectedSha256Hex)
    {
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256Hex); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private OkObjectResult Success(TokenResult result)
    {
        var body = new Dictionary<string, object>
        {
            ["access_token"] = result.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = result.ExpiresIn
        };
        if (result.RefreshToken is not null) body["refresh_token"] = result.RefreshToken;
        if (result.Scope is not null) body["scope"] = result.Scope;
        return Ok(body);
    }

    private ObjectResult Error(string error, string description, int status = StatusCodes.Status400BadRequest) =>
        StatusCode(status, new Dictionary<string, string> { ["error"] = error, ["error_description"] = description });
}

public class TokenRequest
{
    [FromForm(Name = "grant_type")] public string? GrantType { get; set; }
    [FromForm(Name = "username")] public string? Username { get; set; }
    [FromForm(Name = "password")] public string? Password { get; set; }
    [FromForm(Name = "refresh_token")] public string? RefreshToken { get; set; }
    [FromForm(Name = "api_key")] public string? ApiKey { get; set; }
    [FromForm(Name = "client_id")] public string? ClientId { get; set; }
    [FromForm(Name = "client_secret")] public string? ClientSecret { get; set; }
    [FromForm(Name = "scope")] public string? Scope { get; set; }
}
