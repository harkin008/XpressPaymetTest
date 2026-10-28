using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using XpressTest.Shared.Auth;

namespace XpressTest.Web.Services;

/// <summary>
/// Turns AuthService tokens into the web app's cookie session and keeps them fresh.
/// The tokens live inside the encrypted auth cookie, never in browser-readable storage.
/// </summary>
public sealed class UserSession
{
    public const string AccessTokenName = "access_token";
    public const string RefreshTokenName = "refresh_token";
    public const string ExpiresAtName = "expires_at";

    /// <summary>Refresh this long before the access token expires.</summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long a refresh result is shared with concurrent requests that used the same refresh token.</summary>
    private static readonly TimeSpan ShareRefreshFor = TimeSpan.FromSeconds(30);

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly TokenClient _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<UserSession> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _authMetadata;
    private readonly ConcurrentDictionary<string, (Lazy<Task<TokenResult>> Refresh, DateTimeOffset StartedAt)> _refreshes = new();

    public UserSession(TokenClient tokens, IHttpClientFactory httpClients, IOptions<ServiceEndpoints> endpoints, TimeProvider clock, ILogger<UserSession> logger)
    {
        _tokens = tokens;
        _clock = clock;
        _logger = logger;
        // Same discovery + JWKS the other services use; keys are cached and refreshed on rotation.
        _authMetadata = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{endpoints.Value.Auth.TrimEnd('/')}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(httpClients.CreateClient(ServiceEndpoints.AuthClient)) { RequireHttps = endpoints.Value.RequireHttpsMetadata });
    }

    /// <summary>Signs the browser in with tokens just obtained from AuthService.</summary>
    public async Task SignInAsync(HttpContext context, TokenSet tokens, bool persistent)
    {
        var principal = await CreatePrincipalAsync(tokens.AccessToken);
        var properties = new AuthenticationProperties { IsPersistent = persistent };
        StoreTokens(properties, tokens);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    }

    public async Task SignOutAsync(HttpContext context)
    {
        if (await context.GetTokenAsync(RefreshTokenName) is { } refreshToken)
            await _tokens.RevokeAsync(refreshToken, context.RequestAborted);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Cookie validation hook (runs on every authenticated request): refreshes the access token
    /// shortly before it expires and re-reads the claims, so role changes and deactivation
    /// made in AuthService reach the web app. If refreshing fails, the user is signed out.
    /// </summary>
    public async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var expiresAtText = context.Properties.GetTokenValue(ExpiresAtName);
        if (DateTimeOffset.TryParse(expiresAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
            && expiresAt - _clock.GetUtcNow() > RefreshWindow)
            return;

        var refreshToken = context.Properties.GetTokenValue(RefreshTokenName);
        TokenResult? result;
        try
        {
            result = refreshToken is null ? null : await RefreshOnceAsync(refreshToken);
        }
        catch (HttpRequestException ex) when (expiresAt > _clock.GetUtcNow())
        {
            // AuthService unreachable but the current token still works: keep the session, retry later.
            _logger.LogWarning(ex, "Token refresh failed; keeping the current access token until it expires");
            return;
        }
        if (result?.Tokens is not { } tokens)
        {
            _logger.LogInformation("Session for {User} ended: {Reason}", context.Principal?.Identity?.Name,
                result?.Errors.FirstOrDefault() ?? "no refresh token");
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        context.ReplacePrincipal(await CreatePrincipalAsync(tokens.AccessToken));
        StoreTokens(context.Properties, tokens);
        context.ShouldRenew = true;
    }

    /// <summary>
    /// AuthService rotates refresh tokens and treats a replayed one as theft. A page load fires
    /// several requests at once (page, CSS, JS) that all carry the same old cookie, so they must
    /// share a single refresh call instead of racing each other.
    /// </summary>
    private Task<TokenResult> RefreshOnceAsync(string refreshToken)
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, stale) in _refreshes)
            if (now - stale.StartedAt > ShareRefreshFor) _refreshes.TryRemove(key, out _);

        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        // CancellationToken.None: the shared call must not die with whichever request started it.
        var entry = _refreshes.GetOrAdd(id, _ => (new Lazy<Task<TokenResult>>(() => _tokens.RefreshAsync(refreshToken, CancellationToken.None)), now));
        return entry.Refresh.Value;
    }

    /// <summary>Validates an AuthService access token and builds the cookie identity from it.</summary>
    private async Task<ClaimsPrincipal> CreatePrincipalAsync(string accessToken)
    {
        var metadata = await _authMetadata.GetConfigurationAsync(CancellationToken.None);
        var result = await Handler.ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidIssuer = metadata.Issuer,
            ValidAudience = XpressAudiences.AuthService,
            IssuerSigningKeys = metadata.SigningKeys,
            ValidTypes = [XpressTokenTypes.AccessToken],
            ClockSkew = TimeSpan.FromSeconds(30)
        });
        if (!result.IsValid)
        {
            // Signing keys may have rotated since they were cached.
            _authMetadata.RequestRefresh();
            throw new SecurityTokenException("AuthService returned an access token that failed validation.", result.Exception);
        }

        var keep = new[] { XpressClaims.Subject, XpressClaims.Email, XpressClaims.Name, XpressClaims.Role };
        var claims = result.ClaimsIdentity.Claims.Where(c => keep.Contains(c.Type)).Select(c => new Claim(c.Type, c.Value));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, XpressClaims.Name, XpressClaims.Role));
    }

    private static void StoreTokens(AuthenticationProperties properties, TokenSet tokens)
    {
        var list = new List<AuthenticationToken>
        {
            new() { Name = AccessTokenName, Value = tokens.AccessToken },
            new() { Name = ExpiresAtName, Value = tokens.ExpiresAt.ToString("o", CultureInfo.InvariantCulture) }
        };
        if (tokens.RefreshToken is not null) list.Add(new() { Name = RefreshTokenName, Value = tokens.RefreshToken });
        properties.StoreTokens(list);
    }
}
