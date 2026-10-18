using Divakar.FactoryOS.Connectors.Drivers;
using Opc.Ua;

namespace Divakar.FactoryOS.UnitTests;

public sealed class OpcUaSecurityTests
{
    private static EndpointDescription E(MessageSecurityMode mode, string policy, byte level) =>
        new() { EndpointUrl = "opc.tcp://plc:4840", SecurityMode = mode, SecurityPolicyUri = SecurityPolicies.BaseUri + policy, SecurityLevel = level };

    private static readonly EndpointDescription[] Offered =
    [
        E(MessageSecurityMode.None, "None", 0),
        E(MessageSecurityMode.Sign, "Basic256Sha256", 3),
        E(MessageSecurityMode.SignAndEncrypt, "Basic256Sha256", 13),
        E(MessageSecurityMode.SignAndEncrypt, "Aes256_Sha256_RsaPss", 15),
    ];

    [Fact]
    public void The_requested_mode_and_policy_are_matched()
    {
        Assert.Equal(MessageSecurityMode.None, OpcUaSecurity.Choose(Offered, "None", null).SecurityMode);
        Assert.Equal(MessageSecurityMode.Sign, OpcUaSecurity.Choose(Offered, "Sign", null).SecurityMode);
        Assert.Equal("Aes256_Sha256_RsaPss", OpcUaSecurity.PolicyName(OpcUaSecurity.Choose(Offered, "SignAndEncrypt", null).SecurityPolicyUri));
        Assert.Equal("Basic256Sha256", OpcUaSecurity.PolicyName(OpcUaSecurity.Choose(Offered, "SignAndEncrypt", "basic256sha256").SecurityPolicyUri));
        Assert.Equal(15, OpcUaSecurity.Choose(Offered, "Best", null).SecurityLevel);
    }

    [Fact]
    public void A_mode_the_server_lacks_is_explained()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OpcUaSecurity.Choose(Offered[..2], "SignAndEncrypt", null));
        Assert.Contains("It offers: None/None, Sign/Basic256Sha256", ex.Message);
        Assert.Throws<FormatException>(() => OpcUaSecurity.Choose(Offered, "Encrypt", null));
    }
}
