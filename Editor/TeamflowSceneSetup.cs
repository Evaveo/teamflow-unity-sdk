using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using TeamflowSDK;

namespace TeamflowSDK.Editor
{
    /// <summary>
    /// Editor utility — sets up all TeamFlow components in the current scene in one click.
    /// Menu: Tools → TeamFlow → Setup Scene
    ///
    /// Creates:
    ///   • [TeamflowClient]          — singleton, persists across scenes
    ///   • [TeamflowHUD]             — IMGUI overlay (works flat + VR)
    ///   • [WhisperBackendInference] — Whisper STT (requires com.unity.ai.inference)
    ///
    /// Models are stored in Assets/WhisperModels/ as ModelAssets (required by InferenceEngine).
    /// vocab.json is stored in Assets/StreamingAssets/Whisper/ (loaded at runtime via path).
    /// </summary>
    public static class TeamflowSceneSetup
    {
        private const string ASSET_FOLDER  = "Assets/WhisperModels";
        private const string ENCODER_FILE  = "whisper_encoder.onnx";
        private const string DECODER1_FILE = "whisper_decoder.onnx";
        private const string DECODER2_FILE = "whisper_decoder_with_past.onnx";
        private const string LOGMEL_FILE   = "whisper_logmel.onnx";

        [MenuItem("Tools/TeamFlow/Setup Scene", priority = 0)]
        public static void SetupScene()
        {
            // ── TeamflowClient ─────────────────────────────────────────────
            var clientGO = GameObject.Find("[TeamflowClient]");
            if (clientGO == null)
            {
                clientGO = new GameObject("[TeamflowClient]");
                clientGO.AddComponent<TeamflowClient>();
                Debug.Log("[TeamFlow Setup] Created [TeamflowClient]");
            }

            // ── TeamflowHUD ────────────────────────────────────────────────
            var hudGO = GameObject.Find("[TeamflowHUD]");
            if (hudGO == null)
            {
                hudGO = new GameObject("[TeamflowHUD]");
                hudGO.AddComponent<TeamflowHUD>();
                Debug.Log("[TeamFlow Setup] Created [TeamflowHUD]");
            }

            // ── WhisperBackendInference ────────────────────────────────────
            bool whisperConfigured = SetupWhisper();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Selection.activeGameObject = clientGO;

            string whisperStatus = whisperConfigured
                ? "✅ WhisperBackendInference configuré avec les modèles."
                : "⚠️ Modèles Whisper manquants.\n   Lance : Tools → TeamFlow → Download Whisper Models";

            EditorUtility.DisplayDialog(
                "TeamFlow Setup ✅",
                "Scène configurée !\n\n" +
                "Créé :\n" +
                "  • [TeamflowClient]\n" +
                "  • [TeamflowHUD]\n" +
                "  • [WhisperBackendInference]\n\n" +
                whisperStatus + "\n\n" +
                "Étapes suivantes :\n" +
                "1. Sauvegarde la scène (Ctrl+S)\n" +
                "2. Appuie sur Play — le HUD s'affiche en haut à droite\n" +
                "   (bouton ☰ ou F1 pour l'ouvrir)",
                "OK");

            if (!whisperConfigured)
            {
                bool openDownloader = EditorUtility.DisplayDialog(
                    "Télécharger les modèles Whisper ?",
                    "Les modèles ONNX Whisper-Tiny (~450 MB) ne sont pas encore dans Assets/WhisperModels/.\n\n" +
                    "Ouvrir le téléchargeur maintenant ?\n" +
                    "(Nécessite com.unity.ai.inference via Package Manager)",
                    "Ouvrir le téléchargeur", "Plus tard");

                if (openDownloader)
                    WhisperModelDownloader.ShowWindow();
            }
        }

        [MenuItem("Tools/TeamFlow/Remove from Scene", priority = 10)]
        public static void RemoveFromScene()
        {
            int removed = 0;
            foreach (var name in new[] { "[TeamflowClient]", "[TeamflowHUD]", "[WhisperBackendInference]" })
            {
                var go = GameObject.Find(name);
                if (go != null) { Object.DestroyImmediate(go); removed++; }
            }
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[TeamFlow Setup] Supprimé {removed} objet(s) TeamFlow de la scène.");
        }

        /// <summary>
        /// Called by WhisperModelDownloader after download+import to assign models to scene component.
        /// Also called by SetupScene if models already exist.
        /// </summary>
        /// <summary>Returns true if all 4 ONNX files exist on disk in Assets/WhisperModels/.</summary>
        public static bool ModelsExistOnDisk()
        {
            string root = Path.Combine(Application.dataPath, "WhisperModels");
            return File.Exists(Path.Combine(root, ENCODER_FILE))
                && File.Exists(Path.Combine(root, DECODER1_FILE))
                && File.Exists(Path.Combine(root, DECODER2_FILE))
                && File.Exists(Path.Combine(root, LOGMEL_FILE));
        }

        private const string WHISPER_TYPE = "TeamflowSDK.WhisperBackendInference, TeamflowSDK.Whisper";

        public static bool SetupWhisper()
        {
            var whisperType = Type.GetType(WHISPER_TYPE);
            if (whisperType == null)
            {
                Debug.LogWarning("[TeamFlow Setup] com.unity.ai.inference non installé — WhisperBackendInference introuvable.");
                return false;
            }

            if (!ModelsExistOnDisk())
            {
                var existing2 = (Component)Object.FindAnyObjectByType(whisperType);
                if (existing2 == null)
                    new GameObject("[WhisperBackendInference]").AddComponent(whisperType);
                Debug.LogWarning("[TeamFlow Setup] Modèles ONNX non trouvés dans Assets/WhisperModels/");
                return false;
            }

            AssetDatabase.ImportAsset($"{ASSET_FOLDER}/{ENCODER_FILE}",  ImportAssetOptions.Default);
            AssetDatabase.ImportAsset($"{ASSET_FOLDER}/{DECODER1_FILE}", ImportAssetOptions.Default);
            AssetDatabase.ImportAsset($"{ASSET_FOLDER}/{DECODER2_FILE}", ImportAssetOptions.Default);
            AssetDatabase.ImportAsset($"{ASSET_FOLDER}/{LOGMEL_FILE}",   ImportAssetOptions.Default);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var encoder  = AssetDatabase.LoadAssetAtPath<Object>($"{ASSET_FOLDER}/{ENCODER_FILE}");
            var decoder1 = AssetDatabase.LoadAssetAtPath<Object>($"{ASSET_FOLDER}/{DECODER1_FILE}");
            var decoder2 = AssetDatabase.LoadAssetAtPath<Object>($"{ASSET_FOLDER}/{DECODER2_FILE}");
            var logmel   = AssetDatabase.LoadAssetAtPath<Object>($"{ASSET_FOLDER}/{LOGMEL_FILE}");

            var existing = (Component)Object.FindAnyObjectByType(whisperType);
            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject("[WhisperBackendInference]");

            if (existing == null)
                go.AddComponent(whisperType);

            if (encoder != null && decoder1 != null && decoder2 != null && logmel != null)
            {
                var so = new SerializedObject(go.GetComponent(whisperType));
                so.FindProperty("audioEncoder").objectReferenceValue  = encoder;
                so.FindProperty("audioDecoder1").objectReferenceValue = decoder1;
                so.FindProperty("audioDecoder2").objectReferenceValue = decoder2;
                so.FindProperty("logMelSpectro").objectReferenceValue = logmel;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(go);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                Debug.Log("[TeamFlow Setup] WhisperBackendInference configuré avec les ModelAssets ✅");
                return true;
            }

            Debug.LogWarning("[TeamFlow Setup] Import ONNX réussi mais LoadAssetAtPath a retourné null — relance Setup Scene.");
            return false;
        }
    }
}
