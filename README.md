# Archi-Agent VR

**Archi-Agent VR** est un assistant d'architecture et de design d'intérieur en réalité virtuelle, piloté entièrement à la voix en français. L'utilisateur enfile un casque Meta Quest, parle à un avatar IA qui se tient face à lui, et regarde sa maison se construire en temps réel autour de lui.

> *"Construis-moi une maison à trois chambres"*, *"Mets une chaise devant moi"*, *"Téléporte-moi à la cuisine"*, *"Affiche-moi le dashboard"* — tout passe par le langage naturel, dans une boucle voix → action → voix qui prend moins de 5 secondes en bout en bout.

---

## 📖 Contexte du projet

Ce projet est un travail de recherche sur **l'interaction multimodale VR + LLM** pour la conception architecturale assistée par IA. L'objectif est double :

1. **Démontrer qu'on peut piloter Unity en VR uniquement à la voix**, sans manettes pour la navigation ni interfaces 2D — la voix devient la couche de commande, les mains servent uniquement à manipuler le mobilier.
2. **Valider qu'un LLM raisonnablement petit (Llama 3.3 70B sur Groq, ou Gemini 2.5 Flash-Lite) suffit à orchestrer une scène VR complexe** : placer des prefabs, téléporter, interroger l'état du monde, consulter une base de normes architecturales françaises (DTU), maintenir un historique d'actions annulables.

Le projet utilise **uniquement des paliers gratuits** (Groq, Deepgram, Gemini, Edge-TTS) pour rester reproductible par un étudiant ou un chercheur sans budget. Les choix techniques privilégient la **résilience** (pipeline en cascade à 3 niveaux pour chaque API critique) et la **simplicité de déploiement** (Docker Compose pour MongoDB, un seul `uvicorn` pour le backend, scène Unity prête à l'emploi).

---

## ✨ Fonctionnalités principales

- 🎤 **Push-to-talk** — touche `V` au clavier ou bouton `B` du Quest pour parler. Reconnaissance vocale française via Deepgram Nova-3 (~300 ms p50).
- 🗣️ **Réponses parlées** par un avatar humanoïde rigué animé (Sleep → WakeUp → Idle → Thinking → Talking), synthèse vocale via Edge-TTS (voix française naturelle, gratuite).
- 🏠 **Construction par la voix** — 8 modèles de maisons modulaires, 5 chaises, 5 tables, ensemble cuisine, luminaires, voies & clôtures. Placement intelligent à 3 m devant le joueur, avec raycast au sol pour le mobilier.
- ✋ **Mains VR riggées** avec préhension — animator-driven (Grip/Trigger), saisir une chaise = approcher la main + serrer le grip.
- 🚀 **Téléportation vocale** — *"emmène-moi à la maison 3"*, transition fondu noir, atterrissage 5 m devant l'entrée.
- 📊 **Tableau de bord en VR** — panneau WorldSpace qui s'affiche dans le champ de vision sur commande vocale.
- 🧠 **IA contextuelle** — l'agent connaît à chaque instant la position et l'orientation du joueur, peut interroger la scène (*"il y a combien de chaises dans la cuisine ?"*), consulter le RAG normatif, et annuler ses actions.
- 💾 **Persistance** — votre maison, vos meubles et vos statistiques de session survivent aux redémarrages (MongoDB).

---

## 🏗️ Architecture générale

```
┌─────────────────────┐  voix WAV  ┌──────────────────────────────────────┐
│  Quest / PC (Unity) │ ─────────▶ │            Backend FastAPI           │
│                     │            │                                      │
│  • VoiceRecorder    │            │  ┌──────┐  ┌──────┐  ┌──────────┐    │
│  • HandPresence     │ ◀────────  │  │ STT  │→ │ LLM  │→ │ TTS      │    │
│  • PrefabPlacer     │   MP3 b64  │  │ x3   │  │ x3   │  │ Edge-TTS │    │
│  • TeleportReceiver │  + JSON    │  └──────┘  └──┬───┘  └──────────┘    │
│  • DashboardCtrl    │            │               │                      │
│  • AvatarHUD        │            │       ┌───────┴───────┐              │
│  • AnalyticsTracker │            │       │  8 outils LLM │              │
└──────────┬──────────┘            │       │  + RAG BM25   │              │
           │                       │       └───────┬───────┘              │
           │  polling 1.5 - 2 s    │               │                      │
           ├──────────────────────▶│       ┌───────┴───────┐              │
           │  /placements/pending  │       │   MongoDB     │              │
           │  /teleport/pending    │       │ (persistence) │              │
           │  /ui/pending          │       └───────────────┘              │
           │                       │                                      │
           │  ACK + telemetry      │                                      │
           └──────────────────────▶└──────────────────────────────────────┘
```

**Pourquoi du polling et pas du WebSocket ?** Le Wi-Fi du casque Quest est instable, surtout en Air Link. Le polling se reconnecte tout seul si la liaison saute pendant 2 secondes ; un WebSocket aurait demandé une gestion explicite de reconnexion. Les endpoints `/pending` retournent ce qui n'a pas encore été confirmé par Unity ; chaque action est ACK-ée explicitement avant d'être marquée comme délivrée. Coût négligeable (3 requêtes GET/2 s, ~200 octets chacune).

---

## 🎬 La scène — design et structure

La scène principale (`Unity/Assets/Scenes/MaMaison.unity`) est volontairement minimaliste à l'ouverture, pour laisser l'IA construire le monde :

- **Un terrain plat** (`Terrain` Unity standard, texturé herbe/terre, ~500×500 m) — fournit un sol pour le raycast de placement de mobilier.
- **Un éclairage directionnel** simulant la lumière du jour + une skybox.
- **Le rig joueur** (`Player` GameObject) :
  - `XR Origin` (compatible Quest)
  - `Main Camera` (taggée `MainCamera`, FOV 60°)
  - Enfants `LeftHand` et `RightHand` portant chacun un `HandPresence`, un `SphereCollider` (trigger), un `XRDirectInteractor` (pour la saisie XR native) et une instance du prefab `Animated Hands/Left|Right Hand Model`.
- **L'avatar IA** (`Avatar` GameObject) — modèle Mixamo Ch41 placé 3 m devant le joueur au démarrage. Voir section ci-dessous.
- **Un `GameManager`** qui héberge les services persistants : `VoiceRecorder`, `PrefabPlacer`, `TeleportReceiver`, `UICommandPoller`, `DashboardController`, `AnalyticsTracker`, `LayoutReceiver`. Toutes les valeurs `apiBaseUrl` y pointent sur `http://127.0.0.1:8000`.
- **Un canvas HUD** (`AvatarHUD`) en mode **World Space** — affiche l'avatar via une `RenderTexture` (le mesh de l'avatar est sur le Layer 8, une seconde caméra `AvatarCamera` ne rend que ce layer dans la RenderTexture, le HUD affiche la texture). Le HUD suit la caméra principale (légèrement attaché au-dessus à droite).
- **Un canvas de dashboard** (`DashboardCanvas`), invisible par défaut, activé par la commande vocale. Voir section *Tableau de bord*.

Tout ce qui apparaît ensuite — murs, sols, chaises, tables, lampadaires, maisons entières — est **instancié dynamiquement à l'exécution** par `PrefabPlacer` et `LayoutReceiver` à partir des décisions du LLM. La scène ne contient aucun de ces objets en dur, ce qui permet à l'utilisateur de partir d'une page blanche à chaque session (ou de reprendre exactement où il s'était arrêté grâce à la persistance MongoDB).

---

## 🧍 L'avatar IA — rigging et animations

L'avatar est un **humanoïde Mixamo Ch41** (FBX rigué squelettiquement, hiérarchie Hips → Spine → Chest → Neck → Head + bras et jambes), importé dans `Unity/Assets/Avatar/`. Le mesh est skinné sur le squelette via un `SkinnedMeshRenderer` ; l'avatar peut donc bouger naturellement (les épaules tournent, le cou s'incline, les doigts restent solidaires).

### Le rig

- **Type d'avatar** : `Humanoid` (Unity remappe automatiquement les os Mixamo vers son squelette standard, ce qui permet de réutiliser n'importe quelle animation humanoïde du Asset Store).
- **Configuration de l'Avatar** validée dans l'inspecteur (toutes les correspondances os détectées correctement, pas d'os manquant).
- **Textures** : `Ch41_body.png` + `Ch41_FacialExpressions.png`, importées avec leurs métadonnées Mixamo.
- **Échelle** : 1:1 (échelle humaine ~1.75 m).

### La machine d'états d'animation

L'`Animator` de l'avatar (`Assets/Avatar/Animations/AvatarController.controller`) gère cinq états enchaînés par des transitions déclenchées par des `Trigger` Unity :

```
   ┌──────┐  WakeUp   ┌────────┐   start   ┌──────┐
   │ Sleep│ ────────▶ │ WakeUp │  (auto)   │ Idle │
   └──┬───┘           └────────┘ ────────▶ └──┬───┘
      │                                       │
      │              ┌──────────┐             │ Think
      └─────────────▶│ Thinking │◀────────────┘
        Sleep        └────┬─────┘
                          │ Talk
                          ▼
                     ┌──────────┐  silence (auto)
                     │ Talking  │ ──────────────┐
                     └──────────┘               │
                          ▲                     │
                          └─────────────────────┘
                                  retour Idle
```

- **Sleep** : pose au repos, légère respiration (animation en boucle). État par défaut au lancement.
- **WakeUp** : transition courte (~1 s) déclenchée au premier push-to-talk de la session.
- **Idle** : attente active — l'avatar regarde devant lui, légères micro-bougies.
- **Thinking** : déclenché quand la requête a quitté le client (STT lancé). L'avatar penche la tête, croise les bras, regarde en l'air.
- **Talking** : déclenché à la réception du MP3 TTS. L'avatar gesticule et bouge la mâchoire pendant toute la durée du clip audio, retour automatique à `Idle` à la fin.

### Synchronisation avec le pipeline vocal

`Unity/Assets/Scripts/AvatarEventBridge.cs` (attaché au même GameObject que `VoiceRecorder`) expose 4 hooks publics que `VoiceRecorder` appelle aux transitions de pipeline :

| Hook                  | Moment exact                                  | État résultant |
|-----------------------|-----------------------------------------------|----------------|
| `OnRecordingStart()`  | Touche `V` enfoncée                            | WakeUp → Idle  |
| `OnSendStart()`       | Audio envoyé au backend, attente de réponse    | Thinking       |
| `OnTtsStart(clip)`    | MP3 reçu et décodé, lecture débutée            | Talking        |
| `OnTtsEnd()`          | Clip audio terminé                             | Idle           |

### Support de l'interruption

Si l'utilisateur appuie sur `V` pendant que l'avatar est en `Thinking` ou `Talking`, `VoiceRecorder` :
1. Coupe la requête HTTP en cours (`CancellationToken`).
2. Stoppe la lecture du `AudioSource` TTS.
3. Émet `OnRecordingStart()` → l'avatar repasse en `Idle` puis enregistre la nouvelle phrase.

Pratique pour reformuler une commande sans devoir attendre la fin d'une réponse longue.

---

## 🎙️ Pipeline vocal — STT, LLM, TTS en cascade

Chaque API critique du pipeline est servie par une **cascade à 3 niveaux**. Si le niveau 1 timeoute ou retourne une erreur retryable (429, 5xx), le niveau 2 prend le relais, puis le niveau 3. Chaque niveau utilise une **clé d'API distincte** (ou un fournisseur distinct) pour éviter qu'un quota épuisé sur un service bloque tout le pipeline.

### Cascade STT (voix → texte)

| Niveau | Service              | Modèle                  | Latence p50 | Timeout dur |
|--------|----------------------|-------------------------|-------------|-------------|
| 1      | **Deepgram**         | `nova-3` (langue=fr)    | ~300 ms     | 8 s         |
| 2      | **Groq Whisper**     | `whisper-large-v3` (fr) | 1 – 5 s     | 10 s        |
| 3      | **Gemini**           | `gemini-2.5-flash-lite` | 2 – 4 s     | (par défaut)|

- Deepgram Nova-3 a remplacé Nova-2 le 30/05 — Nova-2 retournait des chaînes vides sur les phrases courtes (<2 s) et forçait la cascade à descendre au niveau 2.
- Groq Whisper a un quota gratuit généreux (18 000 secondes-audio/h) mais peut « mettre en file d'attente » des requêtes en cas de saturation — d'où le `timeout=10` dur côté client.

### Cascade LLM (texte → action)

| Niveau | Service     | Modèle                       | Fenêtre TPM | Timeout |
|--------|-------------|------------------------------|-------------|---------|
| 1      | **Groq**    | `llama-3.3-70b-versatile`    | 12 000      | 15 s    |
| 2      | **Groq**    | `llama-3.1-8b-instant`       | 30 000      | 15 s    |
| 3      | **Gemini**  | `gemini-2.5-flash-lite`      | n/a (free)  | 15 s    |

Le pipeline détecte les effets de bord (`placer_objet` qui a déjà inséré une chaise) **avant** de retomber sur le niveau suivant, pour éviter de placer deux chaises si le niveau 1 a appelé l'outil mais a timeouté pendant la rédaction de sa réponse texte. Cette protection est implémentée dans `Backend/api.py:_run_agent_with_fallback`.

### TTS (texte → voix)

Un seul niveau : **Edge-TTS** (interface non-officielle au service Microsoft Azure TTS, gratuite, sans clé). Voix par défaut : `fr-FR-DeniseNeural`. Latence ~500 – 800 ms pour une phrase de 10 mots. Audio retourné en MP3 base64 dans le JSON de réponse, décodé côté Unity et lu via un `AudioSource` ordinaire.

---

## 🧰 Les 8 outils du LLM

L'agent (basé sur la lib [`agno`](https://github.com/agno-agi/agno)) dispose de 8 outils déclarés dans `Backend/tools.py`. À chaque tour, le LLM peut en appeler 0, 1, ou plusieurs (le `tool_call_limit` est fixé à 3 pour éviter les boucles).

| Outil                          | Signature                                                   | Effet de bord |
|--------------------------------|-------------------------------------------------------------|---------------|
| `placer_objet`                 | `(object_name: str)`                                        | Insère un document dans `placements` Mongo. PrefabPlacer Unity polle, instancie. |
| `teleporter_joueur`            | `(destination: str)`                                        | Insère dans `teleports`. TeleportReceiver fade-and-move. |
| `obtenir_etat_scene`           | `()` ou `(filter: str)`                                     | Lit `placements` + `rooms`. Retourne la liste structurée pour le LLM. |
| `annuler_derniere_action`      | `()`                                                        | Dépile `history.py`, marque le placement comme supprimé. PrefabPlacer détecte et détruit l'instance. |
| `obtenir_contexte_analytics`   | `()`                                                        | Lit les stats de session pour donner des réponses contextuelles ("tu m'as placé 5 chaises ce matin"). |
| `interroger_normes`            | `(question: str)`                                           | BM25 sur `Backend/knowledge_base/*.md` (DTU, dimensions standards, normes accessibilité). |
| `dashboard`                    | `(action: 'open' \| 'close')`                                | Pousse une commande UI dans `ui_commands` Mongo. UICommandPoller la transmet à DashboardController. |
| `position_joueur`              | `()`                                                        | Retourne les coordonnées et l'angle actuel du joueur (depuis le contextvar de la requête). |

Le prompt système (dans `Backend/llm_agent.py`) reste volontairement court (~500 tokens au lieu des ~8 000 tokens d'un prompt verbeux) pour tenir dans la fenêtre TPM de Groq Llama 3.3 70B. Il donne les conventions d'unité (mètres), le repère Unity (yaw=0 face à +Z), la liste d'objets connus, et les règles d'annulation.

---

## 📍 Détection de la position et de l'orientation du joueur

L'IA doit savoir **où** est le joueur pour pouvoir poser une chaise « devant » lui ou répondre à *"qu'est-ce qu'il y a derrière moi ?"*. Cette information est transmise à chaque requête, sans intervention du LLM :

### Côté Unity (`VoiceRecorder.cs`)

À chaque envoi de l'audio (touche `V` relâchée), `VoiceRecorder` ajoute 4 query params à l'URL `/api/chat/audio` :

```
?house_id=maison_001
&player_x=12.45      ← Camera.main.transform.position.x
&player_z=-3.10      ← Camera.main.transform.position.z
&player_angle=145.7  ← Camera.main.transform.eulerAngles.y
&session_id=<uuid>
```

Pas de `player_y` — pour la construction architecturale, seule la position au sol importe.

### Côté Backend (`api.py` + `spatial.py`)

`api.py:chat_audio` extrait ces params, les attache au contexte de la requête via `contextvars` (variable globale par-requête, **thread-safe**) puis injecte dans le prompt LLM un bloc préformaté :

```
[POSITION: x=12.5, z=-3.1, angle=146°]
[BUILD_AHEAD: x=13.7, z=-1.0]
```

Le `BUILD_AHEAD` est calculé par `spatial.calculate_build_position()` — 3 m devant le joueur dans la direction qu'il regarde, avec une **gotcha critique** sur la convention angulaire d'Unity :

```python
# Unity: yaw=0 → forward = +Z, yaw=90 → forward = +X
# (et NON +X comme dans la convention mathématique standard)
build_x = player_x + distance * math.sin(math.radians(angle))
build_z = player_z + distance * math.cos(math.radians(angle))
#                                ^^^ sin/cos sont INVERSÉS
```

C'est cette inversion qui a coûté une journée de debug en mai. Elle est commentée et testée dans le code.

### Pourquoi des `contextvars` et pas des paramètres explicites ?

Les outils du LLM (`placer_objet`, etc.) ne reçoivent comme arguments que ce que le LLM décide d'écrire. On ne peut pas compter sur lui pour rappeler chaque fois `(player_x, player_z, angle)` — il le faisait mal au début du projet, d'où des chaises qui apparaissaient à `(0, 0)`. Avec `contextvars`, les outils accèdent à `get_build_position()` qui retourne automatiquement les coordonnées de la requête courante, indépendamment de ce que le LLM a écrit.

---

## 🪑 Placement intelligent d'objets

L'utilisateur dit *"mets une chaise"* → `placer_objet(object_name="chaise")` est appelé → un document est inséré dans `placements_col` Mongo :

```json
{
  "_id": "obj_chaise_20260530143012",
  "house_id": "maison_001",
  "prefab": "Furniture/Chaise/Chaise_1",
  "world_x": 13.7,  // depuis BUILD_AHEAD
  "world_y": 0.0,   // remis à zéro par le raycast Unity
  "world_z": -1.0,
  "rotation_y": 326.0,  // dos tourné au joueur (angle + 180°)
  "status": "pending",
  "created_at": "..."
}
```

Côté Unity, `PrefabPlacer.cs` polle `/api/placements/pending?house_id=...` toutes les 1,5 s. À chaque nouveau document :

1. **Résolution du prefab** — le `prefab` (chemin relatif sous `Resources/`) est résolu par `Resources.Load<GameObject>(path)`.
2. **Raycast au sol** — depuis `(world_x, 10, world_z)` vers le bas, distance max 20 m, couche `Terrain`. Si touche → `world_y = hit.point.y`. Évite les chaises flottantes ou enterrées.
3. **Instanciation** — `Instantiate(prefab, hit.point, Quaternion.Euler(0, rotation_y, 0))`.
4. **Ajout des composants d'interaction** — un `Rigidbody` (kinematic), un `XRGrabInteractable` (pour la saisie VR), un `BoxCollider` si le prefab n'en a pas.
5. **ACK** — appel à `POST /api/placements/{id}/confirm` qui passe le document à `status: "delivered"`. Plus jamais retourné par le polling.

### Catalogue des objets connus

Défini dans `Backend/spawnable_objects.json` :

| Catégorie    | Variantes                                                   |
|--------------|-------------------------------------------------------------|
| Maisons      | `maison1` à `maison8` (ModularHousePack1)                   |
| Chaises      | `chaise`, `chaise 1` à `chaise 5`                           |
| Tables       | `table`, `table 1` à `table 5`                              |
| Cuisine      | `ensemble cuisine`, `evier`, `four`, `frigo`, ...           |
| Voies        | `route`, `sentier`                                          |
| Clôtures     | `cloture`, `barriere`                                       |
| Luminaires   | `lampadaire`                                                |

Le LLM voit la liste dans son prompt système et résout les synonymes (*"chaise"* sans variante → choisit `chaise 1`, *"un évier"* → `evier`).

---

## 🚀 Système de téléportation

Les déplacements en VR sont fatigants (motion sickness en locomotion continue) — la téléportation vocale permet de couvrir 50 m en 1 seconde sans gêne.

### Pipeline

1. Utilisateur dit *"téléporte-moi à la maison 3"* ou *"emmène-moi à la cuisine"*.
2. LLM appelle `teleporter_joueur(destination="maison 3")`.
3. `tools.py` résout `destination` → coordonnées :
   - Si nom de maison → 5 m devant l'entrée de la maison (position lue depuis `houses_col` Mongo).
   - Si nom de pièce → centre de la pièce (depuis `rooms_col`).
   - Si nom propre d'objet → 1 m face à l'objet (depuis `placements_col`).
4. Document inséré dans `teleports_col` avec `status: "pending"`.
5. `TeleportReceiver.cs` (Unity) polle `/api/teleport/pending` toutes les 2 s, détecte le document.
6. **Fondu noir** (Image fullscreen alpha 0 → 1 sur 0,5 s).
7. `Player.transform.position = new Vector3(target_x, current_y, target_z)`.
8. **Fondu retour** (alpha 1 → 0 sur 0,5 s).
9. ACK `POST /api/teleport/{id}/confirm`.

Total ~1,2 s, transition douce, pas de cinétose.

---

## 📊 Tableau de bord en VR

Affichage de statistiques de session sans quitter l'expérience immersive.

### Déclenchement

Détection d'intention **avant le LLM** (dans `api.py:chat_endpoint`) — regex sur la transcription : *"dashboard"*, *"tableau de bord"*, *"statistiques"*, *"montre les stats"*, etc. Match → court-circuit, aucun appel LLM, latence ~450 ms vs ~3 – 9 s pour un appel complet.

### Mécanique d'affichage

- Une commande `{action: "open_dashboard"}` est insérée dans `ui_commands_col`.
- `UICommandPoller.cs` (Unity) polle `/api/ui/pending`, fire l'event `OnOpenDashboard`.
- `DashboardController.cs` s'abonne à cet event, active son `Canvas` (mode WorldSpace, attaché à la `Main Camera` avec un léger décalage devant + au-dessus).
- Au même moment, `DashboardController` fetch `/api/analytics/dashboard` → reçoit un JSON plat :

```json
{
  "status": "ok",
  "voice_commands": 47,
  "placements": 23,
  "teleports": 5,
  "avg_latency_ms": 4271,
  "session_duration_s": 612,
  "rag_queries": 3
}
```

- Les valeurs sont injectées dans des `TMP_Text` sur le panneau (un label + une valeur par stat).

### Auto-fermeture

Quand l'utilisateur appuie à nouveau sur `V` pour parler, `VoiceRecorder` émet un event `OnRecordingStart` que `DashboardController` écoute aussi — fermeture du canvas. Évite à l'utilisateur de devoir dire *"ferme le dashboard"* avant de reparler.

---

## ✋ Mains VR — `Animated Hands` + `HandPresence`

Les mains visibles dans le casque (et en mode PC) sont fournies par le package **Animated Hands** (modèles bleus stylisés Oculus, 16 os par main, blend-tree Animator à 3 poses : *default*, *fist*, *pinch*). Le contrôleur est `HandPresence.cs`, un composant unique qui gère les deux modes :

### Mode VR (casque Quest actif)

```csharp
var device = InputDevices.GetDeviceAtXRNode(handedness == Right ? RightHand : LeftHand);
device.TryGetFeatureValue(CommonUsages.devicePosition, out var pos);
device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rot);
device.TryGetFeatureValue(CommonUsages.grip,    out float gripValue);
device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue);

transform.localPosition = pos + vrPositionOffset;
transform.localRotation = rot * Quaternion.Euler(vrRotationOffsetEuler);
animator.SetFloat("Grip", gripValue);
animator.SetFloat("Trigger", triggerValue);
```

La saisie d'objets est gérée nativement par l'`XRDirectInteractor` posé sur le même GameObject — détection de `XRGrabInteractable` dans le `SphereCollider` trigger + appui grip = grab automatique.

### Mode PC (pas de casque)

```csharp
transform.position = camera.transform.TransformPoint(pcHandOffset);  // x mirroré pour la main gauche
transform.rotation = camera.transform.rotation * Quaternion.Euler(pcRotationOffsetEuler);
animator.SetFloat("Grip", Input.GetKey(pcGripKey) ? 1 : 0);
```

La saisie en mode PC se fait par `OverlapSphere` autour de la main quand `J` (main droite) ou `H` (main gauche) est pressée. Trouve le `Rigidbody` le plus proche, le parente à la main, `isKinematic = true`. Au relâchement, déparente et fige où la chaise a été lâchée.

---

## 💾 Persistance des sessions

MongoDB stocke tout ce qui doit survivre à un redémarrage :

| Collection         | Contenu                                                         |
|--------------------|-----------------------------------------------------------------|
| `houses`           | Maisons enregistrées (id, position, type, métadonnées)          |
| `rooms`            | Pièces dessinées par l'IA dans une maison (murs, sols)          |
| `placements`       | Tous les objets posés (chaises, tables, etc.) — statut pending/delivered/deleted |
| `teleports`        | Historique des téléportations                                   |
| `scene_objects`    | Indice consolidé pour `obtenir_etat_scene`                      |
| `events`           | Journal d'événements pour analytics (push-to-talk, latence, etc.)|
| `sessions`         | Métadonnées de session (durée, count d'événements)              |
| `ui_commands`      | File des commandes UI (open/close dashboard)                    |

Au démarrage de Unity, `PrefabPlacer.OnEnable` appelle `/api/placements?house_id=...` (route différente de `/pending`) — récupère toutes les placements **delivered** pour ce house_id et les replay localement. Vous revenez exactement à l'état où vous aviez quitté.

---

## 🧪 Stack technique

| Couche       | Technologie                                                   |
|--------------|---------------------------------------------------------------|
| Client VR    | Unity 2022.3 LTS, XR Interaction Toolkit 3.5, Oculus XR Plugin |
| Backend      | Python 3.11, FastAPI, Uvicorn, agno (LLM agent framework)     |
| Base de données | MongoDB 7 (via Docker Compose)                              |
| LLM          | Groq Llama 3.3 70B → 3.1 8B → Gemini 2.5 Flash-Lite          |
| STT          | Deepgram Nova-3 → Groq Whisper Large v3 → Gemini             |
| TTS          | Edge-TTS (Microsoft Azure non-officiel, gratuit)             |
| RAG          | BM25 (rank_bm25) sur `knowledge_base/*.md`                   |
| Avatar       | Mixamo Ch41 humanoïde, rig Humanoid Unity                    |
| Mains VR     | Animated Hands (Asset Store), Animator blend-tree            |

---

## 🚀 Installation

### Prérequis

- **Unity 2022.3 LTS ou plus récent** (module Android pour build Quest standalone)
- **Python 3.11+**
- **Docker Desktop** (pour MongoDB)
- **Casque Meta Quest 2/3** *(optionnel — mode PC disponible)*
- **Clés API** (toutes paliers gratuits) :
  - [Groq](https://console.groq.com/keys) — créez **deux comptes** : un pour le LLM, un pour Whisper STT (évite que les deux services se battent pour le même quota TPM)
  - [Google AI Studio](https://aistudio.google.com/apikey) — Gemini fallback
  - [Deepgram](https://console.deepgram.com/signup) — STT principal (200 h/mois gratuit)

### 1. Cloner et configurer

```bash
git clone https://github.com/Younes5408/Unity_project.git
cd Unity_project/Backend
cp .env.example .env
```

Éditez `Backend/.env` et collez vos clés :

```
LLM_API_KEY=gsk_xxx_votre_cle_Groq_compte_1
GROQ_API_KEY=gsk_xxx_votre_cle_Groq_compte_2
GEMINI_API_KEY=AIzaSy_xxx
DEEPGRAM_API_KEY=xxx
```

### 2. Lancer le backend

```bash
# depuis Backend/
python -m venv .venv && .venv\Scripts\activate    # source .venv/bin/activate sur Mac/Linux
pip install -r requirements.txt
docker-compose up -d                                # MongoDB sur :27017
uvicorn api:app --port 8000
```

Vérification : `http://localhost:8000/api/status` doit retourner `{"status":"ok","provider":"groq",...}`.

### 3. Lancer le client Unity

1. Ouvrir Unity Hub → Add → sélectionner le dossier `Unity/` → ouvrir avec **Unity 2022.3 LTS ou plus récent**.
2. Ouvrir la scène `Assets/Scenes/MaMaison.unity`.
3. Appuyer sur **Play**.

C'est tout. Tout le reste (XR Plug-in, wiring de la scène, assets, prefabs de mains, source audio, avatar Mixamo) est **pré-configuré et committé dans le repo**. Pour déployer en standalone sur le casque : `File → Build Settings → Android → Build And Run` avec le casque connecté en USB.

### 4. Parler à l'agent

| Entrée                                | Action                                              |
|---------------------------------------|-----------------------------------------------------|
| Maintenir **V** (PC) / **B** (Quest)  | Push-to-talk                                        |
| Maintenir **J** / **H** (PC)          | Ferme la main droite / gauche, saisit le Rigidbody le plus proche |
| Serrer le grip (VR)                   | Saisie native via `XRDirectInteractor`              |
| **WASD** (PC)                         | Déplacement libre du joueur                         |

Quelques phrases pour démarrer : *"mets une chaise"*, *"affiche le dashboard"*, *"téléporte-moi à la cuisine"*, *"il y a combien de chaises dans le salon ?"*, *"annule ça"*.

---

## 🛠️ Personnalisation

**Ajouter un prefab à la palette de l'IA**

1. Déposer le prefab dans `Unity/Assets/Resources/Furniture/MonObjet.prefab`.
2. Ajouter une entrée à `Backend/spawnable_objects.json` :
   ```json
   { "name": "monobjet", "prefab": "Furniture/MonObjet", "category": "furniture" }
   ```
3. Redémarrer le backend. *"Mets un monobjet"* fonctionne.

**Ajuster la position des mains en mode PC** — sélectionner `Player/LeftHand` ou `RightHand` dans la scène → composant `HandPresence` → champs `Pc Hand Offset` / `Pc Rotation Offset Euler`. Les valeurs se modifient en live pendant le Play.

**Changer la voix TTS** — `Backend/voice.py` → constante `EDGE_TTS_VOICE` (liste : `edge-tts --list-voices`). Quelques voix françaises : `fr-FR-DeniseNeural` (femme, par défaut), `fr-FR-HenriNeural` (homme), `fr-CA-SylvieNeural` (québécoise).

**Étendre le corpus RAG** — ajouter des fichiers `.md` dans `Backend/knowledge_base/`. L'index BM25 est reconstruit au démarrage de `uvicorn`.

**Ajuster la portée de saisie en mode PC** — `HandPresence.pcGrabRadius` (défaut 0,35 m).

---

## 👤 Auteur

**Younes Lyazidi** — projet de recherche sur l'interaction multimodale VR + LLM pour la conception architecturale.

Pour toute question ou suggestion, ouvrez une issue sur GitHub.
