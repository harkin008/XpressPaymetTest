using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using XpressTest.Web;
using XpressTest.Web.Services;

namespace XpressTest.Tests.Infrastructure;

/// <summary>
/// The web front end wired to a real AuthService and OrderService (notifications are faked inside
/// OrderService). Its clock is controllable so token refresh can be exercised.
/// </summary>
public sealed class WebAppFactory : WebApplicationFactory<WebEntryPoint>
{
    public AuthServiceFactory Auth { get; } = new();
    public OrderServiceFactory Orders { get; }
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public WebAppFactory() => Orders = new OrderServiceFactory(Auth);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Services:Auth", AuthServiceFactory.Issuer);
        builder.UseSetting("Services:Orders", "http://localhost");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient(ServiceEndpoints.AuthClient).ConfigurePrimaryHttpMessageHandler(() => Auth.Server.CreateHandler());
            services.AddHttpClient<UsersApiClient>().ConfigurePrimaryHttpMessageHandler(() => Auth.Server.CreateHandler());
            services.AddHttpClient<OrdersApiClient>().ConfigurePrimaryHttpMessageHandler(() => Orders.Server.CreateHandler());
        });
    }

    /// <summary>A browser-like client: keeps cookies, does not follow redirects.</summary>
    public Browser NewBrowser() => new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    public async Task<Browser> SignedInAsync(string email, string password)
    {
        var browser = NewBrowser();
        var response = await browser.SubmitAsync("/account/login", "/account/login", new() { ["Email"] = email, ["Password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return browser;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Orders.Dispose();
            Auth.Dispose();
        }
    }
}

public sealed partial class Browser(HttpClient client)
{
    public HttpClient Client => client;

    public Task<HttpResponseMessage> GetAsync(string url) => client.GetAsync(url);

    public async Task<string> GetHtmlAsync(string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} returned {(int)response.StatusCode} {response.Headers.Location}");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Loads <paramref name="formPage"/> for its antiforgery token, then posts the form like a browser.</summary>
    public async Task<HttpResponseMessage> SubmitAsync(string formPage, string action, Dictionary<string, string> fields)
    {
        var html = await GetHtmlAsync(formPage);
        var token = AntiforgeryToken().Match(html);
        Assert.True(token.Success, $"No antiforgery token on {formPage}");
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value);
        return await client.PostAsync(action, new FormUrlEncodedContent(fields));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
