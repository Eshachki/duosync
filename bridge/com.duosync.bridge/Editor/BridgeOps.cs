using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using JsonUtility = UnityEngine.JsonUtility;

namespace DuoSync.Bridge
{
    /// <summary>
    /// Commands from the app, one at a time. Each op is a small state machine whose step lives in SessionState,
    /// so a domain reload in the middle (new scripts after Refresh) continues where it stopped (decisions E1).
    /// </summary>
    static class BridgeOps
    {
        const string K = "DuoSync.op.";
        const string ParkKey = "DuoSync.park";
        const string DisallowedKey = "DuoSync.autoRefreshDisallowed";

        static readonly HashSet<string> YamlExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".unity", ".prefab", ".asset", ".mat", ".anim", ".controller", ".overrideController", ".mask", ".playable",
            ".physicMaterial", ".physicsMaterial2D", ".lighting", ".preset", ".signal", ".mixer", ".spriteatlas", ".spriteatlasv2",
            ".terrainlayer", ".renderTexture", ".fontsettings", ".guiskin", ".brush", ".shadervariants", ".scenetemplate",
        };

        static double _nextScan;

        public static string CurrentOpId => SessionState.GetString(K + "id", "");
        public static bool IsParked => SessionState.GetString(ParkKey, "").Length > 0;

        public static string Phase
        {
            get
            {
                var op = SessionState.GetString(K + "op", "");
                if (op.Length > 0) return op + ":" + StepName;
                if (IsParked) return "parked";
                if (EditorApplication.isCompiling) return "compiling";
                if (EditorApplication.isUpdating) return "importing";
                if (EditorApplication.isPlayingOrWillChangePlaymode) return "playing";
                return "idle";
            }
        }

        static string StepName
        {
            get { return SessionState.GetString(K + "step", ""); }
            set { SessionState.SetString(K + "step", value); }
        }

        static string[] Paths
        {
            get { return SessionState.GetString(K + "paths", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries); }
        }

        public static void Init()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode && IsParked)
                {
                    EditorApplication.isPlaying = false;
                    Debug.LogWarning("DuoSync: идёт получение изменений, Play Mode будет доступен после него.");
                }
            };
        }

        public static void Pump()
        {
            if (CurrentOpId.Length == 0)
            {
                var now = EditorApplication.timeSinceStartup;
                if (now < _nextScan) return;
                _nextScan = now + 0.2;
                if (!PickNext()) Watchdog();
                return;
            }
            try { Step(); }
            catch (Exception e)
            {
                Debug.LogException(e);
                Respond(false, "Ошибка моста DuoSync: " + e.Message);
            }
        }

        static string ResPath(string id) { return BridgeIO.Path("res-" + id + ".json"); }

        static bool PickNext()
        {
            string[] files;
            try { files = Directory.GetFiles(BridgeIO.Dir, "cmd-*.json"); }
            catch (IOException) { return false; }
            foreach (var f in files.OrderBy(File.GetCreationTimeUtc))
            {
                BridgeCommand cmd = null;
                try { cmd = JsonUtility.FromJson<BridgeCommand>(BridgeIO.ReadShared(f)); }
                catch (Exception) { }
                TryDelete(f);
                if (cmd == null || string.IsNullOrEmpty(cmd.id) || File.Exists(ResPath(cmd.id))) continue;
                Begin(cmd.id, cmd.op, cmd.paths);
                return true;
            }
            return false;
        }

        static void Begin(string id, string op, string[] paths)
        {
            SessionState.SetString(K + "id", id);
            SessionState.SetString(K + "op", op ?? "");
            SessionState.SetString(K + "paths", string.Join("\n", paths ?? new string[0]));
            StepName = "start";
        }

        static void Step()
        {
            switch (SessionState.GetString(K + "op", ""))
            {
                case "status": Respond(true, null); break;
                case "save": SaveStep(); break;
                case "park": Park(); break;
                case "finish": FinishStep(); break;
                case "stopPlay": StopPlayStep(); break;
                default: Respond(false, "Неизвестная команда моста."); break;
            }
        }

        // ------------------------------------------------------------------ save

        static string SaveRefusal()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Unity в режиме игры (Play Mode). Останови игру и повтори.";
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.isDirty) return "Открыт несохранённый префаб. Сохрани его (Ctrl+S) или выйди из режима префаба.";
            var windows = DuoSyncBridge.UnsavedWindows();
            if (windows.Length > 0) return "Есть несохранённые окна: " + string.Join(", ", windows) + ". Сохрани их или закрой.";
            if (DuoSyncBridge.OpenScenes().Any(s => s.isDirty && string.IsNullOrEmpty(s.path)))
                return "Есть несохранённая безымянная сцена. Сохрани её (Ctrl+S) или закрой.";
            return null;
        }

        static void SaveStep()
        {
            if (StepName == "start")
            {
                var refusal = SaveRefusal();
                if (refusal != null) { Respond(false, refusal); return; }
                var dirty = DuoSyncBridge.OpenScenes()
                    .Where(s => s.isDirty && !string.IsNullOrEmpty(s.path) && !CompileGuard.IsGuarded(s.path)).ToArray();
                StepName = "wait-idle"; // before Refresh: new scripts may reload the domain
                if (dirty.Length > 0) EditorSceneManager.SaveScenes(dirty);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(); // .meta for files that a neural network wrote past Unity
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            // SaveScenes may report success while a save guard blocked the write (6000.3): trust isDirty.
            var still = DuoSyncBridge.OpenScenes()
                .Where(s => s.isDirty && !string.IsNullOrEmpty(s.path) && !CompileGuard.IsGuarded(s.path)).Select(s => s.path).ToArray();
            if (still.Length > 0) { Respond(false, "Не удалось сохранить: " + string.Join(", ", still)); return; }
            Respond(true, null, EditorUtility.scriptCompilationFailed ? DuoSyncBridge.CompileErrors : null);
        }

        // ------------------------------------------------------------------ park

        static void Park()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Respond(false, "Unity в режиме игры (Play Mode). Останови игру и повтори."); return; }
            var wanted = new HashSet<string>(Paths, StringComparer.OrdinalIgnoreCase);
            var scenes = DuoSyncBridge.OpenScenes();
            var affected = scenes.Where(s => !string.IsNullOrEmpty(s.path) && wanted.Contains(s.path)).ToList();
            var dirty = affected.Where(s => s.isDirty).Select(s => s.path).ToArray();
            if (dirty.Length > 0)
            {
                Respond(false, "Сцена " + string.Join(", ", dirty) + " изменилась во время получения. Сохрани её и повтори.");
                return;
            }

            // Snapshot BEFORE closing (decisions E2), GUIDs so a moved scene reopens at its new path.
            var active = SceneManager.GetActiveScene();
            var state = new ParkState
            {
                scenes = scenes.Select(s => new ParkedScene
                {
                    path = s.path,
                    guid = string.IsNullOrEmpty(s.path) ? "" : AssetDatabase.AssetPathToGUID(s.path),
                    active = s == active,
                    loaded = s.isLoaded,
                    closed = affected.Contains(s),
                }).ToArray(),
                tempScene = affected.Count > 0 && affected.Count == scenes.Count,
                parkedUtcMs = BridgeIO.NowMs,
            };
            if (state.tempScene) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            foreach (var s in affected) EditorSceneManager.CloseScene(s, true);

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && wanted.Contains(stage.assetPath)) StageUtility.GoToMainStage();

            AssetDatabase.DisallowAutoRefresh();
            SessionState.SetBool(DisallowedKey, true);
            AssetDatabase.ReleaseCachedFileHandles();
            var json = JsonUtility.ToJson(state);
            SessionState.SetString(ParkKey, json);
            BridgeIO.AtomicWrite(BridgeIO.Path("park.json"), json);
            Respond(true, null);
        }

        // ------------------------------------------------------------------ finish

        static void FinishStep()
        {
            switch (StepName)
            {
                case "start":
                    if (EditorApplication.isPlayingOrWillChangePlaymode) { EditorApplication.isPlaying = false; return; }
                    AllowRefreshOnce();
                    if (Paths.Any(p => p == "Packages/manifest.json" || p == "Packages/packages-lock.json"))
                        UnityEditor.PackageManager.Client.Resolve();
                    StepName = "wait-idle"; // before Refresh: compilation may reload the domain
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    return;
                case "wait-idle":
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                    StepName = "reopen";
                    return;
                default:
                    var reopened = ReopenParked();
                    var failed = EditorUtility.scriptCompilationFailed;
                    if (failed) CompileGuard.Activate(Paths.Where(p => YamlExtensions.Contains(Path.GetExtension(p))).ToArray());
                    Respond(true, null, failed ? DuoSyncBridge.CompileErrors : null, CountMissingScripts(), reopened);
                    return;
            }
        }

        static void AllowRefreshOnce()
        {
            if (!SessionState.GetBool(DisallowedKey, false)) return;
            AssetDatabase.AllowAutoRefresh();
            SessionState.SetBool(DisallowedKey, false);
        }

        /// <summary>
        /// Opens again only the scenes park closed. RestoreSceneManagerSetup is not used: it reloads untouched scenes
        /// and silently drops their unsaved edits (decisions E3).
        /// </summary>
        static string[] ReopenParked()
        {
            var json = SessionState.GetString(ParkKey, "");
            if (json.Length == 0) return new string[0];
            var state = JsonUtility.FromJson<ParkState>(json);
            var reopened = new List<string>();
            var current = new List<KeyValuePair<ParkedScene, string>>();
            foreach (var e in state.scenes)
            {
                var path = string.IsNullOrEmpty(e.guid) ? e.path : AssetDatabase.GUIDToAssetPath(e.guid);
                current.Add(new KeyValuePair<ParkedScene, string>(e, path));
            }
            foreach (var pair in current.Where(c => c.Key.closed))
            {
                var path = pair.Value;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue; // deleted by the incoming change
                if (!SceneManager.GetSceneByPath(path).IsValid())
                {
                    EditorSceneManager.OpenScene(path, pair.Key.loaded ? OpenSceneMode.Additive : OpenSceneMode.AdditiveWithoutLoading);
                    reopened.Add(path);
                }
            }
            var ordered = current.Where(c => !string.IsNullOrEmpty(c.Value))
                .Select(c => SceneManager.GetSceneByPath(c.Value)).Where(s => s.IsValid()).ToList();
            for (int i = 1; i < ordered.Count; i++) EditorSceneManager.MoveSceneAfter(ordered[i], ordered[i - 1]);
            if (ordered.Count > 0 && SceneManager.GetSceneAt(0) != ordered[0]) EditorSceneManager.MoveSceneBefore(ordered[0], SceneManager.GetSceneAt(0));
            var act = current.FirstOrDefault(c => c.Key.active && !string.IsNullOrEmpty(c.Value));
            if (act.Key != null)
            {
                var s = SceneManager.GetSceneByPath(act.Value);
                if (s.IsValid() && s.isLoaded) SceneManager.SetActiveScene(s);
            }
            foreach (var s in DuoSyncBridge.OpenScenes().Where(s => string.IsNullOrEmpty(s.path) && !s.isDirty).ToList())
                if (SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(s, true);
            SessionState.EraseString(ParkKey);
            TryDelete(BridgeIO.Path("park.json"));
            return reopened.ToArray();
        }

        static int CountMissingScripts()
        {
            int count = 0;
            foreach (var s in DuoSyncBridge.OpenScenes().Where(s => s.isLoaded))
                foreach (var root in s.GetRootGameObjects())
                    foreach (var t in root.GetComponentsInChildren<UnityEngine.Transform>(true))
                        count += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
            return count;
        }

        // ------------------------------------------------------------------ stopPlay

        static void StopPlayStep()
        {
            if (StepName == "start" && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                StepName = "wait";
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Respond(true, null);
        }

        // ------------------------------------------------------------------ watchdog

        /// <summary>If the app died while scenes were parked, bring them back instead of leaving Unity half-closed.</summary>
        static void Watchdog()
        {
            if (!IsParked) return;
            var leasePath = BridgeIO.Path("lease.json");
            bool expired;
            if (File.Exists(leasePath))
            {
                Lease lease = null;
                try { lease = JsonUtility.FromJson<Lease>(BridgeIO.ReadShared(leasePath)); } catch (Exception) { }
                expired = lease == null || (BridgeIO.NowMs > lease.untilUtcMs + 30000 && !ProcessAlive(lease.pid));
            }
            else
            {
                var state = JsonUtility.FromJson<ParkState>(SessionState.GetString(ParkKey, "{}"));
                expired = BridgeIO.NowMs - state.parkedUtcMs > 60000;
            }
            if (!expired) return;
            Debug.LogWarning("DuoSync: программа перестала отвечать во время получения — возвращаю сцены.");
            var closed = JsonUtility.FromJson<ParkState>(SessionState.GetString(ParkKey, "{}")).scenes
                .Where(s => s.closed).Select(s => s.path).ToArray();
            Begin("watchdog-" + BridgeIO.NowMs, "finish", closed);
        }

        static bool ProcessAlive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        // ------------------------------------------------------------------ response

        static void Respond(bool ok, string error, string[] compileErrors = null, int missingScripts = 0, string[] reopened = null)
        {
            var id = CurrentOpId;
            var response = new BridgeResponse
            {
                id = id,
                ok = ok,
                error = error ?? "",
                compileErrors = compileErrors ?? new string[0],
                missingScripts = missingScripts,
                reopened = reopened ?? new string[0],
                utcMs = BridgeIO.NowMs,
            };
            foreach (var k in new[] { "id", "op", "step", "paths" }) SessionState.EraseString(K + k);
            DuoSyncBridge.RefreshNow();
            if (!id.StartsWith("watchdog-", StringComparison.Ordinal))
                BridgeIO.AtomicWrite(ResPath(id), JsonUtility.ToJson(response));
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
