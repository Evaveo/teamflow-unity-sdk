using UnityEngine;
using UnityEditor;
using TeamflowSDK;

/// <summary>
/// Editor utility — adds the TeamFlow HUD to the current scene in one click.
/// Menu: Tools → TeamFlow → Setup VR Scene
/// 
/// What this creates:
///   • [TeamflowClient] — singleton, persists across scenes
///   • [TeamflowHUD]    — IMGUI overlay always on top, world-locked for VR
///   • [WhisperManager] — auto-created by WhisperManager.Instance at runtime
/// 
/// For Meta Quest / XR:
///   The HUD uses Unity's OnGUI (IMGUI) rendered in screen-space on top of
///   everything — it works in VR without any additional World Space Canvas setup.
///   The panel opens/closes with a virtual button you map to a controller input.
/// </summary>
#if UNITY_EDITOR
public class TeamflowSceneSetup : Editor
{
    [MenuItem("Tools/TeamFlow/Setup VR Scene", priority = 0)]
    public static void SetupScene()
    {
        // ── TeamflowClient ─────────────────────────────────────────────────
        var clientGO = GameObject.Find("[TeamflowClient]");
        if (clientGO == null)
        {
            clientGO = new GameObject("[TeamflowClient]");
            clientGO.AddComponent<TeamflowClient>();
            Debug.Log("[TeamFlow Setup] Created [TeamflowClient]");
        }
        else
        {
            Debug.Log("[TeamFlow Setup] [TeamflowClient] already exists — skipped.");
        }

        // ── TeamflowHUD ────────────────────────────────────────────────────
        var hudGO = GameObject.Find("[TeamflowHUD]");
        if (hudGO == null)
        {
            hudGO = new GameObject("[TeamflowHUD]");
            hudGO.AddComponent<TeamflowHUD>();
            Debug.Log("[TeamFlow Setup] Created [TeamflowHUD]");
        }
        else
        {
            Debug.Log("[TeamFlow Setup] [TeamflowHUD] already exists — skipped.");
        }

        // Select HUD in hierarchy so dev can configure BaseUrl in Inspector
        Selection.activeGameObject = clientGO;

        EditorUtility.DisplayDialog(
            "TeamFlow VR Setup ✅",
            "Scene configured successfully!\n\n" +
            "Next steps:\n" +
            "1. Select [TeamflowClient] in the Hierarchy\n" +
            "2. Set Base Url to:\n   https://teamflow-api-544622760078.us-central1.run.app\n\n" +
            "3. (Optional) For offline speech-to-text:\n" +
            "   • Install com.unity.sentis via Package Manager\n" +
            "   • Tools → TeamFlow → Download Whisper Models\n\n" +
            "4. Press Play — the HUD appears top-right.\n" +
            "   Click the ☰ button or press F1 to toggle it.",
            "OK");
    }

    [MenuItem("Tools/TeamFlow/Remove from Scene", priority = 10)]
    public static void RemoveFromScene()
    {
        int removed = 0;
        foreach (var name in new[] { "[TeamflowClient]", "[TeamflowHUD]", "[WhisperManager]" })
        {
            var go = GameObject.Find(name);
            if (go != null) { GameObject.DestroyImmediate(go); removed++; }
        }
        Debug.Log($"[TeamFlow Setup] Removed {removed} TeamFlow object(s) from scene.");
    }
}
#endif
