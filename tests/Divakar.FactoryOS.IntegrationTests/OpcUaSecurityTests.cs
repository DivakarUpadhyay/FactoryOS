using System.Net;
using System.Net.Sockets;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Connectors.Drivers;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace Divakar.FactoryOS.IntegrationTests;

/// <summary>A real OPC UA server in the test process, offering only signed and encrypted endpoints.</summary>
public sealed class OpcUaSecurityTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fos-opcua-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ITelemetryContext telemetry = DefaultTelemetry.Create(_ => { });
    private ApplicationInstance? serverApp;
    private StandardServer? server;
    private string url = "";

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        url = $"opc.tcp://localhost:{port}/plc";

        var pki = Path.Combine(root, "server");
        serverApp = new ApplicationInstance(telemetry) { ApplicationName = "Test PLC", ApplicationType = ApplicationType.Server };
        await serverApp.Build("urn:localhost:TestPlc", "urn:test:plc")
            .AsServer([url])
            .AddSignAndEncryptPolicies()
            .AddSignPolicies()
            .AddSecurityConfiguration(ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates("CN=Test PLC, DC=localhost", CertificateStoreType.Directory, pki), pki)
            .SetAutoAcceptUntrustedCertificates(true)   // the PLC trusts any client; the client side is what is under test
            .CreateAsync();
        await serverApp.CheckApplicationInstanceCertificatesAsync(false, null);
        server = new StandardServer();
        await serverApp.StartAsync(server);
        Environment.SetEnvironmentVariable("FOS_OPCUA_PKI", Path.Combine(root, "client"));
    }

    public async Task DisposeAsync()
    {
        if (serverApp is not null)
        {
            await serverApp.StopAsync();
        }
        Environment.SetEnvironmentVariable("FOS_OPCUA_PKI", null);
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
            // certificate files may still be locked for a moment
        }
    }

    private AssetBinding Binding(string settings) => new(Guid.NewGuid(), Guid.NewGuid(), "plc-1", "plc", "opcua", url, settings, 1000, null,
        [new TagBinding(Guid.NewGuid(), "state", "machine_state", "gauge", "ns=0;i=2259", 1, 0, 0, null, null)]);   // Server_ServerStatus_State: 0 = running

    [Fact]
    public async Task Encrypted_session_reads_values_and_an_unknown_server_certificate_is_refused_until_trusted()
    {
        // the server offers no unsecured endpoint: asking for None says what it does offer
        await using (var none = new OpcUaConnector(Binding("{}")))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => none.ConnectAsync(default));
            Assert.Contains("SignAndEncrypt/Basic256Sha256", ex.Message);
        }

        // the PLC's certificate is unknown: refused, and parked in the rejected folder for an admin to trust
        await using (var strict = new OpcUaConnector(Binding("""{"securityMode":"SignAndEncrypt","securityPolicy":"Basic256Sha256"}""")))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => strict.ConnectAsync(default));
            Assert.Contains("not trusted", ex.Message);
        }
        var rejected = Directory.GetFiles(Path.Combine(root, "client", "rejected"), "*.der", SearchOption.AllDirectories);
        Assert.NotEmpty(rejected);

        // the admin trusts it: the encrypted session works
        var trusted = Path.Combine(root, "client", "trusted", "certs");
        Directory.CreateDirectory(trusted);
        foreach (var file in rejected)
        {
            File.Copy(file, Path.Combine(trusted, Path.GetFileName(file)), true);
        }
        await using (var secure = new OpcUaConnector(Binding("""{"securityMode":"SignAndEncrypt","securityPolicy":"Basic256Sha256"}""")))
        {
            await secure.ConnectAsync(default);
            Assert.Equal(MessageSecurityMode.SignAndEncrypt, secure.SecurityMode);
            Assert.Equal("Basic256Sha256", secure.SecurityPolicy);
            var values = await secure.ReadAsync(DateTime.UtcNow, default);
            Assert.Equal(0, Assert.Single(values).Value);
            Assert.Equal(Divakar.FactoryOS.Domain.Plant.Quality.Good, values[0].Quality);
        }

        // "Best" picks the strongest endpoint the server has
        await using (var best = new OpcUaConnector(Binding("""{"securityMode":"Best"}""")))
        {
            await best.ConnectAsync(default);
            Assert.Equal(MessageSecurityMode.SignAndEncrypt, best.SecurityMode);
        }

        // FactoryOS created its own application certificate once
        var files = Directory.GetFiles(Path.Combine(root, "client"), "*", SearchOption.AllDirectories);
        Assert.True(files.Any(f => f.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", files));
    }
}
