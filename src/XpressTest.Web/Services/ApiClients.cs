using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace XpressTest.Web.Services;

/// <summary>Attaches the signed-in user's access token (from the auth cookie) to back-end calls.</summary>
public sealed class UserAccessTokenHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (accessor.HttpContext is { } context && await context.GetTokenAsync(UserSession.AccessTokenName) is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}

/// <summary>The back end rejected the user's token (e.g. account deactivated); the user must sign in again.</summary>
public sealed class SessionExpiredException() : Exception("The session is no longer valid.");

/// <summary>Outcome of a back-end call: the value, or the problem details to show the user.</summary>
public sealed record ApiResult<T>(T? Value, HttpStatusCode Status, string? Message, IReadOnlyList<string> Errors, TimeSpan? RetryAfter)
{
    public bool Succeeded => (int)Status is >= 200 and < 300;
    public bool IsRateLimited => Status == HttpStatusCode.TooManyRequests;

    /// <summary>One message suitable for the page.</summary>
    public string ErrorText => IsRateLimited
        ? $"You're going too fast. Please wait {Math.Ceiling(RetryAfter?.TotalSeconds ?? 60)} seconds and try again."
        : Errors.Count > 0 ? string.Join(" ", Errors) : Message ?? $"The request failed ({(int)Status}).";
}

public abstract class ApiClientBase(HttpClient http)
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SessionExpiredException();

        if (response.IsSuccessStatusCode)
        {
            var value = response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(NoContent)
                ? default
                : await response.Content.ReadFromJsonAsync<T>(Json, ct);
            return new ApiResult<T>(value, response.StatusCode, null, [], null);
        }

        string? message = null;
        var errors = new List<string>();
        if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            message = problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()
                : problem.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (problem.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                errors.AddRange(e.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(m => m.GetString() ?? "")));
        }
        return new ApiResult<T>(default, response.StatusCode, message, errors, response.Headers.RetryAfter?.Delta);
    }
}

/// <summary>Marker for calls whose success has no body.</summary>
public sealed record NoContent;

public sealed class OrdersApiClient(HttpClient http) : ApiClientBase(http)
{
    public Task<ApiResult<PagedDto<OrderDto>>> ListAsync(string? status, int page, int pageSize, CancellationToken ct) =>
        SendAsync<PagedDto<OrderDto>>(HttpMethod.Get,
            $"api/orders?page={page}&pageSize={pageSize}{(string.IsNullOrEmpty(status) ? "" : $"&status={Uri.EscapeDataString(status)}")}", null, ct);

    public Task<ApiResult<OrderDto>> GetAsync(long id, CancellationToken ct) => SendAsync<OrderDto>(HttpMethod.Get, $"api/orders/{id}", null, ct);

    public Task<ApiResult<OrderDto>> CreateAsync(string productName, int quantity, decimal unitPrice, CancellationToken ct) =>
        SendAsync<OrderDto>(HttpMethod.Post, "api/orders", new { productName, quantity, unitPrice }, ct);

    public Task<ApiResult<OrderDto>> UpdateAsync(long id, string productName, int quantity, decimal unitPrice, string? status, CancellationToken ct) =>
        SendAsync<OrderDto>(HttpMethod.Put, $"api/orders/{id}", new { productName, quantity, unitPrice, status }, ct);

    public Task<ApiResult<NoContent>> CancelAsync(long id, CancellationToken ct) => SendAsync<NoContent>(HttpMethod.Delete, $"api/orders/{id}", null, ct);

    public Task<ApiResult<OrderStatsDto>> StatsAsync(CancellationToken ct) => SendAsync<OrderStatsDto>(HttpMethod.Get, "api/orders/stats", null, ct);
}

public sealed class UsersApiClient(HttpClient http) : ApiClientBase(http)
{
    public Task<ApiResult<UserPageDto>> ListAsync(string? search, string? role, bool? isActive, int page, int pageSize, CancellationToken ct)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(role)) query.Add($"role={Uri.EscapeDataString(role)}");
        if (isActive is not null) query.Add($"isActive={isActive.Value.ToString().ToLowerInvariant()}");
        return SendAsync<UserPageDto>(HttpMethod.Get, "api/users?" + string.Join('&', query), null, ct);
    }

    public Task<ApiResult<UserDto>> GetAsync(long id, CancellationToken ct) => SendAsync<UserDto>(HttpMethod.Get, $"api/users/{id}", null, ct);

    public Task<ApiResult<UserStatsDto>> StatsAsync(CancellationToken ct) => SendAsync<UserStatsDto>(HttpMethod.Get, "api/users/stats", null, ct);

    public Task<ApiResult<UserDto>> CreateAsync(string fullName, string email, string password, string role, CancellationToken ct) =>
        SendAsync<UserDto>(HttpMethod.Post, "api/users", new { fullName, email, password, role }, ct);

    public Task<ApiResult<NoContent>> UpdateAsync(long id, string fullName, string email, string role, CancellationToken ct) =>
        SendAsync<NoContent>(HttpMethod.Put, $"api/users/{id}", new { fullName, email, role }, ct);

    public Task<ApiResult<NoContent>> SetActiveAsync(long id, bool active, CancellationToken ct) =>
        SendAsync<NoContent>(HttpMethod.Post, $"api/users/{id}/{(active ? "activate" : "deactivate")}", null, ct);

    public Task<ApiResult<BulkDeactivateDto>> DeactivateManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct) =>
        SendAsync<BulkDeactivateDto>(HttpMethod.Post, "api/users/deactivate", new { ids }, ct);

    public Task<ApiResult<NoContent>> DeleteAsync(long id, CancellationToken ct) =>
        SendAsync<NoContent>(HttpMethod.Delete, $"api/users/{id}", null, ct);
}

// Client-side shapes of the service responses. Kept here rather than shared with the services,
// so each service can evolve its API without recompiling the web app.
public static class OrderStatuses
{
    public const string Pending = "Pending";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Pending, "Confirmed", "Shipped", Cancelled];
}

public record OrderDto(long Id, long UserId, string CustomerEmail, string CustomerName, string ProductName,
    int Quantity, decimal UnitPrice, decimal Total, string Status, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public record PagedDto<T>(List<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public record OrderStatsDto(int Total, Dictionary<string, int> ByStatus);

public record UserDto(long Id, string FullName, string Email, string Role, bool IsActive, bool CanManage, bool CanDelete);

public record UserPageDto(List<UserDto> Items, int Page, int PageSize, int TotalCount, int TotalPages, List<string> AssignableRoles);

public record RoleCountDto(string? Role, bool IsActive, int Count);

public record UserStatsDto(int Total, int Active, int Inactive, List<RoleCountDto> Breakdown);

public record BulkDeactivateDto(int Deactivated);
