# TeamFlow VR Starter Sample

## Installation rapide (2 minutes)

### 1. Installer le Sample
Package Manager → **TeamFlow SDK** → **Samples** → **VR Starter** → `Import`

### 2. Configurer la scène
Menu : **Tools → TeamFlow → Setup VR Scene**

Cela crée automatiquement dans ta scène :
- `[TeamflowClient]` — singleton de connexion API
- `[TeamflowHUD]` — overlay IMGUI (s'affiche en haut à droite)

### 3. Configurer l'URL backend
Sélectionne `[TeamflowClient]` dans la Hierarchy →  
**Base Url** : `https://teamflow-api-544622760078.us-central1.run.app`

### 4. Lancer

Press **Play** → Le HUD apparaît en haut à droite de l'écran.

---

## Fonctionnement du HUD en VR (Meta Quest)

Le HUD utilise **Unity IMGUI** (`OnGUI`) — il s'affiche en **screen-space** par-dessus
tout le rendu VR, sans Canvas World Space ni XR Rig supplémentaire.

```
┌─────────────────────────────────────┐
│ ☰ TeamFlow                    [−]   │  ← bouton toggle
├─────────────────────────────────────┤
│ [Non authentifié]                   │
│  Code VR : [    ]  [Se connecter]   │  ← code 4 chiffres depuis le portail
├─────────────────────────────────────┤
│ [Authentifié]                       │
│  Projet : ○ Projet A                │
│           ○ Projet B                │
│  Titre : [___________] 🎤           │  ← dictée vocale FR (Whisper)
│  Desc  : [___________] 🎤           │
│  [📎 Pièce jointe]  [📷 Screenshot] │
│  [✚ Créer la tâche]                 │
└─────────────────────────────────────┘
```

**Ouvrir/fermer le HUD :** cliquer le bouton `☰` en haut à droite,  
ou appeler `TeamflowHUD.Instance.Toggle()` depuis n'importe quel script  
(ex: mappé sur un bouton contrôleur Quest).

---

## Authentification — Flux VR (Device Code)

1. L'utilisateur ouvre le **portail client** sur PC/mobile
2. Clique **MODE VR** (bouton violet bas-droite) → un **code à 4 chiffres** s'affiche
3. Dans le casque, saisit ce code dans le HUD → `[Se connecter]`
4. ✅ Authentifié — peut créer des tâches

---

## Speech-to-Text FR offline (optionnel)

Nécessite **Unity Sentis** :

1. Package Manager → **+** → **Add package by name** → `com.unity.sentis` → Add
2. Menu : **Tools → TeamFlow → Download Whisper Models** (~75 MB, Hugging Face)
3. Les boutons 🎤 apparaissent à côté des champs Titre et Description
4. Cliquer 🎤 → parler en français → texte transcrit automatiquement

---

## Mapper l'ouverture du HUD sur un bouton Quest

```csharp
using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;
using TeamflowSDK;

public class TeamflowVRTrigger : MonoBehaviour
{
    private void Update()
    {
        // Bouton Menu (gauche) du Quest 2/3
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller,
            devices);

        foreach (var device in devices)
        {
            if (device.TryGetFeatureValue(CommonUsages.menuButton, out bool pressed) && pressed)
            {
                TeamflowHUD.Instance.Toggle();
                break;
            }
        }
    }
}
```

Attache `TeamflowVRTrigger` à n'importe quel GameObject actif dans ta scène.
