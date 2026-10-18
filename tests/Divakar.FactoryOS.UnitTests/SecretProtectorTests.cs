using System.Security.Cryptography;
using System.Text.Json;
using Divakar.FactoryOS.Infrastructure.Security;

namespace Divakar.FactoryOS.UnitTests;

public sealed class SecretProtectorTests
{
    private static readonly SecretProtector P = new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Secrets_are_encrypted_and_other_settings_stay_readable()
    {
        var stored = P.Protect("""{"username":"plc","password":"S3cret!","unitId":1,"apiToken":"abc"}""")!;
        var o = JsonDocument.Parse(stored).RootElement;
        Assert.Equal("plc", o.GetProperty("username").GetString());
        Assert.Equal(1, o.GetProperty("unitId").GetInt32());
        Assert.StartsWith(SecretProtector.Prefix, o.GetProperty("password").GetString());
        Assert.StartsWith(SecretProtector.Prefix, o.GetProperty("apiToken").GetString());
        Assert.DoesNotContain("S3cret!", stored);

        var back = JsonDocument.Parse(P.Unprotect(stored)!).RootElement;
        Assert.Equal("S3cret!", back.GetProperty("password").GetString());
        Assert.Equal("abc", back.GetProperty("apiToken").GetString());
    }

    [Fact]
    public void Protecting_twice_does_not_double_encrypt_and_each_encryption_differs()
    {
        var once = P.Protect("""{"password":"x"}""");
        Assert.Equal(once, P.Protect(once));
        Assert.NotEqual(P.Encrypt("x"), P.Encrypt("x"));   // random nonce
    }

    [Fact]
    public void A_wrong_key_leaves_the_value_encrypted_instead_of_failing()
    {
        var stored = P.Protect("""{"password":"x"}""")!;
        var other = new SecretProtector(RandomNumberGenerator.GetBytes(32));
        Assert.StartsWith(SecretProtector.Prefix, JsonDocument.Parse(other.Unprotect(stored)!).RootElement.GetProperty("password").GetString());
        Assert.Throws<AuthenticationTagMismatchException>(() => other.Decrypt(JsonDocument.Parse(stored).RootElement.GetProperty("password").GetString()!));
    }
}
