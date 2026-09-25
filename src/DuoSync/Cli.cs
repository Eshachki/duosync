using DuoSync.Core.Git;
using DuoSync.Core.Ops;
using DuoSync.Core.Setup;

namespace DuoSync;

/// <summary>
/// Debug/automation entry: <c>DuoSync.exe --cli prepare|receive|send|undo|status &lt;folder&gt; [message]</c>.
/// Same engine and settings as the window (DUOSYNC_HOME applies).
/// </summary>
static class Cli
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --cli prepare|receive|send|undo|status <folder> [message]");
            return 2;
        }
        var settings = AppSettings.Load();
        var me = string.IsNullOrWhiteSpace(settings.MeName) ? Environment.UserName : settings.MeName;
        var friend = string.IsNullOrWhiteSpace(settings.FriendName) ? "друг" : settings.FriendName;
        var email = string.IsNullOrWhiteSpace(settings.MeEmail) ? "duosync@example.invalid" : settings.MeEmail;
        var folder = Path.GetFullPath(args[2]);
        var repo = new Repo(new GitRunner(folder, me, email));
        var engine = new SyncEngine(repo, new SyncOptions { MeName = me, FriendName = friend, Progress = Console.WriteLine },
            new DuoSync.Core.Unity.UnityBridgeClient(folder));

        OpResult result;
        switch (args[1])
        {
            case "prepare":
                result = new ProjectSetup(repo, me, friend).ApplyAsync().GetAwaiter().GetResult();
                break;
            case "receive":
                result = engine.ReceiveAsync().GetAwaiter().GetResult();
                break;
            case "send":
                result = engine.SendAsync(args.Length > 3 ? args[3] : null).GetAwaiter().GetResult();
                break;
            case "undo":
                result = engine.UndoReceiveAsync().GetAwaiter().GetResult();
                break;
            case "unity":
                // Debug: --cli unity <folder> save|stopPlay|state
                var bridge = new DuoSync.Core.Unity.UnityBridgeClient(folder);
                var what = args.Length > 3 ? args[3] : "state";
                if (what == "state")
                {
                    var s = bridge.ReadState();
                    Console.WriteLine(s == null ? "no state" : $"phase={s.phase} parked={s.parked} compileFailed={s.compileFailed} bgAgeMs={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.bgMs}");
                    return 0;
                }
                var br = what == "stopPlay" ? bridge.StopPlayAsync(default).GetAwaiter().GetResult() : bridge.SaveAsync(default).GetAwaiter().GetResult();
                Console.WriteLine($"{(br.Ok ? "ok" : "fail")}: {br.Error} {string.Join("; ", br.CompileErrors)}");
                return br.Ok ? 0 : 1;
            case "status":
                var st = engine.CheckStatusAsync().GetAwaiter().GetResult();
                Console.WriteLine($"{st.State} incoming={st.IncomingFiles.Count} unsent={st.UnsentFiles.Count} overlap={string.Join(",", st.Overlap)}");
                return 0;
            default:
                Console.Error.WriteLine("unknown command " + args[1]);
                return 2;
        }
        Console.WriteLine($"{result.Status}: {result.Message}");
        if (!string.IsNullOrWhiteSpace(result.Detail)) Console.WriteLine("detail: " + result.Detail.Trim());
        if (result.ConflictedPaths.Count > 0) Console.WriteLine("conflicts: " + string.Join(", ", result.ConflictedPaths));
        return result.Succeeded ? 0 : 1;
    }
}
