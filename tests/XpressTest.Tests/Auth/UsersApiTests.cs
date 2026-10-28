using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using XpressTest.Tests.Infrastructure;

namespace XpressTest.Tests.Auth;

public sealed class UsersApiTests : IDisposable
{
    private readonly AuthServiceFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AsAsync(string email, string password)
    {
        var token = (await _factory.PasswordGrantAsync(email, password)).GetProperty("access_token").GetString();
        var client = _factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<HttpClient> AdminAsync() => AsAsync("admin@xpress.local", "Admin@123");
    private Task<HttpClient> SuperAsync() => AsAsync("superadmin@xpress.local", "SuperAdmin@123");

    private static async Task<JsonElement> UserAsync(HttpClient client, string email) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/users?search={Uri.EscapeDataString(email)}")).GetProperty("items")[0];

    [Fact]
    public async Task Requires_an_admin_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateApiClient().GetAsync("/api/users")).StatusCode);

        var user = await AsAsync("user@xpress.local", "User@123");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task List_tells_each_admin_what_they_may_do()
    {
        var admin = await AdminAsync();
        var page = await admin.GetFromJsonAsync<JsonElement>("/api/users?pageSize=10");
        Assert.Equal(["User"], page.GetProperty("assignableRoles").EnumerateArray().Select(r => r.GetString()));

        var byEmail = page.GetProperty("items").EnumerateArray().ToDictionary(u => u.GetProperty("email").GetString()!);
        Assert.True(byEmail["user@xpress.local"].GetProperty("canManage").GetBoolean());
        Assert.False(byEmail["user@xpress.local"].GetProperty("canDelete").GetBoolean());
        Assert.False(byEmail["superadmin@xpress.local"].GetProperty("canManage").GetBoolean());

        var super = await SuperAsync();
        var superPage = await super.GetFromJsonAsync<JsonElement>("/api/users?pageSize=10");
        Assert.Equal(["User", "Admin"], superPage.GetProperty("assignableRoles").EnumerateArray().Select(r => r.GetString()));
        Assert.True((await UserAsync(super, "admin@xpress.local")).GetProperty("canDelete").GetBoolean());
    }

    [Fact]
    public async Task Admins_create_users_but_not_admins()
    {
        var admin = await AdminAsync();

        var user = await admin.PostAsJsonAsync("/api/users", new { fullName = "U", email = "u1@x.local", password = "User@1234", role = "User" });
        Assert.Equal(HttpStatusCode.Created, user.StatusCode);

        var promoted = await admin.PostAsJsonAsync("/api/users", new { fullName = "A", email = "a1@x.local", password = "User@1234", role = "Admin" });
        Assert.Equal(HttpStatusCode.Forbidden, promoted.StatusCode);

        var super = await SuperAsync();
        var byS = await super.PostAsJsonAsync("/api/users", new { fullName = "A", email = "a2@x.local", password = "User@1234", role = "Admin" });
        Assert.Equal(HttpStatusCode.Created, byS.StatusCode);
    }

    [Fact]
    public async Task Invalid_input_returns_validation_problems()
    {
        var response = await (await AdminAsync()).PostAsJsonAsync("/api/users",
            new { fullName = "Dup", email = "user@xpress.local", password = "User@1234", role = "User" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is already taken", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_super_admins_delete_and_nobody_touches_superiors()
    {
        var admin = await AdminAsync();
        var userId = (await UserAsync(admin, "user@xpress.local")).GetProperty("id").GetInt64();
        var superId = (await UserAsync(admin, "superadmin@xpress.local")).GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync($"/api/users/{userId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/users/{superId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/users/{userId}/deactivate", null)).StatusCode);

        var super = await SuperAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await super.DeleteAsync($"/api/users/{userId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await super.GetAsync($"/api/users/{userId}")).StatusCode);
    }

    [Fact]
    public async Task Bulk_deactivate_skips_users_the_caller_cannot_manage()
    {
        var admin = await AdminAsync();
        var ids = new List<long>();
        foreach (var email in new[] { "user@xpress.local", "admin@xpress.local", "superadmin@xpress.local" })
            ids.Add((await UserAsync(admin, email)).GetProperty("id").GetInt64());

        var result = await (await admin.PostAsJsonAsync("/api/users/deactivate", new { ids })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("deactivated").GetInt32());
    }

    [Fact]
    public async Task Stats_are_available_to_admins()
    {
        var stats = await (await AdminAsync()).GetFromJsonAsync<JsonElement>("/api/users/stats");
        Assert.Equal(3, stats.GetProperty("total").GetInt32());
    }
}
