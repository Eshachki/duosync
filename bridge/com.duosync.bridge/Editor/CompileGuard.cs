using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DuoSync.Bridge
{
    /// <summary>
    /// After receiving, if the code does not compile Unity still runs the old code: saving an incoming scene or prefab
    /// then would silently drop the friend's new serialized fields. Until the next successful compilation those
    /// paths are not saved (architecture §4.3 step 7, decisions E4).
    /// </summary>
    public static class CompileGuard
    {
        const string Key = "DuoSync.guard";

        public static string[] Paths
        {
            get { return SessionState.GetString(Key, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries); }
        }

        public static bool IsGuarded(string assetPath)
        {
            return Paths.Contains(assetPath, StringComparer.OrdinalIgnoreCase);
        }

        public static void Activate(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            SessionState.SetString(Key, string.Join("\n", paths));
            Debug.LogError("DuoSync: есть ошибки компиляции. Пока они не исправлены, полученные сцены и префабы не сохраняются, " +
                           "иначе Unity сотрёт новые поля друга. Исправь ошибки или сними защиту в окне сцены (панель DuoSync).");
        }

        public static void Clear()
        {
            SessionState.EraseString(Key);
        }

        /// <summary>A domain reload means the code compiled; a failed compilation does not reload the domain.</summary>
        public static void OnDomainLoaded()
        {
            if (Paths.Length > 0 && !EditorUtility.scriptCompilationFailed)
            {
                Clear();
                Debug.Log("DuoSync: код скомпилировался, защита сохранения снята.");
            }
        }
    }

    public sealed class DuoSyncSaveGuard : AssetModificationProcessor
    {
        static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string[] OnWillSaveAssets(string[] paths)
        {
            if (!DuoSyncBridge.Enabled) return paths;
            var guarded = CompileGuard.Paths;
            if (guarded.Length == 0) return paths;
            var set = new HashSet<string>(guarded, StringComparer.OrdinalIgnoreCase);
            var keep = new List<string>(paths.Length);
            foreach (var p in paths)
            {
                var asset = p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 5) : p;
                if (set.Contains(asset))
                {
                    if (Logged.Add(p))
                        Debug.LogError("DuoSync: " + p + " не сохранён — пока есть ошибки компиляции, сохранение стёрло бы новые поля друга.");
                    continue;
                }
                keep.Add(p);
            }
            return keep.ToArray();
        }
    }
}
