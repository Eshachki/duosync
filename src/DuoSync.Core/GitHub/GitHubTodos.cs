using System.Net;
using System.Text.Json;

namespace DuoSync.Core.GitHub;

public enum TodoStatus { Todo, Doing, Done }

/// <summary>A task of the project's «Черновик»: a GitHub issue with the <see cref="GitHubClient.TodoLabel"/> label.</summary>
public sealed record TodoItem(int Number, string Title, TodoStatus Status, IReadOnlyList<string> Labels, string Author, DateTimeOffset Created,
    string? Assignee = null)
{
    /// <summary>GitHub login of whoever does the task: the assignee, otherwise the one who wrote it.</summary>
    public string Owner => Assignee ?? Author;
}

/// <summary>One list request: its tasks and the ETag for the next conditional request.</summary>
public sealed record TodoPage(IReadOnlyList<TodoItem> Items, string? ETag);

/// <summary>
/// «Черновик»: the two people's shared task list, kept in the project repository's Issues. Both already have access,
/// so there is no server and no extra password. «В работе» is a label, «готово» a closed issue, «удалено» an issue
/// closed as not planned (only repository admins may really delete issues). The texts never go to Claude.
/// </summary>
public sealed partial class GitHubClient
{
    public const string TodoLabel = "duosync-todo";
    public const string DoingLabel = "duosync-doing";

    readonly HashSet<string> _labelsReady = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Open tasks, oldest first; null when nothing changed since <paramref name="etag"/>.</summary>
    public Task<TodoPage?> OpenTodosAsync(string fullName, string? etag, CancellationToken ct = default) =>
        TodoPageAsync(fullName, $"state=open&labels={TodoLabel}&per_page=100&sort=created&direction=asc", etag, ct);

    /// <summary>Recently finished (and deleted) tasks; null when nothing changed since <paramref name="etag"/>.</summary>
    public Task<TodoPage?> ClosedTodosAsync(string fullName, string? etag, CancellationToken ct = default) =>
        TodoPageAsync(fullName, $"state=closed&labels={TodoLabel}&per_page=50&sort=updated&direction=desc", etag, ct);

    /// <summary>Conditional request: a 304 answer is cheap and does not count against the API rate limit.</summary>
    async Task<TodoPage?> TodoPageAsync(string fullName, string query, string? etag, CancellationToken ct)
    {
        Check(fullName);
        using var request = NewRequest(HttpMethod.Get, $"repos/{fullName}/issues?{query}", null);
        if (etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await SendRawAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw Error(response.StatusCode, text);
        using var doc = JsonDocument.Parse(text);
        return new TodoPage(ParseTodos(doc.RootElement.EnumerateArray()).ToList(), response.Headers.ETag?.ToString());
    }

    /// <summary>Issues → tasks. Pull requests and deleted tasks (closed as not planned) are left out.</summary>
    public static IEnumerable<TodoItem> ParseTodos(IEnumerable<JsonElement> issues)
    {
        foreach (var issue in issues)
        {
            if (issue.TryGetProperty("pull_request", out _)) continue;
            var labels = issue.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                    .Where(x => x.Length > 0).ToList()
                : new List<string>();
            var closed = issue.GetProperty("state").GetString() == "closed";
            var reason = issue.TryGetProperty("state_reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (closed && reason == "not_planned") continue;
            var status = closed ? TodoStatus.Done : labels.Contains(DoingLabel) ? TodoStatus.Doing : TodoStatus.Todo;
            var author = issue.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object && u.TryGetProperty("login", out var login)
                ? login.GetString() ?? "" : "";
            var created = issue.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetDateTimeOffset(out var at)
                ? at : DateTimeOffset.MinValue;
            var assignee = issue.TryGetProperty("assignees", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.TryGetProperty("login", out var al) ? al.GetString() : null).FirstOrDefault(x => !string.IsNullOrEmpty(x))
                : null;
            yield return new TodoItem(issue.GetProperty("number").GetInt32(), issue.GetProperty("title").GetString() ?? "", status, labels, author, created, assignee);
        }
    }

    /// <summary>New task; <paramref name="assignee"/> is the login of whoever will do it (null: the one who writes it).</summary>
    public async Task<TodoItem> AddTodoAsync(string fullName, string title, string? assignee = null, CancellationToken ct = default)
    {
        Check(fullName);
        await EnsureTodoLabelsAsync(fullName, ct);
        var body = new Dictionary<string, object> { ["title"] = title, ["labels"] = new[] { TodoLabel } };
        if (assignee != null) body["assignees"] = new[] { assignee };
        using var doc = await SendAsync(HttpMethod.Post, $"repos/{fullName}/issues", body, ct);
        return ParseTodos(new[] { doc.RootElement }).First();
    }

    /// <summary>
    /// GitHub login of the other person in the project: from the tasks, then from the commits, then from the people
    /// who may be assigned (when that is exactly one besides me). Null while it cannot be told.
    /// </summary>
    public async Task<string?> FriendLoginAsync(string fullName, IEnumerable<TodoItem> known, CancellationToken ct = default)
    {
        Check(fullName);
        var me = await LoginAsync(ct);
        bool Other(string? login) => !string.IsNullOrEmpty(login) && !string.Equals(login, me, StringComparison.OrdinalIgnoreCase)
                                     && !login.EndsWith("[bot]", StringComparison.Ordinal);
        var fromTasks = known.SelectMany(t => new[] { t.Author, t.Assignee }).FirstOrDefault(Other);
        if (fromTasks != null) return fromTasks;
        try
        {
            using (var commits = await SendAsync(HttpMethod.Get, $"repos/{fullName}/commits?per_page=50", null, ct))
                foreach (var c in commits.RootElement.EnumerateArray())
                    if (c.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
                        && author.TryGetProperty("login", out var login) && Other(login.GetString()))
                        return login.GetString();
        }
        catch (GitHubException e) when (e.Kind is GitHubError.Other or GitHubError.NotFound) { } // empty repository: 409
        var others = await PeopleAsync(fullName, ct);
        return others.Count == 1 ? others[0] : null;
    }

    /// <summary>Everyone besides me who can be given a task in this repository (GitHub logins, bots left out).</summary>
    public async Task<IReadOnlyList<string>> PeopleAsync(string fullName, CancellationToken ct = default)
    {
        Check(fullName);
        var me = await LoginAsync(ct);
        using var people = await SendAsync(HttpMethod.Get, $"repos/{fullName}/assignees?per_page=100", null, ct);
        return people.RootElement.EnumerateArray().Select(p => p.GetProperty("login").GetString() ?? "")
            .Where(login => login.Length > 0 && !string.Equals(login, me, StringComparison.OrdinalIgnoreCase) && !login.EndsWith("[bot]", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>New status and/or title, or «удалить» (closes the issue as not planned: deleting issues is for admins only).</summary>
    public async Task UpdateTodoAsync(string fullName, TodoItem item, TodoStatus? status = null, string? title = null, bool delete = false,
        string? assignee = null, CancellationToken ct = default)
    {
        Check(fullName);
        var body = new Dictionary<string, object>();
        if (title != null) body["title"] = title;
        if (assignee != null) body["assignees"] = new[] { assignee };
        if (delete)
        {
            body["state"] = "closed";
            body["state_reason"] = "not_planned";
        }
        else if (status is { } s)
        {
            await EnsureTodoLabelsAsync(fullName, ct);
            var labels = item.Labels.Where(x => x != DoingLabel).ToList();
            if (!labels.Contains(TodoLabel)) labels.Add(TodoLabel);
            if (s == TodoStatus.Doing) labels.Add(DoingLabel);
            body["labels"] = labels;
            body["state"] = s == TodoStatus.Done ? "closed" : "open";
            if (s == TodoStatus.Done) body["state_reason"] = "completed";
        }
        if (body.Count == 0) return;
        using var _ = await SendAsync(HttpMethod.Patch, $"repos/{fullName}/issues/{item.Number}", body, ct);
    }

    /// <summary>Turns Issues on for a repository created with them off. Needs admin rights (its creator has them).</summary>
    public async Task<bool> EnableIssuesAsync(string fullName, CancellationToken ct = default)
    {
        Check(fullName);
        try
        {
            using var _ = await SendAsync(HttpMethod.Patch, $"repos/{fullName}", new { has_issues = true }, ct);
            return true;
        }
        catch (GitHubException e) when (e.Kind is GitHubError.Forbidden or GitHubError.NotFound) { return false; }
    }

    async Task EnsureTodoLabelsAsync(string fullName, CancellationToken ct)
    {
        if (_labelsReady.Contains(fullName)) return;
        foreach (var (name, color, description) in new[] { (TodoLabel, "c5def5", "Черновик DuoSync"), (DoingLabel, "fbca04", "Черновик DuoSync: в работе") })
        {
            try { using var _ = await SendAsync(HttpMethod.Get, $"repos/{fullName}/labels/{name}", null, ct); }
            catch (GitHubException e) when (e.Kind == GitHubError.NotFound)
            {
                try { using var _ = await SendAsync(HttpMethod.Post, $"repos/{fullName}/labels", new { name, color, description }, ct); }
                catch (GitHubException x) when (x.Kind == GitHubError.AlreadyExists) { }
            }
        }
        _labelsReady.Add(fullName);
    }
}
