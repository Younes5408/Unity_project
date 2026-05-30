# Archi-Agent VR

**Archi-Agent VR** est un assistant d'architecture et de design d'intérieur en réalité virtuelle, contrôlé entièrement à la voix en français. Mettez votre casque Meta Quest, parlez à l'IA, et regardez la maison se construire autour de vous en temps réel.

> *"Construis-moi une maison à trois chambres", "Mets une chaise devant moi", "Téléporte-moi à la maison 5", "Affiche-moi le dashboard"* — tout fonctionne en langage naturel.

---

## ✨ Fonctionnalités

### 🎤 Contrôle vocal complet
- **Push-to-talk** : maintenir la touche `V` (PC) ou le bouton `B` du Quest pour parler
- **Reconnaissance vocale française** via Deepgram Nova-2 (~300ms de latence)
- **Réponses parlées** générées par TTS Edge-TTS avec voix française naturelle
- **Avatar animé** dans le casque — il dort, se réveille, réfléchit, parle (synchronisé avec l'état du pipeline)

### 🏠 Construction par la voix
- **Placer des maisons** : *"mets une maison ici"*, *"ajoute une maison à côté"* (8 modèles de maisons disponibles via ModularHousePack)
- **Mobilier** : *"mets une chaise"*, *"ajoute une table"* (5 modèles de chaises, 5 modèles de tables, + ensemble cuisine)
- **Voies et clôtures** : routes, sentiers, barrières
- **Placement intelligent** : tout est posé 3m devant le joueur, orienté correctement, avec un raycast au sol pour le mobilier (pas de meubles flottants)
- **Persistance** : les placements survivent aux redémarrages — quand vous revenez, votre maison est toujours là

### ✋ Mains VR avec préhension
- **Mains squelettées** (16 os, doigts riggés) qui suivent les manettes Oculus
- **Doigts qui se ferment** proportionnellement à la pression sur la gâchette grip
- **Saisir les objets** : approchez la main d'une chaise, serrez le grip → la chaise suit votre main
- **Mode PC** : sans casque, appuyez sur `J` pour saisir l'objet le plus proche (test sans VR)

### 🚀 Téléportation
- *"Téléporte-moi à la maison 3"*, *"emmène-moi à la cuisine"*
- Transition fondu noir, déplacement instantané, retour à la vue
- Atterrit automatiquement 5m devant l'entrée

### 📊 Tableau de bord en VR
- *"Affiche-moi le dashboard"* ou *"montre les statistiques"*
- Panneau qui apparaît dans votre champ de vision, attaché à la caméra
- Stats affichées : **commandes vocales**, **placements**, **latence moyenne**, **durée de session**
- Se ferme automatiquement quand vous appuyez sur push-to-talk pour reparler à l'IA

### 🧠 IA contextuelle
- L'IA **sait où vous êtes** — votre position et orientation sont envoyées à chaque requête
- L'IA peut **interroger la scène** : *"il y a combien de chaises dans la cuisine ?"*, *"quelles sont les coordonnées de la maison 3 ?"*
- **RAG local** sur les normes architecturales françaises (DTU, dimensions standards)
- **Mémoire de session** — l'IA se souvient des actions passées, peut faire un *"annule"* pour revenir en arrière

---

## 🏗️ Architecture

```
┌──────────────────┐  voix   ┌────────────────────────────────────────┐
│  Casque Quest /  │ ──────► │            FastAPI (Backend)           │
│   PC (Unity)     │         │                                        │
│                  │         │  ┌────────┐  ┌────────┐  ┌─────────┐  │
│  - VoiceRecorder │ ◄────── │  │  STT   │→ │  LLM   │→ │   TTS   │  │
│  - PrefabPlacer  │  audio  │  │cascade │  │cascade │  │Edge-TTS │  │
│  - Teleport      │  texte  │  └────────┘  └───┬────┘  └─────────┘  │
│  - Dashboard     │  +JSON  │                  │                    │
│  - Hands + Grab  │         │           ┌──────┴──────┐             │
└────────┬─────────┘         │           │  8 outils   │             │
         │                   │           │     +       │             │
         │   poll 1.5-2s     │           │  RAG local  │             │
         ├──────────────────►│           └──────┬──────┘             │
         │ /placements/pending                  │                    │
         │ /teleport/pending                    ▼                    │
         │ /ui/pending                  ┌──────────────┐             │
         └──────────────────────────────│   MongoDB    │             │
                                        │ (persistence)│             │
                                        └──────────────┘             │
                                                                     │
                                        └────────────────────────────┘
```

### Cascade STT (parole → texte)
1. **Deepgram Nova-2** (français) — ~300ms, 200h/mois gratuit
2. **Groq Whisper large-v3** — fallback gratuit, démarrage à froid 5-20s
3. **Gemini 2.5 flash-lite** — dernier recours

### Cascade LLM (texte → action)
1. **Groq Llama 3.3 70B** — qualité maximale
2. **Groq Llama 3.1 8B** — fallback rapide
3. **Gemini 2.5 flash-lite** — fallback final

### Les 8 outils du LLM
1. `placer_objet` — pose un prefab à une position du monde
2. `téléporter_joueur` — déplace le joueur vers une destination nommée
3. `obtenir_etat_scene` — liste les objets présents, leurs positions
4. `annuler_derniere_action` — undo
5. `obtenir_contexte_analytics` — stats de session pour réponses contextuelles
6. `interroger_normes` — RAG sur le corpus architectural
7. `dashboard` — ouvre/ferme le panneau de statistiques
8. `position_joueur` — coordonnées actuelles du joueur

### Pattern de communication
- **Polling, pas WebSocket** — Unity interroge `/api/placements/pending`, `/api/teleport/pending`, `/api/ui/pending` toutes les 1.5-2s
- **Pourquoi ?** Le réseau VR est instable (Wi-Fi du casque) — le polling est résilient aux coupures, pas de reconnexion à gérer
- **ACK explicite** — Unity confirme chaque action exécutée pour vider la queue

### Stack technique
| Couche | Technologie |
|---|---|
| Client VR | Unity 2022+, XR Interaction Toolkit 3.5, Oculus XR Plugin |
| Backend | Python 3.11, FastAPI, Uvicorn |
| Base de données | MongoDB 7 (via Docker Compose) |
| LLM | Groq (Llama 3.x), Google Gemini |
| STT | Deepgram, Groq Whisper, Gemini |
| TTS | Edge-TTS (Microsoft, gratuit) |
| RAG | BM25 local sur `knowledge_base/*.md` |

---

## 🚀 Installation

### Prérequis
- **Unity 2022.3 LTS ou plus récent** (avec module Android pour build Quest)
- **Python 3.11+**
- **Docker Desktop** (pour MongoDB)
- **Casque Meta Quest 2/3** (optionnel — mode PC disponible avec touche J)
- **Clés API** (toutes ont un palier gratuit) :
  - [Groq](https://console.groq.com/keys) — pour LLM principal **et** Whisper STT (deux comptes recommandés pour éviter le rate-limit)
  - [Google AI Studio](https://aistudio.google.com/apikey) — pour Gemini (fallback)
  - [Deepgram](https://console.deepgram.com/signup) — pour STT principal (200h/mois gratuit)

### 1. Cloner le repo
```bash
git clone https://github.com/Younes5408/Unity_project.git
cd Unity_project
```

### 2. Backend (FastAPI + MongoDB)

```bash
cd Backend

# Environnement Python virtuel
python -m venv venv
venv\Scripts\activate     # Windows
# source venv/bin/activate  # Mac/Linux

# Dépendances
pip install -r requirements.txt

# Variables d'environnement — créez un fichier .env
echo LLM_API_KEY=gsk_xxx_votre_cle_Groq_compte_1 >> .env
echo GROQ_API_KEY=gsk_xxx_votre_cle_Groq_compte_2 >> .env
echo GEMINI_API_KEY=AIza_xxx_votre_cle_Gemini >> .env
echo DEEPGRAM_API_KEY=xxx_votre_cle_Deepgram >> .env

# MongoDB via Docker
docker-compose up -d

# Lancer le serveur (port 8000 par défaut)
uvicorn api:app --reload --port 8000
```

Vérifiez `http://localhost:8000/api/status` — doit retourner `{"status": "ok", ...}`.

### 3. Unity Client

**Packs Asset Store à réimporter** (exclus du repo pour rester léger) :
- [ModularHousePack1](https://assetstore.unity.com/) — les 8 maisons
- [SimpleHands](https://assetstore.unity.com/) — uniquement le FBX source si vous voulez modifier le mesh
- [TextMesh Pro Essentials](https://assetstore.unity.com/) — pour les UI
- Un avatar **Mixamo** (FBX humanoïde) → mettre dans `Assets/Avatar/Models/`

**Étapes :**

1. Ouvrir le dossier `Unity/` dans Unity Hub → Add → ouvrir avec Unity 2022.3+
2. Laisser Unity importer tous les packages (peut prendre 5-10 min la première fois)
3. **Activer le loader Oculus** :
   - `Edit → Project Settings → XR Plug-in Management`
   - Onglet **PC** : cocher ☑ Oculus
   - Onglet **Android** : cocher ☑ Oculus
4. Ouvrir la scène `Assets/Scenes/MaMaison.unity`
5. Sélectionner le GameObject `GameManager` dans la hiérarchie → vérifier que `apiBaseUrl` pointe sur `http://127.0.0.1:8000`
6. **Lancer la grab interaction** : menu `Tools → XR → Setup Hand Grab` (configure automatiquement les XRGrabInteractable sur le mobilier)

### 4. Tester

#### Mode PC (sans casque, validation rapide)
- Cliquer Play dans Unity
- Maintenir `V` pour parler — *"mets une chaise"*
- Avancer en WASD jusqu'à la chaise → appuyer sur `J` pour la saisir → relâcher pour poser
- Dire *"affiche le dashboard"* → le panneau apparaît dans la caméra

#### Mode VR (Quest)
- Connecter le casque via Quest Link / Air Link
- Cliquer Play dans Unity → l'écran se duplique dans le casque
- Maintenir `B` (manette droite) pour parler
- Approcher la main d'un objet → serrer `grip` pour saisir
- Pour build standalone Quest : `File → Build Settings → Android → Build And Run` (casque connecté en USB)

---

## 📂 Structure du projet

```
.
├── Unity/                          # Client VR
│   ├── Assets/
│   │   ├── Scripts/                # Code C# custom
│   │   │   ├── VoiceRecorder.cs    # Push-to-talk + envoi audio
│   │   │   ├── PrefabPlacer.cs     # Polling + instanciation des placements
│   │   │   ├── TeleportReceiver.cs # Fade + déplacement joueur
│   │   │   ├── XRHandFollower.cs   # Mains qui suivent les manettes
│   │   │   ├── HandFingerCurl.cs   # Doigts qui se ferment au grip
│   │   │   ├── PCHandSimulator.cs  # Test PC avec touche J
│   │   │   ├── DashboardController.cs
│   │   │   ├── UICommandPoller.cs
│   │   │   ├── AnalyticsTracker.cs # Logs d'événements vers backend
│   │   │   └── Editor/SetupXRGrab.cs  # Menu Tools/XR/Setup Hand Grab
│   │   ├── Scenes/MaMaison.unity   # Scène principale
│   │   ├── Resources/Furniture/    # Prefabs de mobilier (chaises, tables)
│   │   └── SimpleHands/            # Prefabs des mains (Black + White)
│   ├── Packages/                   # Manifeste UPM
│   └── ProjectSettings/
│
├── Backend/                        # Serveur FastAPI
│   ├── api.py                      # Routes HTTP
│   ├── voice.py                    # Cascade STT + TTS
│   ├── llm_agent.py                # Cascade LLM + outils
│   ├── tools.py                    # Les 8 outils du LLM
│   ├── analytics.py                # Events, sessions, dashboard
│   ├── rag.py                      # BM25 sur knowledge_base/
│   ├── spatial.py                  # Calculs de position (placement devant le joueur)
│   ├── database.py                 # Connexion MongoDB + collections
│   ├── history.py                  # Stack d'actions pour undo
│   ├── validator.py                # Validation des arguments d'outils
│   ├── config.py                   # Constantes + chargement .env
│   ├── main.py                     # Entrypoint Uvicorn
│   ├── knowledge_base/             # Corpus RAG (normes archi FR)
│   ├── docker-compose.yml          # MongoDB
│   └── requirements.txt
│
└── README.md
```

---

## 🛠️ Personnalisation

### Ajouter un nouveau prefab
1. Mettre le prefab dans `Unity/Assets/Resources/Furniture/MonObjet.prefab`
2. Ajouter une entrée dans `Backend/spawnable_objects.json` :
   ```json
   { "name": "monobjet", "prefab": "Furniture/MonObjet", "category": "furniture" }
   ```
3. Redémarrer le backend — l'IA peut maintenant placer cet objet via *"mets un monobjet"*

### Ajuster la portée de saisie des mains
- Sélectionner `LeftHand` ou `RightHand` dans la scène
- Modifier le rayon du `SphereCollider` (par défaut 8cm)

### Changer la voix TTS
- `Backend/voice.py` → variable `EDGE_TTS_VOICE` (liste : `edge-tts --list-voices`)

---

## 👤 Auteur

**Younes Lyazidi** — projet de recherche sur l'interaction VR + LLM pour la conception architecturale.

Pour toute question ou suggestion, ouvrez une issue sur GitHub.
