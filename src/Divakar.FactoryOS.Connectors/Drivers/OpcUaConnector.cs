using System.Globalization;
using Divakar.FactoryOS.Application.Connectivity;
using Opc.Ua;
using Opc.Ua.Client;
using DomainQuality = Divakar.FactoryOS.Domain.Plant.Quality;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>
/// OPC UA client. Endpoint "opc.tcp://host:4840"; settings {"username", "password", "acceptUntrusted": false}.
/// Tag address = NodeId, e.g. "ns=2;s=Line1.Temperature" or "ns=3;i=1005". All tags are read in one request.
/// This phase connects with security mode None (anonymous or username); signed/encrypted sessions need
/// certificate management and come with the hardening phase.
/// </summary>
public sealed class OpcUaConnector(AssetBinding asset) : IConnector
{
    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });
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

    public async Task ConnectAsync(CancellationToken ct)
    {
        var s = new Settings(asset.Settings);
        var config = new ApplicationConfiguration
        {
            ApplicationName = "Divakar FactoryOS",
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:DivakarFactoryOS",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier(),
                AutoAcceptUntrustedCertificates = s.Bool("acceptUntrusted", false),
                RejectSHA1SignedCertificates = true,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
        };
        config.CertificateValidator = new CertificateValidator(Telemetry);
        var url = asset.Endpoint ?? throw new FormatException("The asset has no endpoint (opc.tcp://host:4840).");
        var endpoint = await CoreClientUtils.SelectEndpointAsync(config, url, false, 15_000, Telemetry, ct);
        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(config));
        IUserIdentity identity = s.String("username") is { } user
            ? new UserIdentity(user, System.Text.Encoding.UTF8.GetBytes(s.String("password") ?? ""))
            : new UserIdentity(new AnonymousIdentityToken());
        session = await new DefaultSessionFactory(Telemetry).CreateAsync(config, configured, false, false,
            $"FactoryOS {asset.AssetCode}", 60_000, identity, null, ct);
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
