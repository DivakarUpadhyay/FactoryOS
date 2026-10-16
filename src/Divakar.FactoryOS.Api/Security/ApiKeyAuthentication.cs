using System.Security.Claims;
using System.Text.Encodings.Web;
using Divakar.FactoryOS.Application.Platform;
using Divakar.FactoryOS.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Divakar.FactoryOS.Api.Security;

/// <summary>
/// Integration keys (X-Api-Key header) for ERP, BI and other systems: read-only access to one company's data.
/// A key may only make GET requests; it carries the read permissions of every module.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IServiceScopeFactory scopes) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string Header = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var values) || string.IsNullOrWhiteSpace(values.ToString()))
        {
            return AuthenticateResult.NoResult();
        }
        if (!HttpMethods.IsGet(Request.Method))
        {
            return AuthenticateResult.Fail("API keys are read-only.");
        }
        await using var scope = scopes.CreateAsyncScope();
        var owner = await scope.ServiceProvider.GetRequiredService<IApiKeyRepository>().ResolveAsync(ApiKeyService.Hash(values.ToString()), Context.RequestAborted);
        if (owner is null || owner.TenantStatus is "Suspended" or "Cancelled")
        {
            return AuthenticateResult.Fail("Unknown, revoked or suspended API key.");
        }
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, Guid.Empty.ToString()),
            new(ClaimNames.Tenant, owner.TenantId.ToString()),
            new("name", "API key"),
        };
        claims.AddRange(ApiKeyService.Permissions.Select(p => new Claim(ClaimNames.Permission, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "name", null));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
