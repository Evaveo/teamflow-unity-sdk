using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TeamflowSDK
{
    /// <summary>
    /// TeamFlow HUD — IMGUI overlay in Play Mode.
    /// Features: project selector, assignee selector, title/desc,
    /// screenshot capture, file attachment, create task.
    /// </summary>
    public class TeamflowHUD : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static TeamflowHUD Instance { get; private set; }

        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("HUD position")]
        [SerializeField] private int marginRight = 16;
        [SerializeField] private int marginTop   = 16;
        [SerializeField] private int panelWidth  = 340;

        // ── State ─────────────────────────────────────────────────────────────

        private bool   _expanded      = false;
        private string _taskTitle     = "";
        private string _taskDesc      = "";
        private string _statusMsg     = "";
        private bool   _statusIsError = false;
        private bool   _isBusy        = false;

        // ── VR Device Code auth ───────────────────────────────────────────────
        private string _vrCode        = "";
        private bool   _vrAuthBusy    = false;

        // ── Whisper voice input ───────────────────────────────────────────────
        private enum MicTarget { None, Title, Desc }
        private MicTarget _micTarget    = MicTarget.None;
        private bool      _whisperReady = false;

        private List<TeamflowProject> _projects    = new List<TeamflowProject>();
        private int                   _projectIdx  = 0;

        private List<TeamflowMember>  _members     = new List<TeamflowMember>();
        private int                   _assigneeIdx = 0; // 0 = "Non assigné"
        private string                _lastProjectId = "";

        private byte[]   _attachBytes    = null;
        private string   _attachFilename = "";
        private Texture2D _attachPreview = null;
        private bool     _hasScreenshot  = false;

        private GUIStyle _panelStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _btnStyle;
        private GUIStyle _statusStyle;
        private GUIStyle _labelStyle;
        private bool     _stylesReady = false;

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Toggle the HUD open/closed — call from controller button, etc.</summary>
        public void Toggle() => _expanded = !_expanded;

        /// <summary>Force-open the HUD.</summary>
        public void Open()  => _expanded = true;

        /// <summary>Force-close the HUD.</summary>
        public void Close() => _expanded = false;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            // Initialise Whisper via decoupled WhisperService
            WhisperService.OnStateChanged += OnWhisperStateChanged;
            WhisperService.OnTranscribed  += OnWhisperResult;
            _whisperReady = WhisperService.IsReady;

            if (!TeamflowClient.Instance.IsAuthenticated)
            {
                SetStatus("Entrez le code VR affiché sur le portail client", false);
                return;
            }
            SetStatus($"Connecté : {TeamflowClient.Instance.CurrentUser?.name}", false);
            TeamflowClient.Instance.GetProjects(
                onSuccess: list =>
                {
                    _projects = list;
                    SetStatus($"Connecté : {TeamflowClient.Instance.CurrentUser?.name}", false);
                    LoadMembersForCurrentProject();
                },
                onError: err => SetStatus($"Projets non chargés : {err}", true));
        }

        private void LoadMembersForCurrentProject()
        {
            if (_projects.Count == 0) return;
            var pid = _projects[_projectIdx].id;
            if (pid == _lastProjectId) return;
            _lastProjectId = pid;
            _members.Clear();
            _assigneeIdx = 0;
            TeamflowClient.Instance.GetProjectMembers(pid,
                onSuccess: list => _members = list,
                onError:   _    => { });
        }

        // ── IMGUI ─────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EnsureStyles();

            float x = Screen.width - panelWidth - marginRight;
            float y = marginTop;

            // ── Toggle button ─────────────────────────────────────────────
            string userName    = TeamflowClient.Instance.CurrentUser?.name ?? "Non connecté";
            string arrow       = _expanded ? "▼" : "▶";
            if (GUI.Button(new Rect(x, y, panelWidth, 32), $"{arrow}  TeamFlow  |  {userName}", _titleStyle))
                _expanded = !_expanded;

            if (!_expanded) return;

            bool auth = TeamflowClient.Instance.IsAuthenticated;

            // ── Dynamic panel height ──────────────────────────────────────
            float panelHeight;
            if (!auth)
            {
                panelHeight = 70f;
            }
            else
            {
                float projH    = _projects.Count > 0 ? _projects.Count * 22f + 26f : 26f;
                float membH    = _members.Count  > 0 ? (_members.Count + 1) * 22f + 26f : 26f;
                float fixedH   = 26f  // Titre label+field
                               + 26f  // field
                               + 24f  // Desc label
                               + 52f  // textarea
                               + 28f  // attachment row
                               + 28f  // screenshot row
                               + 34f  // create button
                               + 28f  // status
                               + 30f; // padding
                panelHeight = projH + membH + fixedH;
            }

            var panelRect = new Rect(x, y + 34, panelWidth, panelHeight);
            GUI.Box(panelRect, GUIContent.none, _panelStyle);

            GUILayout.BeginArea(new Rect(panelRect.x + 10, panelRect.y + 8,
                                          panelRect.width - 20, panelRect.height - 16));

            if (!auth)
            {
                GUILayout.Label("Mode VR — Identification", _labelStyle);
                GUILayout.Space(4);
                GUILayout.Label("Code affiché sur le portail web :", _statusStyle);
                GUILayout.Space(4);
                _vrCode = GUILayout.TextField(_vrCode, 4, GUILayout.Height(36));
                GUILayout.Space(6);
                GUI.enabled = !_vrAuthBusy && _vrCode.Length == 4;
                if (GUILayout.Button(_vrAuthBusy ? "Connexion…" : "Se connecter", _btnStyle, GUILayout.Height(32)))
                {
                    _vrAuthBusy = true;
                    SetStatus("Vérification du code…", false);
                    TeamflowClient.Instance.AuthWithDeviceCode(
                        _vrCode,
                        onSuccess: user =>
                        {
                            _vrAuthBusy = false;
                            _vrCode     = "";
                            SetStatus($"Connecté : {user.name}", false);
                            TeamflowClient.Instance.GetProjects(
                                onSuccess: list => { _projects = list; LoadMembersForCurrentProject(); },
                                onError:   err  => SetStatus($"Projets : {err}", true));
                        },
                        onError: err =>
                        {
                            _vrAuthBusy = false;
                            SetStatus($"Code invalide : {err}", true);
                        });
                }
                GUI.enabled = true;
                GUILayout.Space(4);
                if (!string.IsNullOrEmpty(_statusMsg))
                    GUILayout.Label(_statusMsg, _statusStyle);
                GUILayout.EndArea();
                return;
            }

            // ── Project selector ──────────────────────────────────────────
            GUILayout.Label("Projet :", _labelStyle);
            if (_projects.Count > 0)
            {
                var names = _projects.ConvertAll(p => p.name).ToArray();
                int prev = _projectIdx;
                _projectIdx = GUILayout.SelectionGrid(_projectIdx, names, 1, GUI.skin.toggle);
                if (_projectIdx != prev) LoadMembersForCurrentProject();
            }
            else
            {
                GUILayout.Label("Chargement…", _statusStyle);
            }

            GUILayout.Space(4);

            // ── Assignee selector ─────────────────────────────────────────
            GUILayout.Label("Assigner à :", _labelStyle);
            if (_members.Count > 0)
            {
                var opts = new List<string> { "— Non assigné —" };
                _members.ForEach(m => opts.Add(m.name));
                _assigneeIdx = GUILayout.SelectionGrid(_assigneeIdx, opts.ToArray(), 1, GUI.skin.toggle);
            }
            else
            {
                GUILayout.Label("Aucun membre chargé", _statusStyle);
            }

            GUILayout.Space(4);

            // ── Task form ─────────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Label("Titre :", _labelStyle, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            if (_whisperReady)
            {
                bool recTitle = _micTarget == MicTarget.Title;
                string micTitleLbl = recTitle ? "⏹ Stop" : "🎤";
                GUI.color = recTitle ? new Color(1f, 0.4f, 0.4f) : Color.white;
                if (GUILayout.Button(micTitleLbl, GUILayout.Width(38), GUILayout.Height(20)))
                    ToggleMic(MicTarget.Title);
                GUI.color = Color.white;
            }
            GUILayout.EndHorizontal();
            _taskTitle = GUILayout.TextField(_taskTitle, GUILayout.Height(24));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Description :", _labelStyle, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            if (_whisperReady)
            {
                bool recDesc = _micTarget == MicTarget.Desc;
                string micDescLbl = recDesc ? "⏹ Stop" : "🎤";
                GUI.color = recDesc ? new Color(1f, 0.4f, 0.4f) : Color.white;
                if (GUILayout.Button(micDescLbl, GUILayout.Width(38), GUILayout.Height(20)))
                    ToggleMic(MicTarget.Desc);
                GUI.color = Color.white;
            }
            GUILayout.EndHorizontal();
            _taskDesc = GUILayout.TextArea(_taskDesc, GUILayout.Height(50));

            GUILayout.Space(4);

            // ── Attachment row ────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            string attachLabel = string.IsNullOrEmpty(_attachFilename)
                ? "📎  Pièce jointe…"
                : $"📎  {_attachFilename}";
            if (GUILayout.Button(attachLabel, _btnStyle, GUILayout.Height(26)))
                PickFile();
            if (!string.IsNullOrEmpty(_attachFilename))
            {
                if (GUILayout.Button("✕", GUILayout.Width(26), GUILayout.Height(26)))
                {
                    _attachBytes    = null;
                    _attachFilename = "";
                    _attachPreview  = null;
                    _hasScreenshot  = false;
                }
            }
            GUILayout.EndHorizontal();

            // ── Screenshot row ────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            string ssLabel = _hasScreenshot ? "📷  Screenshot joint ✓" : "📷  Capturer l'écran";
            if (GUILayout.Button(ssLabel, _btnStyle, GUILayout.Height(26)))
            {
                if (!_hasScreenshot)
                    StartCoroutine(CaptureScreenshot());
                else
                {
                    _attachBytes    = null;
                    _attachFilename = "";
                    _hasScreenshot  = false;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // ── Create button ─────────────────────────────────────────────
            GUI.enabled = !_isBusy && _projects.Count > 0 && _taskTitle.Trim().Length > 0;
            if (GUILayout.Button(_isBusy ? "Création…" : "✚  Créer la tâche", GUILayout.Height(32)))
                CreateTask();
            GUI.enabled = true;

            // ── Status ────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(_statusMsg))
            {
                GUI.color = _statusIsError ? new Color(1f, 0.4f, 0.4f) : new Color(0.4f, 1f, 0.6f);
                GUILayout.Label(_statusMsg, _statusStyle);
                GUI.color = Color.white;
            }

            GUILayout.EndArea();
        }

        // ── Whisper callbacks ─────────────────────────────────────────────────

        private void OnWhisperStateChanged(WhisperService.State state)
        {
            _whisperReady = (state == WhisperService.State.Idle);
            switch (state)
            {
                case WhisperService.State.LoadingModel:
                    SetStatus("Chargement modèle Whisper…", false); break;
                case WhisperService.State.Recording:
                    SetStatus("🎤 Parlez en français…", false); break;
                case WhisperService.State.Transcribing:
                    SetStatus("Transcription en cours…", false); break;
                case WhisperService.State.Idle:
                    if (!string.IsNullOrEmpty(_statusMsg) && _statusMsg.StartsWith("🎤"))
                        SetStatus("", false);
                    break;
                case WhisperService.State.Error:
                    SetStatus(WhisperService.LastError, true); break;
            }
        }

        private void OnWhisperResult(string text)
        {
            if (string.IsNullOrEmpty(text)) { _micTarget = MicTarget.None; return; }
            if (_micTarget == MicTarget.Title)
                _taskTitle = text;
            else if (_micTarget == MicTarget.Desc)
                _taskDesc += (string.IsNullOrEmpty(_taskDesc) ? "" : " ") + text;
            _micTarget = MicTarget.None;
        }

        private void ToggleMic(MicTarget target)
        {
            if (_micTarget == target)
            {
                WhisperService.StopListening();
                _micTarget = MicTarget.None;
            }
            else
            {
                if (_micTarget != MicTarget.None) WhisperService.StopListening();
                _micTarget = target;
                if (!WhisperService.StartListening())
                {
                    SetStatus(WhisperService.LastError, true);
                    _micTarget = MicTarget.None;
                }
            }
        }

        // ── Screenshot ────────────────────────────────────────────────────────

        private IEnumerator CaptureScreenshot()
        {
            _expanded = false; // hide HUD during capture
            yield return new WaitForEndOfFrame();

            var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            tex.Apply();

            _attachBytes    = tex.EncodeToJPG(85);
            _attachFilename = $"screenshot_{System.DateTime.Now:yyyyMMdd_HHmmss}.jpg";
            _attachPreview  = tex;
            _hasScreenshot  = true;
            _expanded       = true;
            SetStatus("Screenshot capturé ✓", false);
        }

        // ── File picker ───────────────────────────────────────────────────────

        private void PickFile()
        {
#if UNITY_EDITOR
            var path = UnityEditor.EditorUtility.OpenFilePanel(
                "Sélectionner une pièce jointe", "", "png,jpg,jpeg,pdf,txt");
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _attachBytes    = File.ReadAllBytes(path);
                _attachFilename = Path.GetFileName(path);
                _hasScreenshot  = false;
                SetStatus($"Fichier joint : {_attachFilename}", false);
            }
#else
            SetStatus("Sélection de fichier non disponible hors éditeur.", true);
#endif
        }

        // ── Task creation ─────────────────────────────────────────────────────

        private void CreateTask()
        {
            if (_projects.Count == 0 || _taskTitle.Trim().Length == 0) return;
            _isBusy = true;
            SetStatus("Création en cours…", false);

            var creator = GetComponent<TaskCreator>() ?? gameObject.AddComponent<TaskCreator>();

            System.Action<TeamflowTask> onCreated = null;
            System.Action<string>       onErr     = null;

            onCreated = task =>
            {
                creator.OnTaskCreated -= onCreated;
                creator.OnError       -= onErr;
                _isBusy         = false;
                _taskTitle      = "";
                _taskDesc       = "";
                _attachBytes    = null;
                _attachFilename = "";
                _hasScreenshot  = false;
                SetStatus($"✅ Tâche \"{task.title}\" créée !", false);
            };
            onErr = err =>
            {
                creator.OnTaskCreated -= onCreated;
                creator.OnError       -= onErr;
                _isBusy = false;
                SetStatus($"Erreur : {err}", true);
            };

            creator.OnTaskCreated += onCreated;
            creator.OnError       += onErr;

            // Assignee (index 0 = none)
            string assigneeId = (_assigneeIdx > 0 && _assigneeIdx - 1 < _members.Count)
                ? _members[_assigneeIdx - 1].user_id
                : null;

            creator.CreateTask(
                title:         _taskTitle,
                description:   _taskDesc,
                projectId:     _projects[_projectIdx].id,
                photoBytes:    _attachBytes,
                photoFilename: _attachFilename);

            // TODO: pass assigneeId once TaskCreator supports it
            _ = assigneeId;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SetStatus(string msg, bool isError)
        {
            _statusMsg     = msg;
            _statusIsError = isError;
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 8, 8),
                normal  = { background = MakeTexture(new Color(0.10f, 0.10f, 0.16f, 0.94f)) }
            };
            _titleStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 13,
                alignment = TextAnchor.MiddleLeft,
                normal    = { background = MakeTexture(new Color(0.31f, 0.27f, 0.90f, 0.97f)), textColor = Color.white },
                hover     = { background = MakeTexture(new Color(0.38f, 0.34f, 0.97f, 0.97f)), textColor = Color.white },
            };
            _btnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize  = 11,
                alignment = TextAnchor.MiddleLeft,
            };
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 11,
                normal    = { textColor = new Color(0.75f, 0.75f, 0.85f) }
            };
            _statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                wordWrap = true,
            };
        }

        private static Texture2D MakeTexture(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
