using System.Net;
using System.Net.Http.Json;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class AuthTests(FactoryOsApp app)
{
    [Fact]
    public async Task Health_endpoint_answers()
    {
        var res = await app.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Setup_runs_once_and_operator_gets_platform_permissions()
    {
        var op = await app.OperatorAsync();
        var again = await app.CreateClient().PostAsJsonAsync("/api/v1/auth/setup",
            new { email = "second@x.test", displayName = "Second", password = FactoryOsApp.Password });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var me = await op.GetFromJsonAsync<MeDto>("/api/v1/auth/me");
        Assert.True(me!.IsSuperAdmin);
        Assert.Contains("platform.tenants.manage", me.Permissions);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_locks_after_five_attempts()
    {
        var (_, email, _) = await app.NewTenantAsync();
        var client = app.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var bad = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong-password-123" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
        // now locked: even the right password is refused
        var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = FactoryOsApp.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("Too many failed attempts", await locked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_email_gets_the_same_answer_as_a_wrong_password()
    {
        var res = await app.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@x.test", password = "whatever-123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("Sign-in failed", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refresh_token_rotates_and_reuse_ends_the_session()
    {
        var (_, email, _) = await app.NewTenantAsync();
        var client = app.CreateClient();
        var first = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = FactoryOsApp.Password }))
            .Content.ReadFromJsonAsync<AuthResponse>();

        var second = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first!.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var rotated = await second.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotEqual(first.RefreshToken, rotated!.RefreshToken);
        // the replacement is used, so the session has clearly moved on
        var third = await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated.RefreshToken }))
            .Content.ReadFromJsonAsync<AuthResponse>();

        // the first token again: treated as theft, so the whole family (including the newest token) is revoked
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var afterReuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = third!.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task A_refresh_whose_response_was_lost_can_be_retried()
    {
        var (_, email, _) = await app.NewTenantAsync();
        var client = app.CreateClient();
        var first = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = FactoryOsApp.Password }))
            .Content.ReadFromJsonAsync<AuthResponse>();
        // the browser sent this refresh but navigated away before storing the answer
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first!.RefreshToken })).StatusCode);

        var retry = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var next = await retry.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = next!.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var (_, email, _) = await app.NewTenantAsync();
        var client = app.CreateClient();
        var auth = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = FactoryOsApp.Password }))
            .Content.ReadFromJsonAsync<AuthResponse>();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = auth!.RefreshToken })).StatusCode);
        var res = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Calls_without_a_token_are_refused()
    {
        var res = await app.CreateClient().GetAsync("/api/v1/plant/tree");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Suspended_tenant_cannot_sign_in()
    {
        var (tenantId, email, _) = await app.NewTenantAsync();
        var op = await app.OperatorAsync();
        var res = await op.PatchAsJsonAsync($"/api/v1/platform/tenants/{tenantId}", new { status = "Suspended" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var login = await app.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password = FactoryOsApp.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Contains("subscription is not active", await login.Content.ReadAsStringAsync());
    }
}
