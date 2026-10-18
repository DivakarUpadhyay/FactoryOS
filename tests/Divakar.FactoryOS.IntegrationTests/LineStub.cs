using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Divakar.FactoryOS.IntegrationTests;

/// <summary>Stands in for api.line.me: records every request (headers and body) and answers 200 {}.</summary>
public sealed class LineStub
{
    public static readonly LineStub Instance = new();

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly List<string> requests = [];

    public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (requests) return [.. requests];
        }
    }

    private LineStub()
    {
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[65536];
                var text = new StringBuilder();
                int contentLength = -1, headerEnd = -1;
                while (headerEnd < 0 || Encoding.UTF8.GetByteCount(text.ToString()[(headerEnd + 4)..]) < contentLength)
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    headerEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0 && contentLength < 0)
                    {
                        var line = text.ToString()[..headerEnd].Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase));
                        contentLength = line is null ? 0 : int.Parse(line.Split(':')[1].Trim());
                    }
                }
                lock (requests) requests.Add(text.ToString());
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"));
            }
        });
    }
}
