using System.Security.Cryptography;
using System.Text;

namespace Divakar.FactoryOS.Application.Auth;

/// <summary>Time-based one-time passwords (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits) as used by authenticator apps.</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static long Step(DateTime utc) => (long)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds / StepSeconds);

    public static string Code(byte[] secret, long step, int digits = 6)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--, step >>= 8)
        {
            counter[i] = (byte)(step & 0xFF);
        }
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString(new string('0', digits));
    }

    /// <summary>
    /// The step a code belongs to, allowing one step of clock drift either way, or null. Steps at or before
    /// <paramref name="lastUsedStep"/> are refused, so a code seen over someone's shoulder cannot be replayed.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTime utc, long? lastUsedStep)
    {
        var clean = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (clean.Length != 6)
        {
            return null;
        }
        var now = Step(utc);
        for (var s = now - 1; s <= now + 1; s++)
        {
            if (s > (lastUsedStep ?? long.MinValue) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, s)), Encoding.ASCII.GetBytes(clean)))
            {
                return s;
            }
        }
        return null;
    }

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }
        return sb.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant().Where(c => c != '=' && c != ' '))
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0)
            {
                throw new FormatException("Not base32.");
            }
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. bytes];
    }

    /// <summary>The otpauth:// link an authenticator app reads from the QR code.</summary>
    public static string Uri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

    /// <summary>Ten one-time recovery codes like "k7f2-9qxm", for when the phone is lost.</summary>
    public static IReadOnlyList<string> NewRecoveryCodes()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        return [.. Enumerable.Range(0, 10).Select(_ =>
        {
            var s = RandomNumberGenerator.GetString(chars, 8);
            return $"{s[..4]}-{s[4..]}";
        })];
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()))).ToLowerInvariant();
}
