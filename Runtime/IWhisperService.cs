using System;
using UnityEngine;

namespace TeamflowSDK
{
    /// <summary>
    /// Minimal interface that decouples TeamflowHUD from WhisperManager.
    /// WhisperManager registers itself here at Awake.
    /// When no Inference Engine is installed, the stub implementation is used automatically.
    /// </summary>
    public static class WhisperService
    {
        public enum State { Idle, LoadingModel, Recording, Transcribing, Error }

        public static event Action<State>  OnStateChanged;
        public static event Action<string> OnTranscribed;

        public static bool IsReady   { get; private set; } = false;
        public static string LastError { get; private set; } = "";

        // Called by WhisperManager when it changes state
        internal static void NotifyState(State s, string error = "")
        {
            IsReady   = (s == State.Idle);
            LastError = error;
            OnStateChanged?.Invoke(s);
        }

        // Called by WhisperManager when transcription is done
        internal static void NotifyTranscribed(string text)
        {
            OnTranscribed?.Invoke(text);
        }

        // Delegates — set by WhisperManager at startup
        private static Func<bool>  _startListening;
        private static Action      _stopListening;

        internal static void Register(Func<bool> start, Action stop)
        {
            _startListening = start;
            _stopListening  = stop;
        }

        public static bool StartListening()
        {
            if (_startListening == null)
            {
                LastError = "Install com.unity.sentis or com.unity.ai.inference to enable speech-to-text.";
                return false;
            }
            return _startListening();
        }

        public static void StopListening() => _stopListening?.Invoke();
    }
}
