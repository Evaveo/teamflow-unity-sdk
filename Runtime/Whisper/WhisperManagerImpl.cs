#if UNITY_AI_INFERENCE
// This file is only compiled when com.unity.ai.inference is installed.
// Add a WhisperBackendInference component to a GameObject in your scene
// and assign the 4 ModelAssets in the Inspector.
// Models: https://huggingface.co/unity/inference-engine-whisper-tiny/tree/main/models
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Unity.InferenceEngine;

namespace TeamflowSDK
{
    /// <summary>
    /// Whisper-Tiny backend using Unity Inference Engine (com.unity.ai.inference, Unity 6+).
    /// Assign the 4 ModelAssets from unity/inference-engine-whisper-tiny in the Inspector,
    /// then this component auto-registers itself with WhisperManager on Start.
    /// </summary>
    public class WhisperBackendInference : MonoBehaviour, IWhisperBackend
    {
        // ── Inspector fields ──────────────────────────────────────────────────
        [Header("Whisper Models (from unity/inference-engine-whisper-tiny)")]
        [SerializeField] private ModelAsset audioEncoder;
        [SerializeField] private ModelAsset audioDecoder1;
        [SerializeField] private ModelAsset audioDecoder2;
        [SerializeField] private ModelAsset logMelSpectro;

        [Header("Language (default: French)")]
        [SerializeField] private int languageToken = 50265; // FR=50265, EN=50259, DE=50261

        // ── IWhisperBackend ───────────────────────────────────────────────────
        public WhisperService.State State   { get; private set; } = WhisperService.State.Idle;
        public bool                 IsReady { get; private set; } = false;
        public string               LastError { get; private set; } = "";

        public event Action<string>               OnTranscribed;
        public event Action<WhisperService.State> OnStateChanged;

        private Action<string> _onTranscribed;
        private Action<string> _onError;

        public void Init(Action<string> onTranscribed, Action<string> onError)
        {
            _onTranscribed = onTranscribed;
            _onError       = onError;
            StartCoroutine(LoadModels());
        }

        // ── Tokens ────────────────────────────────────────────────────────────
        private const int END_OF_TEXT        = 50257;
        private const int START_OF_TRANSCRIPT = 50258;
        private const int TRANSCRIBE         = 50359;
        private const int NO_TIMESTAMPS      = 50363;
        private const int MAX_TOKENS         = 224;
        private const int MAX_SAMPLES        = 30 * 16000;
        private const int SAMPLE_RATE        = 16000;
        private const int MAX_RECORD_SECS    = 10;

        // ── Workers ───────────────────────────────────────────────────────────
        private Worker _encoder;
        private Worker _decoder1;
        private Worker _decoder2;
        private Worker _spectrogram;
        private Worker _argmax;

        // ── Mic ───────────────────────────────────────────────────────────────
        private AudioClip   _micClip;
        private string      _micDevice;
        private bool        _isRecording = false;
        private int         _lastMicPos  = 0;
        private List<float> _audioBuffer = new List<float>();

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            WhisperManager.Instance.RegisterBackend(this);
        }

        private void OnDestroy()
        {
            _encoder?.Dispose();
            _decoder1?.Dispose();
            _decoder2?.Dispose();
            _spectrogram?.Dispose();
            _argmax?.Dispose();
        }

        // ── Model loading ─────────────────────────────────────────────────────

        private IEnumerator LoadModels()
        {
            SetState(WhisperService.State.LoadingModel);

            if (audioEncoder == null || audioDecoder1 == null || audioDecoder2 == null || logMelSpectro == null)
            {
                LastError = "Whisper ModelAssets non assignés. Glissez les 4 modèles depuis " +
                            "unity/inference-engine-whisper-tiny dans l'Inspector du WhisperBackendInference.";
                Debug.LogError($"[WhisperManager] {LastError}");
                SetState(WhisperService.State.Error);
                _onError?.Invoke(LastError);
                yield break;
            }

            yield return null;
            try
            {
                _encoder     = new Worker(ModelLoader.Load(audioEncoder),   BackendType.GPUCompute);
                _decoder1    = new Worker(ModelLoader.Load(audioDecoder1),  BackendType.GPUCompute);
                _decoder2    = new Worker(ModelLoader.Load(audioDecoder2),  BackendType.GPUCompute);
                _spectrogram = new Worker(ModelLoader.Load(logMelSpectro),  BackendType.GPUCompute);

                var graph    = new FunctionalGraph();
                var inp      = graph.AddInput(DataType.Float, new DynamicTensorShape(1, 1, 51865));
                var amax     = Functional.ArgMax(inp, -1, false);
                _argmax      = new Worker(graph.Compile(amax), BackendType.GPUCompute);

                IsReady = true;
                Debug.Log("[WhisperManager] Whisper-Tiny models loaded (InferenceEngine).");
                SetState(WhisperService.State.Idle);
            }
            catch (Exception ex)
            {
                LastError = $"Model load failed: {ex.Message}";
                Debug.LogError($"[WhisperManager] {LastError}");
                SetState(WhisperService.State.Error);
                _onError?.Invoke(LastError);
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
                SetState(WhisperService.State.Error);
                _onError?.Invoke(LastError);
                return false;
            }
            _micDevice   = Microphone.devices[0];
            _audioBuffer.Clear();
            _micClip     = Microphone.Start(_micDevice, true, MAX_RECORD_SECS, SAMPLE_RATE);
            _isRecording = true;
            _lastMicPos  = 0;
            SetState(WhisperService.State.Recording);
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
            _ = TranscribeAsync(trimmed);
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
                int len   = pos - _lastMicPos;
                float[] chunk = new float[len];
                _micClip.GetData(chunk, _lastMicPos);
                _audioBuffer.AddRange(chunk);
                _lastMicPos = pos;
            }
            if (_audioBuffer.Count >= SAMPLE_RATE * MAX_RECORD_SECS) StopListening();
        }

        // ── Transcription (async — official Unity pattern) ────────────────────

        private async Awaitable TranscribeAsync(float[] pcm)
        {
            SetState(WhisperService.State.Transcribing);
            try
            {
                int numSamples = Mathf.Min(pcm.Length, MAX_SAMPLES);
                var audioData  = new float[MAX_SAMPLES];
                Array.Copy(pcm, audioData, numSamples);

                using var audioTensor = new Tensor<float>(new TensorShape(1, MAX_SAMPLES), audioData);
                _spectrogram.Schedule(audioTensor);
                var logmel = _spectrogram.PeekOutput() as Tensor<float>;

                _encoder.Schedule(logmel);
                var encodedAudio = _encoder.PeekOutput() as Tensor<float>;

                var outputTokens = new NativeArray<int>(MAX_TOKENS, Allocator.Persistent);
                outputTokens[0] = START_OF_TRANSCRIPT;
                outputTokens[1] = languageToken;
                outputTokens[2] = TRANSCRIBE;
                int tokenCount  = 3;

                var lastToken       = new NativeArray<int>(1, Allocator.Persistent);
                lastToken[0]        = NO_TIMESTAMPS;
                var lastTokenTensor = new Tensor<int>(new TensorShape(1, 1), new[] { NO_TIMESTAMPS });
                var tokensTensor    = new Tensor<int>(new TensorShape(1, MAX_TOKENS));

                string outputString = "";

                while (tokenCount < MAX_TOKENS - 1)
                {
                    tokensTensor.Reshape(new TensorShape(1, tokenCount));
                    tokensTensor.dataOnBackend.Upload<int>(outputTokens, tokenCount);

                    _decoder1.SetInput("input_ids",             tokensTensor);
                    _decoder1.SetInput("encoder_hidden_states", encodedAudio);
                    _decoder1.Schedule();

                    _decoder2.SetInput("input_ids",             lastTokenTensor);
                    _decoder2.SetInput("encoder_hidden_states", encodedAudio);
                    for (int layer = 0; layer < 4; layer++)
                    {
                        _decoder2.SetInput($"past_key_values.{layer}.decoder.key",   _decoder1.PeekOutput($"present.{layer}.decoder.key")   as Tensor<float>);
                        _decoder2.SetInput($"past_key_values.{layer}.decoder.value", _decoder1.PeekOutput($"present.{layer}.decoder.value") as Tensor<float>);
                        _decoder2.SetInput($"past_key_values.{layer}.encoder.key",   _decoder1.PeekOutput($"present.{layer}.encoder.key")   as Tensor<float>);
                        _decoder2.SetInput($"past_key_values.{layer}.encoder.value", _decoder1.PeekOutput($"present.{layer}.encoder.value") as Tensor<float>);
                    }
                    _decoder2.Schedule();

                    var logits = _decoder2.PeekOutput("logits") as Tensor<float>;
                    _argmax.Schedule(logits);

                    using var tokenResult = await _argmax.PeekOutput().ReadbackAndCloneAsync() as Tensor<int>;
                    int index = tokenResult[0];

                    outputTokens[tokenCount] = lastToken[0];
                    lastToken[0]             = index;
                    tokenCount++;
                    lastTokenTensor.dataOnBackend.Upload<int>(lastToken, 1);

                    if (index == END_OF_TEXT) break;
                    if (index < 50257)
                        outputString += WhisperTokenizer.Decode(index);
                }

                outputTokens.Dispose();
                lastToken.Dispose();
                lastTokenTensor.Dispose();
                tokensTensor.Dispose();

                string result = outputString.Trim();
                Debug.Log($"[WhisperManager] Transcription: {result}");
                _onTranscribed?.Invoke(result);
                OnTranscribed?.Invoke(result);
                WhisperService.NotifyTranscribed(result);
                SetState(WhisperService.State.Idle);
            }
            catch (Exception ex)
            {
                LastError = $"Transcription failed: {ex.Message}";
                Debug.LogError($"[WhisperManager] {LastError}");
                SetState(WhisperService.State.Error);
                _onError?.Invoke(LastError);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SetState(WhisperService.State state)
        {
            State = state;
            OnStateChanged?.Invoke(state);
            WhisperService.NotifyState(state,
                state == WhisperService.State.Error ? LastError : "");
        }
    }
}
#endif

