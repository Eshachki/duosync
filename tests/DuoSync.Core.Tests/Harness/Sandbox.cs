using System.Text;
using DuoSync.Core.Git;
using DuoSync.Core.Ops;

namespace DuoSync.Core.Tests.Harness;

/// <summary>A local "GitHub" (bare repository) and two working copies: the owner and the friend. Synthetic content only.</summary>
public sealed class Sandbox : IAsyncDisposable
{
    public string Root { get; }
    public string RemoteDir { get; }
    public Clone Owner { get; private set; } = null!;
    public Clone Friend { get; private set; } = null!;

    Sandbox(string root)
    {
        Root = root;
        RemoteDir = Path.Combine(root, "remote.git");
    }

    public static async Task<Sandbox> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "duosync-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(root);
        var sb = new Sandbox(root);

        var raw = new GitRunner(root, "Seed", "seed@example.invalid");
        await raw.RunCheckedAsync("init", "--bare", "-b", "main", sb.RemoteDir);

        var seedDir = Path.Combine(root, "seed");
        await raw.RunCheckedAsync("clone", "-q", sb.RemoteDir, seedDir);
        var seed = new GitRunner(seedDir, "Seed", "seed@example.invalid");
        await seed.RunCheckedAsync("checkout", "-q", "-B", "main");
        Write(seedDir, ".gitignore", "*.log\n\n" + DuoSync.Core.Setup.Templates.GitIgnoreBlock());
        Write(seedDir, ".gitattributes", DuoSync.Core.Setup.Templates.GitAttributesBlock(Array.Empty<string>()));
        Write(seedDir, "Assets/Scripts/Player.cs", Lines("class Player", "{", "    int speed = 5;", "    int jump = 2;", "}"));
        Write(seedDir, "Assets/Scripts/Player.cs.meta", "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\n");
        Write(seedDir, "Assets/Scenes/Main.unity", Lines("%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!1 &100", "GameObject:", "  m_Name: Hero"));
        Write(seedDir, "Assets/Scenes/Main.unity.meta", "fileFormatVersion: 2\nguid: 22222222222222222222222222222222\n");
        Write(seedDir, "ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.5.9f1\n");
        await seed.RunCheckedAsync("add", "-A");
        await seed.RunCheckedAsync("commit", "-q", "-m", "Seed");
        await seed.RunCheckedAsync("push", "-q", "origin", "main");

        sb.Owner = await Clone.CreateAsync(sb, "owner", "Аня", "Боря");
        sb.Friend = await Clone.CreateAsync(sb, "friend", "Боря", "Аня");
        return sb;
    }

    public static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    public static void Write(string dir, string rel, string content)
    {
        var full = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    public async Task<string> RemoteHeadAsync()
        => await new GitRunner(RemoteDir, "t", "t@example.invalid").OutAsync("rev-parse", "main");

    public ValueTask DisposeAsync()
    {
        ForceDelete(Root);
        return ValueTask.CompletedTask;
    }

    static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        for (int i = 0; i < 5; i++)
        {
            try { Directory.Delete(dir, true); return; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}

public sealed class Clone
{
    public string Dir { get; }
    public List<string> ProgressLines { get; } = new();
    public GitRunner Git { get; }
    public Repo Repo { get; }
    public SyncEngine Engine { get; }
    /// <summary>Tests switch the role here: CanResolve and Resolver make this clone the integrator.</summary>
    public SyncOptions Options { get; }

    Clone(string dir, string me, string friend)
    {
        Dir = dir;
        Git = new GitRunner(dir, me, me.ToLowerInvariant() + "@example.invalid");
        Repo = new Repo(Git);
        Options = new SyncOptions
        {
            MeName = me, FriendName = friend, PushLfs = false,
            RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero },
            Progress = ProgressLines.Add,
            ToolCacheDir = Path.Combine(Path.GetTempPath(), "duosync-tests", "uym-cache"),
        };
        Engine = new SyncEngine(Repo, Options);
    }

    public static async Task<Clone> CreateAsync(Sandbox sb, string name, string me, string friend)
    {
        var dir = Path.Combine(sb.Root, name);
        var raw = new GitRunner(sb.Root, me, "x@example.invalid");
        await raw.RunCheckedAsync("clone", "-q", sb.RemoteDir, dir);
        return new Clone(dir, me, friend);
    }

    public void Write(string rel, string content) => Sandbox.Write(Dir, rel, content);
    public string Read(string rel) => File.ReadAllText(Path.Combine(Dir, rel)).Replace("\r\n", "\n");
    public bool Exists(string rel) => File.Exists(Path.Combine(Dir, rel));
    public void Delete(string rel) => File.Delete(Path.Combine(Dir, rel));
    public Task<string> OutAsync(params string[] args) => Git.OutAsync(args);
    public async Task<string> HeadAsync() => (await Repo.HeadAsync())!;
}
