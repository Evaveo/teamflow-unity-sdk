using System;

namespace TeamflowSDK
{
    public interface IWhisperBackend
    {
        bool IsReady { get; }
        void Init(Action<string> onTranscribed, Action<string> onError);
        bool StartListening();
        void StopListening();
    }
}
