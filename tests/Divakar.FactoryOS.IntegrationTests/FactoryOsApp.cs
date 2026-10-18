using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

/// <summary>
/// The real API against a throw-away database (DivakarFactoryOS_Test) on the local SQL Server.
/// The database is dropped and rebuilt by the migrations once per test run.
/// </summary>
public sealed class FactoryOsApp : WebApplicationFactory<Program>
{
    public const string Database = "DivakarFactoryOS_Test";
    public const string OperatorEmail = "operator@divakar-factoryos.test";
    public const string Password = "Test-Password-2026";

    public static readonly string ConnectionString =
        $@"Server=DIVAKARLAPTOP\DEVELOPERMSSQL;Database={Database};Integrated Security=true;TrustServerCertificate=true";

    public FactoryOsApp()
    {
        DropDatabase();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:FactoryOS", ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-for-integration-tests-only-0123456789");
        builder.UseSetting("Secrets:Key", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        builder.UseSetting("Line:ApiBase", LineStub.Instance.Url);
        builder.UseSetting("RateLimit:AuthPerMinute", "1000");
        builder.UseSetting("RateLimit:RefreshPerMinute", "1000");
        // The test server has no client address; present calls as coming from this machine (setup needs that).
        builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, LoopbackFilter>());
    }

    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) =>
            {
                ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                return nxt(ctx);
            });
            next(app);
        };
    }

    private static void DropDatabase()
    {
        using var con = new SqlConnection(ConnectionString.Replace($"Database={Database}", "Database=master"));
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            IF DB_ID('{Database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{Database}];
            END
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Signs in and returns a client that sends the access token.</summary>
    public async Task<HttpClient> SignInAsync(string email, string password = Password)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    private bool operatorReady;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The platform operator account, created through the first-time setup endpoint.</summary>
    public async Task<HttpClient> OperatorAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!operatorReady)
            {
                var res = await CreateClient().PostAsJsonAsync("/api/v1/auth/setup",
                    new { email = OperatorEmail, displayName = "Divakar (platform)", password = Password });
                if (res.StatusCode != HttpStatusCode.Conflict)
                {
                    res.EnsureSuccessStatusCode();
                }
                operatorReady = true;
            }
        }
        finally
        {
            gate.Release();
        }
        return await SignInAsync(OperatorEmail);
    }

    /// <summary>Runs the real ingestion engine against the test database for a few passes, starting now.</summary>
    public async Task RunEngineAsync(int passes, int secondsApart = 6)
    {
        using var scope = Services.CreateScope();
        var engine = new Divakar.FactoryOS.Application.Connectivity.IngestionEngine(
            scope.ServiceProvider.GetRequiredService<Divakar.FactoryOS.Application.Connectivity.IIngestionStore>(),
            scope.ServiceProvider.GetRequiredService<Divakar.FactoryOS.Application.Connectivity.ConnectorRegistry>(),
            new Divakar.FactoryOS.Application.Connectivity.IngestionOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Divakar.FactoryOS.Application.Connectivity.IngestionEngine>.Instance);
        var t = DateTime.UtcNow;
        for (var i = 0; i < passes; i++)
        {
            await engine.RunOnceAsync(t.AddSeconds(i * secondsApart), default);
        }
        await engine.DisposeAsync();
    }

    /// <summary>The bearer token a signed-in client sends.</summary>
    public static string TokenOf(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;

    /// <summary>A new tenant with its factory administrator; returns the admin's signed-in client.</summary>
    public async Task<(Guid TenantId, string AdminEmail, HttpClient Admin)> NewTenantAsync(string plan = "pro")
    {
        var op = await OperatorAsync();
        var code = "t" + Guid.NewGuid().ToString("N")[..10];
        var email = $"admin@{code}.test";
        var res = await op.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            code, name = $"Tenant {code}", planCode = plan, adminEmail = email, adminName = "Factory Admin", adminPassword = Password,
        });
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<IdResponse>())!.TenantId;
        return (id, email, await SignInAsync(email));
    }
}

public sealed record AuthResponse(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, MeDto User);
public sealed record MeDto(Guid UserId, string Email, bool IsSuperAdmin, Guid? TenantId, string? PlanCode, List<string> Modules, List<string> Permissions);
public sealed record IdResponse(Guid TenantId, Guid UserId, Guid NodeId, Guid AssetId, Guid RoleId, Guid TagId);

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<FactoryOsApp>
{
    public const string Name = "FactoryOS API";
}
