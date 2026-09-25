using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuoSync.Bridge
{
    /// <summary>
    /// Entry point of the DuoSync bridge (architecture §4, decisions E). Everything runs from
    /// <see cref="EditorApplication.update"/>: delayCall does not fire while Unity is unfocused.
    /// The heartbeat is written from a background thread so the app can tell "busy importing" from "gone".
    /// </summary>
    [InitializeOnLoad]
    public static class DuoSyncBridge
    {
        public const string Version = "0.1.0";
        const string ReloadsKey = "DuoSync.domainReloads";
        const string ErrorsKey = "DuoSync.compileErrors";

        static readonly object Gate = new object();
        static BridgeState _state;
        static System.Threading.Timer _heartbeat;
        static double _nextState, _nextWindows;
        static string[] _unsavedWindows = new string[0];

        /// <summary>
        /// Asset import workers are separate Unity processes that load editor scripts too; a bridge there would write
        /// an empty state and could take the app's commands (found on stage 1). Batch mode has no person to serve.
        /// </summary>
        public static bool Enabled { get; private set; }

        static DuoSyncBridge()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || Application.isBatchMode) return;
            Enabled = true;
            Directory.CreateDirectory(BridgeIO.Dir);
            SessionState.SetInt(ReloadsKey, SessionState.GetInt(ReloadsKey, 0) + 1);
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += StopHeartbeat;
            EditorApplication.quitting += StopHeartbeat;
            CompilationPipeline.compilationStarted += _ => SessionState.SetString(ErrorsKey, "");
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompileGuard.OnDomainLoaded();
            BridgeOps.Init();
            RefreshState();
            _heartbeat = new System.Threading.Timer(_ => WriteHeartbeat(), null, 0, 1000);
        }

        static void StopHeartbeat()
        {
            var t = _heartbeat;
            _heartbeat = null;
            if (t != null) t.Dispose();
        }

        static void Tick()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now >= _nextState)
            {
                _nextState = now + 0.25;
                RefreshState();
            }
            BridgeOps.Pump();
        }

        static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            var errors = messages.Where(m => m.type == CompilerMessageType.Error)
                .Select(m => m.message).ToList(); // the message already starts with "file(line,col):"
            if (errors.Count == 0) return;
            var old = SessionState.GetString(ErrorsKey, "");
            SessionState.SetString(ErrorsKey, old + string.Join("\n", errors) + "\n");
        }

        public static string[] CompileErrors
        {
            get
            {
                return SessionState.GetString(ErrorsKey, "")
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct().Take(50).ToArray();
            }
        }

        public static List<Scene> OpenScenes()
        {
            var list = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++) list.Add(SceneManager.GetSceneAt(i));
            return list;
        }

        public static string[] UnsavedWindows()
        {
            return Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(w => w != null && w.hasUnsavedChanges)
                .Select(w => w.titleContent != null ? w.titleContent.text : w.GetType().Name).ToArray();
        }

        /// <summary>Called right after a command response so the state never lags behind it.</summary>
        public static void RefreshNow()
        {
            RefreshState();
            WriteHeartbeat();
        }

        static void RefreshState()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now >= _nextWindows)
            {
                _nextWindows = now + 2;
                _unsavedWindows = UnsavedWindows();
            }
            var active = SceneManager.GetActiveScene();
            var scenes = OpenScenes();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var s = new BridgeState
            {
                helper = Version,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                unity = Application.unityVersion,
                projectPath = Directory.GetCurrentDirectory().Replace('\\', '/'),
                mainMs = BridgeIO.NowMs,
                phase = BridgeOps.Phase,
                focused = InternalEditorUtility.isApplicationActive,
                playing = EditorApplication.isPlayingOrWillChangePlaymode,
                compiling = EditorApplication.isCompiling,
                updating = EditorApplication.isUpdating,
                compileFailed = EditorUtility.scriptCompilationFailed,
                errors = CompileErrors,
                scenes = scenes.Select(sc => new SceneInfo
                {
                    path = sc.path,
                    guid = string.IsNullOrEmpty(sc.path) ? "" : AssetDatabase.AssetPathToGUID(sc.path),
                    dirty = sc.isDirty,
                    active = sc == active,
                    loaded = sc.isLoaded,
                }).ToArray(),
                untitledDirty = scenes.Any(sc => sc.isDirty && string.IsNullOrEmpty(sc.path)),
                prefabStagePath = stage != null ? stage.assetPath : "",
                prefabStageDirty = stage != null && stage.scene.isDirty,
                unsavedWindows = _unsavedWindows,
                parked = BridgeOps.IsParked,
                guard = CompileGuard.Paths,
                currentOp = BridgeOps.CurrentOpId,
                logPath = Application.consoleLogPath,
                domainReloads = SessionState.GetInt(ReloadsKey, 0),
            };
            lock (Gate) _state = s;
        }

        /// <summary>Background thread: JsonUtility is safe off the main thread, other Unity APIs are not used here.</summary>
        static void WriteHeartbeat()
        {
            string json;
            lock (Gate)
            {
                if (_state == null) return;
                _state.bgMs = BridgeIO.NowMs;
                json = JsonUtility.ToJson(_state);
            }
            try { BridgeIO.AtomicWrite(BridgeIO.Path("state.json"), json); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
