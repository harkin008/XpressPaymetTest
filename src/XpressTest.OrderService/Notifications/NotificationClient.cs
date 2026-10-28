using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace XpressTest.OrderService.Notifications;

public interface INotificationClient
{
    /// <summary>Posts a JSON message to NotificationService. Throws if it is not accepted.</summary>
    Task SendAsync(string route, string jsonPayload, CancellationToken ct);
}

public sealed class NotificationClient(HttpClient http, ServiceTokenClient tokens) : INotificationClient
{
    public async Task SendAsync(string route, string jsonPayload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct));

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) tokens.Invalidate();
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"NotificationService returned {(int)response.StatusCode}: {detail[..Math.Min(500, detail.Length)]}",
                null, response.StatusCode);
        }
    }
}
