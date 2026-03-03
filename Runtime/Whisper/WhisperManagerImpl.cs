// This file is only compiled when com.unity.ai.inference is installed
// (enforced by TeamflowSDK.Whisper.asmdef defineConstraints: ["UNITY_AI_INFERENCE"])
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.InferenceEngine;

namespace TeamflowSDK
{
    /// <summary>
    /// Real Whisper implementation using Unity Inference Engine (com.unity.ai.inference).
    /// Only compiled when the package is installed. Registers itself with WhisperService on Awake.
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

        public WhisperState State     { get; private set; } = WhisperState.Idle;
        public bool         IsReady   { get; private set; } = false;
        public string       LastError { get; private set; } = "";

        public event Action<string>       OnTranscribed;
        public event Action<WhisperState> OnStateChanged;

        // ── Config ────────────────────────────────────────────────────────────
        private const int   SAMPLE_RATE     = 16000;
        private const int   MAX_RECORD_SECS = 10;

        private const string ENCODER_FILENAME = "whisper-tiny-encoder.sentis";
        private const string DECODER_FILENAME = "whisper-tiny-decoder.sentis";

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

        // ── Inference Engine ─────────────────────────────────────────────────
        private Model  _encoderModel;
        private Model  _decoderModel;
        private Worker _encoderWorker;
        private Worker _decoderWorker;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            WhisperService.Register(StartListening, StopListening);
        }

        private void Start()
        {
            StartCoroutine(LoadModels());
        }

        private void OnDestroy()
        {
            _encoderWorker?.Dispose();
            _decoderWorker?.Dispose();
        }

        // ── Model loading ─────────────────────────────────────────────────────

        private IEnumerator LoadModels()
        {
            SetState(WhisperState.LoadingModel);

            var encoderPath = Path.Combine(Application.streamingAssetsPath, "Whisper", ENCODER_FILENAME);
            var decoderPath = Path.Combine(Application.streamingAssetsPath, "Whisper", DECODER_FILENAME);

            if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
            {
                LastError = "Whisper models not found in StreamingAssets/Whisper/. " +
                            "Run Tools → TeamFlow → Download Whisper Models.";
                Debug.LogWarning($"[WhisperManager] {LastError}");
                SetState(WhisperState.Error);
                yield break;
            }

            yield return null;
            try
            {
                _encoderModel  = ModelLoader.Load(encoderPath);
                _decoderModel  = ModelLoader.Load(decoderPath);
                _encoderWorker = new Worker(_encoderModel, BackendType.GPUCompute);
                _decoderWorker = new Worker(_decoderModel, BackendType.GPUCompute);
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
        }

        // ── Public API ────────────────────────────────────────────────────────

        public bool StartListening()
        {
            if (!IsReady) { Debug.LogWarning("[WhisperManager] Models not ready."); return false; }
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
            return true;
        }

        public void StopListening()
        {
            if (!_isRecording) return;
            int pos = Microphone.GetPosition(_micDevice);
            Microphone.End(_micDevice);
            _isRecording = false;

            float[] samples = new float[_micClip.samples * _micClip.channels];
            _micClip.GetData(samples, 0);
            int end = pos > 0 ? pos : samples.Length;
            float[] trimmed = new float[end];
            Array.Copy(samples, trimmed, end);
            StartCoroutine(TranscribeCoroutine(trimmed));
        }

        // ── Update ────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_isRecording || _micClip == null) return;
            int pos = Microphone.GetPosition(_micDevice);
            if (pos < _lastMicPos)
            {
                int tail = _micClip.samples - _lastMicPos;
                float[] t = new float[tail];
                _micClip.GetData(t, _lastMicPos);
                _audioBuffer.AddRange(t);
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
            if (_audioBuffer.Count >= SAMPLE_RATE * MAX_RECORD_SECS) StopListening();
        }

        // ── Transcription ─────────────────────────────────────────────────────

        private IEnumerator TranscribeCoroutine(float[] pcm16k)
        {
            SetState(WhisperState.Transcribing);
            yield return null;
            string result = "";
            bool done = false;
            yield return StartCoroutine(RunInference(pcm16k, r => { result = r; done = true; }));
            while (!done) yield return null;
            Debug.Log($"[WhisperManager] Transcription: {result}");
            OnTranscribed?.Invoke(result);
            WhisperService.NotifyTranscribed(result);
            SetState(WhisperState.Idle);
        }

        private IEnumerator RunInference(float[] pcm, Action<string> callback)
        {
            float[] mel = ComputeMelSpectrogram(pcm);
            yield return null;

            using var melTensor = new TensorFloat(new TensorShape(1, 80, 3000), mel);
            _encoderWorker.Execute(melTensor);
            var audioFeatures = _encoderWorker.PeekOutput("output") as TensorFloat;
            audioFeatures?.MakeReadable();
            yield return null;

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

                int vocabSize = logits?.shape[^1] ?? 0;
                int lastRow   = (logits != null ? logits.shape[1] - 1 : 0);
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
                if (i % 8 == 0) yield return null;
            }
            callback?.Invoke(text.Trim());
        }

        // ── Mel Spectrogram ───────────────────────────────────────────────────

        private static float[] ComputeMelSpectrogram(float[] pcm)
        {
            const int N_FFT    = 400;
            const int HOP      = 160;
            const int N_MELS   = 80;
            const int N_FRAMES = 3000;
            int targetLen = 480000;
            float[] padded = new float[targetLen];
            Array.Copy(pcm, padded, Mathf.Min(pcm.Length, targetLen));

            float[] window = new float[N_FFT];
            for (int i = 0; i < N_FFT; i++)
                window[i] = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (N_FFT - 1)));

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
            float maxVal = float.NegativeInfinity;
            for (int i = 0; i < mel.Length; i++) { mel[i] = Mathf.Log10(mel[i]); if (mel[i] > maxVal) maxVal = mel[i]; }
            for (int i = 0; i < mel.Length; i++) mel[i] = Mathf.Max(mel[i], maxVal - 8f);
            for (int i = 0; i < mel.Length; i++) mel[i] = (mel[i] + 4f) / 4f;
            return mel;
        }

        private static float[] BuildMelFilterbank(int nFft, int nMels, int sr)
        {
            int nFreqs = nFft / 2 + 1;
            float[] filters = new float[nMels * nFreqs];
            float melMin = HzToMel(0f), melMax = HzToMel(sr / 2f);
            float[] melPoints = new float[nMels + 2];
            for (int i = 0; i < melPoints.Length; i++)
                melPoints[i] = MelToHz(melMin + i * (melMax - melMin) / (nMels + 1));
            float[] freqBins = new float[nFreqs];
            for (int i = 0; i < nFreqs; i++) freqBins[i] = i * sr / (float)nFft;
            for (int m = 0; m < nMels; m++)
            {
                float lo = melPoints[m], center = melPoints[m + 1], hi = melPoints[m + 2];
                for (int f = 0; f < nFreqs; f++)
                {
                    float hz = freqBins[f], val = 0f;
                    if (hz >= lo && hz <= center && center > lo) val = (hz - lo) / (center - lo);
                    else if (hz > center && hz <= hi && hi > center) val = (hi - hz) / (hi - center);
                    filters[m * nFreqs + f] = val;
                }
            }
            return filters;
        }

        private static float HzToMel(float hz) => 2595f * Mathf.Log10(1f + hz / 700f);
        private static float MelToHz(float mel) => 700f * (Mathf.Pow(10f, mel / 2595f) - 1f);

        private static float[] FFTPower(float[] x)
        {
            int n = x.Length;
            float[] re = (float[])x.Clone(), im = new float[n];
            int j = 0;
            for (int i = 1; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { float t = re[i]; re[i] = re[j]; re[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang = -2f * Mathf.PI / len, wRe = Mathf.Cos(ang), wIm = Mathf.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float curRe = 1f, curIm = 0f;
                    for (int k = 0; k < len / 2; k++)
                    {
                        float uRe = re[i+k], uIm = im[i+k];
                        float vRe = re[i+k+len/2]*curRe - im[i+k+len/2]*curIm;
                        float vIm = re[i+k+len/2]*curIm + im[i+k+len/2]*curRe;
                        re[i+k] = uRe+vRe; im[i+k] = uIm+vIm;
                        re[i+k+len/2] = uRe-vRe; im[i+k+len/2] = uIm-vIm;
                        float nr = curRe*wRe - curIm*wIm; curIm = curRe*wIm + curIm*wRe; curRe = nr;
                    }
                }
            }
            int half = n / 2 + 1;
            float[] power = new float[half];
            for (int i = 0; i < half; i++) power[i] = re[i]*re[i] + im[i]*im[i];
            return power;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SetState(WhisperState state)
        {
            State = state;
            OnStateChanged?.Invoke(state);
            WhisperService.NotifyState((WhisperService.State)(int)state,
                state == WhisperState.Error ? LastError : "");
        }
    }
}
