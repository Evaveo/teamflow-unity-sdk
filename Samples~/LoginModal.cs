using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TeamflowSDK;

/// <summary>
/// Teamflow Login Modal — shows email/password + "Sign in with Google" button.
///
/// Setup:
///   1. Create a Canvas (Screen Space Overlay or World Space for VR).
///   2. Create a Panel child and attach this script to it.
///   3. Wire all [SerializeField] fields in the Inspector.
///   4. Call LoginModal.Show() / LoginModal.Hide() from your game code,
///      or enable Auto Show On Start to show it automatically.
///
/// Works on: Unity Editor, PC Standalone, Android, Meta Quest VR.
/// </summary>
public class LoginModal : MonoBehaviour
{
    // ── Inspector wiring ─────────────────────────────────────────────────

    [Header("Panel root (this GameObject or a child)")]
    [SerializeField] private GameObject modalRoot;

    [Header("Email / Password fields")]
    [SerializeField] private TMP_InputField emailInput;
    [SerializeField] private TMP_InputField passwordInput;

    [Header("Buttons")]
    [SerializeField] private Button loginButton;
    [SerializeField] private Button googleLoginButton;

    [Header("Feedback")]
    [SerializeField] private TMP_Text statusLabel;

    [Header("TeamFlow Config")]
    [SerializeField] private string baseUrl = "https://teamflow-api-544622760078.us-central1.run.app";
    [SerializeField] private bool autoShowOnStart = true;

    // ── Events ────────────────────────────────────────────────────────────

    /// <summary>Fired when the user successfully logs in (email or Google).</summary>
    public System.Action<TeamflowUser> OnLoginSuccess;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    private void Awake()
    {
        TeamflowClient.BaseUrl = baseUrl;

        if (loginButton != null)
            loginButton.onClick.AddListener(OnLoginClicked);

        if (googleLoginButton != null)
            googleLoginButton.onClick.AddListener(OnGoogleLoginClicked);
    }

    private void Start()
    {
        if (autoShowOnStart && !TeamflowClient.Instance.IsAuthenticated)
            Show();
        else if (TeamflowClient.Instance.IsAuthenticated)
            Hide();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Show the login modal.</summary>
    public void Show()
    {
        var root = modalRoot != null ? modalRoot : gameObject;
        root.SetActive(true);
        SetStatus("");
    }

    /// <summary>Hide the login modal.</summary>
    public void Hide()
    {
        var root = modalRoot != null ? modalRoot : gameObject;
        root.SetActive(false);
    }

    // ── Email / Password login ─────────────────────────────────────────────

    private void OnLoginClicked()
    {
        var email    = emailInput    != null ? emailInput.text.Trim()    : "";
        var password = passwordInput != null ? passwordInput.text        : "";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            SetStatus("Veuillez remplir l'email et le mot de passe.", isError: true);
            return;
        }

        SetStatus("Connexion en cours…");
        SetInteractable(false);

        TeamflowClient.Instance.Login(
            email, password,
            onSuccess: user =>
            {
                SetStatus($"Bienvenue, {user.name} !");
                Hide();
                OnLoginSuccess?.Invoke(user);
            },
            onError: err =>
            {
                SetStatus($"Échec : {err}", isError: true);
                SetInteractable(true);
            });
    }

    // ── Google login ───────────────────────────────────────────────────────

    private void OnGoogleLoginClicked()
    {
        SetStatus("Ouverture de Google…");
        SetInteractable(false);

        var googleAuth = GetComponent<TeamflowSDK.GoogleAuth>()
                      ?? gameObject.AddComponent<TeamflowSDK.GoogleAuth>();

        googleAuth.Login(
            onSuccess: user =>
            {
                SetStatus($"Bienvenue, {user.name} !");
                Hide();
                OnLoginSuccess?.Invoke(user);
            },
            onError: err =>
            {
                SetStatus($"Échec Google : {err}", isError: true);
                SetInteractable(true);
            });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void SetStatus(string msg, bool isError = false)
    {
        if (statusLabel == null) return;
        statusLabel.text  = msg;
        statusLabel.color = isError
            ? new Color(1f, 0.35f, 0.35f)
            : new Color(0.25f, 0.85f, 0.5f);
    }

    private void SetInteractable(bool value)
    {
        if (loginButton       != null) loginButton.interactable       = value;
        if (googleLoginButton != null) googleLoginButton.interactable = value;
    }
}
