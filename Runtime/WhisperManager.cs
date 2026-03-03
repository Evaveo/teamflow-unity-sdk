// Stub only - real implementation in Runtime/Whisper/WhisperManagerImpl.cs (TeamflowSDK.Whisper.asmdef)
// That assembly only compiles when com.unity.ai.inference is installed (defineConstraints).
using System;
using UnityEngine;

namespace TeamflowSDK
{
#if !UNITY_AI_INFERENCE
    public class WhisperManager : MonoBehaviour
    {
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

        public enum WhisperState { Idle, LoadingModel, Recording, Transcribing, Error }
        public WhisperState State     { get; private set; } = WhisperState.Idle;
        public bool         IsReady   { get; private set; } = false;
        public string       LastError { get; private set; } = "Install com.unity.ai.inference to enable offline STT.";

        public event Action<string>       OnTranscribed;
        public event Action<WhisperState> OnStateChanged;

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            WhisperService.Register(StartListening, StopListening);
        }

        public bool StartListening() { return false; }
        public void StopListening()  { }
    }
#endif
}
