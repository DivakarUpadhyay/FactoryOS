using System.Text;
using Divakar.FactoryOS.Application.Auth;

namespace Divakar.FactoryOS.UnitTests;

public sealed class TotpTests
{
    private static readonly byte[] Rfc = Encoding.ASCII.GetBytes("12345678901234567890");   // RFC 6238 test key (SHA-1)

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void Codes_match_the_rfc_test_vectors(long unixSeconds, string expected)
    {
        var step = Totp.Step(DateTime.UnixEpoch.AddSeconds(unixSeconds));
        Assert.Equal(expected, Totp.Code(Rfc, step, 8));
        Assert.Equal(expected[2..], Totp.Code(Rfc, step));
    }

    [Fact]
    public void One_step_of_drift_is_allowed_and_used_codes_are_refused()
    {
        var now = new DateTime(2026, 10, 18, 9, 0, 10, DateTimeKind.Utc);
        var step = Totp.Step(now);
        Assert.Equal(step, Totp.Verify(Rfc, Totp.Code(Rfc, step), now, null));
        Assert.Equal(step - 1, Totp.Verify(Rfc, Totp.Code(Rfc, step - 1), now, null));
        Assert.Equal(step + 1, Totp.Verify(Rfc, Totp.Code(Rfc, step + 1).Insert(3, " "), now, null));   // "123 456" as apps show it
        Assert.Null(Totp.Verify(Rfc, Totp.Code(Rfc, step - 2), now, null));
        Assert.Null(Totp.Verify(Rfc, Totp.Code(Rfc, step), now, lastUsedStep: step));                       // replay
        Assert.Null(Totp.Verify(Rfc, "12345", now, null));
    }

    [Fact]
    public void Secrets_survive_base32_and_the_link_has_what_apps_need()
    {
        var secret = Totp.NewSecret();
        Assert.Equal(secret, Totp.FromBase32(Totp.Base32(secret)));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(Rfc));
        var uri = Totp.Uri("Divakar FactoryOS", "a@b.c", Rfc);
        Assert.StartsWith("otpauth://totp/Divakar%20FactoryOS:a%40b.c?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", uri);
        var codes = Totp.NewRecoveryCodes();
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[a-z2-9]{4}-[a-z2-9]{4}$", c));
        Assert.Equal(Totp.Hash("ABCD-efgh "), Totp.Hash("abcd-efgh"));
    }
}
