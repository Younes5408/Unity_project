# Archi-Agent VR

Voice-driven VR architecture & interior design assistant for Meta Quest. Speak to the AI in French, place buildings, furniture, and roads in real-time, grab and rearrange objects with your hands, and view session analytics on an in-world dashboard.

## Structure

```
.
├── Unity/      # VR client (Unity 2022+, XR Interaction Toolkit, Oculus)
└── Backend/    # FastAPI + MongoDB + LLM cascade (Groq / Gemini)
```

## Unity Client (`Unity/`)

- **VR rig** with skinned-mesh hands following Oculus controllers
- **Grip-driven finger curl** — fingers close proportionally with controller grip axis
- **XR Grab Interactable** on all furniture (chairs, tables) — grip to grab, release to drop
- **PC keyboard fallback** — press `J` to grab nearest object when no headset connected
- **Voice push-to-talk** — V key / Quest B button → record → STT → LLM → TTS → action
- **Avatar HUD** — animated character renders to a world-space panel in the player's view
- **Analytics dashboard** — say "dashboard" to see session stats (commands, placements, latency, duration)
- **Teleportation** — say "téléporte-moi à maison 3" to fade-and-jump between locations

### Asset Store packages to re-import after cloning

The Unity project relies on these (excluded from git to keep the repo lean):
- ModularHousePack1
- SimpleHands (source FBX)
- TextMesh Pro Essentials
- (Avatar FBX — any Mixamo character works)

## Backend (`Backend/`)

FastAPI server orchestrating the AI pipeline:

- **STT cascade**: Deepgram Nova-2 (FR) → Groq Whisper → Gemini fallback
- **LLM cascade**: Groq Llama-70b → Groq Llama-8b → Gemini 2.5 flash-lite
- **TTS**: Edge-TTS (free, French voice)
- **8 tools** the LLM can call: place_object, teleport_player, get_scene_state, undo_last, get_analytics_context, etc.
- **Local BM25 RAG** over the `knowledge_base/` corpus (French architectural norms)
- **MongoDB** for placements, sessions, events, teleport queue, dashboard commands
- **Non-blocking analytics** — every voice command and placement logged for the in-world dashboard

### Run

```bash
cd Backend
pip install -r requirements.txt
# Set environment: LLM_API_KEY, GROQ_API_KEY, GEMINI_API_KEY, DEEPGRAM_API_KEY
docker-compose up -d  # MongoDB
uvicorn api:app --reload --port 8000
```

## Architecture

```
[Quest VR] ── voice ──> [FastAPI] ──> [STT cascade] ──> [LLM + tools + RAG] ──> [MongoDB]
     ↑                                                                              │
     └─── poll /api/placements/pending, /api/teleport/pending, /api/ui/pending ────┘
```

Unity polls the backend every 1.5–2s for pending AI actions and executes them locally — no websockets, simple and resilient to flaky VR networks.

## Author

**Younes Lyazidi** — built as part of a VR architecture research project.
