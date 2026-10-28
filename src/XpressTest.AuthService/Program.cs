using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Data;
using XpressTest.AuthService.Data.Queries;
using XpressTest.AuthService.Models;
using XpressTest.AuthService.Services;
using XpressTest.AuthService.Tokens;
using XpressTest.Shared.Auth;
using XpressTest.Shared.Data;
using XpressTest.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

IdGenerator.Configure(builder.Configuration.GetValue<int?>("Ids:WorkerId")
                      ?? throw new InvalidOperationException("Ids:WorkerId is not configured."));

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseConfiguredDatabase(builder.Configuration, "AuthDb"));
builder.Services.AddScoped<UserQueries>();
builder.Services.AddScoped<UserManagementService>();

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<long>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 6;
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager<ApplicationSignInManager>()
    .AddDefaultTokenProviders();

// Re-validate the security stamp on every request so deactivation and role changes take effect immediately.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.SlidingExpiration = true;
});

// Token issuing. Other services validate these tokens through /.well-known/jwks.json.
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .Validate(o => Uri.IsWellFormedUriString(o.Issuer, UriKind.Absolute), "Jwt:Issuer must be the absolute AuthService URL.")
    .Validate(o => o.Clients.All(c => c.AllowedScopes.All(o.ApiScopes.ContainsKey)), "Every client scope must be listed in Jwt:ApiScopes.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddScoped<TokenService>();

// /api/* authenticates with this service's own bearer tokens (401/403, never a login redirect);
// every other path is the browser portal and uses the Identity cookie.
const string SelectorScheme = "BearerOrCookie";
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = SelectorScheme;
        options.DefaultAuthenticateScheme = SelectorScheme;
        options.DefaultChallengeScheme = SelectorScheme;
        options.DefaultForbidScheme = SelectorScheme;
    })
    .AddPolicyScheme(SelectorScheme, null, options =>
        options.ForwardDefaultSelector = context => context.Request.Path.StartsWithSegments("/api")
            ? JwtBearerDefaults.AuthenticationScheme
            : IdentityConstants.ApplicationScheme)
    .AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<SigningKeyProvider, Microsoft.Extensions.Options.IOptions<JwtOptions>>((options, keys, jwt) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = XpressAudiences.AuthService,
            IssuerSigningKey = keys.PublicKey,
            ValidTypes = [XpressTokenTypes.AccessToken],
            NameClaimType = XpressClaims.Name,
            RoleClaimType = XpressClaims.Role
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AppPolicies.RequireAdmin, p => p.RequireRole(AppRoles.Admin, AppRoles.SuperAdmin))
    .AddPolicy(AppPolicies.RequireSuperAdmin, p => p.RequireRole(AppRoles.SuperAdmin))
    // Everything requires a signed-in user unless explicitly marked [AllowAnonymous].
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddProblemDetails();
builder.Services
    .AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

await DbSeeder.SeedAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

namespace XpressTest.AuthService
{
    /// <summary>Entry-point marker for WebApplicationFactory in the tests.</summary>
    public sealed class AuthServiceEntryPoint;
}
