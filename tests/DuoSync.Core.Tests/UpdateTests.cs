using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DuoSync.Core.Updates;

namespace DuoSync.Core.Tests;

public class UpdateTests
{
    static readonly TimeSpan[] NoWait = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };

    [Fact]
    public void Release_json_is_parsed_and_checked()
    {
        var sha = new string('a', 64);
        var r = ReleaseInfo.Parse($$"""{"version":"0.3.1","exe":"DuoSync.exe","size":10,"sha256":"{{sha}}","notes":["Раз","Два"]}""");

        Assert.Equal(new Version(0, 3, 1), r.Version);
        Assert.Equal(sha.ToUpperInvariant(), r.Sha256);
        Assert.Equal(new[] { "Раз", "Два" }, r.Notes);
        Assert.Throws<InvalidDataException>(() => ReleaseInfo.Parse($$"""{"version":"0.3.1","exe":"../x.exe","size":10,"sha256":"{{sha}}"}"""));
    }

    [Fact]
    public async Task Download_resumes_where_the_connection_dropped()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        await using var server = new FlakyServer(data);
        server.DropAfter.Enqueue(100_000);
        server.DropAfter.Enqueue(50_000);

        var path = await ReleaseFeed.DownloadAsync(Release(data), TempDir(), NoWait, url: server.Url);

        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Equal(new long?[] { null, 100_000, 150_000 }, server.RangeStarts);
    }

    [Fact]
    public async Task Server_without_range_support_starts_over()
    {
        var data = RandomNumberGenerator.GetBytes(200_000);
        await using var server = new FlakyServer(data) { IgnoreRange = true };
        server.DropAfter.Enqueue(120_000);

        var path = await ReleaseFeed.DownloadAsync(Release(data), TempDir(), NoWait, url: server.Url);

        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Content_that_does_not_match_is_deleted()
    {
        var data = RandomNumberGenerator.GetBytes(50_000);
        await using var server = new FlakyServer(data);
        var dir = TempDir();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ReleaseFeed.DownloadAsync(Release(data) with { Sha256 = new string('0', 64) }, dir, NoWait, url: server.Url));
        Assert.Empty(Directory.GetFiles(dir));
    }

    static ReleaseInfo Release(byte[] data) =>
        new(new Version(9, 9, 9), "DuoSync.exe", data.Length, Convert.ToHexString(SHA256.HashData(data)), Array.Empty<string>());

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "duosync-tests", "upd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Tiny HTTP server that honours Range and can cut a response off after N body bytes (a dropped connection).</summary>
    sealed class FlakyServer : IAsyncDisposable
    {
        readonly byte[] _data;
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        readonly Task _loop;

        public Queue<int> DropAfter { get; } = new();
        public bool IgnoreRange { get; init; }
        public List<long?> RangeStarts { get; } = new();
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/DuoSync.exe";

        public FlakyServer(byte[] data)
        {
            _data = data;
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                using (client) await ServeAsync(client);
            }
        }

        async Task ServeAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
                head.Append((char)one[0]);

            long? start = null;
            foreach (var line in head.ToString().Split("\r\n"))
                if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                    start = long.Parse(line["Range: bytes=".Length..].TrimEnd('-'));
            lock (RangeStarts) RangeStarts.Add(start);

            var from = start is { } s && !IgnoreRange ? s : 0;
            var status = from > 0 ? "206 Partial Content" : "200 OK";
            var headers = $"HTTP/1.1 {status}\r\nContent-Length: {_data.Length - from}\r\nConnection: close\r\n" +
                          (from > 0 ? $"Content-Range: bytes {from}-{_data.Length - 1}/{_data.Length}\r\n" : "") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));

            int? cut;
            lock (DropAfter) cut = DropAfter.Count > 0 ? DropAfter.Dequeue() : null;
            var count = (int)Math.Min(_data.Length - from, cut ?? long.MaxValue);
            await stream.WriteAsync(_data.AsMemory((int)from, count));
            if (cut != null) client.Client.LingerState = new LingerOption(true, 0); // reset instead of a clean close
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch (Exception) { }
        }
    }
}
