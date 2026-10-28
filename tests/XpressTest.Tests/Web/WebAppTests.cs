using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using XpressTest.Tests.Infrastructure;
using XpressTest.Web.Services;

namespace XpressTest.Tests.Web;

public sealed partial class WebAppTests : IDisposable
{
    private readonly WebAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_sign_in()
    {
        var response = await _app.NewBrowser().GetAsync("/orders");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/login", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Wrong_password_shows_an_error()
    {
        var response = await _app.NewBrowser().SubmitAsync("/account/login", "/account/login",
            new() { ["Email"] = "user@xpress.local", ["Password"] = "nope" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid username or password.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Register_signs_the_new_user_in()
    {
        var browser = _app.NewBrowser();
        var response = await browser.SubmitAsync("/account/register", "/account/register", new()
        {
            ["FullName"] = "Rita Register", ["Email"] = "rita@x.local", ["Password"] = "Rita@1234", ["ConfirmPassword"] = "Rita@1234"
        });
        Assert.Equal("/orders", response.Headers.Location?.OriginalString);

        var html = await browser.GetHtmlAsync("/orders");
        Assert.Contains("Welcome, Rita Register!", html);
        Assert.Contains("My orders", html);
        Assert.DoesNotContain(">Admin<", html);
    }

    [Fact]
    public async Task Register_shows_service_errors_for_duplicate_email()
    {
        var response = await _app.NewBrowser().SubmitAsync("/account/register", "/account/register", new()
        {
            ["FullName"] = "Dup", ["Email"] = "user@xpress.local", ["Password"] = "Dup@12345", ["ConfirmPassword"] = "Dup@12345"
        });
        Assert.Contains("is already taken", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task User_creates_and_views_orders_and_is_rate_limited()
    {
        var browser = await _app.SignedInAsync("user@xpress.local", "User@123");

        var created = await CreateOrderAsync(browser, "Espresso machine");
        var details = await browser.GetHtmlAsync($"/orders/{created}");
        Assert.Contains($"Order #{created} placed. A confirmation email is on its way.", details);
        Assert.Contains("Espresso machine", details);
        Assert.DoesNotContain("Change status", details);

        await CreateOrderAsync(browser, "Grinder");
        await CreateOrderAsync(browser, "Beans");
        var list = await browser.GetHtmlAsync("/orders");
        Assert.Contains("Espresso machine", list);
        Assert.Contains("(3)", list);

        var limited = await browser.SubmitAsync("/orders/new", "/orders/new",
            new() { ["ProductName"] = "One too many", ["Quantity"] = "1", ["UnitPrice"] = "5" });
        Assert.Equal(HttpStatusCode.OK, limited.StatusCode);
        Assert.Contains("You&#x27;re going too fast. Please wait 60 seconds", await limited.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Users_cannot_open_admin_pages()
    {
        var browser = await _app.SignedInAsync("user@xpress.local", "User@123");

        foreach (var url in new[] { "/admin", "/admin/users" })
        {
            var response = await browser.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/account/access-denied", response.Headers.Location?.PathAndQuery);
        }
    }

    [Fact]
    public async Task Admins_see_all_orders_and_change_status()
    {
        var customer = await _app.SignedInAsync("user@xpress.local", "User@123");
        var orderId = await CreateOrderAsync(customer, "Kettle");

        var admin = await _app.SignedInAsync("admin@xpress.local", "Admin@123");
        var list = await admin.GetHtmlAsync("/orders");
        Assert.Contains("All orders", list);
        Assert.Contains("user@xpress.local", list);

        var changed = await admin.SubmitAsync($"/orders/{orderId}", $"/orders/{orderId}/status", new() { ["status"] = "Shipped" });
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        var details = await admin.GetHtmlAsync($"/orders/{orderId}");
        Assert.Contains("is now Shipped. The customer has been notified.", details);

        // The customer can no longer edit a shipped order.
        Assert.DoesNotContain(">Edit<", await customer.GetHtmlAsync($"/orders/{orderId}"));
    }

    [Fact]
    public async Task Admin_dashboard_shows_order_and_user_statistics()
    {
        var customer = await _app.SignedInAsync("user@xpress.local", "User@123");
        await CreateOrderAsync(customer, "Mug");

        var html = await (await _app.SignedInAsync("admin@xpress.local", "Admin@123")).GetHtmlAsync("/admin");
        Assert.Contains("Dashboard", html);
        Assert.Matches(@"Total</div><div class=""fs-3"">1<", html);   // orders
        Assert.Matches(@"Total</div><div class=""fs-3"">3<", html);   // seeded users
    }

    [Fact]
    public async Task Admins_manage_users_but_cannot_create_admins_or_delete()
    {
        var admin = await _app.SignedInAsync("admin@xpress.local", "Admin@123");

        var users = await admin.GetHtmlAsync("/admin/users");
        Assert.DoesNotContain(">Delete<", users);

        var form = await admin.GetHtmlAsync("/admin/users/new");
        Assert.Matches("<option[^>]*>User</option>", form);
        Assert.DoesNotMatch("<option[^>]*>Admin</option>", form);

        var created = await admin.SubmitAsync("/admin/users/new", "/admin/users/new", new()
        {
            ["FullName"] = "Made By Admin", ["Email"] = "made@x.local", ["Password"] = "Made@1234", ["Role"] = "User"
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Contains("Created User made@x.local.", await admin.GetHtmlAsync("/admin/users"));

        // Tampered form: the service refuses, and the page says so.
        var tampered = await admin.SubmitAsync("/admin/users/new", "/admin/users/new", new()
        {
            ["FullName"] = "Sneaky", ["Email"] = "sneaky@x.local", ["Password"] = "Sneak@1234", ["Role"] = "Admin"
        });
        Assert.Contains("Your role does not allow this action", await tampered.Content.ReadAsStringAsync());

        var madeId = UserId(await admin.GetHtmlAsync("/admin/users?search=made"));
        var delete = await admin.SubmitAsync("/admin/users", $"/admin/users/{madeId}/delete", new());
        Assert.StartsWith("/account/access-denied", delete.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task SuperAdmins_create_admins_and_delete_users()
    {
        var super = await _app.SignedInAsync("superadmin@xpress.local", "SuperAdmin@123");

        Assert.Matches("<option[^>]*>Admin</option>", await super.GetHtmlAsync("/admin/users/new"));
        await super.SubmitAsync("/admin/users/new", "/admin/users/new", new()
        {
            ["FullName"] = "New Admin", ["Email"] = "newadmin@x.local", ["Password"] = "Admin@1234", ["Role"] = "Admin"
        });

        var list = await super.GetHtmlAsync("/admin/users?search=newadmin");
        Assert.Contains(">Admin</span>", list);
        Assert.Contains(">Delete<", list);

        var deleted = await super.SubmitAsync("/admin/users", $"/admin/users/{UserId(list)}/delete", new());
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Contains("User deleted.", await super.GetHtmlAsync("/admin/users"));
        Assert.Contains("(0)", await super.GetHtmlAsync("/admin/users?search=newadmin"));
    }

    [Fact]
    public async Task Expired_access_tokens_are_refreshed_once_even_when_several_requests_carry_the_old_cookie()
    {
        // Manage cookies by hand so several requests can deliberately send the same old cookie,
        // as a browser does when a page load fires requests in parallel.
        var http = _app.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var jar = new Dictionary<string, string>();
        var loginPage = await SendAsync(http, HttpMethod.Get, "/account/login", jar);
        var token = WebUtility.HtmlDecode(AntiforgeryField().Match(await loginPage.Content.ReadAsStringAsync()).Groups[1].Value);
        var login = await SendAsync(http, HttpMethod.Post, "/account/login", jar, new()
        {
            ["Email"] = "user@xpress.local", ["Password"] = "User@123", ["__RequestVerificationToken"] = token
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var oldCookie = new Dictionary<string, string>(jar);
        _app.Clock.Advance(TimeSpan.FromMinutes(16)); // past the 15-minute access token

        for (var i = 0; i < 4; i++)
        {
            var response = await SendAsync(http, HttpMethod.Get, "/orders", new Dictionary<string, string>(oldCookie), updateFrom: jar);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Had each request refreshed on its own, AuthService would have treated the second as a
        // replayed refresh token and revoked the session. It is still alive:
        _app.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(http, HttpMethod.Get, "/orders", jar)).StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string url,
        Dictionary<string, string> cookies, Dictionary<string, string>? form = null, Dictionary<string, string>? updateFrom = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        var response = await http.SendAsync(request);

        var jar = updateFrom ?? cookies;
        foreach (var header in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
        {
            var pair = header.Split(';')[0];
            var eq = pair.IndexOf('=');
            var (name, value) = (pair[..eq], pair[(eq + 1)..]);
            if (value.Length == 0 || header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)) jar.Remove(name);
            else jar[name] = value;
        }
        return response;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    [Fact]
    public async Task Deactivated_users_are_signed_out_when_their_token_is_next_refreshed()
    {
        var user = await _app.SignedInAsync("user@xpress.local", "User@123");
        var admin = await _app.SignedInAsync("admin@xpress.local", "Admin@123");
        var userId = UserId(await admin.GetHtmlAsync("/admin/users?search=user%40"));
        await admin.SubmitAsync("/admin/users", $"/admin/users/{userId}/deactivate", new());

        _app.Clock.Advance(TimeSpan.FromMinutes(16));
        var response = await user.GetAsync("/orders");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/login", response.Headers.Location?.PathAndQuery);

        var login = await _app.NewBrowser().SubmitAsync("/account/login", "/account/login",
            new() { ["Email"] = "user@xpress.local", ["Password"] = "User@123" });
        Assert.Contains("This account has been deactivated.", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sign_out_ends_the_session()
    {
        var browser = await _app.SignedInAsync("user@xpress.local", "User@123");
        var signOut = await browser.SubmitAsync("/orders", "/account/logout", new());
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/orders")).StatusCode);
    }

    [Fact]
    public async Task Unknown_pages_show_the_site_not_found_page()
    {
        // Anonymous visitors are sent to sign in first (fallback policy), so this needs a signed-in user.
        var browser = await _app.SignedInAsync("user@xpress.local", "User@123");
        var response = await browser.GetAsync("/no-such-page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Page not found", html);
        Assert.Contains("Xpress Orders", html);
    }

    [Fact]
    public async Task Posts_without_an_antiforgery_token_show_the_site_error_page()
    {
        var response = await _app.NewBrowser().Client.PostAsync("/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "user@xpress.local", ["Password"] = "User@123" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Bad request", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unreachable_services_show_the_site_error_page_instead_of_the_exception()
    {
        using var app = _app.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient<OrdersApiClient>().ConfigurePrimaryHttpMessageHandler(() => new UnreachableHandler())));
        var browser = new Browser(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        await browser.SubmitAsync("/account/login", "/account/login", new() { ["Email"] = "user@xpress.local", ["Password"] = "User@123" });

        var response = await browser.GetAsync("/orders");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("We couldn&#x27;t reach one of our services.", html);
        Assert.DoesNotContain(nameof(HttpRequestException), html);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }

    private static async Task<long> CreateOrderAsync(Browser browser, string product)
    {
        var response = await browser.SubmitAsync("/orders/new", "/orders/new",
            new() { ["ProductName"] = product, ["Quantity"] = "2", ["UnitPrice"] = "9.99" });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return long.Parse(OrderLink().Match(response.Headers.Location!.OriginalString).Groups[1].Value);
    }

    private static string UserId(string usersPageHtml) => EditLink().Match(usersPageHtml).Groups[1].Value;

    [GeneratedRegex(@"/orders/(\d+)$")]
    private static partial Regex OrderLink();

    [GeneratedRegex(@"/admin/users/([^/""]+)/edit")]
    private static partial Regex EditLink();
}
