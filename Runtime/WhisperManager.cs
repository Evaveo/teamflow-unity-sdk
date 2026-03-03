using System;
using UnityEngine;

namespace TeamflowSDK
{
    /// <summary>
    /// Facade Singleton for Whisper speech-to-text.
    /// Backends (Inference or Sentis) register themselves here at runtime.
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
                    // Search for existing instance first
                    _instance = FindObjectOfType<WhisperManager>();
                    if (_instance == null)
                    {
                        var go = new GameObject("[WhisperManager]");
                        _instance = go.AddComponent<WhisperManager>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }

        // ── Backend ───────────────────────────────────────────────────────────
        private IWhisperBackend _backend;

        public void RegisterBackend(IWhisperBackend backend)
        {
            if (_backend != null)
            {
                Debug.LogWarning($"[WhisperManager] Backend already registered ({_backend.GetType().Name}), ignoring new one ({backend.GetType().Name}).");
                return;
            }
            _backend = backend;
            _backend.Init(
                onTranscribed: text => {
                    OnTranscribed?.Invoke(text);
                    WhisperService.NotifyTranscribed(text);
                    SetState(WhisperState.Idle);
                },
                onError: error => {
                    LastError = error;
                    SetState(WhisperState.Error);
                }
            );
            Debug.Log($"[WhisperManager] Registered backend: {backend.GetType().Name}");
            SetState(WhisperState.Idle); 
        }

        // ── State ─────────────────────────────────────────────────────────────
        public enum WhisperState { Idle, LoadingModel, Recording, Transcribing, Error }
        
        public WhisperState State     { get; private set; } = WhisperState.Idle;
        public bool         IsReady   => _backend?.IsReady ?? false;
        public string       LastError { get; private set; } = "";

        public event Action<string>       OnTranscribed;
        public event Action<WhisperState> OnStateChanged;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            
            // Register legacy static service bridge
            WhisperService.Register(StartListening, StopListening);
        }

        private void Start()
        {
            // If no backend registered yet, give a hint. 
            if (_backend == null)
            {
                LastError = "No Whisper backend found. Install com.unity.ai.inference (Unity 6) or com.unity.sentis (Unity 2022).";
            }
        }

        // ── Public API ────────────────────────────────────────────────────────
        public bool StartListening()
        {
            if (_backend == null) 
            {
                Debug.LogWarning("[WhisperManager] No backend registered.");
                return false;
            }
            
            bool started = _backend.StartListening();
            if (started) SetState(WhisperState.Recording);
            return started;
        }

        public void StopListening()
        {
            if (_backend == null) return;
            _backend.StopListening();
            SetState(WhisperState.Transcribing);
        }

        public void SetState(WhisperState state)
        {
            State = state;
            OnStateChanged?.Invoke(state);
            WhisperService.NotifyState((WhisperService.State)(int)state, LastError);
        }
    }
}
