using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Divakar.FactoryOS.Application.Auth;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class AccountSecurityTests(FactoryOsApp app)
{
    private async Task<(string Email, Guid UserId)> NewUserAsync(HttpClient admin)
    {
        var email = $"u{Guid.NewGuid():N}"[..12] + "@example.test";
        var r = await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Line Lead", password = FactoryOsApp.Password, roles = new[] { "operator" } });
        r.EnsureSuccessStatusCode();
        return (email, (await r.Content.ReadFromJsonAsync<IdResponse>())!.UserId);
    }

    private async Task<HttpResponseMessage> Login(string email, string password) =>
        await app.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    [Fact]
    public async Task Password_change_and_reset_links_end_old_sessions()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var (email, userId) = await NewUserAsync(admin);
        var first = await (await Login(email, FactoryOsApp.Password)).Content.ReadFromJsonAsync<AuthResponse>();
        var user = app.CreateClient();
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first!.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "wrong-password-1", newPassword = "New-Password-2026" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = FactoryOsApp.Password, newPassword = "short1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = FactoryOsApp.Password, newPassword = "New-Password-2026" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(email, FactoryOsApp.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(email, "New-Password-2026")).StatusCode);

        // forgot password: the same answer whether or not the address exists
        Assert.Equal(HttpStatusCode.Accepted, (await app.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot", new { email })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await app.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot", new { email = "nobody@nowhere.test" })).StatusCode);

        // no mail server here: the admin makes a one-time link instead
        var link = await (await admin.PostAsync($"/api/v1/users/{userId}/reset-link", null)).Content.ReadFromJsonAsync<JsonElement>();
        var url = link.GetProperty("url").GetString()!;
        Assert.Contains("/reset?token=", url);
        var token = url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];
        var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/v1/auth/reset", new { token, newPassword = "weak" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/v1/auth/reset", new { token = (token[0] == '0' ? "1" : "0") + token[1..], newPassword = "Reset-Password-2026" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/v1/auth/reset", new { token, newPassword = "Reset-Password-2026" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/v1/auth/reset", new { token, newPassword = "Again-Password-2026" })).StatusCode);   // once only
        Assert.Equal(HttpStatusCode.OK, (await Login(email, "Reset-Password-2026")).StatusCode);

        // an admin of another company cannot touch this user
        var (_, _, stranger) = await app.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/v1/users/{userId}/reset-link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/v1/users/{userId}/reset-2fa", null)).StatusCode);
    }

    [Fact]
    public async Task Two_step_sign_in_with_an_authenticator_app_and_recovery_codes()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var (email, userId) = await NewUserAsync(admin);
        var user = await app.SignInAsync(email);

        Assert.False((await user.GetFromJsonAsync<JsonElement>("/api/v1/auth/2fa")).GetProperty("enabled").GetBoolean());
        var setup = await (await user.PostAsync("/api/v1/auth/2fa/setup", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = Totp.FromBase32(setup.GetProperty("secret").GetString()!);
        Assert.StartsWith("otpauth://totp/Divakar%20FactoryOS:", setup.GetProperty("otpAuthUri").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/v1/auth/2fa/enable", new { code = "000000" })).StatusCode);
        var step = Totp.Step(DateTime.UtcNow);
        var enabled = await user.PostAsJsonAsync("/api/v1/auth/2fa/enable", new { code = Totp.Code(secret, step) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var recovery = (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, recovery.Count);
        Assert.Equal(10, (await user.GetFromJsonAsync<JsonElement>("/api/v1/auth/2fa")).GetProperty("recoveryCodesLeft").GetInt32());

        // the password alone no longer gives a session
        async Task<string> Challenge()
        {
            var r = await (await Login(email, FactoryOsApp.Password)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(r.GetProperty("mfaRequired").GetBoolean());
            Assert.False(r.TryGetProperty("accessToken", out _));
            return r.GetProperty("mfaToken").GetString()!;
        }
        var anon = app.CreateClient();
        var mfa = await Challenge();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login/2fa", new { mfaToken = mfa, code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login/2fa", new { mfaToken = mfa, code = Totp.Code(secret, step) })).StatusCode);   // already used
        var ok = await anon.PostAsJsonAsync("/api/v1/auth/login/2fa", new { mfaToken = mfa, code = Totp.Code(secret, step + 1) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ok.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login/2fa", new { mfaToken = mfa, code = Totp.Code(secret, step + 1) })).StatusCode);   // challenge spent

        // phone lost: a recovery code works once
        var viaRecovery = await anon.PostAsJsonAsync("/api/v1/auth/login/2fa", new { mfaToken = await Challenge(), code = recovery[0].ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.OK, viaRecovery.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login/2fa", new { mfaToken = await Challenge(), code = recovery[0] })).StatusCode);

        // too many wrong codes end the challenge
        var guessed = await Challenge();
        for (var i = 0; i < 5; i++)
        {
            await anon.PostAsJsonAsync("/api/v1/auth/login/2fa", new { mfaToken = guessed, code = $"00000{i}" });
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login/2fa", new { mfaToken = guessed, code = recovery[1] })).StatusCode);

        // turning it off needs the password and a code; an admin can also reset it
        var session = app.CreateClient();
        session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await viaRecovery.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.PostAsJsonAsync("/api/v1/auth/2fa/disable", new { password = "Wrong-Password-1", code = recovery[2] })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/users/{userId}/reset-2fa", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(email, FactoryOsApp.Password)).StatusCode);
        Assert.True((await (await Login(email, FactoryOsApp.Password)).Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("accessToken", out _));
    }
}
