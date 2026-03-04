using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace TeamflowSDK
{
    /// <summary>
    /// Google STT backend — proxied via the TeamFlow backend API.
    /// The API key is stored server-side; this component calls /api/stt/transcribe.
    /// Add this component to any GameObject in the scene (or let TeamflowHUD auto-create it).
    /// </summary>
    public class GoogleSTTBackend : MonoBehaviour, IWhisperBackend
    {
        [Header("Recording")]
        [SerializeField] private int sampleRate    = 16000;
        [SerializeField] private int maxDurationSec = 10;

        // ── IWhisperBackend ───────────────────────────────────────────────────
        public bool IsReady { get; private set; } = false;

        private Action<string> _onTranscribed;
        private Action<string> _onError;

        private AudioClip  _clip;
        private bool       _isRecording = false;
        private string     _micDevice;

        public void Init(Action<string> onTranscribed, Action<string> onError)
        {
            _onTranscribed = onTranscribed;
            _onError       = onError;
            StartCoroutine(CheckSttEnabled());
        }

        private IEnumerator CheckSttEnabled()
        {
            string url = $"{TeamflowClient.Instance.BaseUrl}/api/stt/config";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Authorization", $"Bearer {TeamflowClient.Instance.AuthToken}");
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var json = JsonUtility.FromJson<SttConfigResponse>(req.downloadHandler.text);
                IsReady = json.enabled;
                if (!IsReady)
                    Debug.Log("[GoogleSTT] Service disabled server-side.");
                else
                    Debug.Log($"[GoogleSTT] Ready — language: {json.language}");
            }
            else
            {
                Debug.LogWarning($"[GoogleSTT] Could not fetch config: {req.error}");
                IsReady = false;
            }

            WhisperManager.Instance.RegisterBackend(this);
        }

        private void Start() { }

        public bool StartListening()
        {
            if (!IsReady) return false;
            if (Microphone.devices.Length == 0)
            {
                _onError?.Invoke("Aucun microphone détecté.");
                return false;
            }

            _micDevice  = Microphone.devices[0];
            _clip       = Microphone.Start(_micDevice, false, maxDurationSec, sampleRate);
            _isRecording = true;
            return true;
        }

        public void StopListening()
        {
            if (!_isRecording) return;
            _isRecording = false;

            int pos = Microphone.GetPosition(_micDevice);
            Microphone.End(_micDevice);

            if (pos <= 0) { _onError?.Invoke("Aucun audio enregistré."); return; }

            // Trim clip to actual recorded length
            var samples = new float[pos * _clip.channels];
            _clip.GetData(samples, 0);

            StartCoroutine(SendToBackend(samples));
        }

        private IEnumerator SendToBackend(float[] samples)
        {
            // Convert float PCM → 16-bit PCM bytes
            var pcmBytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
                pcmBytes[i * 2]     = (byte)(s & 0xFF);
                pcmBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            // WAV header + PCM data
            byte[] wav = BuildWav(pcmBytes, sampleRate, 1);
            string b64 = Convert.ToBase64String(wav);

            string body = $"{{\"audioBase64\":\"{b64}\",\"sampleRate\":{sampleRate}}}";
            string url  = $"{TeamflowClient.Instance.BaseUrl}/api/stt/transcribe";

            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", $"Bearer {TeamflowClient.Instance.AuthToken}");

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var resp = JsonUtility.FromJson<SttTranscribeResponse>(req.downloadHandler.text);
                if (!string.IsNullOrEmpty(resp.transcript))
                    _onTranscribed?.Invoke(resp.transcript);
                else
                    _onTranscribed?.Invoke("");
            }
            else
            {
                _onError?.Invoke($"STT error: {req.error}");
            }
        }

        // ── WAV builder ───────────────────────────────────────────────────────
        private static byte[] BuildWav(byte[] pcmData, int sampleRate, int channels)
        {
            int dataLen  = pcmData.Length;
            int totalLen = 44 + dataLen;
            var wav = new byte[totalLen];

            // RIFF header
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            BitConverter.GetBytes(totalLen - 8).CopyTo(wav, 4);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
            // fmt chunk
            Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
            BitConverter.GetBytes(16).CopyTo(wav, 16);          // chunk size
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);    // PCM
            BitConverter.GetBytes((short)channels).CopyTo(wav, 22);
            BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
            BitConverter.GetBytes(sampleRate * channels * 2).CopyTo(wav, 28); // byte rate
            BitConverter.GetBytes((short)(channels * 2)).CopyTo(wav, 32);     // block align
            BitConverter.GetBytes((short)16).CopyTo(wav, 34);   // bits per sample
            // data chunk
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
            BitConverter.GetBytes(dataLen).CopyTo(wav, 40);
            pcmData.CopyTo(wav, 44);

            return wav;
        }

        // ── JSON helpers ──────────────────────────────────────────────────────
        [Serializable] private class SttConfigResponse    { public bool enabled; public string language; }
        [Serializable] private class SttTranscribeResponse { public string transcript; }
    }
}
