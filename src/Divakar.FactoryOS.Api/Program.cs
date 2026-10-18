using System.Threading.RateLimiting;
using Divakar.FactoryOS.Api.Endpoints;
using Divakar.FactoryOS.Api.Live;
using Divakar.FactoryOS.Api.Security;
using Divakar.FactoryOS.Application;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Connectors;
using Divakar.FactoryOS.Infrastructure;
using Divakar.FactoryOS.Infrastructure.Database;
using Divakar.FactoryOS.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("FACTORYOS_");
builder.Host.UseWindowsService(o => o.ServiceName = "Divakar FactoryOS API");   // no effect unless installed as a Windows service
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddConnectors();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
// Users sign in with JWTs; other systems use read-only API keys. The policy scheme picks one per request.
builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = "Smart";
    o.DefaultChallengeScheme = "Smart";
})
.AddPolicyScheme("Smart", "JWT or API key", o => o.ForwardDefaultSelector = ctx =>
    ctx.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.Header) ? ApiKeyAuthenticationHandler.SchemeName : JwtBearerDefaults.AuthenticationScheme)
.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
.AddJwtBearer(o =>
{
    o.MapInboundClaims = false;   // keep "sub", "tid", "perm" as issued
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = JwtTokenService.SigningKey(jwt),
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "name",
    };
    // Browsers cannot set headers on WebSockets, so the live hub takes the token from the query string.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var token))
            {
                ctx.Token = token;
            }
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddSignalR();
if (builder.Configuration.GetValue("Live:Broadcast", true))
{
    builder.Services.AddHostedService<LiveBroadcaster>();
}
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddAuthorization();

// Password endpoints: 10 attempts per minute per IP address; token refresh: 120 per minute
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    void PerIp(string policy, int perMinute) => o.AddPolicy(policy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) }));
    PerIp(Endpoints.AuthRateLimit, builder.Configuration.GetValue("RateLimit:AuthPerMinute", 10));
    PerIp(Endpoints.RefreshRateLimit, builder.Configuration.GetValue("RateLimit:RefreshPerMinute", 120));
    PerIp(Endpoints.IngestRateLimit, builder.Configuration.GetValue("RateLimit:IngestPerMinute", 600));
});

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:3000"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials().WithExposedHeaders("Content-Disposition")));
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    Migrator.Run(app.Services.GetRequiredService<DbOptions>().ConnectionString);
    // earlier versions stored device passwords in plain text: encrypt them once
    var encrypted = app.Services.GetRequiredService<SecretProtector>().EncryptStoredSecrets(app.Services.GetRequiredService<DbOptions>().ConnectionString);
    if (encrypted > 0)
    {
        app.Logger.LogInformation("Encrypted the connection secrets of {Count} assets", encrypted);
    }
}

app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
else
{
    app.MapOpenApi();
}
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapFactoryOs();
app.MapHub<LiveHub>("/hubs/live");

await app.RunAsync();

/// <summary>Visible to the integration tests (WebApplicationFactory).</summary>
public partial class Program;
