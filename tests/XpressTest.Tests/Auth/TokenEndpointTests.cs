using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using XpressTest.AuthService.Models;
using XpressTest.Shared.Auth;
using XpressTest.Tests.Infrastructure;

namespace XpressTest.Tests.Auth;

public sealed class TokenEndpointTests : IDisposable
{
    private const string OrderServiceSecret = "order-service-dev-secret";

    private readonly AuthServiceFactory _factory = new();
    private readonly HttpClient _client;

    public TokenEndpointTests() => _client = _factory.CreateApiClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Publishes_discovery_and_only_the_public_signing_key()
    {
        var discovery = await _client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        Assert.Equal(AuthServiceFactory.Issuer, discovery.GetProperty("issuer").GetString());
        Assert.Equal("http://localhost/.well-known/jwks.json", discovery.GetProperty("jwks_uri").GetString());

        var key = (await _client.GetFromJsonAsync<JsonElement>("/.well-known/jwks.json")).GetProperty("keys")[0];
        Assert.Equal(("RSA", "RS256", "sig"), (key.GetProperty("kty").GetString(), key.GetProperty("alg").GetString(), key.GetProperty("use").GetString()));
        foreach (var privatePart in new[] { "d", "p", "q", "dp", "dq", "qi" })
            Assert.False(key.TryGetProperty(privatePart, out _), $"JWKS leaked private parameter '{privatePart}'");
    }

    [Fact]
    public async Task Password_grant_issues_a_signed_access_token_with_user_claims_and_a_refresh_token()
    {
        var response = await TokenAsync(new() { ["grant_type"] = "password", ["username"] = "admin@xpress.local", ["password"] = "Admin@123" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(900, body.GetProperty("expires_in").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refresh_token").GetString()));

        var token = await ValidateAsync(body.GetProperty("access_token").GetString()!, XpressAudiences.OrderService);
        Assert.Equal("at+jwt", token.Typ);
        Assert.Equal("admin@xpress.local", token.GetClaim(XpressClaims.Email).Value);
        Assert.Equal("Default Admin", token.GetClaim(XpressClaims.Name).Value);
        Assert.Equal(["Admin"], token.Claims.Where(c => c.Type == XpressClaims.Role).Select(c => c.Value));
        Assert.Contains(XpressAudiences.AuthService, token.Audiences);
        Assert.DoesNotContain(XpressAudiences.NotificationService, token.Audiences);
    }

    [Theory]
    [InlineData("user@xpress.local", "wrong-password")]
    [InlineData("nobody@xpress.local", "User@123")]
    public async Task Password_grant_gives_the_same_error_for_wrong_password_and_unknown_user(string email, string password)
    {
        var response = await TokenAsync(new() { ["grant_type"] = "password", ["username"] = email, ["password"] = password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_grant", error.GetProperty("error").GetString());
        Assert.Equal("Invalid username or password.", error.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task Deactivated_users_cannot_get_tokens_and_their_refresh_tokens_stop_working()
    {
        var tokens = await _factory.PasswordGrantAsync("user@xpress.local", "User@123");
        await DeactivateAsync("user@xpress.local");

        var refresh = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()! });
        Assert.Equal("invalid_grant", (await refresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var login = await TokenAsync(new() { ["grant_type"] = "password", ["username"] = "user@xpress.local", ["password"] = "User@123" });
        Assert.Contains("deactivated", await login.Content.ReadAsStringAsync());

        var apiKey = await TokenAsync(new() { ["grant_type"] = "urn:xpress:grant-type:api-key", ["api_key"] = "xpk_demo_user_key" });
        Assert.Equal(HttpStatusCode.BadRequest, apiKey.StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_replaying_an_old_one_revokes_the_session()
    {
        var first = (await _factory.PasswordGrantAsync("user@xpress.local", "User@123")).GetProperty("refresh_token").GetString()!;

        var rotated = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = first });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(first, second);

        var replay = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = first });
        Assert.Contains("already used", await replay.Content.ReadAsStringAsync());

        // The legitimate-looking newer token is revoked too, because the family was compromised.
        var afterReplay = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = second });
        Assert.Equal(HttpStatusCode.BadRequest, afterReplay.StatusCode);
    }

    [Fact]
    public async Task Revocation_ends_the_session()
    {
        var refresh = (await _factory.PasswordGrantAsync("user@xpress.local", "User@123")).GetProperty("refresh_token").GetString()!;

        var revoked = await _client.PostAsync("/connect/revocation", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh }));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        var reuse = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh });
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
    }

    [Fact]
    public async Task Api_keys_are_exchanged_for_access_tokens_without_refresh_tokens()
    {
        var response = await TokenAsync(new() { ["grant_type"] = "urn:xpress:grant-type:api-key", ["api_key"] = "xpk_demo_user_key" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.TryGetProperty("refresh_token", out _));
        var token = await ValidateAsync(body.GetProperty("access_token").GetString()!, XpressAudiences.OrderService);
        Assert.Equal("user@xpress.local", token.GetClaim(XpressClaims.Email).Value);
        Assert.True(token.TryGetClaim(XpressClaims.ApiKeyId, out _));

        var unknown = await TokenAsync(new() { ["grant_type"] = "urn:xpress:grant-type:api-key", ["api_key"] = "xpk_guessed" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Client_credentials_issue_scoped_service_tokens_for_the_notification_service()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = XpressScopes.NotificationsSend })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"order-service:{OrderServiceSecret}")));

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(XpressScopes.NotificationsSend, body.GetProperty("scope").GetString());

        var token = await ValidateAsync(body.GetProperty("access_token").GetString()!, XpressAudiences.NotificationService);
        Assert.Equal("order-service", token.GetClaim(XpressClaims.ClientId).Value);
        Assert.Equal(XpressScopes.NotificationsSend, token.GetClaim(XpressClaims.Scope).Value);
        Assert.False(token.TryGetClaim(XpressClaims.Email, out _));
    }

    [Fact]
    public async Task Client_credentials_reject_bad_secrets_and_unknown_scopes()
    {
        var badSecret = await TokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = "order-service", ["client_secret"] = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, badSecret.StatusCode);
        Assert.Contains("invalid_client", await badSecret.Content.ReadAsStringAsync());

        var badScope = await TokenAsync(new()
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "order-service", ["client_secret"] = OrderServiceSecret, ["scope"] = "orders.admin"
        });
        Assert.Contains("invalid_scope", await badScope.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unsupported_grants_are_rejected()
    {
        var response = await TokenAsync(new() { ["grant_type"] = "authorization_code" });
        Assert.Contains("unsupported_grant_type", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Register_creates_a_User_and_me_reads_the_token()
    {
        var register = await _client.PostAsJsonAsync("/api/auth/register", new { fullName = "New Person", email = "new@x.local", password = "New@1234" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var accessToken = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/auth/me")).StatusCode);

        var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var profile = await (await _client.SendAsync(me)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("new@x.local", profile.GetProperty("email").GetString());
        Assert.Equal(["User"], profile.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    private Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form) =>
        _client.PostAsync("/connect/token", new FormUrlEncodedContent(form));

    /// <summary>Validates the way other services do: with the key published in JWKS.</summary>
    private async Task<JsonWebToken> ValidateAsync(string token, string audience)
    {
        var jwks = new JsonWebKeySet(await _client.GetStringAsync("/.well-known/jwks.json"));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = AuthServiceFactory.Issuer,
            ValidAudience = audience,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidTypes = [XpressTokenTypes.AccessToken]
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        return (JsonWebToken)result.SecurityToken;
    }

    private async Task DeactivateAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        user.IsActive = false;
        await users.UpdateSecurityStampAsync(user); // what the portal's Deactivate does
    }
}
