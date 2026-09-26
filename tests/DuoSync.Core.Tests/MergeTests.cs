using System.Text.Json;
using DuoSync.Core.Merge;
using DuoSync.Core.Ops;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

/// <summary>Stands in for Claude: each call runs the next scripted step against the work folder.</summary>
sealed class ScriptedResolver : IConflictResolver
{
    readonly Queue<Action<MergeJob>> _steps;
    public List<(string Message, bool Resume)> Calls { get; } = new();
    public MergeJob? LastJob { get; private set; }

    public ScriptedResolver(params Action<MergeJob>[] steps) => _steps = new Queue<Action<MergeJob>>(steps);

    public Task<ResolveOutcome> ResolveAsync(MergeJob job, string message, string sessionId, bool resume, Action<string> feed, CancellationToken ct)
    {
        Calls.Add((message, resume));
        LastJob = job;
        _steps.Dequeue()(job);
        feed("Claude правит files/…");
        return Task.FromResult(new ResolveOutcome(true, null));
    }

    /// <summary>Writes the resolved file (for text) and result.json with one decision.</summary>
    public static void Answer(MergeJob job, string path, string? content, string kind, string? question = null)
    {
        if (content != null) File.WriteAllText(Path.Combine(job.WorkDir, "files", path.Replace('/', Path.DirectorySeparatorChar)), content);
        File.WriteAllText(job.ResultPath, JsonSerializer.Serialize(new
        {
            summary_ru = "скорость от Ани, прыжок от Бори",
            question_ru = question,
            files = question != null ? Array.Empty<object>() : new object[] { new { path, kind, why_ru = "обе правки совместимы" } },
        }));
    }
}

public class MergeTests
{
    const string Player = "Assets/Scripts/Player.cs";
    static readonly string Resolved = Lines("class Player", "{", "    int speed = 7;", "    int jump = 3;", "}");

    static async Task<(Harness.Sandbox Sb, ScriptedResolver Resolver)> ConflictAsync(ScriptedResolver resolver)
    {
        var sb = await CreateAsync();
        sb.Owner.Options.CanResolve = true;
        sb.Owner.Options.Resolver = resolver;
        sb.Owner.Write(Player, Lines("class Player", "{", "    int speed = 7;", "    int jump = 2;", "}"));
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Быстрее")).Status);
        sb.Friend.Write(Player, Lines("class Player", "{", "    int speed = 9;", "    int jump = 3;", "}"));
        return (sb, resolver);
    }

    [Fact]
    public async Task Friends_conflict_becomes_a_request_and_the_integrator_merges_it()
    {
        var (sb, resolver) = await ConflictAsync(new ScriptedResolver(job => ScriptedResolver.Answer(job, Player, Resolved, "combine")));
        await using var _ = sb;

        // No Claude at the friend's: nothing to choose, the work goes to GitHub as a request.
        var sent = await sb.Friend.Engine.SendAsync("Выше прыжок");
        Assert.Equal(OpStatus.Requested, sent.Status);
        var branch = "refs/heads/duosync/merge/" + SyncEngine.Slug("Боря");
        var request = await sb.Friend.OutAsync("ls-remote", sb.RemoteDir, branch);
        Assert.StartsWith(await sb.Friend.HeadAsync(), request);
        Assert.NotNull((await sb.Friend.Engine.CheckStatusAsync()).RequestWaitingSince);

        // The integrator sees it and merges with the resolver.
        var status = await sb.Owner.Engine.CheckStatusAsync();
        var pending = Assert.Single(status.Requests);
        Assert.Contains(Player, pending.Files);
        var merged = await sb.Owner.Engine.MergeWithClaudeAsync(pending, _ => Task.FromResult<string?>(null));
        Assert.Equal(OpStatus.Done, merged.Status);
        Assert.Equal(Resolved, sb.Owner.Read(Player));
        var parents = (await sb.Owner.OutAsync("log", "-1", "--format=%P")).Split(' ');
        Assert.Equal(2, parents.Length);
        Assert.Contains(pending.Sha, parents);
        Assert.Contains("DuoSync-Request: " + pending.Sha, await sb.Owner.OutAsync("log", "-1", "--format=%B"));
        Assert.Contains("Claude", merged.Message);
        Assert.Empty((await sb.Owner.Engine.CheckStatusAsync()).Requests); // merged here, waits for «Отправить»

        // «Отправить» at the integrator: the friend sees the request merged and gets the result by fast-forward.
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync(null)).Status);
        Assert.True((await sb.Friend.Engine.CheckStatusAsync()).RequestMerged);
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.ReceiveAsync()).Status);
        Assert.Equal(Resolved, sb.Friend.Read(Player));
        Assert.Equal(await sb.RemoteHeadAsync(), await sb.Friend.HeadAsync());
    }

    [Fact]
    public async Task Own_conflict_at_the_integrator_waits_for_the_button_and_then_merges()
    {
        var resolver = new ScriptedResolver(job => ScriptedResolver.Answer(job, Player, Resolved, "combine"));
        await using var sb = await CreateAsync();
        sb.Owner.Options.CanResolve = true;
        sb.Owner.Options.Resolver = resolver;
        // The friend's edit reaches GitHub first; the integrator's unsent edit of the same lines conflicts on «Получить».
        sb.Friend.Write(Player, Lines("class Player", "{", "    int speed = 9;", "    int jump = 3;", "}"));
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("Выше прыжок")).Status);
        sb.Owner.Write(Player, Lines("class Player", "{", "    int speed = 7;", "    int jump = 2;", "}"));

        var received = await sb.Owner.Engine.ReceiveAsync();
        Assert.Equal(OpStatus.Conflict, received.Status);
        Assert.Contains("Слить с Claude", received.Message);
        Assert.Empty(resolver.Calls); // Claude starts only by the person's button

        var merged = await sb.Owner.Engine.MergeWithClaudeAsync(null, _ => Task.FromResult<string?>(null));
        Assert.Equal(OpStatus.Done, merged.Status);
        Assert.Equal(Resolved, sb.Owner.Read(Player));
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync(null)).Status);
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.ReceiveAsync()).Status);
        Assert.Equal(Resolved, sb.Friend.Read(Player));
    }

    [Fact]
    public async Task Leftover_markers_are_sent_back_and_the_second_answer_is_applied()
    {
        var (sb, resolver) = await ConflictAsync(new ScriptedResolver(
            job => ScriptedResolver.Answer(job, Player, File.ReadAllText(Path.Combine(job.WorkDir, "files", Player.Replace('/', Path.DirectorySeparatorChar))), "combine"),
            job => ScriptedResolver.Answer(job, Player, Resolved, "combine")));
        await using var _ = sb;
        sb.Owner.Options.CanResolve = true;
        Assert.Equal(OpStatus.Requested, (await sb.Friend.Engine.SendAsync("Выше прыжок")).Status);
        var pending = Assert.Single((await sb.Owner.Engine.CheckStatusAsync()).Requests);

        var merged = await sb.Owner.Engine.MergeWithClaudeAsync(pending, _ => Task.FromResult<string?>(null));

        Assert.Equal(OpStatus.Done, merged.Status);
        Assert.Equal(2, resolver.Calls.Count);
        Assert.True(resolver.Calls[1].Resume);
        Assert.Contains("маркер", resolver.Calls[1].Message);
        Assert.Equal(Resolved, sb.Owner.Read(Player));
    }

    [Fact]
    public async Task Claudes_question_goes_to_the_person_and_the_answer_continues_the_session()
    {
        var (sb, resolver) = await ConflictAsync(new ScriptedResolver(
            job => ScriptedResolver.Answer(job, Player, null, "combine", question: "Скорость 7 или 9? 1 — 7, 2 — 9"),
            job => ScriptedResolver.Answer(job, Player, Resolved, "combine")));
        await using var _ = sb;
        Assert.Equal(OpStatus.Requested, (await sb.Friend.Engine.SendAsync("Выше прыжок")).Status);
        var pending = Assert.Single((await sb.Owner.Engine.CheckStatusAsync()).Requests);
        string? asked = null;

        var merged = await sb.Owner.Engine.MergeWithClaudeAsync(pending, q => { asked = q; return Task.FromResult<string?>("1"); });

        Assert.Equal(OpStatus.Done, merged.Status);
        Assert.StartsWith("Скорость 7 или 9", asked);
        Assert.Equal(("1", true), resolver.Calls[1]);
    }

    [Fact]
    public async Task Unanswered_question_leaves_everything_as_it_was()
    {
        var (sb, resolver) = await ConflictAsync(new ScriptedResolver(job => ScriptedResolver.Answer(job, Player, null, "combine", question: "Какую скорость?")));
        await using var _ = sb;
        Assert.Equal(OpStatus.Requested, (await sb.Friend.Engine.SendAsync("Выше прыжок")).Status);
        var pending = Assert.Single((await sb.Owner.Engine.CheckStatusAsync()).Requests);
        var before = await sb.Owner.HeadAsync();

        var merged = await sb.Owner.Engine.MergeWithClaudeAsync(pending, _ => Task.FromResult<string?>(null));

        Assert.Equal(OpStatus.Blocked, merged.Status);
        Assert.Contains(Lines("    int speed = 7;").Trim(), sb.Owner.Read(Player));
        Assert.Single((await sb.Owner.Engine.CheckStatusAsync()).Requests);
        Assert.StartsWith(before, await sb.Owner.OutAsync("rev-parse", "HEAD~0"));
    }

    [Fact]
    public async Task Binary_changed_by_both_takes_one_side_whole()
    {
        var resolver = new ScriptedResolver(job =>
        {
            var item = Assert.Single(job.Items);
            Assert.Equal(ConflictKind.Binary, item.Kind);
            Assert.True(File.Exists(Path.Combine(job.WorkDir, "theirs", "Assets", "Art", "logo.bin")));
            ScriptedResolver.Answer(job, "Assets/Art/logo.bin", null, "choice-theirs");
        });
        await using var sb = await CreateAsync();
        sb.Owner.Options.CanResolve = true;
        sb.Owner.Options.Resolver = resolver;
        File.WriteAllBytes(Path.Combine(sb.Owner.Dir, "Assets", "Art", "logo.bin").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), new byte[] { 0, 1, 2 });
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Лого")).Status);
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.ReceiveAsync()).Status);
        File.WriteAllBytes(Path.Combine(sb.Owner.Dir, "Assets", "Art", "logo.bin"), new byte[] { 0, 7, 7 });
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Лого красное")).Status);
        File.WriteAllBytes(Path.Combine(sb.Friend.Dir, "Assets", "Art", "logo.bin"), new byte[] { 0, 9, 9 });
        Assert.Equal(OpStatus.Requested, (await sb.Friend.Engine.SendAsync("Лого синее")).Status);
        var pending = Assert.Single((await sb.Owner.Engine.CheckStatusAsync()).Requests);

        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.MergeWithClaudeAsync(pending, _ => Task.FromResult<string?>(null))).Status);

        Assert.Equal(new byte[] { 0, 9, 9 }, File.ReadAllBytes(Path.Combine(sb.Owner.Dir, "Assets", "Art", "logo.bin")));
    }
}

static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
