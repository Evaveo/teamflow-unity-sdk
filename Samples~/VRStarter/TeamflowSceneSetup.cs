using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using TeamflowSDK;

/// <summary>
/// Editor utility — sets up TeamFlow in the current scene in one click.
/// Menu: Tools → TeamFlow → Setup VR Scene
///
/// What this creates:
///   • [TeamflowClient]          — singleton, persists across scenes
///   • [TeamflowHUD]             — IMGUI overlay always on top, world-locked for VR
///   • [WhisperBackendInference] — Whisper STT (if com.unity.ai.inference installed)
/// </summary>
#if UNITY_EDITOR
public class TeamflowSceneSetup : Editor
{
    private const string ASSET_FOLDER  = "Assets/WhisperModels";
    private const string ENCODER_FILE  = "whisper_encoder.onnx";
    private const string DECODER1_FILE = "whisper_decoder.onnx";
    private const string DECODER2_FILE = "whisper_decoder_with_past.onnx";
    private const string LOGMEL_FILE   = "whisper_logmel.onnx";

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

        // ── TeamflowHUD ────────────────────────────────────────────────────
        var hudGO = GameObject.Find("[TeamflowHUD]");
        if (hudGO == null)
        {
            hudGO = new GameObject("[TeamflowHUD]");
            hudGO.AddComponent<TeamflowHUD>();
            Debug.Log("[TeamFlow Setup] Created [TeamflowHUD]");
        }

        // ── WhisperBackendInference ────────────────────────────────────────
        bool whisperConfigured = SetupWhisper();

        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Selection.activeGameObject = clientGO;

        string whisperStatus = whisperConfigured
            ? "✅ WhisperBackendInference configuré avec les modèles."
            : "⚠️ Modèles Whisper manquants — lance Tools → TeamFlow → Download Whisper Models.";

        EditorUtility.DisplayDialog(
            "TeamFlow VR Setup ✅",
            "Scène configurée !\n\n" +
            "Créé :\n" +
            "  • [TeamflowClient]\n" +
            "  • [TeamflowHUD]\n" +
            "  • [WhisperBackendInference]\n\n" +
            whisperStatus + "\n\n" +
            "Étapes suivantes :\n" +
            "1. Sélectionne [TeamflowClient] → vérifie Base Url\n" +
            "2. Sauvegarde la scène (Ctrl+S)\n" +
            "3. Appuie sur Play — le HUD apparaît en haut à droite.\n" +
            "   Bouton ☰ ou F1 pour l'ouvrir.",
            "OK");

        if (!whisperConfigured)
        {
            bool openDownloader = EditorUtility.DisplayDialog(
                "Télécharger les modèles Whisper ?",
                "Les modèles Whisper ne sont pas encore téléchargés.\n\n" +
                "Veux-tu ouvrir le téléchargeur maintenant ?\n" +
                "(~450 MB — nécessite com.unity.ai.inference)",
                "Ouvrir le téléchargeur", "Plus tard");

            if (openDownloader)
                TeamflowSDK.Editor.WhisperModelDownloader.ShowWindow();
        }
    }

    private static bool SetupWhisper()
    {
#if UNITY_AI_INFERENCE
        var encoder  = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>($"{ASSET_FOLDER}/{ENCODER_FILE}");
        var decoder1 = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>($"{ASSET_FOLDER}/{DECODER1_FILE}");
        var decoder2 = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>($"{ASSET_FOLDER}/{DECODER2_FILE}");
        var logmel   = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>($"{ASSET_FOLDER}/{LOGMEL_FILE}");

        var existing = Object.FindAnyObjectByType<TeamflowSDK.WhisperBackendInference>();
        GameObject go = existing != null
            ? existing.gameObject
            : new GameObject("[WhisperBackendInference]");

        if (existing == null)
            go.AddComponent<TeamflowSDK.WhisperBackendInference>();

        if (encoder != null && decoder1 != null && decoder2 != null && logmel != null)
        {
            var so = new SerializedObject(go.GetComponent<TeamflowSDK.WhisperBackendInference>());
            so.FindProperty("audioEncoder").objectReferenceValue  = encoder;
            so.FindProperty("audioDecoder1").objectReferenceValue = decoder1;
            so.FindProperty("audioDecoder2").objectReferenceValue = decoder2;
            so.FindProperty("logMelSpectro").objectReferenceValue = logmel;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(go);
            Debug.Log("[TeamFlow Setup] WhisperBackendInference configuré avec les ModelAssets.");
            return true;
        }

        Debug.LogWarning("[TeamFlow Setup] Modèles Whisper non trouvés dans Assets/WhisperModels/ — composant créé sans modèles.");
        return false;
#else
        Debug.LogWarning("[TeamFlow Setup] com.unity.ai.inference non installé — WhisperBackendInference ignoré.");
        return false;
#endif
    }

    [MenuItem("Tools/TeamFlow/Remove from Scene", priority = 10)]
    public static void RemoveFromScene()
    {
        int removed = 0;
        foreach (var name in new[] { "[TeamflowClient]", "[TeamflowHUD]", "[WhisperBackendInference]", "[WhisperManager]" })
        {
            var go = GameObject.Find(name);
            if (go != null) { DestroyImmediate(go); removed++; }
        }
        Debug.Log($"[TeamFlow Setup] Removed {removed} TeamFlow object(s) from scene.");
    }
}
#endif
