using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Infrastructure.Database;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Divakar.FactoryOS.Infrastructure.Security;

/// <summary>
/// Encrypts the secret values (password, token, secret, key) inside device connection settings with AES-256-GCM before they
/// are stored, and decrypts them only when a connector needs them. The key never goes into the database: it comes from
/// Secrets:Key (base64, 32 bytes) or a key file (Secrets:KeyFile, default %ProgramData%\DivakarFactoryOS\secrets.key) shared by
/// the API and the worker on the same machine, created on first use.
/// </summary>
public sealed class SecretProtector
{
    public const string Prefix = "enc:v1:";
    private readonly byte[] key;

    public SecretProtector(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The secrets key must be 32 bytes.");
        }
        this.key = key;
    }

    public static SecretProtector FromConfiguration(IConfiguration config)
    {
        if (config["Secrets:Key"] is { Length: > 0 } b64)
        {
            return new SecretProtector(Convert.FromBase64String(b64));
        }
        var path = config["Secrets:KeyFile"] is { Length: > 0 } p ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DivakarFactoryOS", "secrets.key");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                fs.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
            }
            catch (IOException) when (File.Exists(path))
            {
                // the other process (API or worker) created it at the same moment
            }
        }
        return new SecretProtector(Convert.FromBase64String(File.ReadAllText(path).Trim()));
    }

    public static bool IsSecretKey(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Equals("key", StringComparison.OrdinalIgnoreCase);

    public string Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Decrypt(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }
        var all = Convert.FromBase64String(value[Prefix.Length..]);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(12, plain.Length), all.AsSpan(12 + plain.Length, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static string? Transform(string? json, Func<string, string> f)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
        if (o is null)
        {
            return json;
        }
        foreach (var name in o.Select(p => p.Key).Where(IsSecretKey).ToList())
        {
            if (o[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 && s != SecretMask.Masked)
            {
                o[name] = f(s);
            }
        }
        return o.ToJsonString();
    }

    /// <summary>Encrypts plain secret values; values already encrypted are left as they are.</summary>
    public string? Protect(string? json) => Transform(json, s => s.StartsWith(Prefix, StringComparison.Ordinal) ? s : Encrypt(s));

    /// <summary>Decrypts secret values for a connector. A value that cannot be decrypted (wrong key) is left encrypted, so the device refuses it visibly.</summary>
    public string? Unprotect(string? json) => Transform(json, s =>
    {
        try
        {
            return Decrypt(s);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return s;
        }
    });

    /// <summary>One-time upgrade at startup: encrypts secrets stored in plain text by earlier versions.</summary>
    public int EncryptStoredSecrets(string connectionString)
    {
        using var con = new SqlConnection(connectionString);
        con.Open();
        con.Execute("EXEC sp_set_session_context N'System', 1;");
        var rows = con.Query<(Guid AssetId, string Settings)>(
            "SELECT AssetId, ConnectorSettings FROM plant.Assets WHERE ConnectorSettings IS NOT NULL AND ConnectorSettings NOT LIKE '%enc:v1:%'").ToList();
        var changed = 0;
        foreach (var (id, settings) in rows)
        {
            var protectedJson = Protect(settings);
            if (protectedJson != settings)
            {
                con.Execute("UPDATE plant.Assets SET ConnectorSettings = @protectedJson WHERE AssetId = @id", new { protectedJson, id });
                changed++;
            }
        }
        return changed;
    }
}
