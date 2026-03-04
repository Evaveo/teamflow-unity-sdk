# TeamFlow Unity SDK

Create and manage TeamFlow tasks directly from Unity — supports **VR (Meta Quest / OpenXR)**, **Editor Window** (temps réel sans Play Mode), and **mobile** (Android / iOS).

---

## Installation

### Via Unity Package Manager (local)

1. Copy the `unity-sdk/` folder anywhere accessible.
2. In Unity: **Window → Package Manager → + → Add package from disk…**
3. Select `unity-sdk/package.json`.

### Via git URL

```
https://github.com/Evaveo/teamflow-unity-sdk.git#v1.4.1
```

---

## Quickstart — Runtime (scène / VR)

```csharp
using TeamflowSDK;
using UnityEngine;

public class Example : MonoBehaviour
{
    private TaskCreator _creator;

    void Start()
    {
        // 1. Set your backend URL (once, before anything else)
        TeamflowClient.BaseUrl = "https://teamflow-api-544622760078.us-central1.run.app";

        // 2. Login — JWT is persisted in PlayerPrefs automatically
        TeamflowClient.Instance.Login("you@example.com", "password",
            onSuccess: user => Debug.Log($"Logged in as {user.name}"),
            onError:   err  => Debug.LogError(err));

        // 3. Create a task
        _creator = gameObject.AddComponent<TaskCreator>();
        _creator.OnTaskCreated += task => Debug.Log($"Task created: {task.id}");
        _creator.OnError       += err  => Debug.LogError(err);
        _creator.OnProgress    += pct  => Debug.Log($"Progress: {pct:P0}");
    }

    public void SubmitTask()
    {
        _creator.CreateTask(
            title:       "Bug in scene XR_Lobby",
            description: "Object clips through floor near door trigger.",
            projectId:   "your-project-id",
            photoBytes:  null   // or pass JPEG bytes from CameraCapture
        );
    }
}
```

---

## Speech-to-Text (Google STT)

Depuis v1.4.1, le SDK utilise **Google Cloud Speech-to-Text** via un proxy sécurisé sur le backend — aucune clé API dans Unity, aucun modèle local.

### Activation

1. Dans le portail TeamFlow : **Admin → STT (Voix)** → coller la clé API Google + activer.
2. Dans Unity : **Tools → TeamFlow → Setup Scene** → crée automatiquement `[GoogleSTTBackend]`.
3. En jeu : le bouton 🎤 du HUD s'affiche si le service est activé côté serveur.

### Fonctionnement

```
Unity mic → WAV base64 → POST /api/stt/transcribe → Google STT API → transcript
```

- La clé API n'est **jamais** envoyée au client Unity.
- Le service peut être activé/désactivé à tout moment depuis le portail admin.
- Quota Google : 60 min/mois gratuit.

---

## Quickstart — Editor Window (temps réel)

Ouvre la fenêtre depuis le menu Unity :

```
Tools → TeamFlow → Create Task   (Ctrl+Shift+T)
Tools → TeamFlow → Setup Scene
Tools → TeamFlow → Settings
```

**Workflow** :
1. Entrer l'URL du backend dans **Settings** (déjà pré-rempli).
2. Se connecter (email + mot de passe).
3. Choisir un projet, saisir le titre, ajouter une capture d'écran si besoin.
4. Cliquer **Create Task** → la tâche apparaît immédiatement dans TeamFlow.

> La session est persistée dans `EditorPrefs` — pas besoin de se reconnecter à chaque ouverture de l'éditeur.

---

## Capture photo

### En scène / VR

```csharp
var capture = gameObject.AddComponent<CameraCapture>();

// Afficher le preview dans un RawImage
capture.StartPreview(myRawImage);

// Capturer quand l'utilisateur appuie
capture.CapturePhoto((bytes, filename) =>
{
    _creator.CreateTask("Ma tâche", "desc", projectId,
        photoBytes: bytes, photoFilename: filename);
});
```

### En Editor (sans caméra physique)
`CameraCapture` génère automatiquement une image placeholder bleue — le workflow peut être testé entièrement sans hardware.

---

## Compatibilité plateforme

| Plateforme | Auth | Créer tâche | Photo |
|---|---|---|---|
| Unity Editor | ✅ | ✅ | ✅ WebCam ou placeholder |
| Android / iOS | ✅ | ✅ | ✅ WebCamTexture |
| Meta Quest 2/3 | ✅ | ✅ | ✅ WebCamTexture (passthrough) |
| Windows Standalone | ✅ | ✅ | ✅ WebCam |
| WebGL | ✅ | ✅ | ⚠️ Limité (pas de PlayerPrefs persistant) |

> **Pas de** `System.Net.Http` — uniquement `UnityWebRequest`, compatible avec IL2CPP et toutes les plateformes Unity.

---

## Structure du package

```
unity-sdk/
├── package.json
├── Runtime/
│   ├── TeamflowSDK.asmdef
│   ├── TeamflowClient.cs       ← Singleton HTTP (auth + requêtes)
│   ├── TaskCreator.cs          ← Créer tâche + upload photo
│   ├── CameraCapture.cs        ← Webcam / placeholder
│   ├── TeamflowHUD.cs          ← HUD IMGUI overlay (flat + VR)
│   ├── WhisperManager.cs       ← Façade STT (dispatche vers le backend actif)
│   ├── IWhisperBackend.cs      ← Interface backend STT
│   ├── GoogleSTTBackend.cs     ← Backend Google STT (proxy via /api/stt)
│   └── Models/
│       └── TeamflowModels.cs   ← User, Task, Project, DTOs
├── Editor/
│   ├── TeamflowEditor.asmdef
│   ├── TeamflowEditorWindow.cs ← Fenêtre Editor temps réel
│   └── TeamflowSceneSetup.cs  ← Setup Scene automatique (Tools → TeamFlow)
└── Samples~/
    └── VRStarter/README.md     ← Guide démarrage rapide VR
```

---

## API Reference

### `TeamflowClient`

| Méthode | Description |
|---|---|
| `Login(email, pass, onSuccess, onError)` | Authentification, JWT stocké dans PlayerPrefs |
| `Logout()` | Efface la session locale |
| `GetProjects(onSuccess, onError)` | Liste les projets accessibles |
| `CreateTask(payload, onSuccess, onError)` | Crée une tâche |
| `UploadTaskPhoto(taskId, bytes, filename, ...)` | Attache une image à une tâche |
| `IsAuthenticated` | `true` si JWT présent |
| `CurrentUser` | `TeamflowUser` connecté |
| `BaseUrl` | URL backend (static, à setter avant usage) |

### `TaskCreator` (MonoBehaviour)

| Membre | Description |
|---|---|
| `CreateTask(title, desc, projectId, photoBytes?, filename?)` | Crée + upload en une seule méthode |
| `OnTaskCreated` | Event `Action<TeamflowTask>` |
| `OnError` | Event `Action<string>` |
| `OnProgress` | Event `Action<float>` (0..1) |

### `CameraCapture` (MonoBehaviour)

| Méthode | Description |
|---|---|
| `StartPreview(RawImage?)` | Démarre WebCamTexture |
| `CapturePhoto(Action<byte[], string>)` | Capture le frame courant en JPEG |
| `StopPreview()` | Arrête la caméra |

---

## Dépendances

- Unity **2021.3 LTS** ou supérieur
- TextMeshPro (pour le sample)
- `com.unity.editorcoroutines` **1.0+** recommandé (sinon le shim interne est utilisé)
- **Aucun** modèle local ni package `com.unity.ai.inference` requis pour le STT
