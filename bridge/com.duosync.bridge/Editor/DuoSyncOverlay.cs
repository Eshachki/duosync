using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace DuoSync.Bridge
{
    /// <summary>Scene View panel (visible by default in all 6000.x): bridge status and the save-guard switch.</summary>
    [Overlay(typeof(SceneView), "duosync-status", "DuoSync", true)]
    public sealed class DuoSyncOverlay : IMGUIOverlay
    {
        public override void OnGUI()
        {
            GUILayout.Label(BridgeOps.IsParked ? "Идёт получение изменений…" : "Мост DuoSync работает", EditorStyles.miniLabel);
            if (CompileGuard.Paths.Length > 0)
            {
                GUILayout.Label("Сцены и префабы друга не сохраняются,\nпока есть ошибки компиляции.", EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button("Снять защиту (я понимаю)")) CompileGuard.Clear();
            }
        }
    }
}
