using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace TeamflowSDK.Editor
{
    /// <summary>
    /// Downloads Whisper-Tiny ONNX models from unity/inference-engine-whisper-tiny,
    /// imports them as ModelAssets into Assets/WhisperModels/, then auto-assigns
    /// them to a WhisperBackendInference component in the active scene.
    ///
    /// Menu: Tools → TeamFlow → Download Whisper Models (FR offline)
    /// </summary>
    public class WhisperModelDownloader : EditorWindow
    {
        // Official Unity Whisper models (pre-validated for InferenceEngine)
        private const string HF_BASE          = "https://huggingface.co/unity/inference-engine-whisper-tiny/resolve/main/models/";
        private const string ENCODER_URL      = HF_BASE + "encoder_model.onnx";
        private const string DECODER1_URL     = HF_BASE + "decoder_model.onnx";
        private const string DECODER2_URL     = HF_BASE + "decoder_with_past_model.onnx";
        private const string LOGMEL_URL       = HF_BASE + "logmel_spectrogram.onnx";
        private const string VOCAB_URL        = "https://huggingface.co/openai/whisper-tiny/resolve/main/vocab.json";

        private const string ENCODER_FILE     = "whisper_encoder.onnx";
        private const string DECODER1_FILE    = "whisper_decoder.onnx";
        private const string DECODER2_FILE    = "whisper_decoder_with_past.onnx";
        private const string LOGMEL_FILE      = "whisper_logmel.onnx";
        private const string VOCAB_FILE       = "vocab.json";

        private const string ASSET_FOLDER     = "Assets/WhisperModels";
        private static string VocabStreamingPath =>
            Path.Combine(Application.streamingAssetsPath, "Whisper");

        private bool   _isDownloading = false;
        private string _status        = "";
        private float  _progress      = 0f;
        private string _log           = "";
        private CancellationTokenSource _cts;

        [MenuItem("Tools/TeamFlow/Download Whisper Models (FR offline)")]
        public static void ShowWindow()
        {
            var win = GetWindow<WhisperModelDownloader>("Whisper Models");
            win.minSize = new Vector2(500, 380);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Whisper-Tiny — Speech-to-Text FR offline", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.HelpBox(
                "Télécharge les 4 modèles ONNX officiels Unity depuis HuggingFace (~450 MB total)\n" +
                "→ Les importe dans Assets/WhisperModels/ comme ModelAssets\n" +
                "→ Les assigne automatiquement au WhisperBackendInference dans la scène active\n\n" +
                "Nécessite : com.unity.ai.inference installé via Package Manager.",
                MessageType.Info);

            EditorGUILayout.Space(8);

            bool encoderOk  = AssetExists(ENCODER_FILE);
            bool decoder1Ok = AssetExists(DECODER1_FILE);
            bool decoder2Ok = AssetExists(DECODER2_FILE);
            bool logmelOk   = AssetExists(LOGMEL_FILE);
            bool vocabOk    = File.Exists(Path.Combine(VocabStreamingPath, VOCAB_FILE));

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ToggleLeft($"Encoder           ({ENCODER_FILE})",    encoderOk);
                EditorGUILayout.ToggleLeft($"Decoder           ({DECODER1_FILE})",   decoder1Ok);
                EditorGUILayout.ToggleLeft($"Decoder with past ({DECODER2_FILE})",   decoder2Ok);
                EditorGUILayout.ToggleLeft($"LogMel spectro    ({LOGMEL_FILE})",     logmelOk);
                EditorGUILayout.ToggleLeft($"Vocab             ({VOCAB_FILE})",      vocabOk);
            }

            EditorGUILayout.Space(8);

            if (_isDownloading)
            {
                Rect r = GUILayoutUtility.GetRect(18, 18, "TextField");
                EditorGUI.ProgressBar(r, _progress, _status);
                EditorGUILayout.Space(4);
                if (GUILayout.Button("Annuler", GUILayout.Height(28)))
                    _cts?.Cancel();
            }

            if (!string.IsNullOrEmpty(_log))
                EditorGUILayout.HelpBox(_log, _log.StartsWith("✅") ? MessageType.Info : MessageType.Error);

            EditorGUILayout.Space(4);

            bool allExist = encoderOk && decoder1Ok && decoder2Ok && logmelOk && vocabOk;
            using (new EditorGUI.DisabledScope(_isDownloading))
            {
                if (GUILayout.Button(
                    allExist ? "Re-télécharger et ré-assigner" : "Télécharger et configurer automatiquement",
                    GUILayout.Height(40)))
                {
                    StartDownload();
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Destination modèles : {ASSET_FOLDER}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Destination vocab   : {VocabStreamingPath}", EditorStyles.miniLabel);
        }

        private static bool AssetExists(string filename) =>
            File.Exists(Path.GetFullPath(Path.Combine(ASSET_FOLDER, filename)));

        private void StartDownload()
        {
            _isDownloading = true;
            _log           = "";
            _progress      = 0f;
            _status        = "Initialisation...";

            if (!AssetDatabase.IsValidFolder(ASSET_FOLDER))
                AssetDatabase.CreateFolder("Assets", "WhisperModels");

            if (!Directory.Exists(VocabStreamingPath))
                Directory.CreateDirectory(VocabStreamingPath);

            _cts = new CancellationTokenSource();
            _ = RunDownloadAsync(_cts.Token);
        }

        private async Task RunDownloadAsync(CancellationToken ct)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Unity Editor)");
            client.Timeout = TimeSpan.FromMinutes(15);

            try
            {
                await DownloadFileAsync(client, ENCODER_URL,
                    AssetPath(ENCODER_FILE),  "Encoder",          0.00f, 0.15f, ct);

                await DownloadFileAsync(client, DECODER1_URL,
                    AssetPath(DECODER1_FILE), "Decoder",          0.15f, 0.55f, ct);

                await DownloadFileAsync(client, DECODER2_URL,
                    AssetPath(DECODER2_FILE), "Decoder-with-past", 0.55f, 0.90f, ct);

                await DownloadFileAsync(client, LOGMEL_URL,
                    AssetPath(LOGMEL_FILE),   "LogMel",           0.90f, 0.95f, ct);

                await DownloadFileAsync(client, VOCAB_URL,
                    Path.Combine(VocabStreamingPath, VOCAB_FILE), "Vocab", 0.95f, 1.0f, ct);

                Debug.Log("[WhisperModelDownloader] Tous les modèles téléchargés.");
                EditorApplication.delayCall += FinishSetup;
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("[WhisperModelDownloader] Annulé.");
                EditorApplication.delayCall += () =>
                {
                    _status = "Annulé."; _log = "⚠️ Téléchargement annulé.";
                    _isDownloading = false; Repaint();
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WhisperModelDownloader] Erreur : {ex}");
                EditorApplication.delayCall += () =>
                {
                    _status = "Erreur !"; _log = $"❌ {ex.Message}";
                    _isDownloading = false; Repaint();
                };
            }
        }

        private void FinishSetup()
        {
            SetProgress("Import des assets...", 1f);

            AssetDatabase.ImportAsset(AssetDbPath(ENCODER_FILE),  ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(AssetDbPath(DECODER1_FILE), ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(AssetDbPath(DECODER2_FILE), ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(AssetDbPath(LOGMEL_FILE),   ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            bool assigned = TeamflowSceneSetup.SetupWhisper();

            _progress = 1f;
            _status   = "Terminé !";
            _log      = assigned
                ? "✅ Modèles téléchargés et assignés au WhisperBackendInference dans la scène !"
                : "✅ Modèles téléchargés dans Assets/WhisperModels/\n⚠️ Aucun WhisperBackendInference dans la scène — lance Tools → TeamFlow → Setup Scene.";
            _isDownloading = false;
            Repaint();
        }

        private static string AssetPath(string filename) =>
            Path.GetFullPath(Path.Combine(ASSET_FOLDER, filename));

        private static string AssetDbPath(string filename) =>
            $"{ASSET_FOLDER}/{filename}";

        private void SetProgress(string status, float progress)
        {
            EditorApplication.delayCall += () => { _status = status; _progress = progress; Repaint(); };
        }

        private async Task DownloadFileAsync(
            HttpClient client, string url, string destPath,
            string label, float progressStart, float progressEnd,
            CancellationToken ct)
        {
            SetProgress($"Téléchargement {label}...", progressStart);
            Debug.Log($"[WhisperModelDownloader] Downloading {label} from {url}");

            using var response  = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes    = response.Content.Headers.ContentLength;
            using var stream    = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var  buffer     = new byte[81920];
            long downloaded = 0;
            int  read;

            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                downloaded += read;
                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    float p = progressStart + (progressEnd - progressStart) * ((float)downloaded / totalBytes.Value);
                    SetProgress($"Téléchargement {label}... {downloaded / 1024} KB", p);
                }
            }

            Debug.Log($"[WhisperModelDownloader] Saved {label}: {destPath} ({downloaded / 1024} KB)");
        }

        private void OnDestroy() => _cts?.Cancel();
    }
}
