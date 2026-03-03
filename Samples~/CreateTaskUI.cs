using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TeamflowSDK;

/// <summary>
/// Example UGUI panel for creating a TeamFlow task with an optional photo.
///
/// Works on:
///   • Android / iOS  (camera + keyboard)
///   • VR (Meta Quest) — assign a WorldSpaceCanvas + XR Ray Interactor
///   • Unity Editor Play Mode — webcam or placeholder image
///
/// Setup:
///   1. Create a Canvas (Screen Space or World Space for VR).
///   2. Attach this script to a panel GameObject.
///   3. Wire up all serialized fields in the Inspector.
///   4. Set TeamflowClient.BaseUrl in the Inspector or via code before Start().
/// </summary>
public class CreateTaskUI : MonoBehaviour
{
    // ── Inspector wiring ─────────────────────────────────────────────────

    [Header("Auth Panel")]
    [SerializeField] private GameObject   authPanel;
    [SerializeField] private TMP_InputField emailInput;
    [SerializeField] private TMP_InputField passwordInput;
    [SerializeField] private Button        loginButton;
    [SerializeField] private Button        googleLoginButton;

    [Header("Task Panel")]
    [SerializeField] private GameObject   taskPanel;
    [SerializeField] private TMP_Dropdown projectDropdown;
    [SerializeField] private TMP_InputField titleInput;
    [SerializeField] private TMP_InputField descriptionInput;
    [SerializeField] private Button        capturePhotoButton;
    [SerializeField] private RawImage      photoPreview;
    [SerializeField] private Button        createTaskButton;
    [SerializeField] private Button        logoutButton;

    [Header("Feedback")]
    [SerializeField] private TMP_Text  statusLabel;
    [SerializeField] private Slider    progressBar;

    [Header("TeamFlow Config")]
    [SerializeField] private string baseUrl = "https://teamflow-api-544622760078.us-central1.run.app";

    // ── Private state ─────────────────────────────────────────────────────

    private CameraCapture _camera;
    private TaskCreator   _creator;
    private List<TeamflowProject> _projects = new();
    private byte[]  _capturedPhotoBytes;
    private string  _capturedPhotoFilename;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    private void Awake()
    {
        TeamflowClient.BaseUrl = baseUrl;

        _camera  = gameObject.AddComponent<CameraCapture>();
        _creator = gameObject.AddComponent<TaskCreator>();

        _creator.OnTaskCreated += OnTaskCreated;
        _creator.OnError       += OnError;
        _creator.OnProgress    += OnProgress;

        loginButton.onClick.AddListener(OnLoginClicked);
        if (googleLoginButton != null)
            googleLoginButton.onClick.AddListener(OnGoogleLoginClicked);
        capturePhotoButton.onClick.AddListener(OnCaptureClicked);
        createTaskButton.onClick.AddListener(OnCreateTaskClicked);
        logoutButton.onClick.AddListener(OnLogoutClicked);
        projectDropdown.onValueChanged.AddListener(_ => { });
    }

    private void Start()
    {
        if (progressBar != null) progressBar.value = 0;

        if (TeamflowClient.Instance.IsAuthenticated)
        {
            ShowTaskPanel();
            LoadProjects();
        }
        else
        {
            ShowAuthPanel();
        }
    }

    private void OnDestroy() => _camera?.StopPreview();

    // ── Auth ──────────────────────────────────────────────────────────────

    private void OnLoginClicked()
    {
        SetStatus("Signing in…");
        loginButton.interactable = false;

        TeamflowClient.Instance.Login(
            emailInput.text.Trim(),
            passwordInput.text,
            onSuccess: user =>
            {
                SetStatus($"Welcome, {user.name}!");
                ShowTaskPanel();
                LoadProjects();
            },
            onError: err =>
            {
                SetStatus($"Login failed: {err}", isError: true);
                loginButton.interactable = true;
            });
    }

    private void OnGoogleLoginClicked()
    {
        SetStatus("Opening Google sign-in…");
        if (googleLoginButton != null) googleLoginButton.interactable = false;
        if (loginButton != null)       loginButton.interactable       = false;

        var googleAuth = gameObject.GetComponent<TeamflowSDK.GoogleAuth>()
                      ?? gameObject.AddComponent<TeamflowSDK.GoogleAuth>();

        googleAuth.Login(
            onSuccess: user =>
            {
                SetStatus($"Welcome, {user.name}!");
                ShowTaskPanel();
                LoadProjects();
            },
            onError: err =>
            {
                SetStatus($"Google login failed: {err}", isError: true);
                if (googleLoginButton != null) googleLoginButton.interactable = true;
                if (loginButton != null)       loginButton.interactable       = true;
            });
    }

    private void OnLogoutClicked()
    {
        TeamflowClient.Instance.Logout();
        _projects.Clear();
        projectDropdown.ClearOptions();
        titleInput.text       = "";
        descriptionInput.text = "";
        _capturedPhotoBytes   = null;
        if (photoPreview != null) photoPreview.texture = null;
        ShowAuthPanel();
        SetStatus("Logged out.");
    }

    // ── Projects ──────────────────────────────────────────────────────────

    private void LoadProjects()
    {
        SetStatus("Loading projects…");
        TeamflowClient.Instance.GetProjects(
            onSuccess: projects =>
            {
                _projects = projects;
                projectDropdown.ClearOptions();
                var options = new List<TMP_Dropdown.OptionData>();
                foreach (var p in projects)
                    options.Add(new TMP_Dropdown.OptionData(p.name));
                projectDropdown.AddOptions(options);
                SetStatus("Ready.");
            },
            onError: err => SetStatus($"Could not load projects: {err}", isError: true));
    }

    // ── Camera ────────────────────────────────────────────────────────────

    private void OnCaptureClicked()
    {
        _camera.StartPreview(photoPreview);
        SetStatus("Camera started — tap again to capture.");
        capturePhotoButton.onClick.RemoveAllListeners();
        capturePhotoButton.onClick.AddListener(DoCapture);

        var btnLabel = capturePhotoButton.GetComponentInChildren<TMP_Text>();
        if (btnLabel != null) btnLabel.text = "📸 Capture";
    }

    private void DoCapture()
    {
        _camera.CapturePhoto((bytes, filename) =>
        {
            _capturedPhotoBytes    = bytes;
            _capturedPhotoFilename = filename;
            _camera.StopPreview();

            // Show thumbnail in preview RawImage
            if (photoPreview != null && bytes != null)
            {
                var tex = new Texture2D(2, 2);
                tex.LoadImage(bytes);
                photoPreview.texture = tex;
            }

            SetStatus("Photo captured.");
            capturePhotoButton.onClick.RemoveAllListeners();
            capturePhotoButton.onClick.AddListener(OnCaptureClicked);

            var btnLabel = capturePhotoButton.GetComponentInChildren<TMP_Text>();
            if (btnLabel != null) btnLabel.text = "📷 Retake";
        });
    }

    // ── Task creation ─────────────────────────────────────────────────────

    private void OnCreateTaskClicked()
    {
        if (_projects.Count == 0) { SetStatus("No project selected.", isError: true); return; }

        var project = _projects[projectDropdown.value];
        createTaskButton.interactable = false;

        _creator.CreateTask(
            title:          titleInput.text,
            description:    descriptionInput.text,
            projectId:      project.id,
            photoBytes:     _capturedPhotoBytes,
            photoFilename:  _capturedPhotoFilename);
    }

    private void OnTaskCreated(TeamflowTask task)
    {
        SetStatus($"✅ Task \"{task.title}\" created!");
        titleInput.text            = "";
        descriptionInput.text      = "";
        _capturedPhotoBytes        = null;
        _capturedPhotoFilename     = null;
        if (photoPreview != null)  photoPreview.texture = null;
        createTaskButton.interactable = true;
        if (progressBar != null)   progressBar.value = 0;
    }

    private void OnError(string msg)
    {
        SetStatus(msg, isError: true);
        createTaskButton.interactable = true;
    }

    private void OnProgress(float value)
    {
        if (progressBar != null) progressBar.value = value;
    }

    // ── UI helpers ────────────────────────────────────────────────────────

    private void ShowAuthPanel()
    {
        authPanel.SetActive(true);
        taskPanel.SetActive(false);
    }

    private void ShowTaskPanel()
    {
        authPanel.SetActive(false);
        taskPanel.SetActive(true);
    }

    private void SetStatus(string msg, bool isError = false)
    {
        if (statusLabel == null) return;
        statusLabel.text  = msg;
        statusLabel.color = isError ? new Color(1f, 0.35f, 0.35f) : new Color(0.25f, 0.85f, 0.5f);
    }
}
