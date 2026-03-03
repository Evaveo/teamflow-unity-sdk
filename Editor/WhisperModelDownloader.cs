using System;
using System.IO;
using System.Collections;
using UnityEngine;
using UnityEditor;

namespace TeamflowSDK.Editor
{
    /// <summary>
    /// Editor utility to download Whisper-Tiny ONNX models from Hugging Face
    /// into Assets/StreamingAssets/Whisper/.
    ///
    /// Menu: Tools → TeamFlow → Download Whisper Models (FR offline)
    /// </summary>
    public class WhisperModelDownloader : EditorWindow
    {
        // Hugging Face — onnx-community/whisper-tiny (converted to Sentis format)
        // These are the standard ONNX exports compatible with Unity Sentis 1.4+
        private const string HF_BASE     = "https://huggingface.co/onnx-community/whisper-tiny/resolve/main/onnx/";
        private const string ENCODER_URL = HF_BASE + "encoder_model.onnx";
        private const string DECODER_URL = HF_BASE + "decoder_model_merged.onnx";
        private const string VOCAB_URL   = "https://huggingface.co/openai/whisper-tiny/resolve/main/vocab.json";

        private const string ENCODER_FILENAME = "whisper-tiny-encoder.sentis";
        private const string DECODER_FILENAME = "whisper-tiny-decoder.sentis";
        private const string VOCAB_FILENAME   = "vocab.json";

        private static string DestFolder =>
            Path.Combine(Application.streamingAssetsPath, "Whisper");

        private bool   _isDownloading = false;
        private string _status        = "";
        private float  _progress      = 0f;
        private string _log           = "";

        [MenuItem("Tools/TeamFlow/Download Whisper Models (FR offline)")]
        public static void ShowWindow()
        {
            var win = GetWindow<WhisperModelDownloader>("Whisper Models");
            win.minSize = new Vector2(480, 320);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Whisper-Tiny — Speech-to-Text FR offline", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.HelpBox(
                "Downloads Whisper-Tiny ONNX models (~75 MB total) from Hugging Face " +
                "into Assets/StreamingAssets/Whisper/.\n\n" +
                "Requires Unity Sentis (com.unity.sentis ≥ 1.4) installed via Package Manager.\n" +
                "Models run 100% offline on device — no API key needed.",
                MessageType.Info);

            EditorGUILayout.Space(8);

            // Status of existing files
            bool encoderExists = File.Exists(Path.Combine(DestFolder, ENCODER_FILENAME));
            bool decoderExists = File.Exists(Path.Combine(DestFolder, DECODER_FILENAME));
            bool vocabExists   = File.Exists(Path.Combine(DestFolder, VOCAB_FILENAME));

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ToggleLeft($"Encoder  ({ENCODER_FILENAME})", encoderExists);
                EditorGUILayout.ToggleLeft($"Decoder  ({DECODER_FILENAME})", decoderExists);
                EditorGUILayout.ToggleLeft($"Vocab    ({VOCAB_FILENAME})",   vocabExists);
            }

            EditorGUILayout.Space(8);

            // Progress bar
            if (_isDownloading)
            {
                Rect r = GUILayoutUtility.GetRect(18, 18, "TextField");
                EditorGUI.ProgressBar(r, _progress, _status);
                EditorGUILayout.Space(4);
            }

            // Log
            if (!string.IsNullOrEmpty(_log))
            {
                EditorGUILayout.HelpBox(_log, _log.StartsWith("✅") ? MessageType.Info : MessageType.Warning);
            }

            EditorGUILayout.Space(4);

            using (new EditorGUI.DisabledScope(_isDownloading))
            {
                if (GUILayout.Button(
                    encoderExists && decoderExists && vocabExists
                        ? "Re-télécharger les modèles"
                        : "Télécharger les modèles Whisper",
                    GUILayout.Height(36)))
                {
                    StartDownload();
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Destination : " + DestFolder,
                EditorStyles.miniLabel);
        }

        private void StartDownload()
        {
            _isDownloading = true;
            _log           = "";
            _progress      = 0f;

            // Create destination directory
            if (!Directory.Exists(DestFolder))
                Directory.CreateDirectory(DestFolder);

            EditorApplication.update += DownloadTick;
            _downloadEnumerator = DownloadAll();
        }

        private IEnumerator _downloadEnumerator;

        private void DownloadTick()
        {
            if (_downloadEnumerator == null || !_downloadEnumerator.MoveNext())
            {
                EditorApplication.update -= DownloadTick;
                _isDownloading = false;
                Repaint();
                AssetDatabase.Refresh();
            }
            else
            {
                Repaint();
            }
        }

        private IEnumerator DownloadAll()
        {
            // 1. Encoder
            _status = "Téléchargement encoder...";
            _progress = 0.1f;
            yield return null;
            yield return DownloadFile(ENCODER_URL, Path.Combine(DestFolder, ENCODER_FILENAME), 0.1f, 0.5f);

            // 2. Decoder
            _status = "Téléchargement decoder...";
            _progress = 0.5f;
            yield return null;
            yield return DownloadFile(DECODER_URL, Path.Combine(DestFolder, DECODER_FILENAME), 0.5f, 0.9f);

            // 3. Vocab
            _status = "Téléchargement vocab...";
            _progress = 0.9f;
            yield return null;
            yield return DownloadFile(VOCAB_URL, Path.Combine(DestFolder, VOCAB_FILENAME), 0.9f, 1.0f);

            _progress = 1f;
            _status = "Terminé !";
            _log = "✅ Modèles Whisper installés dans StreamingAssets/Whisper/\n" +
                   "Redémarrez Unity si les fichiers n'apparaissent pas dans le projet.";
        }

        private IEnumerator DownloadFile(string url, string destPath, float progressStart, float progressEnd)
        {
            using var www = UnityEngine.Networking.UnityWebRequest.Get(url);
            var op = www.SendWebRequest();

            while (!op.isDone)
            {
                _progress = progressStart + (progressEnd - progressStart) * www.downloadProgress;
                Repaint();
                yield return null;
            }

            if (www.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                _log = $"❌ Erreur téléchargement {Path.GetFileName(destPath)}: {www.error}";
                Debug.LogError($"[WhisperModelDownloader] {_log}");
                yield break;
            }

            File.WriteAllBytes(destPath, www.downloadHandler.data);
            Debug.Log($"[WhisperModelDownloader] Saved: {destPath} ({www.downloadHandler.data.Length / 1024} KB)");
        }
    }
}
