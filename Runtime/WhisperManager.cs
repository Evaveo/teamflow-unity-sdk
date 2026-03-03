using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

#if UNITY_SENTIS || UNITY_AI_INFERENCE
#if UNITY_AI_INFERENCE
using Unity.InferenceEngine;
#else
using Unity.Sentis;
#endif
#endif

namespace TeamflowSDK
{
    /// <summary>
    /// Offline French speech-to-text using Whisper-Tiny via Unity Sentis (ONNX).
    /// 
    /// Setup:
    ///   1. Install Unity Sentis: Window → Package Manager → com.unity.sentis (≥ 1.4)
    ///   2. Download Whisper-Tiny ONNX models (see WhisperModelDownloader) into
    ///      Assets/StreamingAssets/Whisper/
    ///        - whisper-tiny-encoder.sentis
    ///        - whisper-tiny-decoder.sentis
    ///   3. Add WhisperManager to your scene (or let TeamflowHUD auto-add it).
    ///
    /// Usage:
    ///   WhisperManager.Instance.StartListening(targetField);
    ///   WhisperManager.Instance.StopListening();
    /// </summary>
    public class WhisperManager : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static WhisperManager _instance;
        public static WhisperManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[WhisperManager]");
                    _instance = go.AddComponent<WhisperManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        // ── State ─────────────────────────────────────────────────────────────
        public enum WhisperState { Idle, LoadingModel, Recording, Transcribing, Error }

        public WhisperState State { get; private set; } = WhisperState.Idle;
        public bool IsReady      { get; private set; } = false;
        public string LastError  { get; private set; } = "";

        // Callback fired when transcription is complete
        public event Action<string> OnTranscribed;
        // Callback fired on state change (for UI updates)
        public event Action<WhisperState> OnStateChanged;

        // ── Config ────────────────────────────────────────────────────────────
        private const int   SAMPLE_RATE     = 16000;
        private const int   MAX_RECORD_SECS = 10;
        private const float SILENCE_THRESH  = 0.01f;
        private const int   SILENCE_FRAMES  = 24000; // 1.5s @ 16kHz

        private const string ENCODER_FILENAME = "whisper-tiny-encoder.sentis";
        private const string DECODER_FILENAME = "whisper-tiny-decoder.sentis";

        // French language token ID for Whisper
        private const int LANG_TOKEN_FR    = 50297;
        private const int TRANSCRIBE_TOKEN = 50359;
        private const int SOT_TOKEN        = 50258;
        private const int EOT_TOKEN        = 50257;
        private const int NO_TIMESTAMPS    = 50363;
        private const int MAX_NEW_TOKENS   = 224;

        // ── Mic ───────────────────────────────────────────────────────────────
        private AudioClip   _micClip;
        private string      _micDevice;
        private bool        _isRecording = false;
        private int         _lastMicPos  = 0;
        private List<float> _audioBuffer = new List<float>();

#if UNITY_SENTIS
        // ── Sentis ────────────────────────────────────────────────────────────
        private Model   _encoderModel;
        private Model   _decoderModel;
        private IWorker _encoderWorker;
        private IWorker _decoderWorker;
#endif

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            StartCoroutine(LoadModels());
        }

        private void OnDestroy()
        {
#if UNITY_SENTIS || UNITY_AI_INFERENCE
            _encoderWorker?.Dispose();
            _decoderWorker?.Dispose();
#endif
        }

        // ── Model loading ─────────────────────────────────────────────────────

        private IEnumerator LoadModels()
        {
            SetState(WhisperState.LoadingModel);

#if !UNITY_SENTIS && !UNITY_AI_INFERENCE
            LastError = "Unity Inference Engine not installed. Install com.unity.ai.inference via Package Manager.";
            Debug.LogWarning($"[WhisperManager] {LastError}");
            SetState(WhisperState.Error);
            yield break;
#else
            var encoderPath = Path.Combine(Application.streamingAssetsPath, "Whisper", ENCODER_FILENAME);
            var decoderPath = Path.Combine(Application.streamingAssetsPath, "Whisper", DECODER_FILENAME);

            if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
            {
                LastError = $"Whisper models not found in StreamingAssets/Whisper/. " +
                            $"Run Tools → TeamFlow → Download Whisper Models.";
                Debug.LogWarning($"[WhisperManager] {LastError}");
                SetState(WhisperState.Error);
                yield break;
            }

            // Load models (may take a few frames)
            yield return null;
            try
            {
                _encoderModel = ModelLoader.Load(encoderPath);
                _decoderModel = ModelLoader.Load(decoderPath);
                _encoderWorker = WorkerFactory.CreateWorker(BackendType.GPUCompute, _encoderModel);
                _decoderWorker = WorkerFactory.CreateWorker(BackendType.GPUCompute, _decoderModel);
                IsReady = true;
                Debug.Log("[WhisperManager] Whisper-Tiny models loaded (FR offline).");
                SetState(WhisperState.Idle);
            }
            catch (Exception ex)
            {
                LastError = $"Model load failed: {ex.Message}";
                Debug.LogError($"[WhisperManager] {LastError}");
                SetState(WhisperState.Error);
            }
#endif
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Start microphone recording. Call StopListening() to transcribe.</summary>
        public bool StartListening()
        {
            if (!IsReady)
            {
                Debug.LogWarning("[WhisperManager] Models not ready yet.");
                return false;
            }
            if (_isRecording) return false;

            if (Microphone.devices.Length == 0)
            {
                LastError = "Aucun microphone détecté.";
                SetState(WhisperState.Error);
                return false;
            }

            _micDevice   = Microphone.devices[0];
            _audioBuffer.Clear();
            _micClip     = Microphone.Start(_micDevice, true, MAX_RECORD_SECS, SAMPLE_RATE);
            _isRecording = true;
            _lastMicPos  = 0;
            SetState(WhisperState.Recording);
            Debug.Log($"[WhisperManager] Recording on: {_micDevice}");
            return true;
        }

        /// <summary>Stop recording and run transcription. Result via OnTranscribed event.</summary>
        public void StopListening()
        {
            if (!_isRecording) return;

            // Drain remaining samples
            int pos = Microphone.GetPosition(_micDevice);
            Microphone.End(_micDevice);
            _isRecording = false;

            // Collect buffered audio
            float[] samples = new float[_micClip.samples * _micClip.channels];
            _micClip.GetData(samples, 0);

            // Trim to actual recorded length
            int end = pos > 0 ? pos : samples.Length;
            float[] trimmed = new float[end];
            Array.Copy(samples, trimmed, end);

            StartCoroutine(TranscribeCoroutine(trimmed));
        }

        // ── Mic polling (Update) ──────────────────────────────────────────────

        private void Update()
        {
            if (!_isRecording || _micClip == null) return;

            int pos = Microphone.GetPosition(_micDevice);
            if (pos < _lastMicPos) // wrapped
            {
                int tail = _micClip.samples - _lastMicPos;
                float[] tailSamples = new float[tail];
                _micClip.GetData(tailSamples, _lastMicPos);
                _audioBuffer.AddRange(tailSamples);
                _lastMicPos = 0;
            }

            if (pos > _lastMicPos)
            {
                int len = pos - _lastMicPos;
                float[] chunk = new float[len];
                _micClip.GetData(chunk, _lastMicPos);
                _audioBuffer.AddRange(chunk);
                _lastMicPos = pos;
            }

            // Auto-stop after MAX_RECORD_SECS
            if (_audioBuffer.Count >= SAMPLE_RATE * MAX_RECORD_SECS)
                StopListening();
        }

        // ── Transcription ─────────────────────────────────────────────────────

        private IEnumerator TranscribeCoroutine(float[] pcm16k)
        {
            SetState(WhisperState.Transcribing);
            yield return null;

#if !UNITY_SENTIS && !UNITY_AI_INFERENCE
            OnTranscribed?.Invoke("[Inference Engine non installé]");
            SetState(WhisperState.Idle);
            yield break;
#else
            string result = "";
            bool done = false;

            // Run on background thread-like via coroutine chunking
            yield return StartCoroutine(RunInference(pcm16k, r => { result = r; done = true; }));

            while (!done) yield return null;

            Debug.Log($"[WhisperManager] Transcription: {result}");
            OnTranscribed?.Invoke(result);
            SetState(WhisperState.Idle);
#endif
        }

#if UNITY_SENTIS || UNITY_AI_INFERENCE
        private IEnumerator RunInference(float[] pcm, Action<string> callback)
        {
            // 1. Mel spectrogram (80 bins, 3000 frames) from raw PCM
            float[] mel = ComputeMelSpectrogram(pcm);
            yield return null;

            // 2. Encoder
            using var melTensor = new TensorFloat(new TensorShape(1, 80, 3000), mel);
            _encoderWorker.Execute(melTensor);
            var audioFeatures = _encoderWorker.PeekOutput() as TensorFloat;
            audioFeatures?.MakeReadable();
            yield return null;

            // 3. Decoder — greedy token generation
            var tokens = new List<int> { SOT_TOKEN, LANG_TOKEN_FR, TRANSCRIBE_TOKEN, NO_TIMESTAMPS };
            string text = "";

            for (int i = 0; i < MAX_NEW_TOKENS; i++)
            {
                int[] tokenArr = tokens.ToArray();
                using var tokensTensor = new TensorInt(new TensorShape(1, tokenArr.Length), tokenArr);

                var inputs = new Dictionary<string, Tensor>
                {
                    { "audio_features", audioFeatures },
                    { "tokens",         tokensTensor  }
                };
                _decoderWorker.Execute(inputs);

                var logits = _decoderWorker.PeekOutput("logits") as TensorFloat;
                logits?.MakeReadable();

                // Greedy: argmax of last token logits
                int vocabSize = logits?.shape[^1] ?? 0;
                int lastRow   = (logits?.shape[1] - 1) ?? 0;
                int bestToken = 0;
                float bestVal = float.NegativeInfinity;
                for (int v = 0; v < vocabSize; v++)
                {
                    float val = logits?[0, lastRow, v] ?? float.NegativeInfinity;
                    if (val > bestVal) { bestVal = val; bestToken = v; }
                }

                if (bestToken == EOT_TOKEN) break;
                tokens.Add(bestToken);
                text += WhisperTokenizer.Decode(bestToken);

                if (i % 8 == 0) yield return null; // yield every 8 tokens
            }

            callback?.Invoke(text.Trim());
        }

        // ── Mel Spectrogram ───────────────────────────────────────────────────
        // Simplified Whisper-compatible mel spectrogram (80 filters, hop=160, win=400)

        private static float[] ComputeMelSpectrogram(float[] pcm)
        {
            const int N_FFT    = 400;
            const int HOP      = 160;
            const int N_MELS   = 80;
            const int N_FRAMES = 3000;

            // Pad / trim to exactly 30 seconds (480000 samples @ 16kHz)
            int targetLen = 480000;
            float[] padded = new float[targetLen];
            int copy = Mathf.Min(pcm.Length, targetLen);
            Array.Copy(pcm, padded, copy);

            // Hann window
            float[] window = new float[N_FFT];
            for (int i = 0; i < N_FFT; i++)
                window[i] = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (N_FFT - 1)));

            // Mel filterbank
            float[] melFilters = BuildMelFilterbank(N_FFT, N_MELS, SAMPLE_RATE);

            float[] mel = new float[N_MELS * N_FRAMES];

            for (int frame = 0; frame < N_FRAMES; frame++)
            {
                int offset = frame * HOP;
                float[] fftIn = new float[N_FFT];
                for (int k = 0; k < N_FFT; k++)
                {
                    int idx = offset + k;
                    fftIn[k] = idx < padded.Length ? padded[idx] * window[k] : 0f;
                }

                float[] power = FFTPower(fftIn);

                for (int m = 0; m < N_MELS; m++)
                {
                    float sum = 0f;
                    for (int f = 0; f < N_FFT / 2 + 1; f++)
                        sum += melFilters[m * (N_FFT / 2 + 1) + f] * power[f];
                    mel[m * N_FRAMES + frame] = Mathf.Max(sum, 1e-10f);
                }
            }

            // Log-mel + normalize
            float maxVal = float.NegativeInfinity;
            for (int i = 0; i < mel.Length; i++)
            {
                mel[i] = Mathf.Log10(mel[i]);
                if (mel[i] > maxVal) maxVal = mel[i];
            }
            for (int i = 0; i < mel.Length; i++)
                mel[i] = Mathf.Max(mel[i], maxVal - 8f);
            for (int i = 0; i < mel.Length; i++)
                mel[i] = (mel[i] + 4f) / 4f;

            return mel;
        }

        private static float[] BuildMelFilterbank(int nFft, int nMels, int sr)
        {
            int nFreqs = nFft / 2 + 1;
            float[] filters = new float[nMels * nFreqs];

            float fMin = 0f, fMax = sr / 2f;
            float melMin = HzToMel(fMin), melMax = HzToMel(fMax);

            float[] melPoints = new float[nMels + 2];
            for (int i = 0; i < melPoints.Length; i++)
                melPoints[i] = MelToHz(melMin + i * (melMax - melMin) / (nMels + 1));

            float[] freqBins = new float[nFreqs];
            for (int i = 0; i < nFreqs; i++)
                freqBins[i] = i * sr / (float)nFft;

            for (int m = 0; m < nMels; m++)
            {
                float lo = melPoints[m], center = melPoints[m + 1], hi = melPoints[m + 2];
                for (int f = 0; f < nFreqs; f++)
                {
                    float hz = freqBins[f];
                    float val = 0f;
                    if (hz >= lo && hz <= center && center > lo)
                        val = (hz - lo) / (center - lo);
                    else if (hz > center && hz <= hi && hi > center)
                        val = (hi - hz) / (hi - center);
                    filters[m * nFreqs + f] = val;
                }
            }
            return filters;
        }

        private static float HzToMel(float hz) => 2595f * Mathf.Log10(1f + hz / 700f);
        private static float MelToHz(float mel) => 700f * (Mathf.Pow(10f, mel / 2595f) - 1f);

        // Radix-2 FFT (power spectrum only, real input)
        private static float[] FFTPower(float[] x)
        {
            int n = x.Length;
            float[] re = (float[])x.Clone();
            float[] im = new float[n];

            // Bit-reversal permutation
            int j = 0;
            for (int i = 1; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { float t = re[i]; re[i] = re[j]; re[j] = t; }
            }

            // Cooley-Tukey
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang = -2f * Mathf.PI / len;
                float wRe = Mathf.Cos(ang), wIm = Mathf.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float curRe = 1f, curIm = 0f;
                    for (int k = 0; k < len / 2; k++)
                    {
                        float uRe = re[i + k], uIm = im[i + k];
                        float vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                        float vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;
                        re[i + k]           = uRe + vRe; im[i + k]           = uIm + vIm;
                        re[i + k + len / 2] = uRe - vRe; im[i + k + len / 2] = uIm - vIm;
                        float nextRe = curRe * wRe - curIm * wIm;
                        curIm = curRe * wIm + curIm * wRe;
                        curRe = nextRe;
                    }
                }
            }

            // Power spectrum (first half only)
            int half = n / 2 + 1;
            float[] power = new float[half];
            for (int i = 0; i < half; i++)
                power[i] = re[i] * re[i] + im[i] * im[i];
            return power;
        }
#endif

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SetState(WhisperState state)
        {
            State = state;
            OnStateChanged?.Invoke(state);
        }
    }

    // ── Minimal BPE tokenizer (decode only) ─────────────────────────────────
    // Whisper uses a custom BPE vocab. This stub decodes token IDs to text
    // by mapping the token to its byte-level representation. Full vocab must
    // be loaded from vocab.json included in StreamingAssets/Whisper/.

    internal static class WhisperTokenizer
    {
        private static Dictionary<int, string> _vocab;
        private static bool _loaded = false;

        public static string Decode(int tokenId)
        {
            if (!_loaded) LoadVocab();
            if (_vocab != null && _vocab.TryGetValue(tokenId, out var word))
                return word;
            return "";
        }

        private static void LoadVocab()
        {
            _loaded = true;
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, "Whisper", "vocab.json");
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                _vocab = ParseSimpleVocabJson(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WhisperTokenizer] Could not load vocab.json: {ex.Message}");
            }
        }

        // Very simple JSON parser for flat { "token": id } objects
        private static Dictionary<int, string> ParseSimpleVocabJson(string json)
        {
            var result = new Dictionary<int, string>();
            json = json.Trim().TrimStart('{').TrimEnd('}');
            foreach (var entry in json.Split(','))
            {
                var parts = entry.Split(':');
                if (parts.Length != 2) continue;
                var key   = parts[0].Trim().Trim('"').Replace("\\u0120", " ").Replace("\\n", "\n");
                var value = parts[1].Trim();
                if (int.TryParse(value, out int id))
                    result[id] = key;
            }
            return result;
        }
    }
}
