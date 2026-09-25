using System;
using System.IO;

namespace DuoSync.Bridge
{
    /// <summary>cmd-&lt;id&gt;.json written by the DuoSync app.</summary>
    [Serializable]
    public sealed class BridgeCommand
    {
        public string id;
        public string op;
        public string[] paths;
        public long createdUtcMs;
    }

    /// <summary>res-&lt;id&gt;.json written by the bridge when the command is done.</summary>
    [Serializable]
    public sealed class BridgeResponse
    {
        public string id;
        public bool ok;
        public string error;
        public string[] compileErrors;
        public int missingScripts;
        public string[] reopened;
        public long utcMs;
    }

    [Serializable]
    public sealed class SceneInfo
    {
        public string path;
        public string guid;
        public bool dirty;
        public bool active;
        public bool loaded;
    }

    /// <summary>state.json: refreshed on the main thread every 250 ms, written by a background thread every second.</summary>
    [Serializable]
    public sealed class BridgeState
    {
        public int v = 1;
        public string helper;
        public int pid;
        public string unity;
        public string projectPath;
        public long bgMs;
        public long mainMs;
        public string phase;
        public bool focused;
        public bool playing;
        public bool compiling;
        public bool updating;
        public bool compileFailed;
        public string[] errors;
        public SceneInfo[] scenes;
        public bool untitledDirty;
        public string prefabStagePath;
        public bool prefabStageDirty;
        public string[] unsavedWindows;
        public bool parked;
        public string[] guard;
        public string currentOp;
        public string logPath;
        public int domainReloads;
    }

    [Serializable]
    public sealed class ParkedScene
    {
        public string path;
        public string guid;
        public bool active;
        public bool loaded;
        public bool closed;
    }

    [Serializable]
    public sealed class ParkState
    {
        public ParkedScene[] scenes;
        public bool tempScene;
        public long parkedUtcMs;
    }

    /// <summary>lease.json: the app keeps it fresh while it waits for the bridge; the watchdog uses it.</summary>
    [Serializable]
    public sealed class Lease
    {
        public int pid;
        public long untilUtcMs;
    }

    static class BridgeIO
    {
        public const string Dir = "Library/DuoSync";

        public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static string Path(string name) => System.IO.Path.Combine(Dir, name);

        public static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(Dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                return sr.ReadToEnd();
        }
    }
}
