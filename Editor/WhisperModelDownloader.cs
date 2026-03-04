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
    /// Editor utility to download Whisper-Tiny ONNX models from Hugging Face
    /// into Assets/StreamingAssets/Whisper/.
    ///
    /// Menu: Tools → TeamFlow → Download Whisper Models (FR offline)
    /// </summary>
    public class WhisperModelDownloader : EditorWindow
    {
        private const string HF_BASE     = "https://huggingface.co/onnx-community/whisper-tiny/resolve/main/onnx/";
        private const string ENCODER_URL = HF_BASE + "encoder_model.onnx";
        private const string DECODER_URL = HF_BASE + "decoder_model_merged.onnx";
        private const string VOCAB_URL   = "https://huggingface.co/openai/whisper-tiny/resolve/main/vocab.json";

        private const string ENCODER_FILENAME = "whisper-tiny-encoder.onnx";
        private const string DECODER_FILENAME = "whisper-tiny-decoder.onnx";
        private const string VOCAB_FILENAME   = "vocab.json";

        private static string DestFolder =>
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
            win.minSize = new Vector2(480, 320);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Whisper-Tiny — Speech-to-Text FR offline", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.HelpBox(
                "Télécharge les modèles Whisper-Tiny ONNX (~75 MB) depuis Hugging Face " +
                "dans Assets/StreamingAssets/Whisper/.\n\n" +
                "Fonctionne avec com.unity.ai.inference (Unity 6) ou com.unity.sentis (Unity 2022).\n" +
                "Les modèles tournent 100% hors-ligne.",
                MessageType.Info);

            EditorGUILayout.Space(8);

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

            if (_isDownloading)
            {
                Rect r = GUILayoutUtility.GetRect(18, 18, "TextField");
                EditorGUI.ProgressBar(r, _progress, _status);
                EditorGUILayout.Space(4);

                if (GUILayout.Button("Annuler", GUILayout.Height(28)))
                {
                    _cts?.Cancel();
                }
            }

            if (!string.IsNullOrEmpty(_log))
            {
                EditorGUILayout.HelpBox(_log, _log.StartsWith("✅") ? MessageType.Info : MessageType.Error);
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
            EditorGUILayout.LabelField("Destination : " + DestFolder, EditorStyles.miniLabel);
        }

        private void StartDownload()
        {
            _isDownloading = true;
            _log           = "";
            _progress      = 0f;
            _status        = "Initialisation...";

            if (!Directory.Exists(DestFolder))
                Directory.CreateDirectory(DestFolder);

            _cts = new CancellationTokenSource();
            _ = RunDownloadAsync(_cts.Token);
        }

        private async Task RunDownloadAsync(CancellationToken ct)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Unity Editor)");
            client.Timeout = TimeSpan.FromMinutes(10);

            try
            {
                await DownloadFileAsync(client, ENCODER_URL,
                    Path.Combine(DestFolder, ENCODER_FILENAME),
                    "Encoder", 0f, 0.45f, ct);

                await DownloadFileAsync(client, DECODER_URL,
                    Path.Combine(DestFolder, DECODER_FILENAME),
                    "Decoder", 0.45f, 0.9f, ct);

                await DownloadFileAsync(client, VOCAB_URL,
                    Path.Combine(DestFolder, VOCAB_FILENAME),
                    "Vocab", 0.9f, 1.0f, ct);

                Debug.Log("[WhisperModelDownloader] Tous les modèles téléchargés avec succès.");
                EditorApplication.delayCall += () =>
                {
                    _progress = 1f;
                    _status   = "Terminé !";
                    _log      = "✅ Modèles Whisper installés dans StreamingAssets/Whisper/";
                    _isDownloading = false;
                    Repaint();
                    AssetDatabase.Refresh();
                };
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("[WhisperModelDownloader] Téléchargement annulé.");
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

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            using var stream     = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
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

        private void OnDestroy()
        {
            _cts?.Cancel();
        }
    }
}
