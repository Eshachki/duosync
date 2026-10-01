using System.Diagnostics;
using System.Text;

namespace DuoSync.Core.Git;

/// <summary>Result of one git invocation. Stdout is kept as bytes because most parsers read NUL-separated output.</summary>
public sealed record GitResult(int ExitCode, byte[] StdOutBytes, string StdErr, bool TimedOut, IReadOnlyList<string> Args)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string StdOut => Encoding.UTF8.GetString(StdOutBytes);
    public string StdOutTrimmed => StdOut.TrimEnd('\r', '\n');
}

public sealed class GitRunOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public byte[]? StdIn { get; init; }
    /// <summary>Background reads must not take optional locks (status refresh etc.).</summary>
    public bool NoOptionalLocks { get; init; }
    /// <summary>Background calls must never pop up credential dialogs.</summary>
    public bool NonInteractive { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>
    /// Long network steps: short Russian lines about how far git and git-lfs got («Отправляю проект: 45%…»).
    /// Push, fetch and clone also need <c>--progress</c> in their arguments, or git prints nothing without a terminal.
    /// </summary>
    public Action<string>? Progress { get; init; }
}

public sealed class GitException : Exception
{
    public GitResult Result { get; }
    public GitException(GitResult result)
        : base($"git {string.Join(' ', result.Args)} failed ({(result.TimedOut ? "timeout" : result.ExitCode.ToString())}): {result.StdErr.Trim()}")
        => Result = result;
}

/// <summary>
/// The only way DuoSync talks to git. Critical settings are passed with -c on every call so behaviour
/// does not depend on the user's or the repository's config (decisions: §3.3).
/// </summary>
public sealed class GitRunner
{
    public string RepoDir { get; }
    public string GitExe { get; }
    public string UserName { get; }
    public string UserEmail { get; }

    public GitRunner(string repoDir, string userName, string userEmail, string gitExe = "git")
    {
        RepoDir = repoDir;
        UserName = userName;
        UserEmail = userEmail;
        GitExe = gitExe;
    }

    static readonly string[] BaseConfig =
    {
        "core.autocrlf=false", "core.safecrlf=false", "core.quotepath=false", "core.longpaths=true",
        "color.ui=never", "merge.conflictStyle=diff3", "merge.directoryRenames=true", "merge.renameLimit=20000",
        "core.fsync=added,reference", "core.fsyncMethod=batch", "advice.detachedHead=false",
    };

    public async Task<GitResult> RunAsync(IEnumerable<string> args, GitRunOptions? options = null, CancellationToken ct = default)
    {
        options ??= new GitRunOptions();
        var argList = args.ToList();
        var psi = new ProcessStartInfo(GitExe)
        {
            WorkingDirectory = options.WorkingDirectory ?? RepoDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (options.NoOptionalLocks) psi.ArgumentList.Add("--no-optional-locks");
        foreach (var c in BaseConfig) { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(c); }
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.name=" + UserName);
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.email=" + UserEmail);
        foreach (var a in argList) psi.ArgumentList.Add(a);

        psi.Environment["LC_ALL"] = "C";
        // Unity file names contain [ ] * ?; DuoSync never wants glob pathspecs.
        psi.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASK_YESNO"] = "false";
        if (options.NonInteractive) psi.Environment["GCM_INTERACTIVE"] = "never";
        if (options.Env != null)
            foreach (var (k, v) in options.Env) psi.Environment[k] = v;
        // git-lfs reports transfers only to a terminal or to this file.
        string? lfsProgress = null;
        if (options.Progress != null)
        {
            lfsProgress = Path.Combine(Path.GetTempPath(), $"duosync-lfs-{Guid.NewGuid():N}.txt");
            psi.Environment["GIT_LFS_PROGRESS"] = lfsProgress;
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutBuffer = new MemoryStream();
        var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer, CancellationToken.None);
        var progress = options.Progress != null ? new GitProgress(options.Progress) : null;
        var stderrTask = progress != null ? progress.ReadStdErrAsync(proc.StandardError) : proc.StandardError.ReadToEndAsync(CancellationToken.None);
        using var lfsWatch = new CancellationTokenSource();
        var lfsTask = progress != null ? progress.WatchLfsAsync(lfsProgress!, lfsWatch.Token) : Task.CompletedTask;

        if (options.StdIn is { Length: > 0 } stdin)
        {
            try { await proc.StandardInput.BaseStream.WriteAsync(stdin, ct); }
            catch (IOException) { /* process exited early; its exit code tells the story */ }
        }
        try { proc.StandardInput.Close(); } catch (IOException) { }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.Timeout);
        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await proc.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) { await Task.WhenAll(stdoutTask, stderrTask); await StopLfsWatchAsync(); throw; }
        }
        await Task.WhenAll(stdoutTask, stderrTask);
        await StopLfsWatchAsync();
        return new GitResult(timedOut ? -1 : proc.ExitCode, stdoutBuffer.ToArray(), stderrTask.Result, timedOut, argList);

        async Task StopLfsWatchAsync()
        {
            lfsWatch.Cancel();
            try { await lfsTask; } catch (OperationCanceledException) { }
            if (lfsProgress != null) try { File.Delete(lfsProgress); } catch (IOException) { }
        }
    }

    public Task<GitResult> RunAsync(params string[] args) => RunAsync(args, null, CancellationToken.None);

    public async Task<GitResult> RunCheckedAsync(IEnumerable<string> args, GitRunOptions? options = null, CancellationToken ct = default)
    {
        var r = await RunAsync(args, options, ct);
        if (!r.Ok) throw new GitException(r);
        return r;
    }

    public Task<GitResult> RunCheckedAsync(params string[] args) => RunCheckedAsync(args, null, CancellationToken.None);

    /// <summary>Runs a command and returns trimmed stdout, throwing on failure.</summary>
    public async Task<string> OutAsync(params string[] args) => (await RunCheckedAsync(args)).StdOutTrimmed;
}
