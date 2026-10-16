using System.Security.Claims;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Divakar.FactoryOS.Api.Security;

/// <summary>The signed-in caller, read from the access token's claims.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId => Guid.TryParse(Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : Guid.Empty;

    public Guid? TenantId => Guid.TryParse(Principal?.FindFirstValue(ClaimNames.Tenant), out var id) ? id : null;

    public bool IsSuperAdmin => Principal?.FindFirstValue(ClaimNames.SuperAdmin) == "1";

    public IReadOnlySet<string> Permissions =>
        Principal?.FindAll(ClaimNames.Permission).Select(c => c.Value).ToHashSet() ?? [];

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public Guid RequireTenant() =>
        TenantId ?? throw AppException.Forbidden("This action belongs to a company account; platform operators have none.");
}

/// <summary>Policies named "perm:&lt;code&gt;" are created on demand: the caller needs that permission claim.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return await base.GetPolicyAsync(policyName);
        }
        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(ClaimNames.Permission, policyName[Prefix.Length..])
            .Build();
    }
}

public static class EndpointPermissions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(PermissionPolicyProvider.Prefix + permission);
}

/// <summary>Turns expected failures into RFC 7807 problem responses; anything else is a 500 without internals.</summary>
public sealed class AppExceptionHandler(IProblemDetailsService problems, ILogger<AppExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (exception is not AppException app)
        {
            log.LogError(exception, "Unhandled error on {Path}", http.Request.Path);
            return false;
        }
        http.Response.StatusCode = app.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.LimitReached => StatusCodes.Status402PaymentRequired,
            _ => StatusCodes.Status400BadRequest,
        };
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = { Title = app.Kind.ToString(), Detail = app.Message, Status = http.Response.StatusCode },
        });
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var h = context.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        return next(context);
    }
}
