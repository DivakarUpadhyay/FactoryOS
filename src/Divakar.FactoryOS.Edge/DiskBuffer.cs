using System.Text.Json;
using Divakar.FactoryOS.Application.Connectivity;

namespace Divakar.FactoryOS.Edge;

/// <summary>
/// Store and forward on disk: readings are appended to segment files (JSON lines) that roll every few MB or every minute;
/// the forwarder sends the oldest closed segment in batches and deletes it once the cloud has taken every batch. A sidecar file
/// remembers how far a half-sent segment got, so a restart neither loses nor repeats readings. When the buffer passes its size
/// limit the oldest segment is dropped (and counted) so the disk never fills.
/// </summary>
public sealed class DiskBuffer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string dir;
    private readonly long maxBytes;
    private readonly long rollBytes;
    private readonly TimeSpan rollAge;
    private readonly Lock gate = new();
    private StreamWriter? writer;
    private string? current;
    private DateTime openedAt;
    private long dropped;

    public DiskBuffer(string dir, long maxBytes = 512L * 1024 * 1024, long rollBytes = 4L * 1024 * 1024, TimeSpan? rollAge = null)
    {
        this.dir = Directory.CreateDirectory(dir).FullName;
        this.maxBytes = maxBytes;
        this.rollBytes = rollBytes;
        this.rollAge = rollAge ?? TimeSpan.FromSeconds(60);
        // a segment left open by a crash is just an older closed segment now
        foreach (var f in Directory.GetFiles(this.dir, "*.open"))
        {
            File.Move(f, Path.ChangeExtension(f, ".seg"), overwrite: true);
        }
    }

    public long Dropped => Interlocked.Read(ref dropped);

    public void Append(IEnumerable<EdgeReading> readings)
    {
        lock (gate)
        {
            if (writer is null)
            {
                current = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}.open");
                writer = new StreamWriter(new FileStream(current, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                openedAt = DateTime.UtcNow;
            }
            foreach (var r in readings)
            {
                writer.WriteLine(JsonSerializer.Serialize(r, Json));
            }
            writer.Flush();
            if (writer.BaseStream.Length >= rollBytes || DateTime.UtcNow - openedAt >= rollAge)
            {
                RollLocked();
            }
        }
        Trim();
    }

    /// <summary>Closes the open segment (if it has data) so the forwarder can send it.</summary>
    public void Roll()
    {
        lock (gate)
        {
            RollLocked();
        }
    }

    /// <summary>Closes the open segment once it is at least this old: readings go out promptly without one file every few seconds.</summary>
    public void RollIfOlderThan(TimeSpan age)
    {
        lock (gate)
        {
            if (writer is not null && DateTime.UtcNow - openedAt >= age)
            {
                RollLocked();
            }
        }
    }

    private void RollLocked()
    {
        if (writer is null || current is null)
        {
            return;
        }
        var empty = writer.BaseStream.Length == 0;
        writer.Dispose();
        writer = null;
        if (empty)
        {
            File.Delete(current);
        }
        else
        {
            File.Move(current, Path.ChangeExtension(current, ".seg"));
        }
        current = null;
    }

    private IEnumerable<string> Segments() => Directory.GetFiles(dir, "*.seg").Order(StringComparer.Ordinal);

    /// <summary>Readings waiting (closed segments, minus what was sent from a half-sent one, plus the open segment).</summary>
    public long Pending()
    {
        long n = 0;
        foreach (var f in Directory.GetFiles(dir, "*.seg").Concat(Directory.GetFiles(dir, "*.open")))
        {
            try
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(s);
                while (reader.ReadLine() is not null)
                {
                    n++;
                }
                n -= SentCount(f);
            }
            catch (IOException)
            {
            }
        }
        return Math.Max(0, n);
    }

    private static string SentFile(string segment) => segment + ".sent";

    private static int SentCount(string segment) =>
        File.Exists(SentFile(segment)) && int.TryParse(File.ReadAllText(SentFile(segment)), out var n) ? n : 0;

    /// <summary>
    /// Sends the oldest closed segment through <paramref name="send"/> in batches; returns how many readings went out.
    /// A failed batch stops the drain (the next call resumes from it).
    /// </summary>
    public async Task<int> DrainOneAsync(Func<IReadOnlyList<EdgeReading>, Task> send, int batch, CancellationToken ct)
    {
        var segment = Segments().FirstOrDefault();
        if (segment is null)
        {
            return 0;
        }
        var lines = await File.ReadAllLinesAsync(segment, ct);
        var done = SentCount(segment);
        var sent = 0;
        while (done < lines.Length)
        {
            var chunk = lines.Skip(done).Take(batch).Select(l => JsonSerializer.Deserialize<EdgeReading>(l, Json)!).ToList();
            await send(chunk);
            done += chunk.Count;
            sent += chunk.Count;
            await File.WriteAllTextAsync(SentFile(segment), done.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        }
        File.Delete(segment);
        File.Delete(SentFile(segment));
        return sent;
    }

    private void Trim()
    {
        var segments = Segments().ToList();
        var size = segments.Sum(f => new FileInfo(f).Length);
        foreach (var f in segments)
        {
            if (size <= maxBytes)
            {
                break;
            }
            var len = new FileInfo(f).Length;
            var lines = File.ReadLines(f).Count() - SentCount(f);
            File.Delete(f);
            File.Delete(SentFile(f));
            Interlocked.Add(ref dropped, Math.Max(0, lines));
            size -= len;
        }
    }
}
