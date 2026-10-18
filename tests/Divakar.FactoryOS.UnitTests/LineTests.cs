using System.Security.Cryptography;
using System.Text;
using Divakar.FactoryOS.Application.Notifications;

namespace Divakar.FactoryOS.UnitTests;

public sealed class LineTests
{
    private const string User = "U0123456789abcdef0123456789abcdef";
    private const string Group = "Cfedcba9876543210fedcba9876543210";

    private static string Sign(string secret, byte[] body) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void Webhook_signature_is_checked_against_the_channel_secret()
    {
        var body = Encoding.UTF8.GetBytes("""{"destination":"x","events":[]}""");
        Assert.True(LineRules.SignatureValid("s3cret", body, Sign("s3cret", body)));
        Assert.False(LineRules.SignatureValid("other", body, Sign("s3cret", body)));
        Assert.False(LineRules.SignatureValid("s3cret", body, null));
        Assert.False(LineRules.SignatureValid("s3cret", body, "not base64!"));
    }

    [Fact]
    public void Users_and_groups_that_wrote_to_the_bot_are_found()
    {
        var body = Encoding.UTF8.GetBytes($$$"""
            {"events":[
              {"type":"message","source":{"type":"user","userId":"{{{User}}}"},"message":{"type":"text","text":"hello"}},
              {"type":"join","source":{"type":"group","groupId":"{{{Group}}}"}},
              {"type":"follow","source":{"type":"user","userId":"not-an-id"}}
            ]}
            """);
        var s = LineRules.Sources(body);
        Assert.Equal(2, s.Count);
        Assert.Equal((User, "user", "hello"), s[0]);
        Assert.Equal((Group, "group", (string?)null), s[1]);
    }

    [Fact]
    public void Recipient_ids_must_look_like_line_ids()
    {
        Assert.Matches(LineRules.RecipientId(), User);
        Assert.Matches(LineRules.RecipientId(), Group);
        Assert.DoesNotMatch(LineRules.RecipientId(), "someone@example.com");
        Assert.Equal([User, Group], LineRules.Parse($"{User}, {Group};{User}"));
    }
}
