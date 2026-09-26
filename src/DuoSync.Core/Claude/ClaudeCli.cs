using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DuoSync.Core.Claude;

/// <summary>Whether this computer can resolve merges: Claude Code CLI found and signed in.</summary>
public sealed record ClaudeStatus(string? Exe, string? Version, bool LoggedIn, string? Error)
{
    public bool Installed => Exe != null;
    public bool Ready => Installed && LoggedIn;
}

/// <summary>Something Claude did, as a line for the project feed.</summary>
public sealed record ClaudeEvent(string Kind, string Text);

/// <summary>One headless call (§2.5): everything the program needs from the stream-json output.</summary>
public sealed record ClaudeRun(bool Ok, string? SessionId, string? Error, string? LastText, DateTimeOffset? LimitResetsAt, IReadOnlyList<string> EditedFiles);

public sealed record ClaudeRequest
{
    public required string WorkingDirectory { get; init; }
    public required string Prompt { get; init; }
    public required string SystemPrompt { get; init; }
    public required string SessionId { get; init; }
    /// <summary>Continue the session instead of starting it (answers to Claude's question, second attempts).</summary>
    public bool Resume { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
    public required IReadOnlyList<string> Allowed { get; init; }
    public IReadOnlyList<string> Disallowed { get; init; } = Array.Empty<string>();
    /// <summary>Folders (relative to the working directory, "/" separators) where Edit/Write may land; anything else stops the run.</summary>
    public required IReadOnlyList<string> WritablePrefixes { get; init; }
    public int MaxTurns { get; init; } = 60;
    public TimeSpan Total { get; init; } = TimeSpan.FromMinutes(20);
}

/// <summary>
/// Claude Code CLI in headless mode, isolated from the person's own setup (decisions A): no CLAUDE.md, memory, skills,
/// plugins, hooks or MCP (<c>--safe-mode</c>), no settings of the user or of the repository (<c>--setting-sources ""</c>),
/// only the tools and paths of the operation. The permission rules are not a security boundary, so the program also
/// checks every file Claude writes and stops the run on anything outside the allowed folders.
/// </summary>
public static class ClaudeCli
{
    public const string Model = "claude-opus-5-5";
    static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(60);
    static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(5);

    public static string? FindExe()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "claude.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(local) ? local : null;
    }

    public static async Task<ClaudeStatus> DetectAsync(CancellationToken ct = default)
    {
        var exe = FindExe();
        if (exe == null) return new ClaudeStatus(null, null, false, "Claude Code не установлен");
        var (vcode, version, _) = await RunShortAsync(exe, new[] { "--version" }, ct);
        var (acode, auth, aerr) = await RunShortAsync(exe, new[] { "auth", "status" }, ct);
        var loggedIn = false;
        try
        {
            using var doc = JsonDocument.Parse(auth);
            loggedIn = doc.RootElement.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { }
        return new ClaudeStatus(exe, vcode == 0 ? version.Trim() : null, loggedIn,
            loggedIn ? null : acode == 0 ? "Claude Code не вошёл: выполни в терминале claude auth login" : aerr.Trim());
    }

    static async Task<(int Code, string Out, string Err)> RunShortAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = NewStartInfo(exe, Environment.CurrentDirectory);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "", "Claude Code не ответил");
        }
        return (p.ExitCode, await outTask, await errTask);
    }

    static ProcessStartInfo NewStartInfo(string exe, string workingDirectory)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            // Without it the prompt goes out in the console code page and Cyrillic arrives broken (found in the live test).
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        // Nothing from the environment may redirect Claude to another provider or reconfigure it (§2.5).
        foreach (var key in psi.Environment.Keys.ToList())
            if (key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                || key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(key);
        psi.Environment["DISABLE_AUTOUPDATER"] = "1";
        return psi;
    }

    public static async Task<ClaudeRun> RunAsync(string exe, ClaudeRequest request, Action<ClaudeEvent> onEvent, CancellationToken ct)
    {
        var psi = NewStartInfo(exe, request.WorkingDirectory);
        var args = psi.ArgumentList;
        args.Add("-p");
        args.Add(request.Resume ? "--resume" : "--session-id");
        args.Add(request.SessionId);
        foreach (var a in new[] { "--output-format", "stream-json", "--verbose", "--safe-mode", "--setting-sources", "", "--disable-slash-commands",
                                  "--permission-mode", "dontAsk", "--model", Model, "--max-turns", request.MaxTurns.ToString() })
            args.Add(a);
        args.Add("--tools");
        args.Add(string.Join(",", request.Tools));
        if (request.Allowed.Count > 0)
        {
            args.Add("--allowedTools");
            foreach (var rule in request.Allowed) args.Add(rule);
        }
        if (request.Disallowed.Count > 0)
        {
            args.Add("--disallowedTools");
            foreach (var rule in request.Disallowed) args.Add(rule);
        }
        args.Add("--append-system-prompt");
        args.Add(request.SystemPrompt);

        using var process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync(request.Prompt);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var started = DateTime.UtcNow;
        var lastEvent = DateTime.UtcNow;
        var sawInit = false;
        string? sessionId = null, error = null, lastText = null;
        DateTimeOffset? limit = null;
        var edited = new List<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Watchdog: no start in 60 s, 5 min of silence or the whole limit — the process is killed.
        var watchdog = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { return; }
                var now = DateTime.UtcNow;
                string? why = !sawInit && now - started > InitTimeout ? "Claude не запустился за минуту"
                    : now - lastEvent > SilenceTimeout ? "Claude молчит 5 минут"
                    : now - started > request.Total ? $"Claude не уложился в {request.Total.TotalMinutes:0} минут"
                    : null;
                if (why == null) continue;
                error ??= why;
                Kill(process);
                return;
            }
        });

        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                lastEvent = DateTime.UtcNow;
                if (line.Length == 0 || line[0] != '{') continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var e = doc.RootElement;
                    var type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var subtype = e.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                    if (type == "system" && subtype == "init")
                    {
                        sawInit = true;
                        sessionId = e.TryGetProperty("session_id", out var s) ? s.GetString() : sessionId;
                        if (e.TryGetProperty("apiKeySource", out var key) && key.GetString() is { } source && source != "none")
                            onEvent(new ClaudeEvent("info", $"Claude работает через ключ API ({source}), а не через подписку."));
                    }
                    else if (type == "rate_limit_event" && e.TryGetProperty("rate_limit_info", out var info)
                             && info.TryGetProperty("status", out var status) && status.GetString() == "rejected"
                             && info.TryGetProperty("resetsAt", out var resets) && resets.TryGetInt64(out var unix))
                        limit = DateTimeOffset.FromUnixTimeSeconds(unix);
                    else if (type == "assistant" && e.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                             && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            var kind = block.TryGetProperty("type", out var k) ? k.GetString() : null;
                            if (kind == "text" && block.GetProperty("text").GetString() is { Length: > 0 } text)
                            {
                                lastText = text;
                                onEvent(new ClaudeEvent("text", text));
                            }
                            else if (kind == "tool_use")
                            {
                                var tool = block.GetProperty("name").GetString() ?? "";
                                var input = block.TryGetProperty("input", out var i) ? i : default;
                                var path = Arg(input, "file_path") ?? Arg(input, "path") ?? Arg(input, "pattern") ?? "";
                                var shown = Relative(request.WorkingDirectory, path);
                                if (tool is "Edit" or "Write" or "MultiEdit" or "NotebookEdit")
                                {
                                    if (!request.WritablePrefixes.Any(p => shown.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        error = $"Claude пытался писать вне папки операции: {shown}. Остановил.";
                                        Kill(process);
                                        break;
                                    }
                                    if (!edited.Contains(shown)) edited.Add(shown);
                                    onEvent(new ClaudeEvent("tool", $"Claude правит {shown}"));
                                }
                                else if (tool != "StructuredOutput") onEvent(new ClaudeEvent("tool", $"Claude {(tool is "Grep" or "Glob" ? "ищет" : "читает")} {shown}"));
                            }
                        }
                    }
                    else if (type == "system" && subtype == "permission_denied")
                        onEvent(new ClaudeEvent("denied", "Claude хотел сделать то, что запрещено режимом: " + Short(line)));
                    else if (type == "result")
                    {
                        sessionId = e.TryGetProperty("session_id", out var s) ? s.GetString() : sessionId;
                        var isError = e.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                        if (e.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String) lastText = r.GetString();
                        if (isError)
                        {
                            var httpStatus = e.TryGetProperty("api_error_status", out var ae) && ae.ValueKind == JsonValueKind.Number ? ae.GetInt32() : 0;
                            var reason = e.TryGetProperty("terminal_reason", out var tr) ? tr.GetString() : null;
                            error ??= httpStatus switch
                            {
                                429 => limit is { } l ? $"Лимит Claude до {l.ToLocalTime():HH:mm}" : "Кончился лимит Claude",
                                400 => "Claude Code устарел для этой модели: выполни в терминале claude update",
                                401 or 403 => "Claude не вошёл: выполни в терминале claude auth login",
                                404 => $"Модель {Model} недоступна",
                                _ => reason == "max_turns" ? "Claude не уложился в отведённое число шагов" : "Claude ответил ошибкой: " + (lastText ?? reason ?? "неизвестно"),
                            };
                        }
                    }
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            error ??= "Остановлено";
        }
        finally
        {
            stop.Cancel();
            try { await watchdog; } catch (OperationCanceledException) { }
        }

        var err = (await stderr).Trim();
        if (error == null && process.ExitCode != 0)
            error = err.Contains("already in use", StringComparison.OrdinalIgnoreCase) ? "session-in-use" : "Claude Code завершился с ошибкой: " + Short(err);
        return new ClaudeRun(error == null, sessionId, error, lastText, limit, edited);
    }

    static string? Arg(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Path as Claude gave it (absolute, any separators) relative to the project, with "/".</summary>
    public static string Relative(string root, string path)
    {
        if (path.Length == 0) return path;
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            var rel = Path.GetRelativePath(Path.GetFullPath(root), full);
            return rel.Replace('\\', '/');
        }
        catch (ArgumentException) { return path.Replace('\\', '/'); }
    }

    static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

    static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
