using System;
using System.Collections;
using System.Net;
using System.Threading;
using UnityEngine;

namespace TeamflowSDK
{
    /// <summary>
    /// Handles Google OAuth login for the TeamFlow Unity SDK.
    ///
    /// Flow:
    ///   1. Starts a local HttpListener on a random port (localhost:PORT).
    ///   2. Opens the system browser at the TeamFlow Google auth URL
    ///      (which redirects to Google OAuth and back through the backend).
    ///   3. The backend exchanges the code, creates/finds the user,
    ///      and redirects back to localhost:PORT with the Teamflow JWT.
    ///   4. The JWT is stored in PlayerPrefs and the session is restored.
    ///
    /// Compatible with: Unity Editor, PC standalone, Android (browser intent),
    ///                  Meta Quest (opens Oculus browser).
    /// NOT compatible with: WebGL (no HttpListener support).
    /// </summary>
    public class GoogleAuth : MonoBehaviour
    {
        private const int TimeoutSeconds = 120;

        private HttpListener _listener;
        private Thread        _thread;

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Start the Google OAuth flow.
        /// Opens the system browser and waits for the callback.
        /// </summary>
        public void Login(Action<TeamflowUser> onSuccess = null, Action<string> onError = null)
        {
            StartCoroutine(LoginCoroutine(onSuccess, onError));
        }

        // ── Implementation ────────────────────────────────────────────────

        private IEnumerator LoginCoroutine(Action<TeamflowUser> onSuccess, Action<string> onError)
        {
            // Pick a free port
            int port = FindFreePort();
            if (port == 0)
            {
                onError?.Invoke("Could not find a free local port for OAuth callback.");
                yield break;
            }

            string callbackBase = $"http://localhost:{port}/";
            string authUrl = $"{TeamflowClient.BaseUrl}/api/auth/google/unity?redirect_uri={Uri.EscapeDataString(callbackBase)}";

            // Start listener on background thread
            string receivedToken   = null;
            string receivedName    = null;
            string receivedEmail   = null;
            string receivedUserId  = null;
            string listenerError   = null;
            bool   done            = false;

            _listener = new HttpListener();
            _listener.Prefixes.Add(callbackBase);
            try { _listener.Start(); }
            catch (Exception ex)
            {
                onError?.Invoke($"Could not start local OAuth server: {ex.Message}");
                yield break;
            }

            _thread = new Thread(() =>
            {
                try
                {
                    var ctx = _listener.GetContext(); // blocks until request arrives
                    var query = ctx.Request.Url.Query;
                    var parsed = ParseQuery(query);

                    parsed.TryGetValue("token", out receivedToken);
                    parsed.TryGetValue("name",  out receivedName);
                    parsed.TryGetValue("email", out receivedEmail);
                    parsed.TryGetValue("id",    out receivedUserId);

                    // Send a close-tab response
                    var response = ctx.Response;
                    response.ContentType = "text/html; charset=utf-8";
                    byte[] body = System.Text.Encoding.UTF8.GetBytes(
                        "<!DOCTYPE html><html><head><meta charset='utf-8'><title>TeamFlow</title>"
                        + "<script>window.close();</script></head><body>"
                        + "<p style='font-family:sans-serif;text-align:center;margin-top:4rem;color:#4f46e5;font-size:1.2rem;'>"
                        + "✅ Connexion réussie — vous pouvez fermer cet onglet.</p></body></html>");
                    response.ContentLength64 = body.Length;
                    response.OutputStream.Write(body, 0, body.Length);
                    response.Close();
                }
                catch (Exception ex)
                {
                    if (_listener.IsListening) // not a clean abort
                        listenerError = ex.Message;
                }
                finally
                {
                    done = true;
                }
            });
            _thread.IsBackground = true;
            _thread.Start();

            // Open system browser
            Application.OpenURL(authUrl);
            Debug.Log($"[TeamflowSDK] Google OAuth — browser opened. Waiting for callback on port {port}...");

            // Wait with timeout
            float elapsed = 0f;
            while (!done && elapsed < TimeoutSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Stop listener
            try { _listener.Stop(); } catch { }

            if (!done || listenerError != null)
            {
                var msg = listenerError ?? "Google login timed out. Please try again.";
                Debug.LogWarning($"[TeamflowSDK] Google OAuth error: {msg}");
                onError?.Invoke(msg);
                yield break;
            }

            if (string.IsNullOrEmpty(receivedToken))
            {
                var msg = "Authentication failed: no token received.";
                Debug.LogWarning($"[TeamflowSDK] {msg}");
                onError?.Invoke(msg);
                yield break;
            }

            // Build user and persist session
            var user = new TeamflowUser
            {
                id    = receivedUserId  ?? "",
                name  = receivedName    ?? "",
                email = receivedEmail   ?? "",
            };

            // Use the internal setter via TeamflowClient
            TeamflowClient.Instance.SetSessionFromGoogle(receivedToken, user);

            Debug.Log($"[TeamflowSDK] Google login successful: {user.name} ({user.email})");
            onSuccess?.Invoke(user);
            TeamflowClient.Instance.RaiseLoginSuccess(user);
        }

        private void OnDestroy()
        {
            try { _listener?.Stop(); }  catch { }
            try { _thread?.Abort(); }   catch { }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static int FindFreePort()
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            catch { return 0; }
        }

        private static System.Collections.Generic.Dictionary<string, string> ParseQuery(string query)
        {
            var result = new System.Collections.Generic.Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return result;
            query = query.TrimStart('?');
            foreach (var part in query.Split('&'))
            {
                var idx = part.IndexOf('=');
                if (idx < 0) continue;
                var key = Uri.UnescapeDataString(part.Substring(0, idx));
                var val = Uri.UnescapeDataString(part.Substring(idx + 1));
                result[key] = val;
            }
            return result;
        }
    }
}
