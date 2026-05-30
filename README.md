# 🏠 Archi-Agent VR — Assistant Architectural Multimodal VR & PC

**Archi-Agent VR** est un assistant d'architecture et de design d'intérieur en réalité virtuelle et sur PC, entièrement pilotable par la voix en français. Grâce à l'intégration d'un casque VR Meta Quest (ou du simulateur clavier/souris PC), l'utilisateur interagit en langage naturel avec un avatar 3D animé. Cet agent intelligent comprend les intentions et orchestre en temps réel la conception, la construction et l'ameublement de l'espace tridimensionnel tout autour du joueur.

> 🎙️ *"Construis-moi une maison à trois chambres"*, *"Mets une table et deux chaises devant moi"*, *"Place une lampe au plafond"*, *"Téléporte-moi à la cuisine"*, *"Affiche le tableau de bord"* — Le système traduit la parole en actions 3D concrètes dans une boucle voix → action → voix fluide en moins de 5 secondes.

---

## 📖 Contexte & Objectifs du Projet

Ce projet valide une architecture de recherche sur **l'interaction multimodale VR + LLM** pour la conception assistée par IA :
1. **Zéro manette pour la navigation** : La voix devient la couche principale de commande et de déplacement (téléportation). Les mains et les contrôleurs physiques VR servent uniquement à la manipulation directe du mobilier (saisie, ajustement, rotation).
2. **Orchestration résiliente par LLM** : Validation qu'un grand modèle de langage compact (Llama 3.3 70B sur Groq ou Gemini 2.5 Flash-Lite) peut manipuler de manière déterministe l'état d'un monde 3D via des appels d'outils (Tool Calling), interroger une base locale de normes du bâtiment français (DTU) via un système RAG, et maintenir une session persistante.
3. **Paliers gratuits & Haute disponibilité** : Utilisation exclusive de services gratuits (Groq, Gemini, Deepgram, Edge-TTS) avec un système de **cascade à 3 niveaux (fallback cascades)** pour garantir le fonctionnement continu de la reconnaissance vocale et de la génération d'actions même en cas de panne ou de dépassement de quota.

---

## ✨ Fonctionnalités Principales

* 🎤 **Enregistrement Push-To-Talk** : Touche `V` du clavier ou bouton `B` de la manette Quest droite. Transcription vocale française ultra-rapide via Deepgram Nova-3 (~300 ms).
* 🗣️ **Avatar Vocal Animé** : Humanoïde 3D réagissant avec 5 états d'animation (`Sleep` ➔ `WakeUp` ➔ `Idle` ➔ `Thinking` ➔ `Talking`). Synthèse vocale naturelle en français via Microsoft Edge-TTS intégrée dans le mixeur audio Unity.
* 🏠 **Construction Vocale** : Spawn de 8 modèles de maisons modulaires pré-bâties, de routes, de barrières de clôture et d'ensembles de sols.
* 🛋️ **Ameublement Intelligent** : Placement automatique de tables, chaises, cuisines intégrées et accessoires.
* 📐 **Raycasting Avancé selon l'Objet** :
  * **Sol (`floor_aware`)** : Analyse de la hauteur réelle par lancer de rayons vers le bas depuis le niveau de la tête pour poser le mobilier exactement sur le plancher en ignorant les colliders des autres meubles.
  * **Plafond (`ceiling_aware`)** : Lancer de rayons vers le haut pour coller les lampes directement sur le plafond au-dessus du joueur, en orientant la lumière vers le bas.
  * **Mur (`wall_aware`)** : Alignement automatique des objets sur les surfaces verticales selon leur vecteur normal.
* 🔀 **Évitement de collision (Smart Offset)** : Si le point de spawn au sol est occupé, Unity teste automatiquement 5 positions candidates (centre, droite, gauche, devant, derrière) et instancie l'objet sur le premier emplacement libre. Empêche les chaises d'apparaître au centre des tables.
* 🚪 **Fluidité de navigation (Auto-Open Doors)** : Un script désactive récursivement les colliders des portes des prefabs de maisons lors du spawn afin que le joueur puisse traverser librement les pièces sans blocage physique.
* 💡 **Éclairage Dynamique Réaliste** : Injection automatique de composants `PointLight` (teintes chaudes, ombres douces) lors du placement de lampes de plafond ou de lampadaires pour éclairer les intérieurs sombres.
* 🚀 **Téléportation Vocale Intelligente** : Déplacement instantané vers n'importe quelle maison ou pièce nommée avec une transition douce de fondu au noir (Fade in/out) pour éviter le mal des transports (motion sickness).
* 📊 **Tableau de Bord VR (Dashboard)** : Panneau d'analyse flottant en WorldSpace affichant en temps réel les statistiques de la session (nombre de commandes, latence du pipeline, objets placés, historique).
* 💾 **Persistance 3D Complète** : Sauvegarde continue de la position `(X, Y, Z)` et de la rotation 3D réelle de chaque meuble et structure dans MongoDB. Reconstitution fidèle de la scène au démarrage.

---

## 🏗️ Architecture du Système

Le projet est divisé en deux briques logicielles communicantes : un **Client Unity (VR / PC)** et un **Backend FastAPI (Python)** relié à une base de données **MongoDB**.

```
┌────────────────────────────────┐                 ┌──────────────────────────────────────────────────┐
│     Quest / PC (Unity Client)  │                 │             FastAPI Python Backend               │
│                                │                 │                                                  │
│  ┌──────────────────────────┐  │    WAV Audio    │  ┌────────────────────────────────────────────┐  │
│  │ VoiceRecorder.cs         │──┼─────────────────┼─▶│ /api/chat/audio                            │  │
│  └──────────────────────────┘  │                 │  │ 1. STT Cascade (Deepgram -> Groq -> Gem)   │  │
│                                │                 │  │ 2. Intent Heuristics (Dashboard)           │  │
│  ┌──────────────────────────┐  │                 │  │ 3. LLM Agent Cascade (Groq 70B -> 8B -> Gem)│  │
│  │ PrefabPlacer.cs          │◀─┼─────────────────┼──│    • Tool Calling (DB updates)             │  │
│  │ LayoutReceiver.cs        │  │  JSON Metadata  │  │ 4. TTS Cascade (Edge-TTS -> gTTS)          │  │
│  │ TeleportReceiver.cs      │◀─┼─────────────────┼──│ 5. Returns MP3 base64 + Action JSON        │  │
│  │ UICommandPoller.cs       │  │                 │  └────────────────────────────────────────────┘  │
│  └──────────────────────────┘  │                 │                           │                      │
│                                │                 │                           ▼                      │
│  ┌──────────────────────────┐  │   JSON Poll     │  ┌────────────────────────────────────────────┐  │
│  │ AnalyticsTracker.cs      │──┼─────────────────┼─▶│ /api/events (Bulk writes)                  │  │
│  └──────────────────────────┘  │                 │  │ /api/placements/pending                    │  │
│                                │                 │  │ /api/teleport/pending                      │  │
│                                │                 │  │ /api/ui/pending                            │  │
│                                │                 │  └────────────────────────────────────────────┘  │
└────────────────────────────────┘                 └──────────────────────────────────────────────────┘
                                                                            │
                                                                            ▼
                                                                  ┌──────────────────┐
                                                                  │     MongoDB      │
                                                                  │ (Placements,     │
                                                                  │  Rooms, Events,  │
                                                                  │  UI, Teleports)  │
                                                                  └──────────────────┘
```

> [!NOTE]
> Pour contourner les instabilités fréquentes du réseau sans fil (Wi-Fi) des casques autonomes, Unity communique avec le backend via un mécanisme de **Polling Adaptatif** (`1.5s` à `2s` d'intervalle) plutôt que par WebSockets. Si la liaison coupe temporairement, le client réessaie de manière transparente sans perturber l'expérience utilisateur.

---

## 🛠️ Code Source & Composants

### 🎮 Client Unity (`Unity/Assets/Scripts/`)

* **[VoiceRecorder.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/VoiceRecorder.cs)** : Gère le push-to-talk, l'enregistrement du micro, l'envoi du flux audio au backend, la réception de la voix de retour de l'IA (Edge-TTS) et la mise à jour de la machine d'état de l'avatar 3D. Permet l'interruption vocale si l'utilisateur reparle pendant que l'IA s'exprime.
* **[PrefabPlacer.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/PrefabPlacer.cs)** : Interroge `/api/placements/pending`, instancie les prefabs 3D, applique les stratégies d'alignement (`floor_aware`, `ceiling_aware`, `wall_aware`), gère l'évitement de collision *Smart Offset*, applique la réduction d'échelle, configure les lumières dynamiques et confirme la position finale au serveur.
* **[LayoutReceiver.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/LayoutReceiver.cs)** : Récupère et dessine de façon procédurale le plan de sol, les murs, les ouvertures (portes/fenêtres) des pièces de la maison stockées en base de données.
* **[TeleportReceiver.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/TeleportReceiver.cs)** : Écoute `/api/teleport/pending`. À la réception, il effectue un fondu au noir, déplace les coordonnées du contrôleur joueur de manière sécurisée (en désactivant temporairement le `CharacterController` pour éviter les collisions physiques), puis restaure la vue.
* **[UICommandPoller.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/UICommandPoller.cs)** & **[DashboardController.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/DashboardController.cs)** : Récupèrent les commandes d'affichage et pilotent le panneau de statistiques flottant attaché à la caméra VR du joueur.
* **[AnalyticsTracker.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/AnalyticsTracker.cs)** : Collecte les événements de session (positions joueur échantillonnées, commandes vocales, latence, erreurs) et les transmet en arrière-plan par lots (batching).
* **[AutoOpenDoors.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/AutoOpenDoors.cs)** : Désactive les colliders de porte au sein des structures pour garantir un déplacement fluide.
* **[CreateDefaultGround.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/CreateDefaultGround.cs)** : Crée un plan de sol de secours si la scène démarre sans terrain initialisé.
* **[SetupXRGrab.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/Editor/SetupXRGrab.cs)** *(Dossier Editor)* : Script utilitaire qui configure automatiquement les composants de saisie VR (`XRGrabInteractable` et `Rigidbody` cinématiques) sur l'ensemble des prefabs de mobilier.

---

### 🐍 Backend Python (`Backend/`)

* **[api.py](file:///c:/Users/aya/Desktop/unityproject/Backend/api.py)** : Point d'entrée FastAPI. Expose les routes REST d'orchestration, les files d'attente `/pending` pour le polling Unity, le point de confirmation des placements réels et l'ingestion d'analytics.
* **[llm_agent.py](file:///c:/Users/aya/Desktop/unityproject/Backend/llm_agent.py)** : Configure les agents LLM (`agno`). Contient les invites système (system prompts) réduites pour minimiser la consommation de tokens et les cascades de secours.
* **[tools.py](file:///c:/Users/aya/Desktop/unityproject/Backend/tools.py)** : Déclare les outils Python mis à disposition du LLM (création de pièces, positionnement initial, placement d'objets, téléportation, interrogation de normes, etc.).
* **[voice.py](file:///c:/Users/aya/Desktop/unityproject/Backend/voice.py)** : Contient les cascades STT et TTS. Filtre les hallucinations de silence courantes du modèle Whisper et configure les dictionnaires de biais linguistiques (prompts STT).
* **[analytics.py](file:///c:/Users/aya/Desktop/unityproject/Backend/analytics.py)** : Gère le stockage MongoDB des métriques, calcule les grilles thermiques (heatmaps) de positionnement du joueur et formate la ligne de résumé injectée dans l'historique du LLM.
* **[prefab_catalog.py](file:///c:/Users/aya/Desktop/unityproject/Backend/prefab_catalog.py)** : Mappe les désignations françaises en langage naturel vers les chemins des prefabs situés dans le dossier `Assets/Resources/` d'Unity.
* **[rag.py](file:///c:/Users/aya/Desktop/unityproject/Backend/rag.py)** : Système de recherche documentaire ultra-léger basé sur l'algorithme de pertinence BM25, sans dépendances lourdes, pour chercher dans la base réglementaire [knowledge_base](file:///c:/Users/aya/Desktop/unityproject/Backend/knowledge_base).
* **[database.py](file:///c:/Users/aya/Desktop/unityproject/Backend/database.py)** : Initialise la connexion à MongoDB et crée les index optimisés pour le polling rapide.
* **[config.py](file:///c:/Users/aya/Desktop/unityproject/Backend/config.py)** : Centralise les contraintes géométriques (dimensions minimales/maximales des pièces) et les règles d'ajustement.

---

## 🧍 L'Avatar IA — Rigging et Machine d'États

L'avatar 3D qui fait face au joueur et répond à ses requêtes est un modèle humanoïde **Mixamo Ch41** importé dans [Assets/Avatar/](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Avatar/). Le mesh est skinné sur un squelette humanoïde standard.

Son animation et son comportement sont pilotés par deux scripts clés :
* **[AvatarController.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Avatar/Scripts/AvatarController.cs)** : Met à jour la variable entière `"AvatarState"` de l'Animator et gère les couleurs d'état affichées sur l'interface d'état de l'avatar.
* **[AvatarEventBridge.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Avatar/Scripts/AvatarEventBridge.cs)** : Fait le pont entre les événements de cycle de vie de [VoiceRecorder.cs](file:///c:/Users/aya/Desktop/unityproject/Unity/Assets/Scripts/VoiceRecorder.cs) et le contrôleur de l'avatar.

### Machine d'États d'Animation

L'`Animator` de l'avatar (`Assets/Avatar/Animations/AvatarController.controller`) réagit à un paramètre entier `"AvatarState"` qui gère cinq états d'animation distincts :

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

* **Sleep (1)** : État inactif de sommeil par défaut au lancement.
* **WakeUp (2)** : Déclenché lors du premier appui sur le bouton push-to-talk. Une transition automatique le fait passer à `Idle` à la fin de l'animation de réveil.
* **Idle (0)** : État d'attente active, l'avatar respire doucement en face du joueur.
* **Thinking (3)** : Activé dès que l'enregistrement audio s'arrête et que le backend traite la requête (STT + appel de modèle). L'avatar croise les bras et penche la tête en signe de réflexion.
* **Talking (4)** : Activé pendant la diffusion du flux audio Edge-TTS. Les lèvres bougent de façon synchrone et l'avatar fait des gestes de dialogue. Dès que la lecture audio s'achève, il retourne automatiquement à l'état `Idle`.

---


## ⚙️ Les Cascades d'API (Résilience)

Pour éviter qu'une panne d'API ou qu'un dépassement de quota (Rate Limit) ne bloque l'interaction, le backend implémente des pipelines de secours automatiques.

### 1. Reconnaissance Vocale (STT)
1. **Deepgram (Nova-3)** : Choix principal. Latence extrêmement basse (~300 ms) et excellente détection du français.
2. **Groq Whisper (whisper-large-v3)** : Activé si Deepgram échoue ou est hors ligne (limite de 10s pour ne pas bloquer Unity).
3. **Google Gemini (gemini-2.5-flash-lite)** : Ultime secours.

### 2. Modèle de Langage (LLM / Tool Calling)
1. **Groq (Llama 3.3 70B)** : Modèle principal. Très performant sur le Tool Calling et rapide.
2. **Groq (Llama 3.1 8B)** : Activé si le modèle 70B est sous restriction de quota (TPM).
3. **Google Gemini (gemini-2.5-flash-lite)** : Modèle de secours complet en cas d'erreur réseau ou de panne chez Groq.

### 3. Synthèse Vocale (TTS)
1. **Edge-TTS** : Génération de voix neuronale gratuite Microsoft Azure (sans clé API).
2. **gTTS (Google TTS)** : Solution de repli locale en Python.

---

## 📐 Algorithmes Avancés & Spécificités Techniques

### 1. Smart Offset (Évitement de collision de meubles)
Lorsqu'un utilisateur demande plusieurs fois de suite d'ajouter des objets sans se déplacer (ex. *"mets une chaise"*, *"mets une autre chaise"*), le système applique l'algorithme d'évitement implémenté dans `PrefabPlacer.cs` :
```csharp
Vector3[] candidates = new Vector3[]
{
    position,                       // 1. Centre visé
    position + localRight * 0.8f,   // 2. Décalé à droite
    position - localRight * 0.8f,   // 3. Décalé à gauche
    position - localForward * 0.8f, // 4. Décalé devant
    position + localForward * 0.8f  // 5. Décalé derrière
};
```
Pour chaque candidat, un rayon vertical est lancé pour trouver le sol, puis un test `Physics.OverlapSphere` détecte si un collider de type mobilier (`IsFurniture`) est présent dans un rayon de `0.35m`. L'objet est instancié sur la première position libre.

### 2. Ceiling-Aware & Wall-Aware Placement
* **Pour les lampes (`ceiling_aware`)** : Un rayon est lancé vers le haut depuis le sommet du casque VR. S'il intersecte un plafond, la lampe s'instancie sur le point d'impact. Sa rotation est inversée de manière dynamique pour s'orienter vers le bas :
  ```csharp
  rotation = Quaternion.FromToRotation(Vector3.up, hit.normal) * Quaternion.Euler(0f, placement.rotation_y, 0f);
  ```
* **Pour les appliques murales (`wall_aware`)** : Le rayon part du regard (gaze vector) du joueur vers l'avant. L'objet s'oriente parallèlement à la normale de la paroi détectée.

### 3. Gestion Locale Invariante (Floats)
Lors de l'envoi de la confirmation des coordonnées `(X, Y, Z)` calculées par Unity vers le serveur MongoDB, l'utilisation de la culture invariante est obligatoire pour éviter que les machines configurées sous Windows en français ne sérialisent les nombres décimaux avec des virgules (ex: `1,24` au lieu de `1.24`), ce qui ferait échouer l'API JSON du backend :
```csharp
string json = $"{{\n  \"x\": {finalPos.x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}, ... }}";
```

---

## 🚀 Installation & Lancement Rapide

### 1. Prérequis
- **Unity 2022.3 LTS** ou plus récent (compatible Unity 6).
- **Python 3.10+** (installé et ajouté au PATH).
- **Docker Desktop** (pour MongoDB).

### 2. Récupérer le Projet
```bash
git clone https://github.com/Younes5408/Unity_project.git
cd Unity_project/Backend
```

### 3. Configurer l'environnement (`.env`)
Créez un fichier `.env` dans le dossier `Backend/` en copiant le fichier d'exemple :
```bash
cp .env.example .env
```
Renseignez vos clés API gratuites :
```env
LLM_API_KEY=gsk_xxx_groq_compte_llm     # Clé Groq pour le modèle principal
GROQ_API_KEY=gsk_xxx_groq_compte_stt    # Clé Groq pour Whisper (ou réutiliser la même)
GEMINI_API_KEY=AIzaSy_xxx               # Clé Google AI Studio
DEEPGRAM_API_KEY=xxx                    # Clé Deepgram (Console Deepgram)
```

### 4. Démarrer la Base de Données MongoDB
Depuis le dossier `Backend/` :
```bash
docker compose up -d
```
*La base MongoDB est accessible localement sur le port `27017` et l'interface Mongo Express sur `http://localhost:8081` (identifiants: `admin` / `pass`).*

### 5. Préparer et Lancer le Backend Python
```bash
# Créer et activer l'environnement virtuel
python -m venv .venv
.\.venv\Scripts\activate

# Installer les dépendances Python
pip install -r requirements.txt

# Lancer le serveur avec rechargement automatique
python api.py
```
*Le serveur doit afficher qu'il écoute sur `http://127.0.0.1:8000`.*

### 6. Configurer et Lancer le Client Unity
1. Ouvrez le dossier `Unity/` dans **Unity Hub**.
2. Allez dans `File > Open Scene` et ouvrez la scène `Assets/Scenes/MaMaison.unity`.
3. Cliquez sur **Play** dans l'éditeur Unity.
4. Appuyez sur la touche **`V`** du clavier (ou bouton **`B`** de la manette droite en VR) et parlez.

---

## 🎤 Exemples de Commandes Vocales Supportées

Voici une liste non-exhaustive des phrases en français comprises par l'assistant :

| Type de Commande | Phrase Exemple | Outil Appelé |
| :--- | :--- | :--- |
| **Bâtiments** | *"Place une grande maison"* / *"Construis la maison 3"* | `placer_objet("maison3")` |
| **Mobilier** | *"Mets une table et deux chaises"* | `placer_objet("table")`, `placer_objet("chaise")` |
| **Éclairage** | *"Ajoute un lampadaire dehors"* / *"Mets une lampe au plafond"* | `placer_objet("lampadaire")` / `placer_objet("lampe")` |
| **Navigation** | *"Téléporte-moi devant l'entrée"* / *"Emmène-moi dans la maison 2"* | `teleporter_joueur("maison2")` |
| **Réglementation** | *"Quelle est la taille minimale d'une chambre ?"* | `consulter_normes(...)` *(RAG)* |
| **Visualisation** | *"À quoi ressemblerait un salon de 30m² ?"* | `initialiser_maison(30)` |
| **Historique** | *"Annule ce que je viens de faire"* | `annuler_derniere_action()` |
| **Statistiques** | *"Affiche le tableau de bord"* / *"Ferme le dashboard"* | `dashboard(action="open"/"close")` |

