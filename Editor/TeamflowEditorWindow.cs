using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace TeamflowSDK.Editor
{
    /// <summary>
    /// TeamFlow Editor Window — create tasks directly from the Unity Editor
    /// without entering Play Mode.
    ///
    /// Open via:  Tools → TeamFlow → Create Task
    /// </summary>
    public class TeamflowEditorWindow : EditorWindow
    {
        // ── Constants ────────────────────────────────────────────────────

        private const string PREFS_URL      = "teamflow_editor_url";
        private const string PREFS_EMAIL    = "teamflow_editor_email";
        private const string PREFS_TOKEN    = "teamflow_editor_token";
        private const string PREFS_USERNAME = "teamflow_editor_username";
        private const string PREFS_PROJECT  = "teamflow_editor_projectId";

        // ── State ─────────────────────────────────────────────────────────

        // Settings
        private string _baseUrl  = "https://teamflow-api-544622760078.us-central1.run.app";
        private string _email    = "";
        private string _password = "";
        private string _token    = "";
        private string _username = "";

        // Projects
        private List<ProjectItem> _projects = new();
        private int _selectedProjectIndex   = 0;

        // Task form
        private string _taskTitle       = "";
        private string _taskDescription = "";
        private string _taskPriority    = "MEDIUM";
        private string _taskType        = "FEATURE";
        private string _photoPath       = "";
        private Texture2D _photoPreview;

        // Members & Epics
        private List<MemberItem> _members        = new();
        private List<EpicItem>   _epics           = new();
        private bool[]           _memberSelected;
        private int              _selectedEpicIndex = 0;  // 0 = "— No epic —"
        private int              _lastLoadedProjectIndex = -1;

        // UI state
        private enum View { Settings, Login, TaskForm }
        private View _view = View.Login;
        private string _statusMessage = "";
        private bool   _statusIsError;
        private bool   _isBusy;
        private float  _progress;
        private Vector2 _scroll;

        private readonly string[] _priorities = { "LOW", "MEDIUM", "HIGH", "CRITICAL" };
        private readonly string[] _types      = { "FEATURE", "BUG", "IMPROVEMENT", "DOCS", "MEETING" };

        // ── Menu item ────────────────────────────────────────────────────

        [MenuItem("Tools/TeamFlow/Create Task %#t")]
        public static void Open()
        {
            var win = GetWindow<TeamflowEditorWindow>("TeamFlow — Create Task");
            win.minSize = new Vector2(420, 560);
            win.Show();
        }

        [MenuItem("Tools/TeamFlow/Settings")]
        public static void OpenSettings()
        {
            var win = GetWindow<TeamflowEditorWindow>("TeamFlow — Create Task");
            win.minSize = new Vector2(420, 560);
            win._view = View.Settings;
            win.Show();
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        private void OnEnable()
        {
            _baseUrl  = EditorPrefs.GetString(PREFS_URL,      _baseUrl);
            _email    = EditorPrefs.GetString(PREFS_EMAIL,    "");
            _token    = EditorPrefs.GetString(PREFS_TOKEN,    "");
            _username = EditorPrefs.GetString(PREFS_USERNAME, "");
            _selectedProjectIndex = 0;

            if (!string.IsNullOrEmpty(_token))
            {
                _view = View.TaskForm;
                FetchProjects();
            }
        }

        private void OnFocus()
        {
            // Re-fetch projects whenever the window regains focus
            // (e.g. after OAuth browser returns, or user switches back to Unity)
            if (!string.IsNullOrEmpty(_token) && _view == View.TaskForm)
                FetchProjects();
        }

        // ── GUI ───────────────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawHeader();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            switch (_view)
            {
                case View.Settings: DrawSettings(); break;
                case View.Login:    DrawLogin();    break;
                case View.TaskForm: DrawTaskForm(); break;
            }

            EditorGUILayout.EndScrollView();
            DrawStatusBar();
        }

        // ── Header ────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            var headerStyle = new GUIStyle(EditorStyles.toolbar)
            {
                fixedHeight = 36,
                alignment   = TextAnchor.MiddleLeft
            };
            using (new EditorGUILayout.HorizontalScope(headerStyle))
            {
                GUILayout.Label("⚡  TeamFlow", new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize  = 14,
                    alignment = TextAnchor.MiddleLeft
                });
                GUILayout.FlexibleSpace();

                if (!string.IsNullOrEmpty(_token))
                {
                    GUILayout.Label($"👤 {_username}", EditorStyles.miniLabel);
                    GUILayout.Space(8);
                    if (GUILayout.Button("＋ Add to Scene", EditorStyles.toolbarButton))
                        AddClientToScene();
                    GUILayout.Space(4);
                    if (GUILayout.Button("Logout", EditorStyles.toolbarButton))
                        Logout();
                }

                if (GUILayout.Button("⚙", EditorStyles.toolbarButton, GUILayout.Width(26)))
                    _view = View.Settings;
            }
        }

        // ── Settings view ────────────────────────────────────────────────

        private void DrawSettings()
        {
            GUILayout.Space(12);
            GUILayout.Label("Server Settings", EditorStyles.boldLabel);
            GUILayout.Space(4);

            _baseUrl = EditorGUILayout.TextField("Backend URL", _baseUrl);

            GUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save"))
                {
                    EditorPrefs.SetString(PREFS_URL, _baseUrl);
                    SetStatus("Settings saved.", false);
                    _view = string.IsNullOrEmpty(_token) ? View.Login : View.TaskForm;
                }
                if (GUILayout.Button("Cancel"))
                    _view = string.IsNullOrEmpty(_token) ? View.Login : View.TaskForm;
            }
        }

        // ── Login view ────────────────────────────────────────────────────

        private void DrawLogin()
        {
            GUILayout.Space(20);
            GUILayout.Label("Sign in to TeamFlow", EditorStyles.boldLabel);
            GUILayout.Space(8);

            // ── Google Sign-In ────────────────────────────────────────────
            using (new EditorGUI.DisabledScope(_isBusy))
            {
                var googleStyle = new GUIStyle(GUI.skin.button)
                {
                    fontStyle = FontStyle.Bold,
                    fixedHeight = 34
                };
                if (GUILayout.Button("🔵  Sign in with Google", googleStyle))
                    EditorCoroutineUtility.StartCoroutine(GoogleLoginCoroutine(), this);
            }

            GUILayout.Space(8);

            // ── Divider ───────────────────────────────────────────────────
            var lineRect = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(lineRect, new Color(0.5f, 0.5f, 0.5f, 0.4f));
            GUILayout.Space(4);
            GUILayout.Label("— or sign in with email —", EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(4);

            // ── Email / Password ──────────────────────────────────────────
            _email    = EditorGUILayout.TextField("Email", _email);
            _password = EditorGUILayout.PasswordField("Password", _password);

            GUILayout.Space(12);
            using (new EditorGUI.DisabledScope(_isBusy))
            {
                if (GUILayout.Button(_isBusy ? "Signing in…" : "Sign In", GUILayout.Height(32)))
                    EditorCoroutineUtility.StartCoroutine(LoginCoroutine(), this);
            }
        }

        // ── Task form view ────────────────────────────────────────────────

        private void DrawTaskForm()
        {
            GUILayout.Space(12);
            GUILayout.Label("New Task", EditorStyles.boldLabel);
            GUILayout.Space(8);

            // Project selector
            if (_projects.Count > 0)
            {
                var names = _projects.ConvertAll(p => p.name).ToArray();
                var newIdx = EditorGUILayout.Popup("Project", _selectedProjectIndex, names);
                if (newIdx != _selectedProjectIndex || _lastLoadedProjectIndex != newIdx)
                {
                    _selectedProjectIndex = newIdx;
                    FetchMembersAndEpics(_projects[newIdx].id);
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Project", "Loading…");
                    if (GUILayout.Button("↻", GUILayout.Width(26))) FetchProjects();
                }
            }

            GUILayout.Space(6);

            // Title
            GUILayout.Label("Title *");
            _taskTitle = EditorGUILayout.TextField(_taskTitle);

            GUILayout.Space(4);

            // Description
            GUILayout.Label("Description");
            _taskDescription = EditorGUILayout.TextArea(_taskDescription, GUILayout.Height(80));

            GUILayout.Space(6);

            // Priority & Type
            using (new EditorGUILayout.HorizontalScope())
            {
                int pi = Array.IndexOf(_priorities, _taskPriority);
                pi = EditorGUILayout.Popup("Priority", pi < 0 ? 1 : pi, _priorities);
                _taskPriority = _priorities[pi];

                int ti = Array.IndexOf(_types, _taskType);
                ti = EditorGUILayout.Popup("Type", ti < 0 ? 0 : ti, _types);
                _taskType = _types[ti];
            }

            GUILayout.Space(6);

            // Epic selector
            if (_epics.Count > 0)
            {
                var epicNames = new string[_epics.Count + 1];
                epicNames[0] = "— No epic —";
                for (int i = 0; i < _epics.Count; i++) epicNames[i + 1] = _epics[i].title;
                _selectedEpicIndex = EditorGUILayout.Popup("Epic", _selectedEpicIndex, epicNames);
            }
            else
            {
                EditorGUILayout.LabelField("Epic", "None available");
            }

            GUILayout.Space(6);

            // Assignees
            if (_members.Count > 0)
            {
                GUILayout.Label("Assign to", EditorStyles.boldLabel);
                if (_memberSelected == null || _memberSelected.Length != _members.Count)
                    _memberSelected = new bool[_members.Count];

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    for (int i = 0; i < _members.Count; i++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _memberSelected[i] = EditorGUILayout.Toggle(_memberSelected[i], GUILayout.Width(18));
                            GUILayout.Label($"{_members[i].name}  ", EditorStyles.label);
                            GUILayout.Label(_members[i].email, EditorStyles.miniLabel);
                        }
                    }
                }
            }
            else
            {
                EditorGUILayout.LabelField("Assign to", "No members loaded");
            }

            GUILayout.Space(8);

            // Photo attachment
            GUILayout.Label("Screenshot / Photo");
            using (new EditorGUILayout.HorizontalScope())
            {
                _photoPath = EditorGUILayout.TextField(_photoPath);
                if (GUILayout.Button("Browse…", GUILayout.Width(72)))
                {
                    var path = EditorUtility.OpenFilePanel(
                        "Select image", "", "jpg,jpeg,png,bmp");
                    if (!string.IsNullOrEmpty(path))
                    {
                        _photoPath    = path;
                        _photoPreview = LoadPreview(path);
                    }
                }
                if (GUILayout.Button("Screenshot", GUILayout.Width(88)))
                    CaptureEditorScreenshot();
            }

            if (_photoPreview != null)
            {
                GUILayout.Space(4);
                var rect = GUILayoutUtility.GetRect(200, 120, GUILayout.ExpandWidth(false));
                GUI.DrawTexture(rect, _photoPreview, ScaleMode.ScaleToFit);
                if (GUILayout.Button("✕ Remove photo", GUILayout.Width(120)))
                {
                    _photoPath    = "";
                    _photoPreview = null;
                }
            }

            GUILayout.Space(12);

            // Progress bar
            if (_isBusy)
            {
                var barRect = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(barRect, _progress, $"Sending… {(int)(_progress * 100)}%");
                GUILayout.Space(4);
            }

            using (new EditorGUI.DisabledScope(_isBusy || _projects.Count == 0))
            {
                if (GUILayout.Button(_isBusy ? "Creating…" : "Create Task", GUILayout.Height(36)))
                    EditorCoroutineUtility.StartCoroutine(CreateTaskCoroutine(), this);
            }
        }

        // ── Status bar ────────────────────────────────────────────────────

        private void DrawStatusBar()
        {
            if (string.IsNullOrEmpty(_statusMessage)) return;

            var style = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize  = 11,
                alignment = TextAnchor.MiddleLeft,
                wordWrap  = true
            };
            GUI.color = _statusIsError ? new Color(1f, 0.4f, 0.4f) : new Color(0.4f, 1f, 0.6f);
            GUILayout.Label(_statusMessage, style);
            GUI.color = Color.white;
        }

        // ── Google OAuth coroutine (Editor — no MonoBehaviour needed) ──────

        private IEnumerator GoogleLoginCoroutine()
        {
            _isBusy = true;
            SetStatus("Opening Google sign-in in browser…", false);
            Repaint();

            // Pick a free port
            int port = 0;
            try
            {
                var tmp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                tmp.Start();
                port = ((IPEndPoint)tmp.LocalEndpoint).Port;
                tmp.Stop();
            }
            catch
            {
                _isBusy = false;
                SetStatus("Could not find a free local port.", true);
                Repaint();
                yield break;
            }

            string callbackBase = $"http://localhost:{port}/";
            string authUrl = $"{_baseUrl}/api/auth/google/unity?redirect_uri={Uri.EscapeDataString(callbackBase)}";

            // Start local listener on background thread
            string receivedToken  = null;
            string receivedName   = null;
            string receivedEmail  = null;
            string listenerError  = null;
            bool   done           = false;

            var listener = new HttpListener();
            listener.Prefixes.Add(callbackBase);
            try { listener.Start(); }
            catch (Exception ex)
            {
                _isBusy = false;
                SetStatus($"Could not start local OAuth server: {ex.Message}", true);
                Repaint();
                yield break;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    var ctx    = listener.GetContext();
                    var query  = ctx.Request.Url.Query;
                    var parsed = ParseQueryString(query);
                    parsed.TryGetValue("token", out receivedToken);
                    parsed.TryGetValue("name",  out receivedName);
                    parsed.TryGetValue("email", out receivedEmail);

                    var resp = ctx.Response;
                    resp.ContentType = "text/html; charset=utf-8";
                    byte[] body = Encoding.UTF8.GetBytes(
                        "<!DOCTYPE html><html><head><meta charset='utf-8'><title>TeamFlow</title>"
                        + "<script>window.close();</script></head><body>"
                        + "<p style='font-family:sans-serif;text-align:center;margin-top:4rem;color:#4f46e5;font-size:1.2rem;'>"
                        + "✅ Connexion réussie — vous pouvez fermer cet onglet.</p></body></html>");
                    resp.ContentLength64 = body.Length;
                    resp.OutputStream.Write(body, 0, body.Length);
                    resp.Close();
                }
                catch (Exception ex)
                {
                    if (listener.IsListening) listenerError = ex.Message;
                }
                finally { done = true; }
            });
            thread.IsBackground = true;
            thread.Start();

            Application.OpenURL(authUrl);

            // Wait up to 120s for callback
            // NOTE: WaitForSecondsRealtime does not work in Editor coroutines — use timeSinceStartup
            double startTime = EditorApplication.timeSinceStartup;
            double timeout   = 120.0;
            while (!done && (EditorApplication.timeSinceStartup - startTime) < timeout)
            {
                int remaining = (int)(timeout - (EditorApplication.timeSinceStartup - startTime));
                SetStatus($"En attente du callback Google… ({remaining}s)", false);
                Repaint();
                yield return null;
            }

            try { listener.Stop(); } catch { }

            _isBusy = false;

            if (!done || listenerError != null)
            {
                SetStatus(listenerError ?? "Google login timed out. Please try again.", true);
                Repaint();
                yield break;
            }

            if (string.IsNullOrEmpty(receivedToken))
            {
                SetStatus("Authentication failed: no token received.", true);
                Repaint();
                yield break;
            }

            _token    = receivedToken;
            _username = receivedName ?? receivedEmail ?? "User";
            _email    = receivedEmail ?? _email;
            EditorPrefs.SetString(PREFS_TOKEN,    _token);
            EditorPrefs.SetString(PREFS_USERNAME, _username);
            EditorPrefs.SetString(PREFS_EMAIL,    _email);

            _view = View.TaskForm;
            SetStatus($"✅ Welcome, {_username}!", false);
            FetchProjects();
            Repaint();
        }

        private static System.Collections.Generic.Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new System.Collections.Generic.Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return result;
            query = query.TrimStart('?');
            foreach (var part in query.Split('&'))
            {
                var idx = part.IndexOf('=');
                if (idx < 0) continue;
                result[Uri.UnescapeDataString(part.Substring(0, idx))] =
                    Uri.UnescapeDataString(part.Substring(idx + 1));
            }
            return result;
        }

        // ── Login coroutine ───────────────────────────────────────────────

        private IEnumerator LoginCoroutine()
        {
            _isBusy = true;
            SetStatus("Signing in…", false);

            var body = $"{{\"email\":\"{_email}\",\"password\":\"{_password}\"}}";
            using var req = new UnityWebRequest($"{_baseUrl}/api/auth/login", "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();
            _isBusy = false;

            if (req.result != UnityWebRequest.Result.Success)
            {
                SetStatus($"Login failed: {req.error}", true);
                Repaint();
                yield break;
            }

            var resp = JsonUtility.FromJson<LoginResponse>(req.downloadHandler.text);
            if (resp == null || string.IsNullOrEmpty(resp.token))
            {
                SetStatus("Login failed: invalid credentials.", true);
                Repaint();
                yield break;
            }

            _token    = resp.token;
            _username = resp.user?.name ?? _email;
            EditorPrefs.SetString(PREFS_TOKEN,    _token);
            EditorPrefs.SetString(PREFS_USERNAME, _username);
            EditorPrefs.SetString(PREFS_EMAIL,    _email);

            _view = View.TaskForm;
            SetStatus($"Welcome, {_username}!", false);
            FetchProjects();
            Repaint();
        }

        // ── Fetch projects coroutine ──────────────────────────────────────

        private void FetchProjects()
        {
            EditorCoroutineUtility.StartCoroutine(FetchProjectsCoroutine(), this);
        }

        // ── Fetch members + epics ─────────────────────────────────────────

        private void FetchMembersAndEpics(string projectId)
        {
            _lastLoadedProjectIndex = _selectedProjectIndex;
            _members.Clear();
            _epics.Clear();
            _memberSelected    = null;
            _selectedEpicIndex = 0;
            EditorCoroutineUtility.StartCoroutine(FetchMembersCoroutine(projectId), this);
            EditorCoroutineUtility.StartCoroutine(FetchEpicsCoroutine(projectId), this);
        }

        private IEnumerator FetchMembersCoroutine(string projectId)
        {
            using var req = UnityWebRequest.Get($"{_baseUrl}/api/projects/{projectId}/members");
            req.SetRequestHeader("Authorization", $"Bearer {_token}");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success) { Repaint(); yield break; }

            var wrapped = $"{{\"items\":{req.downloadHandler.text}}}";
            var list    = JsonUtility.FromJson<MemberListWrapperLocal>(wrapped);
            _members        = list?.items ?? new List<MemberItem>();
            _memberSelected = new bool[_members.Count];
            Repaint();
        }

        private IEnumerator FetchEpicsCoroutine(string projectId)
        {
            using var req = UnityWebRequest.Get($"{_baseUrl}/api/epics/project/{projectId}");
            req.SetRequestHeader("Authorization", $"Bearer {_token}");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success) { Repaint(); yield break; }

            var wrapped = $"{{\"items\":{req.downloadHandler.text}}}";
            var list    = JsonUtility.FromJson<EpicListWrapperLocal>(wrapped);
            _epics = list?.items ?? new List<EpicItem>();
            Repaint();
        }

        private IEnumerator FetchProjectsCoroutine()
        {
            using var req = UnityWebRequest.Get($"{_baseUrl}/api/projects");
            req.SetRequestHeader("Authorization", $"Bearer {_token}");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                var errBody = req.downloadHandler?.text ?? "";
                Debug.LogWarning($"[TeamflowSDK] FetchProjects failed: HTTP {req.responseCode} — {req.error} — {errBody}");
                SetStatus($"Load projects failed: {req.error} (HTTP {req.responseCode})", true);
                Repaint();
                yield break;
            }

            // /api/projects returns a bare JSON array — wrap for JsonUtility
            var body = req.downloadHandler?.text ?? "";
            if (string.IsNullOrEmpty(body) || body == "null")
            {
                SetStatus($"Load projects: empty response (HTTP {req.responseCode})", true);
                Repaint();
                yield break;
            }
            var wrapped = $"{{\"items\":{body}}}";
            var list    = JsonUtility.FromJson<ProjectListWrapper>(wrapped);
            _projects   = list?.items ?? new List<ProjectItem>();

            // Restore last selected project
            var savedId = EditorPrefs.GetString(PREFS_PROJECT, "");
            if (!string.IsNullOrEmpty(savedId))
            {
                var idx = _projects.FindIndex(p => p.id == savedId);
                if (idx >= 0) _selectedProjectIndex = idx;
            }

            Repaint();
        }

        // ── Create task coroutine ─────────────────────────────────────────

        private IEnumerator CreateTaskCoroutine()
        {
            if (string.IsNullOrWhiteSpace(_taskTitle))
            {
                SetStatus("Title is required.", true);
                yield break;
            }

            var project = _projects[_selectedProjectIndex];
            EditorPrefs.SetString(PREFS_PROJECT, project.id);

            _isBusy   = true;
            _progress = 0.1f;
            SetStatus("Creating task…", false);
            Repaint();

            // 1. Create task
            // Build assigneeIds array
            var assigneeList = new System.Text.StringBuilder();
            assigneeList.Append("[");
            bool firstAssignee = true;
            if (_memberSelected != null)
            {
                for (int i = 0; i < _members.Count && i < _memberSelected.Length; i++)
                {
                    if (!_memberSelected[i]) continue;
                    if (!firstAssignee) assigneeList.Append(",");
                    assigneeList.Append($"\"{_members[i].id}\"");
                    firstAssignee = false;
                }
            }
            assigneeList.Append("]");

            // Epic ID (index 0 = no epic)
            var epicId = (_selectedEpicIndex > 0 && _selectedEpicIndex - 1 < _epics.Count)
                ? _epics[_selectedEpicIndex - 1].id
                : null;
            var epicJson = epicId != null ? $",\"epicId\":\"{epicId}\"" : "";

            var payload = $"{{\"title\":\"{EscapeJson(_taskTitle)}\",\"description\":\"{EscapeJson(_taskDescription)}\"," +
                          $"\"projectId\":\"{project.id}\",\"type\":\"{_taskType}\",\"priority\":\"{_taskPriority}\",\"status\":\"TODO\"" +
                          $",\"assigneeIds\":{assigneeList}{epicJson}}}";

            using var createReq = new UnityWebRequest($"{_baseUrl}/api/tasks", "POST");
            createReq.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            createReq.downloadHandler = new DownloadHandlerBuffer();
            createReq.SetRequestHeader("Content-Type",  "application/json");
            createReq.SetRequestHeader("Authorization", $"Bearer {_token}");

            yield return createReq.SendWebRequest();
            _progress = 0.5f;
            Repaint();

            if (createReq.result != UnityWebRequest.Result.Success)
            {
                _isBusy = false;
                SetStatus($"Error: {createReq.error}", true);
                Repaint();
                yield break;
            }

            var task = JsonUtility.FromJson<TeamflowTask>(createReq.downloadHandler.text);

            // 2. Upload photo if any
            if (!string.IsNullOrEmpty(_photoPath) && File.Exists(_photoPath))
            {
                SetStatus("Uploading photo…", false);
                var bytes    = File.ReadAllBytes(_photoPath);
                var filename = Path.GetFileName(_photoPath);
                var form     = new WWWForm();
                form.AddBinaryData("files", bytes, filename, "image/jpeg");

                using var uploadReq = UnityWebRequest.Post($"{_baseUrl}/api/tasks/{task.id}/attachments", form);
                uploadReq.SetRequestHeader("Authorization", $"Bearer {_token}");

                yield return uploadReq.SendWebRequest();
                _progress = 0.9f;
                Repaint();

                if (uploadReq.result != UnityWebRequest.Result.Success)
                    Debug.LogWarning($"[TeamflowSDK] Photo upload failed: {uploadReq.error}");
            }

            _isBusy          = false;
            _progress        = 1f;
            _taskTitle       = "";
            _taskDescription = "";
            _photoPath       = "";
            _photoPreview    = null;
            _selectedEpicIndex = 0;
            if (_memberSelected != null)
                for (int i = 0; i < _memberSelected.Length; i++) _memberSelected[i] = false;

            SetStatus($"✅ Task \"{task.title}\" created! (#{task.id[..8]}…)", false);
            Repaint();
        }

        // ── Screenshot helper ─────────────────────────────────────────────

        private void CaptureEditorScreenshot()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"teamflow_screenshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");

            ScreenCapture.CaptureScreenshot(path);

            // Wait one frame for file to be written
            EditorApplication.delayCall += () =>
            {
                if (File.Exists(path))
                {
                    _photoPath    = path;
                    _photoPreview = LoadPreview(path);
                    SetStatus("Screenshot captured.", false);
                    Repaint();
                }
            };
        }

        // ── Misc helpers ──────────────────────────────────────────────────

        private void AddClientToScene()
        {
            // Check for duplicates
            var existing = UnityEngine.Object.FindObjectOfType<TeamflowSDK.TeamflowClient>();
            if (existing != null)
            {
                UnityEditor.Selection.activeGameObject = existing.gameObject;
                SetStatus("TeamflowClient is already in the scene.", false);
                return;
            }

            // Create GameObject
            var go = new UnityEngine.GameObject("[TeamflowClient]");
            var client = go.AddComponent<TeamflowSDK.TeamflowClient>();
            TeamflowSDK.TeamflowClient.BaseUrl = _baseUrl;

            // Pre-load session into PlayerPrefs so TryRestoreSession picks it up
            UnityEngine.PlayerPrefs.SetString("teamflow_token",    _token);
            UnityEngine.PlayerPrefs.SetString("teamflow_username", _username);
            UnityEngine.PlayerPrefs.SetString("teamflow_email",    _email);
            UnityEngine.PlayerPrefs.Save();

            // Mark scene dirty so Unity knows to save it
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());

            // Select in Hierarchy
            UnityEditor.Selection.activeGameObject = go;

            // Also add HUD if available
            var hudType = System.Type.GetType("TeamflowHUD") ?? System.Type.GetType("TeamflowHUD, Assembly-CSharp");
            if (hudType != null)
                go.AddComponent(hudType);

            SetStatus($"✅ [TeamflowClient] added to scene — logged in as {_username}.", false);
            UnityEngine.Debug.Log($"[TeamflowSDK] TeamflowClient GameObject added to scene.");
        }

        private void Logout()
        {
            _token    = "";
            _username = "";
            _projects.Clear();
            EditorPrefs.DeleteKey(PREFS_TOKEN);
            EditorPrefs.DeleteKey(PREFS_USERNAME);
            _view = View.Login;
            SetStatus("Logged out.", false);
        }

        private void SetStatus(string msg, bool isError)
        {
            _statusMessage = msg;
            _statusIsError = isError;
        }

        private static Texture2D LoadPreview(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var tex   = new Texture2D(2, 2);
                tex.LoadImage(bytes);
                return tex;
            }
            catch { return null; }
        }

        private static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") ?? "";

        // ── Nested serialisable types ─────────────────────────────────────

        [Serializable] private class ProjectItem        { public string id; public string name; }
        [Serializable] private class ProjectListWrapper  { public List<ProjectItem> items; }
        [Serializable] private class MemberItem          { public string id; public string user_id; public string name; public string email; }
        [Serializable] private class MemberListWrapperLocal { public List<MemberItem> items; }
        [Serializable] private class EpicItem            { public string id; public string title; public string color; }
        [Serializable] private class EpicListWrapperLocal   { public List<EpicItem> items; }
    }

    // ── Play Mode auto-injector ───────────────────────────────────────────
    // When the editor is already logged in (token saved), automatically
    // creates a [TeamflowClient] GameObject in the scene when Play Mode starts,
    // pre-loaded with the saved JWT so the SDK is immediately authenticated.

    [UnityEditor.InitializeOnLoad]
    internal static class TeamflowPlayModeInjector
    {
        static TeamflowPlayModeInjector()
        {
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode) return;

            var token    = UnityEditor.EditorPrefs.GetString("teamflow_editor_token",   "");
            var username = UnityEditor.EditorPrefs.GetString("teamflow_editor_username", "");
            var email    = UnityEditor.EditorPrefs.GetString("teamflow_editor_email",    "");
            var baseUrl  = UnityEditor.EditorPrefs.GetString("teamflow_editor_url",
                               "https://teamflow-api-544622760078.us-central1.run.app");

            if (string.IsNullOrEmpty(token)) return;

            // Avoid duplicates
            if (UnityEngine.Object.FindObjectOfType<TeamflowSDK.TeamflowClient>() != null) return;

            TeamflowSDK.TeamflowClient.BaseUrl = baseUrl;

            // Persist to PlayerPrefs so TeamflowClient.TryRestoreSession() picks it up
            UnityEngine.PlayerPrefs.SetString("teamflow_token",    token);
            UnityEngine.PlayerPrefs.SetString("teamflow_username", username);
            UnityEngine.PlayerPrefs.SetString("teamflow_email",    email);
            UnityEngine.PlayerPrefs.Save();

            // Force singleton creation
            var client = TeamflowSDK.TeamflowClient.Instance;
            UnityEngine.Debug.Log($"[TeamflowSDK] ▶ Play Mode — TeamflowClient injected, logged in as {username}");
        }
    }

    // ── Minimal EditorCoroutineUtility shim ──────────────────────────────
    // Unity 2021.3+ ships com.unity.editorcoroutines — this shim is only
    // used if that package is absent.

#if !UNITY_EDITOR_COROUTINES
    internal static class EditorCoroutineUtility
    {
        public static EditorCoroutine StartCoroutine(IEnumerator routine, object owner)
            => new EditorCoroutine(routine);
    }

    internal class EditorCoroutine
    {
        private readonly IEnumerator _routine;
        private UnityEngine.AsyncOperation _pendingOp;
        private readonly double _startTime = UnityEditor.EditorApplication.timeSinceStartup;

        public EditorCoroutine(IEnumerator routine)
        {
            _routine = routine;
            UnityEditor.EditorApplication.update += Tick;
        }

        private void Tick()
        {
            // If we are waiting for an AsyncOperation (incl. UnityWebRequestAsyncOperation),
            // poll isDone — works even without game loop
            if (_pendingOp != null)
            {
                if (!_pendingOp.isDone) return;
                _pendingOp = null;
            }

            bool hasNext;
            try { hasNext = _routine.MoveNext(); }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[TeamflowSDK] EditorCoroutine exception: {ex.Message}");
                UnityEditor.EditorApplication.update -= Tick;
                return;
            }

            if (!hasNext)
            {
                UnityEditor.EditorApplication.update -= Tick;
                return;
            }

            // UnityWebRequestAsyncOperation inherits AsyncOperation — cast covers both
            if (_routine.Current is UnityEngine.AsyncOperation asyncOp)
                _pendingOp = asyncOp;
            // yield return null → just continue next Tick
        }
    }
#endif
}
