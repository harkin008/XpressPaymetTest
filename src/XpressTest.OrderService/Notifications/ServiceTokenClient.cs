using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace XpressTest.OrderService.Notifications;

/// <summary>This service's own credentials at AuthService (OAuth2 client credentials).</summary>
public sealed class ServiceClientOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
}

/// <summary>
/// Gets and caches a service access token from AuthService's token endpoint,
/// refreshing it shortly before it expires. Registered as a singleton so the cache is shared.
/// </summary>
public sealed class ServiceTokenClient(IHttpClientFactory httpClients, IOptions<ServiceClientOptions> options, TimeProvider clock)
{
    /// <summary>Named HttpClient whose base address is the AuthService URL.</summary>
    public const string HttpClientName = "auth-service";

    private static readonly TimeSpan RefreshEarly = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && clock.GetUtcNow() < _expiresAt) return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _expiresAt) return _token;

            var o = options.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, "connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["scope"] = o.Scope
                })
            };
            // client_secret_basic (RFC 6749 §2.3.1).
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    $"{Uri.EscapeDataString(o.ClientId)}:{Uri.EscapeDataString(o.ClientSecret)}")));

            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                       ?? throw new InvalidOperationException("Empty token response.");

            _token = body.AccessToken;
            _expiresAt = clock.GetUtcNow().AddSeconds(body.ExpiresIn) - RefreshEarly;
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the cached token, e.g. after a 401 (keys rotated or token revoked).</summary>
    public void Invalidate() => _token = null;

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
