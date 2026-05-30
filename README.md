# Archi-Agent VR

Voice-driven architecture and interior-design assistant for Meta Quest. Wear the headset, speak in French, and the AI builds your house around you in real time.

> *"Construis-moi une maison à trois chambres"*, *"Mets une chaise devant moi"*, *"Téléporte-moi à la maison 5"*, *"Affiche-moi le dashboard"* — everything works in natural language.

---

## What it does

- **Push-to-talk** (V on PC, B on Quest) → French STT via Deepgram Nova-3 (~300 ms)
- **8 LLM tools** drive Unity: place houses & furniture, teleport, query scene state, open dashboard, undo, RAG over French building norms
- **TTS reply** spoken by an animated avatar (Edge-TTS, Microsoft, free)
- **VR + PC modes** — full Quest immersion or J/H keys to grab without a headset
- **Persistence** — your house, furniture, and session stats survive restarts (MongoDB)

---

## Architecture

```
┌─────────────────┐  voice  ┌────────────────────────────────────────┐
│  Quest / PC     │ ──────▶ │            FastAPI Backend             │
│  (Unity Client) │         │                                        │
│                 │         │  STT cascade → LLM cascade → TTS       │
│  - VoiceRecorder│ ◀────── │  Deepgram      Groq 70B      Edge-TTS  │
│  - HandPresence │  audio  │  Groq Whisper  Groq 8B                 │
│  - PrefabPlacer │  +JSON  │  Gemini        Gemini                  │
│  - Dashboard    │         │                                        │
│                 │         │  8 tools + BM25 RAG  ↔  MongoDB        │
└─────────────────┘         └────────────────────────────────────────┘
        ▲                                       │
        └────── polls /api/{placements,teleport,ui}/pending every 1.5–2s
```

| Layer | Tech |
|---|---|
| Client | Unity 2022.3+, XR Interaction Toolkit 3.5, Oculus XR Plugin |
| Backend | Python 3.11, FastAPI, Uvicorn |
| DB | MongoDB 7 (Docker Compose) |
| LLM | Groq Llama 3.x → Gemini 2.5 (cascade) |
| STT | Deepgram Nova-3 → Groq Whisper → Gemini |
| TTS | Edge-TTS |
| RAG | BM25 over `Backend/knowledge_base/*.md` |

---

## Quick start

### 1. Clone and configure keys

```bash
git clone https://github.com/Younes5408/Unity_project.git
cd Unity_project/Backend
cp .env.example .env
```

Edit `Backend/.env` and paste your API keys (all have free tiers):
- `LLM_API_KEY` — [Groq](https://console.groq.com/keys) account #1 (for LLM)
- `GROQ_API_KEY` — Groq account #2 (for Whisper STT — separate account avoids rate-limit fights with the LLM)
- `GEMINI_API_KEY` — [Google AI Studio](https://aistudio.google.com/apikey)
- `DEEPGRAM_API_KEY` — [Deepgram](https://console.deepgram.com/signup) (200 h/month free)

### 2. Run the backend

```bash
# in Backend/
python -m venv .venv && .venv\Scripts\activate    # source .venv/bin/activate on Mac/Linux
pip install -r requirements.txt
docker-compose up -d                                # MongoDB on :27017
uvicorn api:app --port 8000
```

Health check: `http://localhost:8000/api/status` → `{"status":"ok",...}`

### 3. Run the Unity client

1. Open Unity Hub → Add → select the `Unity/` folder → open with Unity **2022.3 LTS or newer**
2. Open the scene `Assets/Scenes/MaMaison.unity`
3. Press **Play**

That's it. Everything else (XR Plug-in, scene wiring, assets, hand prefabs, audio source) is pre-configured and committed to the repo. If you want to deploy standalone to a Quest headset: `File → Build Settings → Android → Build And Run` with the headset connected via USB.

### 4. Talk to it

| Input | Action |
|---|---|
| Hold **V** (PC) / **B** (Quest, right controller) | Push-to-talk |
| Hold **J** / **H** (PC) | Close right / left hand fist + grab nearest Rigidbody |
| Squeeze grip (VR) | Grab — handled natively by `XRDirectInteractor` |

Try: *"mets une chaise"*, *"affiche le dashboard"*, *"téléporte-moi à la cuisine"*, *"il y a combien de chaises dans le salon ?"*

---

## Project layout

```
Backend/                  FastAPI server
  api.py                  HTTP routes
  voice.py                STT/TTS cascade
  llm_agent.py            LLM cascade + tool registration
  tools.py                The 8 LLM tools
  analytics.py            Event log, dashboard stats
  rag.py                  BM25 over knowledge_base/
  spatial.py              Placement math (build-ahead-of-player)
  knowledge_base/         French architectural norms corpus
  docker-compose.yml      MongoDB

Unity/Assets/
  Scripts/
    VoiceRecorder.cs      Push-to-talk + audio upload
    PrefabPlacer.cs       Polls /placements/pending, instantiates
    TeleportReceiver.cs   Fade-and-move
    HandPresence.cs       VR/PC hand controller + grab (the one to look at)
    DashboardController.cs, UICommandPoller.cs, AnalyticsTracker.cs
  Animated Hands/         Blue stylized VR hands (Grip/Trigger animator)
  Scenes/MaMaison.unity   Main scene
```

---

## Customization

**Add a prefab to the AI's catalog**

1. Drop the prefab in `Unity/Assets/Resources/Furniture/MyObject.prefab`
2. Add an entry to `Backend/spawnable_objects.json`:
   ```json
   { "name": "myobject", "prefab": "Furniture/MyObject", "category": "furniture" }
   ```
3. Restart the backend. *"Mets un myobject"* now works.

**Tune hand position in PC mode** — select `Player/LeftHand` or `RightHand` in the scene → `HandPresence` component → `Pc Hand Offset` / `Pc Rotation Offset Euler`. Updates live in Play mode.

**Change TTS voice** — `Backend/voice.py` → `EDGE_TTS_VOICE` (list available voices with `edge-tts --list-voices`).

---

## Author

**Younes Lyazidi** — research project on voice-driven LLM interaction for architectural design in VR.
