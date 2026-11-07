using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Divakar.FactoryOS.Application.Connectivity;

namespace Divakar.FactoryOS.Edge;

public sealed class EdgeOptions
{
    /// <summary>The FactoryOS API, e.g. https://factory.example.com (the agent calls {Url}/api/v1/edge/...).</summary>
    public string Url { get; set; } = "http://localhost:5080";

    /// <summary>The agent key shown once when the agent was created (Connectivity → Edge agents).</summary>
    public string Key { get; set; } = "";

    /// <summary>Folder for the reading buffer and the cached configuration.</summary>
    public string DataPath { get; set; } = "data";

    public int BufferMb { get; set; } = 512;
    public int BatchSize { get; set; } = 2000;
    public int ConfigRefreshSeconds { get; set; } = 300;
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>Run the OT detectors next to the machines and send their findings.</summary>
    public bool LocalAi { get; set; } = true;

    /// <summary>Optional Unified Namespace: publish every reading to this MQTT broker (host:port), retained, as {prefix}/{asset}/{tag}.</summary>
    public string? UnsBroker { get; set; }
    public string UnsPrefix { get; set; } = "factoryos";
}

/// <summary>Talks to the FactoryOS API with the agent key.</summary>
public sealed class CloudClient(HttpClient http, EdgeOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var msg = new HttpRequestMessage(method, $"{options.Url.TrimEnd('/')}/api/v1/edge/{path}");
        msg.Headers.Add("X-Edge-Key", options.Key);
        if (body is not null)
        {
            msg.Content = JsonContent.Create(body, options: Json);
        }
        return msg;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage msg, CancellationToken ct)
    {
        var res = await http.SendAsync(msg, ct);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase}: {(text.Length > 300 ? text[..300] : text)}", null, res.StatusCode);
        }
        return res;
    }

    public async Task<EdgeConfig> ConfigAsync(CancellationToken ct)
    {
        using var res = await SendAsync(Request(HttpMethod.Get, "config"), ct);
        return (await res.Content.ReadFromJsonAsync<EdgeConfig>(Json, ct))!;
    }

    public async Task<EdgeUploadResult> UploadAsync(EdgeUpload upload, CancellationToken ct)
    {
        using var res = await SendAsync(Request(HttpMethod.Post, "upload", upload), ct);
        return (await res.Content.ReadFromJsonAsync<EdgeUploadResult>(Json, ct))!;
    }

    public async Task HeartbeatAsync(EdgeHeartbeat h, CancellationToken ct)
    {
        using var _ = await SendAsync(Request(HttpMethod.Post, "heartbeat", h), ct);
    }

    public async Task InsightsAsync(IReadOnlyList<EdgeInsight> list, CancellationToken ct)
    {
        using var _ = await SendAsync(Request(HttpMethod.Post, "insights", list), ct);
    }
}

/// <summary>
/// The last configuration the cloud sent, kept on disk so the agent starts and keeps reading the machines when the cloud cannot be
/// reached. It holds device connection settings (passwords included), so it is encrypted with AES-GCM under a key derived from the
/// agent key.
/// </summary>
public sealed class ConfigCache(string path, string agentKey)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private byte[] Key() => SHA256.HashData(Encoding.UTF8.GetBytes("factoryos-edge-config|" + agentKey));

    public void Save(EdgeConfig config)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(config, Json);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Key(), 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, [.. nonce, .. tag, .. cipher]);
        File.Move(tmp, path, overwrite: true);
    }

    public EdgeConfig? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var all = File.ReadAllBytes(path);
            var plain = new byte[all.Length - 28];
            using (var aes = new AesGcm(Key(), 16))
            {
                aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
            }
            return JsonSerializer.Deserialize<EdgeConfig>(plain, Json);
        }
        catch (CryptographicException)
        {
            return null;   // the agent key changed: the old cache cannot be read and is ignored
        }
    }
}
