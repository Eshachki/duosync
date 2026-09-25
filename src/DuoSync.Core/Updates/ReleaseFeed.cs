using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DuoSync.Core.Updates;

/// <summary><c>release.json</c> of a DuoSync release: a GitHub Releases asset next to the exe.</summary>
public sealed record ReleaseInfo(Version Version, string Exe, long Size, string Sha256, IReadOnlyList<string> Notes)
{
    public static ReleaseInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json.TrimStart('﻿'));
        var root = doc.RootElement;
        var version = Version.Parse(root.GetProperty("version").GetString() ?? "");
        var exe = root.GetProperty("exe").GetString() ?? "";
        var size = root.GetProperty("size").GetInt64();
        var sha = root.GetProperty("sha256").GetString() ?? "";
        if (exe.Length == 0 || exe.Contains('/') || exe.Contains('\\') || size <= 0 || sha.Length != 64)
            throw new InvalidDataException("release.json повреждён");
        var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.Array
            ? n.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : new List<string>();
        return new ReleaseInfo(version, exe, size, sha.ToUpperInvariant(), notes);
    }
}

/// <summary>
/// Releases of the program itself (§8а): <c>release.json</c> and the exe from the public repository's GitHub Releases.
/// No login and no API quota; downloads resume with Range because GitHub from Russia drops connections (decisions I.3–I.4).
/// </summary>
public static class ReleaseFeed
{
    public const string Repository = "Eshachki/duosync";

    /// <summary>Stable link to the newest exe: what to send a person for the first install.</summary>
    public static string LatestExeUrl => $"https://github.com/{Repository}/releases/latest/download/DuoSync.exe";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(20) };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DuoSync");
        return http;
    }

    /// <summary>The newest regular (not pre-) release, or null when the repository has none yet.</summary>
    public static async Task<ReleaseInfo?> LatestAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await Http.GetAsync($"https://github.com/{Repository}/releases/latest/download/release.json", timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return ReleaseInfo.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub не ответил вовремя");
        }
    }

    /// <summary>
    /// Downloads the release exe into <paramref name="dir"/> (resuming a partial file), checks size and SHA-256 and
    /// returns its path. A file that does not match is deleted.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, string dir, IReadOnlyList<TimeSpan> retryDelays,
        Action<string>? progress = null, CancellationToken ct = default, string? url = null)
    {
        Directory.CreateDirectory(dir);
        var final = Path.Combine(dir, release.Exe);
        if (File.Exists(final) && new FileInfo(final).Length == release.Size && await Sha256Async(final, ct) == release.Sha256)
            return final;

        var part = final + ".part";
        url ??= $"https://github.com/{Repository}/releases/download/v{release.Version.ToString(3)}/{Uri.EscapeDataString(release.Exe)}";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadPartAsync(url, part, release.Size, progress, ct);
                break;
            }
            catch (Exception e) when ((e is HttpRequestException or IOException or TimeoutException) && attempt < retryDelays.Count)
            {
                progress?.Invoke($"обрыв связи, повторю через {retryDelays[attempt].TotalSeconds:0} с");
                await Task.Delay(retryDelays[attempt], ct);
            }
        }

        if (new FileInfo(part).Length != release.Size || await Sha256Async(part, ct) != release.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException("скачанное обновление не совпало с release.json и удалено");
        }
        File.Move(part, final, overwrite: true);
        return final;
    }

    static async Task DownloadPartAsync(string url, string part, long size, Action<string>? progress, CancellationToken ct)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > size) { File.Delete(part); have = 0; }
        if (have == size) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(TimeSpan.FromSeconds(60));
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("GitHub не ответил вовремя"); }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(part);
                throw new IOException("докачка не принята, начну сначала");
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent) have = 0; // the server sent the whole file

            await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            var reported = DateTime.UtcNow;
            while (true)
            {
                // A stalled connection never ends a read on its own: 60 s without a byte counts as a drop.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                int n;
                try { n = await body.ReadAsync(buffer, idle.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("GitHub перестал отдавать данные"); }
                if (n == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                have += n;
                if (progress != null && DateTime.UtcNow - reported > TimeSpan.FromSeconds(10))
                {
                    progress($"скачано {have * 100 / size}%");
                    reported = DateTime.UtcNow;
                }
            }
        }
        if (have < size) throw new IOException("связь оборвалась посреди скачивания");
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
}
