using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XpressTest.Shared.Auth;
using XpressTest.Web;
using XpressTest.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ServiceEndpoints>()
    .Bind(builder.Configuration.GetSection("Services"))
    .Validate(s => Uri.IsWellFormedUriString(s.Auth, UriKind.Absolute) && Uri.IsWellFormedUriString(s.Orders, UriKind.Absolute),
        "Services:Auth and Services:Orders must be absolute URLs.")
    .ValidateOnStart();
var endpoints = builder.Configuration.GetSection("Services").Get<ServiceEndpoints>() ?? new ServiceEndpoints();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<UserAccessTokenHandler>();

// Anonymous AuthService calls (sign-in, register, refresh, discovery) and the user-authenticated APIs.
builder.Services.AddHttpClient(ServiceEndpoints.AuthClient, c => c.BaseAddress = new Uri(WithSlash(endpoints.Auth)));
builder.Services.AddHttpClient<UsersApiClient>(c => c.BaseAddress = new Uri(WithSlash(endpoints.Auth)))
    .AddHttpMessageHandler<UserAccessTokenHandler>();
builder.Services.AddHttpClient<OrdersApiClient>(c => c.BaseAddress = new Uri(WithSlash(endpoints.Orders)))
    .AddHttpMessageHandler<UserAccessTokenHandler>();
builder.Services.AddSingleton<TokenClient>();
builder.Services.AddSingleton<UserSession>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "xpress.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/account/access-denied";
        // Matches AuthService's refresh-token lifetime; the access token inside is refreshed as needed.
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = context =>
            context.HttpContext.RequestServices.GetRequiredService<UserSession>().ValidatePrincipalAsync(context);
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(WebPolicies.Admin, p => p.RequireRole(XpressRoles.Admin, XpressRoles.SuperAdmin))
    .AddPolicy(WebPolicies.SuperAdmin, p => p.RequireRole(XpressRoles.SuperAdmin))
    // Every page needs a signed-in user unless marked [AllowAnonymous].
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<SessionExpiredFilter>();
});

var app = builder.Build();

// In every environment: unhandled exceptions (logged by the middleware) and empty error responses
// such as 404 or a failed antiforgery check render the site's own error page, never the framework's.
app.UseExceptionHandler("/error");
app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

static string WithSlash(string url) => url.EndsWith('/') ? url : url + "/";

namespace XpressTest.Web
{
    public static class WebPolicies
    {
        public const string Admin = "Admin";
        public const string SuperAdmin = "SuperAdmin";
    }

    /// <summary>Entry-point marker for WebApplicationFactory in the tests.</summary>
    public sealed class WebEntryPoint;
}
