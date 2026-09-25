using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuoSync.Core.Git;

namespace DuoSync.Core.GitHub;

/// <summary>A DuoSync project on GitHub: a repository marked with the <see cref="GitHubClient.ProjectTopic"/> topic.</summary>
public sealed record RemoteProject(string FullName, string Name, DateTimeOffset PushedAt, string? Description)
{
    public string CloneUrl => $"https://github.com/{FullName}.git";
}

public enum GitHubError { Unauthorized, Forbidden, NotFound, AlreadyExists, Network, Other }

public sealed class GitHubException : Exception
{
    public GitHubError Kind { get; }
    public GitHubException(GitHubError kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

/// <summary>
/// GitHub REST with the credential git already uses for github.com (Git Credential Manager, gh or a stored token),
/// so there is no separate login. Finds DuoSync projects (topic <c>duosync-project</c>) and creates repositories for new ones.
/// The token lives only in memory and goes only to api.github.com.
/// </summary>
public sealed class GitHubClient
{
    /// <summary>Not plain "duosync": that one may describe the program itself, this one marks a synchronized project.</summary>
    public const string ProjectTopic = "duosync-project";

    static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
    static readonly Regex NamePart = new(@"^[A-Za-z0-9._-]+$");
    static readonly Regex RepoUrl = new(
        @"^(?:https?://(?:[^@/]+@)?github\.com/|git@github\.com:|ssh://git@github\.com/)(?<owner>[A-Za-z0-9-]+)/(?<name>[A-Za-z0-9._-]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase);

    readonly string _token;
    /// <summary>What <c>git credential fill</c> returned: handed back to <c>git credential reject</c> if GitHub refuses the token.</summary>
    readonly byte[]? _credential;
    string? _login;

    GitHubClient(string token, byte[]? credential)
    {
        _token = token;
        _credential = credential;
    }

    /// <summary>
    /// Takes the github.com credential git uses for clone and push. <paramref name="interactive"/> lets git show its
    /// sign-in window (only after a button press); otherwise nothing pops up and null means there is no stored login.
    /// </summary>
    public static async Task<GitHubClient?> ConnectAsync(bool interactive, CancellationToken ct = default)
    {
        var git = new GitRunner(Path.GetTempPath(), "DuoSync", "duosync@users.noreply.github.com");
        var r = await git.RunAsync(new[] { "credential", "fill" }, new GitRunOptions
        {
            StdIn = Encoding.UTF8.GetBytes("protocol=https\nhost=github.com\n\n"),
            NonInteractive = !interactive,
            Timeout = interactive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(20),
        }, ct);
        if (r.Ok && ParseCredential(r.StdOut) is { } token) return new GitHubClient(token, r.StdOutBytes);
        return await GhTokenAsync(ct) is { } ghToken ? new GitHubClient(ghToken, null) : null;
    }

    /// <summary>The password line of <c>git credential fill</c> output; for GitHub it is a token.</summary>
    public static string? ParseCredential(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("password=", StringComparison.Ordinal) && line.Length > "password=".Length)
                return line["password=".Length..];
        }
        return null;
    }

    /// <summary>GitHub CLI's token, for machines where git keeps no github.com credential but gh is signed in.</summary>
    static async Task<string?> GhTokenAsync(CancellationToken ct)
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");
            psi.Environment["GH_PROMPT_DISABLED"] = "1";
            p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(timeout.Token);
            var token = (await stdout).Trim();
            return p.ExitCode == 0 && token.Length > 0 && !token.Contains(' ') ? token : null;
        }
        catch (OperationCanceledException)
        {
            try { p?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        finally { p?.Dispose(); }
    }

    /// <summary>
    /// GitHub refused the stored token: tell git to forget it, as git itself does after a failed login,
    /// so the next sign-in asks again instead of reusing the dead token.
    /// </summary>
    public async Task ForgetCredentialAsync()
    {
        if (_credential == null) return;
        var git = new GitRunner(Path.GetTempPath(), "DuoSync", "duosync@users.noreply.github.com");
        await git.RunAsync(new[] { "credential", "reject" }, new GitRunOptions { StdIn = _credential, NonInteractive = true });
    }

    // ------------------------------------------------------------------ Запросы

    public async Task<string> LoginAsync(CancellationToken ct = default)
    {
        if (_login != null) return _login;
        using var doc = await SendAsync(HttpMethod.Get, "user", null, ct);
        return _login = doc.RootElement.GetProperty("login").GetString() ?? "";
    }

    /// <summary>
    /// Organizations this account works in: its memberships plus owners of organization repositories it can reach
    /// (a git token without read:org sees no memberships, but still sees the repositories).
    /// </summary>
    public async Task<IReadOnlyList<string>> OrganizationsAsync(CancellationToken ct = default)
    {
        var result = new List<string>();
        using (var orgs = await SendAsync(HttpMethod.Get, "user/orgs?per_page=100", null, ct))
            result.AddRange(orgs.RootElement.EnumerateArray().Select(o => o.GetProperty("login").GetString()!));
        foreach (var repo in await ReposAsync(ct))
            if (repo.GetProperty("owner").GetProperty("type").GetString() == "Organization"
                && repo.GetProperty("owner").GetProperty("login").GetString() is { } login
                && !result.Contains(login, StringComparer.OrdinalIgnoreCase))
                result.Add(login);
        return result;
    }

    /// <summary>DuoSync projects this account can reach, most recently pushed first.</summary>
    public async Task<IReadOnlyList<RemoteProject>> ProjectsAsync(CancellationToken ct = default) =>
        ParseProjects(await ReposAsync(ct)).OrderByDescending(p => p.PushedAt).ToList();

    async Task<List<JsonElement>> ReposAsync(CancellationToken ct)
    {
        var all = new List<JsonElement>();
        for (int page = 1; page <= 5; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get,
                $"user/repos?per_page=100&sort=pushed&affiliation=owner,collaborator,organization_member&page={page}", null, ct);
            var items = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            all.AddRange(items);
            if (items.Count < 100) break;
        }
        return all;
    }

    /// <summary>Repositories with the DuoSync topic, archived ones left out.</summary>
    public static IEnumerable<RemoteProject> ParseProjects(IEnumerable<JsonElement> repos)
    {
        foreach (var repo in repos)
        {
            if (repo.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True) continue;
            if (!repo.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Array) continue;
            if (!topics.EnumerateArray().Any(t => t.GetString() == ProjectTopic)) continue;
            var fullName = repo.GetProperty("full_name").GetString();
            if (fullName == null || !TryParseFullName(fullName, out _, out var name)) continue;
            var pushed = repo.TryGetProperty("pushed_at", out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out var at)
                ? at : DateTimeOffset.MinValue;
            var description = repo.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            yield return new RemoteProject(fullName, name, pushed, description);
        }
    }

    /// <summary>Creates an empty private repository in <paramref name="owner"/> (an organization or this account).</summary>
    public async Task CreateRepositoryAsync(string owner, string name, CancellationToken ct = default)
    {
        if (!NamePart.IsMatch(owner) || !NamePart.IsMatch(name)) throw new GitHubException(GitHubError.Other, "Недопустимое имя репозитория.");
        var login = await LoginAsync(ct);
        var path = string.Equals(owner, login, StringComparison.OrdinalIgnoreCase) ? "user/repos" : $"orgs/{owner}/repos";
        var body = new
        {
            name,
            @private = true,
            description = "Unity-проект (DuoSync)",
            has_issues = false,
            has_projects = false,
            has_wiki = false,
            auto_init = false,
        };
        using var _ = await SendAsync(HttpMethod.Post, path, body, ct);
    }

    /// <summary>True when the repository exists and has no branches yet (created, nothing pushed).</summary>
    public async Task<bool> IsEmptyAsync(string fullName, CancellationToken ct = default)
    {
        Check(fullName);
        using var doc = await SendAsync(HttpMethod.Get, $"repos/{fullName}/branches?per_page=1", null, ct);
        return doc.RootElement.GetArrayLength() == 0;
    }

    /// <summary>
    /// Puts the DuoSync topic on the repository so the other person's DuoSync finds it. Keeps existing topics;
    /// needs admin rights on the repository, which its creator has.
    /// </summary>
    public async Task MarkAsProjectAsync(string fullName, CancellationToken ct = default)
    {
        Check(fullName);
        List<string> names;
        using (var doc = await SendAsync(HttpMethod.Get, $"repos/{fullName}/topics", null, ct))
            names = doc.RootElement.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToList();
        if (names.Contains(ProjectTopic)) return;
        names.Add(ProjectTopic);
        using var _ = await SendAsync(HttpMethod.Put, $"repos/{fullName}/topics", new { names }, ct);
    }

    // ------------------------------------------------------------------ Имена

    /// <summary>"owner/name" from a GitHub remote URL (https, ssh or scp form); null for anything else.</summary>
    public static string? FullNameFromUrl(string url)
    {
        var m = RepoUrl.Match(url.Trim());
        return m.Success ? $"{m.Groups["owner"].Value}/{m.Groups["name"].Value}" : null;
    }

    /// <summary>Accepts "owner/name" or a GitHub URL.</summary>
    public static bool TryParseFullName(string input, out string owner, out string name)
    {
        owner = name = "";
        var s = input.Trim();
        var full = FullNameFromUrl(s) ?? s;
        var parts = full.Split('/');
        if (parts.Length != 2 || !NamePart.IsMatch(parts[0]) || !NamePart.IsMatch(parts[1]) || parts[1] is "." or "..") return false;
        owner = parts[0];
        name = parts[1];
        return true;
    }

    /// <summary>A folder name turned into a repository name GitHub accepts.</summary>
    public static string SafeRepositoryName(string folderName)
    {
        var s = Regex.Replace(folderName, @"[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        return s.Length == 0 ? "unity-project" : s;
    }

    static void Check(string fullName)
    {
        if (!TryParseFullName(fullName, out _, out _)) throw new GitHubException(GitHubError.Other, "Недопустимое имя репозитория: " + fullName);
    }

    // ------------------------------------------------------------------ HTTP

    async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.UserAgent.ParseAdd("DuoSync");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, ct); }
        catch (HttpRequestException e) { throw new GitHubException(GitHubError.Network, "Нет связи с GitHub.", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new GitHubException(GitHubError.Network, "GitHub не ответил вовремя.", e); }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(text.Length == 0 ? "null" : text);
            var message = ErrorMessage(text);
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new GitHubException(GitHubError.Unauthorized, "GitHub не принял вход."),
                HttpStatusCode.Forbidden => new GitHubException(GitHubError.Forbidden, "GitHub не разрешает это действие: " + message),
                HttpStatusCode.NotFound => new GitHubException(GitHubError.NotFound, "На GitHub не найдено: " + message),
                HttpStatusCode.UnprocessableEntity when message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    => new GitHubException(GitHubError.AlreadyExists, "Такой репозиторий на GitHub уже есть."),
                _ => new GitHubException(GitHubError.Other, $"GitHub ответил {(int)response.StatusCode}: {message}"),
            };
        }
    }

    /// <summary>"message" plus the per-field "errors[].message" GitHub returns (422 keeps the useful part there).</summary>
    public static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var parts = new List<string>();
            if (doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message) parts.Add(message);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var e in errors.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em) && em.GetString() is { Length: > 0 } text)
                        parts.Add(text);
            return parts.Count > 0 ? string.Join("; ", parts) : body;
        }
        catch (JsonException) { return body.Length > 200 ? body[..200] : body; }
    }
}
