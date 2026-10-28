using System.Net.Http.Json;
using System.Text.Json;

namespace XpressTest.Web.Services;

public sealed record TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Either tokens, or the AuthService's error_description / validation messages.</summary>
public sealed record TokenResult(TokenSet? Tokens, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Tokens is not null;
}

/// <summary>Anonymous calls to AuthService: sign-in, registration, refresh and revocation.</summary>
public sealed class TokenClient(IHttpClientFactory httpClients, TimeProvider clock)
{
    private HttpClient Http => httpClients.CreateClient(ServiceEndpoints.AuthClient);

    public Task<TokenResult> PasswordAsync(string email, string password, CancellationToken ct) =>
        PostTokenAsync(new() { ["grant_type"] = "password", ["username"] = email, ["password"] = password }, ct);

    public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PostTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, ct);

    public async Task<TokenResult> RegisterAsync(string fullName, string email, string password, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync("api/auth/register", new { fullName, email, password }, ct);
        if (response.IsSuccessStatusCode) return new TokenResult(await ReadTokensAsync(response, ct), []);

        // ValidationProblemDetails: { errors: { "DuplicateUserName": ["..."], ... } }
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var errors = body.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object
            ? e.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(m => m.GetString() ?? "")).ToList()
            : [body.TryGetProperty("title", out var t) ? t.GetString() ?? "Registration failed." : "Registration failed."];
        return new TokenResult(null, errors);
    }

    /// <summary>Ends the session at AuthService (RFC 7009). Best effort.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            using var _ = await Http.PostAsync("connect/revocation",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }), ct);
        }
        catch (HttpRequestException) { /* signing out locally still succeeds */ }
    }

    private async Task<TokenResult> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await Http.PostAsync("connect/token", new FormUrlEncodedContent(form), ct);
        if (response.IsSuccessStatusCode) return new TokenResult(await ReadTokensAsync(response, ct), []);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var message = body.TryGetProperty("error_description", out var d) ? d.GetString() : null;
        return new TokenResult(null, [message ?? "Sign-in failed."]);
    }

    private async Task<TokenSet> ReadTokensAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new TokenSet(
            body.GetProperty("access_token").GetString()!,
            body.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            clock.GetUtcNow().AddSeconds(body.GetProperty("expires_in").GetInt32()));
    }
}
