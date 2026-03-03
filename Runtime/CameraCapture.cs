using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace TeamflowSDK
{
    /// <summary>
    /// Captures a photo and returns PNG/JPG bytes.
    ///
    /// Platform behaviour:
    ///   Android / iOS   → NativeCamera via WebCamTexture (no plugin required)
    ///   VR (Quest etc.) → Passthrough snapshot via WebCamTexture index 0
    ///   Editor          → Reads from a WebCamTexture (first available device)
    ///                      or falls back to a solid-color placeholder so the
    ///                      workflow can be tested without a physical camera.
    /// </summary>
    public class CameraCapture : MonoBehaviour
    {
        private WebCamTexture _webcam;

        // ── Public API ───────────────────────────────────────────────────

        /// <summary>
        /// Start the camera preview and write the texture into <paramref name="previewTarget"/>.
        /// Call StopPreview() when done.
        /// </summary>
        public void StartPreview(RawImage previewTarget = null)
        {
            if (WebCamTexture.devices.Length == 0)
            {
                Debug.LogWarning("[TeamflowSDK] No camera devices found.");
                return;
            }

            _webcam = new WebCamTexture(WebCamTexture.devices[0].name, 1280, 720, 30);
            _webcam.Play();

            if (previewTarget != null)
                previewTarget.texture = _webcam;
        }

        public void StopPreview()
        {
            if (_webcam != null && _webcam.isPlaying)
                _webcam.Stop();
        }

        /// <summary>
        /// Capture the current webcam frame (or a placeholder in Editor without camera).
        /// Returns JPEG bytes via <paramref name="onCaptured"/>.
        /// </summary>
        public void CapturePhoto(Action<byte[], string> onCaptured)
        {
#if UNITY_EDITOR
            if (_webcam == null || !_webcam.isPlaying)
            {
                // Editor fallback: generate a 256x256 placeholder texture
                var placeholder = CreatePlaceholder();
                var bytes = placeholder.EncodeToJPG(85);
                Destroy(placeholder);
                onCaptured?.Invoke(bytes, "capture_editor_placeholder.jpg");
                return;
            }
#endif
            StartCoroutine(CaptureCoroutine(onCaptured));
        }

        private IEnumerator CaptureCoroutine(Action<byte[], string> onCaptured)
        {
            // Wait for camera to have a valid frame
            float timeout = 5f;
            while ((_webcam == null || !_webcam.didUpdateThisFrame) && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (_webcam == null)
            {
                onCaptured?.Invoke(null, null);
                yield break;
            }

            // Blit webcam frame to a Texture2D
            var snap = new Texture2D(_webcam.width, _webcam.height, TextureFormat.RGB24, false);
            snap.SetPixels(_webcam.GetPixels());
            snap.Apply();

            var bytes = snap.EncodeToJPG(85);
            Destroy(snap);

            var filename = $"capture_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg";
            onCaptured?.Invoke(bytes, filename);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static Texture2D CreatePlaceholder()
        {
            var tex = new Texture2D(256, 256, TextureFormat.RGB24, false);
            var pixels = new Color[256 * 256];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(0.2f, 0.4f, 0.8f); // Teamflow blue
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private void OnDestroy() => StopPreview();
    }
}
