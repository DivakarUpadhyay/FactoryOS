using System.Globalization;
using Divakar.FactoryOS.Application.Connectivity;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using DomainQuality = Divakar.FactoryOS.Domain.Plant.Quality;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>Where FactoryOS keeps its OPC UA application certificate and the trusted / rejected server certificates.</summary>
public static class OpcUaPki
{
    public static string Root =>
        Environment.GetEnvironmentVariable("FOS_OPCUA_PKI") is { Length: > 0 } custom ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DivakarFactoryOS", "pki");

    public static string Trusted => Path.Combine(Root, "trusted");
    public static string Rejected => Path.Combine(Root, "rejected");
}

/// <summary>Chooses the server endpoint that matches the asset's security settings.</summary>
public static class OpcUaSecurity
{
    public static readonly string[] Modes = ["None", "Sign", "SignAndEncrypt", "Best"];

    public static EndpointDescription Choose(IReadOnlyList<EndpointDescription> endpoints, string mode, string? policy)
    {
        var tcp = endpoints.Where(e => e.EndpointUrl?.StartsWith("opc.tcp", StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (tcp.Count == 0)
        {
            throw new InvalidOperationException("The server offers no opc.tcp endpoint.");
        }
        string Offered() => string.Join(", ", tcp.Select(e => $"{e.SecurityMode}/{PolicyName(e.SecurityPolicyUri)}").Distinct());
        IEnumerable<EndpointDescription> pick = mode switch
        {
            "None" => tcp.Where(e => e.SecurityMode == MessageSecurityMode.None),
            "Sign" => tcp.Where(e => e.SecurityMode == MessageSecurityMode.Sign),
            "SignAndEncrypt" => tcp.Where(e => e.SecurityMode == MessageSecurityMode.SignAndEncrypt),
            "Best" => tcp,
            _ => throw new FormatException($"securityMode is one of {string.Join(", ", Modes)}."),
        };
        if (!string.IsNullOrWhiteSpace(policy) && mode != "None")
        {
            pick = pick.Where(e => string.Equals(PolicyName(e.SecurityPolicyUri), policy, StringComparison.OrdinalIgnoreCase));
        }
        return pick.OrderByDescending(e => e.SecurityLevel).ThenByDescending(e => e.SecurityMode).FirstOrDefault()
            ?? throw new InvalidOperationException($"The server does not offer {mode}{(string.IsNullOrWhiteSpace(policy) ? "" : $" with {policy}")}. It offers: {Offered()}.");
    }

    public static string PolicyName(string? uri) => uri is null ? "None" : uri[(uri.LastIndexOf('#') + 1)..];
}

/// <summary>
/// OPC UA client. Endpoint "opc.tcp://host:4840"; settings:
/// {"securityMode": "None" | "Sign" | "SignAndEncrypt" | "Best", "securityPolicy": "Basic256Sha256", "username", "password", "acceptUntrusted": false}.
/// Without securityMode the session is unsecured (None), as before. For Sign / SignAndEncrypt FactoryOS presents its own application
/// certificate (created once under %ProgramData%\DivakarFactoryOS\pki\own) and checks the server's: an unknown server certificate is
/// saved in pki\rejected and refused until it is moved to pki\trusted, or until acceptUntrusted is turned on.
/// Tag address = NodeId, e.g. "ns=2;s=Line1.Temperature" or "ns=3;i=1005". All tags are read in one request.
/// </summary>
public sealed class OpcUaConnector(AssetBinding asset) : IConnector
{
    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });
    private static readonly SemaphoreSlim CertificateLock = new(1, 1);
    private ISession? session;
    private readonly List<(TagBinding Tag, NodeId? Node)> tags = asset.Tags.Select(t =>
    {
        try
        {
            return (t, (NodeId?)NodeId.Parse(t.Address ?? ""));
        }
        catch (ServiceResultException)
        {
            return (t, null);
        }
        catch (ArgumentException)
        {
            return (t, null);
        }
    }).ToList();

    /// <summary>The security of the open session, for diagnostics and tests.</summary>
    public MessageSecurityMode? SecurityMode => session?.Endpoint.SecurityMode;
    public string? SecurityPolicy => session is null ? null : OpcUaSecurity.PolicyName(session.Endpoint.SecurityPolicyUri);

    private static async Task<ApplicationConfiguration> ConfigurationAsync(bool acceptUntrusted, CancellationToken ct)
    {
        await CertificateLock.WaitAsync(ct);   // several assets connect at once: create the certificate only once
        try
        {
            var app = new ApplicationInstance(Telemetry) { ApplicationName = "Divakar FactoryOS", ApplicationType = ApplicationType.Client };
            var config = await app.Build($"urn:{System.Net.Dns.GetHostName()}:DivakarFactoryOS", "urn:divakar:factoryos")
                .AsClient()
                .SetDefaultSessionTimeout(60_000)
                .AddSecurityConfiguration(ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates("CN=Divakar FactoryOS, O=Divakar, DC=localhost",
                    CertificateStoreType.Directory, OpcUaPki.Root), OpcUaPki.Root)
                .SetAutoAcceptUntrustedCertificates(acceptUntrusted)
                .SetRejectSHA1SignedCertificates(true)
                .SetMinimumCertificateKeySize(2048)
                .CreateAsync(ct);
            config.TransportQuotas.OperationTimeout = 15_000;
            await app.CheckApplicationInstanceCertificatesAsync(false, null, ct);
            return config;
        }
        finally
        {
            CertificateLock.Release();
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var s = new Settings(asset.Settings);
        var url = asset.Endpoint ?? throw new FormatException("The asset has no endpoint (opc.tcp://host:4840).");
        var mode = s.String("securityMode") ?? "None";
        var config = await ConfigurationAsync(s.Bool("acceptUntrusted", false), ct);

        EndpointDescriptionCollection endpoints;
        using (var discovery = await DiscoveryClient.CreateAsync(config, new Uri(url), DiagnosticsMasks.None, ct))
        {
            endpoints = await discovery.GetEndpointsAsync(null, ct);
        }
        var chosen = OpcUaSecurity.Choose(endpoints, mode, s.String("securityPolicy"));
        // servers often advertise their own host name; keep the address the user typed
        var typed = new Uri(url);
        chosen.EndpointUrl = new UriBuilder(chosen.EndpointUrl) { Host = typed.Host }.Uri.ToString();

        var configured = new ConfiguredEndpoint(null, chosen, EndpointConfiguration.Create(config));
        IUserIdentity identity = s.String("username") is { } user
            ? new UserIdentity(user, System.Text.Encoding.UTF8.GetBytes(s.String("password") ?? ""))
            : new UserIdentity(new AnonymousIdentityToken());
        try
        {
            session = await new DefaultSessionFactory(Telemetry).CreateAsync(config, configured, false, false,
                $"FactoryOS {asset.AssetCode}", 60_000, identity, null, ct);
        }
        catch (ServiceResultException ex) when (ex.StatusCode is StatusCodes.BadCertificateUntrusted or StatusCodes.BadSecurityChecksFailed
            or StatusCodes.BadCertificateChainIncomplete)
        {
            throw new InvalidOperationException(
                $"The server's certificate is not trusted yet ({ex.StatusCode}). It was saved in {OpcUaPki.Rejected}; move it to {OpcUaPki.Trusted}, " +
                "or set \"acceptUntrusted\": true for this asset. If the server rejects FactoryOS instead, trust FactoryOS's certificate on the server.", ex);
        }
    }

    public async Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct)
    {
        if (session is null || !session.Connected)
        {
            throw new InvalidOperationException("The OPC UA session is closed.");
        }
        var result = tags.Where(t => t.Node is null).Select(t => new RawValue(t.Tag.TagId, null, DomainQuality.Bad, now)).ToList();
        var valid = tags.Where(t => t.Node is not null).ToList();
        if (valid.Count == 0)
        {
            return result;
        }
        var ids = new ReadValueIdCollection(valid.Select(t => new ReadValueId { NodeId = t.Node, AttributeId = Attributes.Value }));
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Source, ids, ct);
        for (var i = 0; i < valid.Count; i++)
        {
            var dv = response.Results[i];
            var quality = StatusCode.IsGood(dv.StatusCode) ? DomainQuality.Good
                : StatusCode.IsUncertain(dv.StatusCode) ? DomainQuality.Uncertain : DomainQuality.Bad;
            var ts = dv.SourceTimestamp == DateTime.MinValue ? now : dv.SourceTimestamp.ToUniversalTime();
            result.Add(new RawValue(valid[i].Tag.TagId, ToDouble(dv.Value), quality, ts));
        }
        return result;
    }

    public static double? ToDouble(object? value) => value switch
    {
        null => null,
        bool b => b ? 1 : 0,
        string str => double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        DateTime => null,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _ => null,
    };

    public async ValueTask DisposeAsync()
    {
        if (session is not null)
        {
            await session.CloseAsync();
            session.Dispose();
        }
    }
}

public sealed class OpcUaConnectorFactory : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["opcua"];

    public IConnector Create(AssetBinding asset) => new OpcUaConnector(asset);
}
