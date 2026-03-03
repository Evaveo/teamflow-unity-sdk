using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace TeamflowSDK
{
    /// <summary>
    /// Singleton HTTP client for the TeamFlow API.
    /// Compatible with Unity Editor, VR (Meta Quest / OpenXR) and mobile.
    /// Uses only UnityWebRequest — no System.Net.Http dependency.
    /// </summary>
    public class TeamflowClient : MonoBehaviour
    {
        // ── Singleton ────────────────────────────────────────────────────

        private static TeamflowClient _instance;
        public static TeamflowClient Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[TeamflowClient]");
                    _instance = go.AddComponent<TeamflowClient>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        /// <summary>
        /// Automatically boot the singleton at Play Mode start if a saved
        /// session exists in PlayerPrefs (e.g. set by the Editor window).
        /// No scene object or manual setup required.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoBootIfSessionExists()
        {
            if (string.IsNullOrEmpty(PlayerPrefs.GetString("teamflow_token", ""))) return;

            // Creating Instance also calls Awake → TryRestoreSession
            var client = Instance;

            // Attach HUD — now in TeamflowSDK namespace, compiled in Runtime asmdef
            if (client.GetComponent<TeamflowHUD>() == null)
                client.gameObject.AddComponent<TeamflowHUD>();
        }

        // ── Configuration ────────────────────────────────────────────────

        private const string PREFS_TOKEN    = "teamflow_token";
        private const string PREFS_USER_ID  = "teamflow_user_id";
        private const string PREFS_USERNAME = "teamflow_username";
        private const string PREFS_EMAIL    = "teamflow_email";

        /// <summary>Base URL of the TeamFlow backend. Override before first use.</summary>
        public static string BaseUrl = "https://teamflow-api-544622760078.us-central1.run.app";

        // ── State ─────────────────────────────────────────────────────────

        public string Token      { get; private set; }
        public TeamflowUser CurrentUser { get; private set; }
        public bool IsAuthenticated => !string.IsNullOrEmpty(Token);

        // ── Events ───────────────────────────────────────────────────────

        public event Action<TeamflowUser> OnLoginSuccess;
        public event Action<string>       OnLoginFailed;
        public event Action               OnLogout;

        // ── Google Auth bridge ───────────────────────────────────────────

        /// <summary>
        /// Called by GoogleAuth after a successful OAuth flow.
        /// Stores the JWT and user info, persists the session.
        /// </summary>
        public void SetSessionFromGoogle(string token, TeamflowUser user)
        {
            Token       = token;
            CurrentUser = user;
            PersistSession();
        }

        /// <summary>
        /// Fires OnLoginSuccess from outside the class (e.g. GoogleAuth).
        /// </summary>
        public void RaiseLoginSuccess(TeamflowUser user)
        {
            OnLoginSuccess?.Invoke(user);
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            TryRestoreSession();
        }

        // ── Auth ─────────────────────────────────────────────────────────

        /// <summary>Restore a previously saved JWT session from PlayerPrefs.</summary>
        private void TryRestoreSession()
        {
            var saved = PlayerPrefs.GetString(PREFS_TOKEN, "");
            if (string.IsNullOrEmpty(saved)) return;

            Token = saved;
            CurrentUser = new TeamflowUser
            {
                id    = PlayerPrefs.GetString(PREFS_USER_ID, ""),
                name  = PlayerPrefs.GetString(PREFS_USERNAME, ""),
                email = PlayerPrefs.GetString(PREFS_EMAIL, ""),
            };
            Debug.Log($"[TeamflowSDK] Session restored for {CurrentUser.email}");
        }

        /// <summary>
        /// Login with email / password.
        /// Fires OnLoginSuccess or OnLoginFailed on the main thread.
        /// </summary>
        public void Login(string email, string password, Action<TeamflowUser> onSuccess = null, Action<string> onError = null)
        {
            StartCoroutine(LoginCoroutine(email, password, onSuccess, onError));
        }

        private IEnumerator LoginCoroutine(string email, string password,
            Action<TeamflowUser> onSuccess, Action<string> onError)
        {
            var body = JsonUtility.ToJson(new LoginRequest { email = email, password = password });

            using var req = new UnityWebRequest($"{BaseUrl}/api/auth/login", "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                var msg = $"Login failed: {req.error}";
                Debug.LogWarning($"[TeamflowSDK] {msg}");
                onError?.Invoke(msg);
                OnLoginFailed?.Invoke(msg);
                yield break;
            }

            var resp = JsonUtility.FromJson<LoginResponse>(req.downloadHandler.text);
            if (resp == null || string.IsNullOrEmpty(resp.token))
            {
                var err = TryParseError(req.downloadHandler.text);
                onError?.Invoke(err);
                OnLoginFailed?.Invoke(err);
                yield break;
            }

            Token       = resp.token;
            CurrentUser = resp.user;
            PersistSession();

            Debug.Log($"[TeamflowSDK] Logged in as {CurrentUser.name}");
            onSuccess?.Invoke(CurrentUser);
            OnLoginSuccess?.Invoke(CurrentUser);
        }

        /// <summary>Clear local session.</summary>
        public void Logout()
        {
            Token       = null;
            CurrentUser = null;
            PlayerPrefs.DeleteKey(PREFS_TOKEN);
            PlayerPrefs.DeleteKey(PREFS_USER_ID);
            PlayerPrefs.DeleteKey(PREFS_USERNAME);
            PlayerPrefs.DeleteKey(PREFS_EMAIL);
            PlayerPrefs.Save();
            OnLogout?.Invoke();
        }

        // ── Device Code Auth (VR Mode) ───────────────────────────────────

        /// <summary>
        /// Authenticate using a 4-digit device code generated from the web portal.
        /// On success, stores the session and fires OnLoginSuccess.
        /// </summary>
        public void AuthWithDeviceCode(string code,
            Action<TeamflowUser> onSuccess = null, Action<string> onError = null)
        {
            StartCoroutine(DeviceCodeCoroutine(code, onSuccess, onError));
        }

        private IEnumerator DeviceCodeCoroutine(string code,
            Action<TeamflowUser> onSuccess, Action<string> onError)
        {
            var body = $"{{\"code\":\"{code}\"}}";
            using var req = new UnityWebRequest($"{BaseUrl}/api/auth/device/exchange", "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                var msg = $"Device auth failed: {req.error}";
                Debug.LogWarning($"[TeamflowSDK] {msg} — body: {req.downloadHandler?.text}");
                onError?.Invoke(msg);
                OnLoginFailed?.Invoke(msg);
                yield break;
            }

            var resp = JsonUtility.FromJson<LoginResponse>(req.downloadHandler.text);
            if (resp == null || string.IsNullOrEmpty(resp.token))
            {
                var err = TryParseError(req.downloadHandler.text);
                Debug.LogWarning($"[TeamflowSDK] Device auth error: {err}");
                onError?.Invoke(err);
                OnLoginFailed?.Invoke(err);
                yield break;
            }

            Token       = resp.token;
            CurrentUser = resp.user;
            PersistSession();

            Debug.Log($"[TeamflowSDK] VR auth success: {CurrentUser.name} ({CurrentUser.email})");
            onSuccess?.Invoke(CurrentUser);
            OnLoginSuccess?.Invoke(CurrentUser);
        }

        // ── Projects ─────────────────────────────────────────────────────

        /// <summary>Fetch all projects the current user can access.</summary>
        public void GetProjects(Action<List<TeamflowProject>> onSuccess, Action<string> onError = null)
        {
            StartCoroutine(GetArrayCoroutine<TeamflowProject>("/api/projects", onSuccess, onError));
        }

        // ── Members ──────────────────────────────────────────────────────

        /// <summary>Fetch non-external members of a project.</summary>
        public void GetProjectMembers(string projectId,
            Action<List<TeamflowMember>> onSuccess, Action<string> onError = null)
        {
            StartCoroutine(GetArrayCoroutine<TeamflowMember>(
                $"/api/projects/{projectId}/members", onSuccess, onError));
        }

        // ── Epics ─────────────────────────────────────────────────────────

        /// <summary>Fetch epics for a project.</summary>
        public void GetProjectEpics(string projectId,
            Action<List<TeamflowEpic>> onSuccess, Action<string> onError = null)
        {
            StartCoroutine(GetArrayCoroutine<TeamflowEpic>(
                $"/api/epics/project/{projectId}", onSuccess, onError));
        }

        // ── Tasks ─────────────────────────────────────────────────────────

        /// <summary>Create a task. Returns the created task on success.</summary>
        public void CreateTask(CreateTaskRequest payload,
            Action<TeamflowTask> onSuccess, Action<string> onError = null)
        {
            StartCoroutine(PostCoroutine<TeamflowTask>("/api/tasks", payload, onSuccess, onError));
        }

        /// <summary>
        /// Upload a photo (PNG/JPG bytes) as a task attachment.
        /// Call after CreateTask to attach the image.
        /// </summary>
        public void UploadTaskPhoto(string taskId, byte[] imageBytes, string filename,
            Action<TeamflowAttachment> onSuccess, Action<string> onError = null)
        {
            StartCoroutine(UploadPhotoCoroutine(taskId, imageBytes, filename, onSuccess, onError));
        }

        private IEnumerator UploadPhotoCoroutine(string taskId, byte[] imageBytes, string filename,
            Action<TeamflowAttachment> onSuccess, Action<string> onError)
        {
            var form = new WWWForm();
            form.AddBinaryData("files", imageBytes, filename, "image/jpeg");

            using var req = UnityWebRequest.Post($"{BaseUrl}/api/tasks/{taskId}/attachments", form);
            req.SetRequestHeader("Authorization", $"Bearer {Token}");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                var msg = $"Upload failed: {req.error}";
                Debug.LogWarning($"[TeamflowSDK] {msg}");
                onError?.Invoke(msg);
                yield break;
            }

            // Backend returns { attachments: [...] } — unwrap first item
            var body = req.downloadHandler.text ?? "";
            Debug.Log($"[TeamflowSDK] Upload response: {body}");
            var wrapper = JsonUtility.FromJson<AttachmentsResponse>(body);
            var attachment = wrapper?.attachments != null && wrapper.attachments.Count > 0
                ? wrapper.attachments[0]
                : null;
            if (attachment == null)
            {
                Debug.LogWarning($"[TeamflowSDK] Upload: could not parse attachment from response: {body}");
                onError?.Invoke("Could not parse attachment response");
                yield break;
            }
            onSuccess?.Invoke(attachment);
        }

        // ── Generic HTTP helpers ─────────────────────────────────────────

        private IEnumerator GetCoroutine<T>(string path, Action<T> onSuccess, Action<string> onError)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}{path}");
            AddAuthHeader(req);
            yield return req.SendWebRequest();

            if (!HandleError(req, onError)) yield break;
            var result = JsonUtility.FromJson<T>(req.downloadHandler.text);
            onSuccess?.Invoke(result);
        }

        // /api/.../members and /api/epics/project/:id return JSON arrays directly
        // JsonUtility cannot deserialize root arrays — wrap them first
        private IEnumerator GetArrayCoroutine<T>(string path,
            Action<List<T>> onSuccess, Action<string> onError)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}{path}");
            AddAuthHeader(req);
            yield return req.SendWebRequest();

            if (!HandleError(req, onError)) yield break;

            // Wrap bare array: [{...}] -> {"items":[{...}]}
            var wrapped = $"{{\"items\":{req.downloadHandler.text}}}";
            var wrapper = JsonUtility.FromJson<ArrayWrapper<T>>(wrapped);
            onSuccess?.Invoke(wrapper?.items ?? new List<T>());
        }

        [Serializable]
        private class ArrayWrapper<T> { public List<T> items; }

        private IEnumerator PostCoroutine<T>(string path, object payload,
            Action<T> onSuccess, Action<string> onError)
        {
            var body = JsonUtility.ToJson(payload);
            using var req = new UnityWebRequest($"{BaseUrl}{path}", "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            AddAuthHeader(req);

            yield return req.SendWebRequest();

            if (!HandleError(req, onError)) yield break;
            var result = JsonUtility.FromJson<T>(req.downloadHandler.text);
            onSuccess?.Invoke(result);
        }

        private void AddAuthHeader(UnityWebRequest req)
        {
            if (!string.IsNullOrEmpty(Token))
                req.SetRequestHeader("Authorization", $"Bearer {Token}");
        }

        private bool HandleError(UnityWebRequest req, Action<string> onError)
        {
            if (req.result == UnityWebRequest.Result.Success) return true;
            var msg = TryParseError(req.downloadHandler?.text) ?? req.error;
            Debug.LogWarning($"[TeamflowSDK] API error: {msg}");
            onError?.Invoke(msg);
            return false;
        }

        private string TryParseError(string json)
        {
            if (string.IsNullOrEmpty(json)) return "Unknown error";
            try { return JsonUtility.FromJson<ApiError>(json)?.error ?? json; }
            catch { return json; }
        }

        private void PersistSession()
        {
            PlayerPrefs.SetString(PREFS_TOKEN,    Token);
            PlayerPrefs.SetString(PREFS_USER_ID,  CurrentUser.id);
            PlayerPrefs.SetString(PREFS_USERNAME, CurrentUser.name);
            PlayerPrefs.SetString(PREFS_EMAIL,    CurrentUser.email);
            PlayerPrefs.Save();
        }

        // ── Nested wrapper for /api/projects (returns array directly) ────

        [Serializable]
        private class TeamflowProjectListWrapper
        {
            public List<TeamflowProject> projects;
        }
    }
}
