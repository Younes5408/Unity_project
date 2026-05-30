import asyncio
import json
import os
import re
import sys
import time
from typing import Optional

# Force UTF-8 I/O on Windows (avoids cp1252 crash on French/Unicode text)
if sys.stdout and hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
if sys.stderr and hasattr(sys.stderr, 'reconfigure'):
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
from pathlib import Path
from fastapi import FastAPI, HTTPException, UploadFile, File, Form, Query
from fastapi.middleware.cors import CORSMiddleware
from fastapi.middleware.gzip import GZipMiddleware
from pydantic import BaseModel
from pymongo import MongoClient

sys.path.insert(0, str(Path(__file__).parent))

from llm_agent import create_unity_agent, create_groq_8b_fallback_agent, create_gemini_fallback_agent, extract_json_from_response, calculate_build_position
from database import clean_doc
from voice import speech_to_text, text_to_speech_base64, clean_text_for_tts
from tools import (
    set_build_position, set_house_id,
    placer_objet, teleporter_joueur, ajouter_piece_unity,
    initialiser_maison, consulter_normes,
)
import analytics

app = FastAPI(title="Archi-Agent VR API")  # v2

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Compress responses >1 KB — significantly shrinks the base64 MP3 + JSON
# payload returned from /api/chat/audio (Unity's UnityWebRequest auto-decompresses).
app.add_middleware(GZipMiddleware, minimum_size=1000)

# MongoDB
mongo_uri = os.getenv("MONGO_URI", "mongodb://localhost:27017/")
mongo_client = MongoClient(mongo_uri, serverSelectionTimeoutMS=5000)
db = mongo_client["house_design"]
rooms_col = db["rooms"]
houses_col = db["houses"]
placements_col = db["placements"]
teleports_col = db["teleports"]
scene_objects_col = db["scene_objects"]

# Clean up stale placements missing required fields (old format)
placements_col.delete_many({"prefab": {"$exists": False}, "status": "pending"})

# Agent — single instance, initialized at startup
agent = create_unity_agent()                       # Tier 1: Groq llama-3.3-70b-versatile (reliable tool calling)
groq_8b_fallback = create_groq_8b_fallback_agent()  # Tier 2: Groq llama-3.1-8b-instant (separate TPM bucket)
gemini_fallback = create_gemini_fallback_agent()    # Tier 3: Gemini gemini-2.5-flash-lite (final)


def _is_retryable_err(err: str) -> bool:
    el = err.lower()
    return ("401" in err or "invalid_api_key" in err or "quota" in el
            or "429" in err or "rate limit" in el or "rate_limit" in el)


async def _run_gemini_fallback(prompt: str, timeout: float):
    """agno's Gemini model has no async client — run sync .run() in a worker thread."""
    return await asyncio.wait_for(
        asyncio.to_thread(gemini_fallback.run, prompt),
        timeout=timeout,
    )


# ==========================================
# DEFENSIVE TOOL-CALL RECOVERY
# ==========================================
# When the LLM (especially the 8b fallback) fails to *invoke* a tool through
# agno's tool-calling mechanism and instead writes the call as PLAIN TEXT
# in its reply (e.g. `placer_objet(object_name="table")\nTable placée !`),
# we still need the VR action to happen. This regex spots that pattern and
# executes the tool server-side ourselves.
#
# A correctly-invoked tool produces a natural-language reply only — no
# `tool_name(...)` substring — so this never double-fires.
_TOOL_CALL_RE = re.compile(
    r'(placer_objet|teleporter_joueur|ajouter_piece_unity|initialiser_maison|consulter_normes)'
    r'\s*\(\s*([^)]*)\)',
    re.IGNORECASE,
)


def _parse_pseudo_arg(arg_str: str) -> str:
    """Extract a single positional/keyword arg value from text like
    `object_name="table"` or `"maison5"` or `salon`."""
    s = arg_str.strip()
    if not s:
        return ""
    # kwarg form (object_name=... / destination=... / room_type=...)
    m = re.match(r'\w+\s*=\s*(.+)', s)
    if m:
        s = m.group(1).strip()
    # take only the first arg if multiple
    if ',' in s:
        s = s.split(',', 1)[0].strip()
    return s.strip('"').strip("'").strip()


def _recover_tool_from_text(reply: str) -> tuple[Optional[str], Optional[str]]:
    """If the LLM wrote a tool call as text, execute it. Returns
    (tool_name, clean_french_confirmation) or (None, None)."""
    if not reply:
        return None, None
    m = _TOOL_CALL_RE.search(reply)
    if not m:
        return None, None
    tool = m.group(1).lower()
    raw_arg = _parse_pseudo_arg(m.group(2))
    print(f"[RECOVER] LLM wrote tool as text — invoking {tool}({raw_arg!r})")

    try:
        if tool == "placer_objet" and raw_arg:
            placer_objet(object_name=raw_arg)
            return tool, f"{raw_arg.capitalize()} placé !"
        if tool == "teleporter_joueur" and raw_arg:
            teleporter_joueur(destination=raw_arg)
            return tool, f"Téléportation vers {raw_arg} en cours !"
        if tool == "ajouter_piece_unity" and raw_arg:
            ajouter_piece_unity(room_type=raw_arg)
            return tool, f"{raw_arg.capitalize()} ajouté !"
        if tool == "initialiser_maison" and raw_arg:
            try:
                initialiser_maison(float(raw_arg))
                return tool, f"Aperçu maison {raw_arg}m² créé !"
            except (ValueError, TypeError):
                return None, None
        if tool == "consulter_normes" and raw_arg:
            res = consulter_normes(raw_arg)
            return tool, (res if isinstance(res, str) else "Norme consultée.")[:200]
    except Exception as e:
        print(f"[RECOVER] tool {tool} raised: {e}")
        return None, None
    return None, None


class _SyntheticResponse:
    """Stand-in for an agno RunResponse when a tier already fired a side-effect
    tool but its reply assembly timed out. Lets the caller continue without
    cascading to the next tier (which would double-fire the tool)."""
    def __init__(self, content: str):
        self.content = content
    def get_content_as_string(self) -> str:
        return self.content


def _side_effect_count() -> int:
    """Sum of pending placements + teleports — used to detect that a tool
    fired during a tier even if the LLM reply timed out afterwards."""
    try:
        return (placements_col.count_documents({"status": "pending"}) +
                teleports_col.count_documents({"status": "pending"}))
    except Exception:
        return 0


def _short_circuit_after_tool(tier_name: str) -> _SyntheticResponse:
    """Build a generic confirmation response so the caller can return success
    without invoking another tier. The actual side-effect (DB insert) already
    happened, so Unity will pick it up via /api/placements/pending."""
    print(f"[LLM] {tier_name} fired a tool — short-circuit, skipping remaining tiers")
    return _SyntheticResponse("OK !")  # generic — front-end already shows what changed


async def _run_agent_with_fallback(prompt: str, primary_timeout: float = 15.0,
                                   mid_timeout: float = 15.0, fb_timeout: float = 15.0):
    """3-tier dispatch: Groq-70b → Groq-8b → Gemini.

    Each tier uses a separate quota bucket — 70b TPM hit doesn't block 8b,
    and Groq quota hit doesn't block Gemini.

    CRITICAL: side-effect detection prevents double-firing. If a tier's tool
    inserted a placement/teleport, we short-circuit even on timeout — falling
    through would re-fire the same tool in tier 2/3.
    """
    baseline = _side_effect_count()

    # Tier 1: 70b
    try:
        return await asyncio.wait_for(agent.arun(prompt), timeout=primary_timeout)
    except asyncio.TimeoutError:
        if _side_effect_count() > baseline:
            return _short_circuit_after_tool("Tier1 (70b)")
        print(f"[LLM] Tier1 (70b) timed out — trying tier2 (8b)")
    except Exception as e:
        if _side_effect_count() > baseline:
            return _short_circuit_after_tool("Tier1 (70b)")
        if not _is_retryable_err(str(e)):
            raise
        print(f"[LLM] Tier1 (70b) failed ({str(e)[:80]}) — trying tier2 (8b)")

    # Tier 2: 8b
    if groq_8b_fallback:
        try:
            return await asyncio.wait_for(groq_8b_fallback.arun(prompt), timeout=mid_timeout)
        except asyncio.TimeoutError:
            if _side_effect_count() > baseline:
                return _short_circuit_after_tool("Tier2 (8b)")
            print(f"[LLM] Tier2 (8b) timed out — trying tier3 (Gemini)")
        except Exception as e:
            if _side_effect_count() > baseline:
                return _short_circuit_after_tool("Tier2 (8b)")
            if not _is_retryable_err(str(e)):
                raise
            print(f"[LLM] Tier2 (8b) failed ({str(e)[:80]}) — trying tier3 (Gemini)")

    # Tier 3: Gemini
    if gemini_fallback:
        print(f"[LLM] Falling through to Gemini")
        try:
            return await _run_gemini_fallback(prompt, fb_timeout)
        except Exception as e:
            if _side_effect_count() > baseline:
                return _short_circuit_after_tool("Tier3 (Gemini)")
            raise

    raise RuntimeError("All LLM tiers exhausted")


# ==========================================
# CHAT — text only (LLM)
# ==========================================
class ChatRequest(BaseModel):
    message: str
    house_id: str = "maison_001"
    player_x: float = 0.0
    player_z: float = 0.0
    player_angle: float = 0.0
    session_id: str = ""


@app.post("/api/chat")
async def chat_endpoint(req: ChatRequest):
    start_t = time.time()
    try:
        build_x, build_z = calculate_build_position(req.player_x, req.player_z, req.player_angle)
        set_build_position(build_x, build_z)
        set_house_id(req.house_id)
        position_block = (
            f"[POSITION: x={req.player_x:.1f}, z={req.player_z:.1f}, angle={req.player_angle:.0f}°]\n"
            f"[BUILD_AHEAD: x={build_x:.1f}, z={build_z:.1f}]\n"
            "\n"
        )

        # Dashboard voice intent — short-circuit, no LLM call
        intent = analytics.detect_dashboard_intent(req.message)
        if intent in ("open", "close"):
            await asyncio.to_thread(analytics.queue_ui_command, req.house_id, f"{intent}_dashboard")
            reply = "Tableau de bord ouvert." if intent == "open" else "Tableau de bord fermé."
            _fire_log_event(req.session_id, "voice_command", req.house_id, {
                "transcription": req.message, "tool_called": f"{intent}_dashboard",
                "success": True, "latency_ms": int((time.time() - start_t) * 1000),
            })
            return {"status": "ok", "reply": reply, "layout": {}}

        # NOTE: get_analytics_context_line() injection disabled — it added
        # 0.2-2 s per call against a populated demo collection. Dashboard
        # still works (voice intent + /api/analytics/dashboard route);
        # we just no longer inject stats into the LLM prompt.
        pre_count = _side_effect_count()
        res = await _run_agent_with_fallback(position_block + req.message)
        content = res.content or res.get_content_as_string() or ""
        print(f"[CHAT] content={repr(content[:80])}")

        # Defensive recovery: if LLM wrote tool call as text, execute it —
        # but ONLY if no side effect already happened (cascade short-circuit
        # or actual tool call). Otherwise we'd double-fire.
        recovered_tool = None
        if _side_effect_count() == pre_count:
            recovered_tool, recovered_reply = _recover_tool_from_text(content)
            if recovered_reply:
                content = recovered_reply
        layout = extract_json_from_response(content)

        _fire_log_event(req.session_id, "voice_command", req.house_id, {
            "transcription": req.message,
            "tool_called": recovered_tool or _infer_tool_from_layout(layout),
            "success": True,
            "latency_ms": int((time.time() - start_t) * 1000),
        })
        return {"status": "ok", "reply": content, "layout": layout}
    except Exception as e:
        _fire_log_event(req.session_id, "voice_command", req.house_id, {
            "transcription": req.message, "tool_called": None,
            "success": False, "latency_ms": int((time.time() - start_t) * 1000),
            "error": str(e)[:160],
        })
        return {"status": "error", "reply": f"Erreur: {str(e)[:160]}", "layout": {}}


def _fire_log_event(session_id: str, event_type: str, house_id: str, data: dict) -> None:
    """Fire-and-forget — schedules log_event in a worker thread so the response
    can return immediately. Errors are swallowed inside analytics.log_event."""
    try:
        loop = asyncio.get_running_loop()
        loop.run_in_executor(None, analytics.log_event, session_id, event_type, house_id, data)
    except Exception as e:
        print(f"[ANALYTICS] fire-and-forget failed: {e}")


def _infer_tool_from_layout(layout: dict) -> Optional[str]:
    """Best-effort tool name from the layout returned by the agent."""
    if not isinstance(layout, dict) or not layout:
        return None
    if "rooms" in layout:
        return "initialiser_maison"
    if "room_added" in layout or "room" in layout:
        return "ajouter_piece_unity"
    if "placement" in layout or "object_name" in layout:
        return "placer_objet"
    if "teleport" in layout:
        return "teleporter_joueur"
    return "unknown"


# ==========================================
# TRANSCRIBE — STT only (audio -> text)
# ==========================================
@app.post("/api/transcribe")
async def transcribe_endpoint(file: UploadFile = File(...)):
    try:
        audio_bytes = await file.read()
        text = await asyncio.to_thread(speech_to_text, audio_bytes, "recording.wav")
        return {"status": "ok" if text else "error", "transcription": text or ""}
    except Exception as e:
        return {"status": "error", "transcription": "", "error": str(e)[:160]}


# ==========================================
# TTS — text only (text -> base64 MP3)
# ==========================================
@app.post("/api/tts")
async def tts_endpoint(text: str = Form(...)):
    try:
        cleaned = clean_text_for_tts(text)
        audio_b64 = await text_to_speech_base64(cleaned)
        if audio_b64:
            return {"status": "ok", "audio": audio_b64}
        return {"status": "error", "audio": None}
    except Exception as e:
        return {"status": "error", "audio": None, "error": str(e)[:160]}


# ==========================================
# CHAT/AUDIO — complete pipeline (WAV -> STT -> LLM -> TTS -> MP3)
# ==========================================
@app.post("/api/chat/audio")
async def chat_audio(
    file: UploadFile = File(...),
    house_id: str = Query(default="maison_001"),
    player_x: float = Query(default=0.0),
    player_z: float = Query(default=0.0),
    player_angle: float = Query(default=0.0),
    session_id: str = Query(default=""),
    scene_context: str = Form(default=""),
):
    start_t = time.time()
    transcription = ""
    try:
        # 1. Read uploaded WAV straight into memory (no disk roundtrip).
        audio_bytes = await file.read()
        t_read = time.time()

        # 2. STT — transcribe audio to text
        transcription = await asyncio.to_thread(speech_to_text, audio_bytes, "recording.wav")
        t_stt = time.time()
        if not transcription:
            return {"status": "error", "message": "Transcription failed", "transcription": ""}

        # 2b. Dashboard intent — short-circuit before any LLM call
        intent = analytics.detect_dashboard_intent(transcription)
        if intent in ("open", "close"):
            await asyncio.to_thread(analytics.queue_ui_command, house_id, f"{intent}_dashboard")
            reply = "Tableau de bord ouvert." if intent == "open" else "Tableau de bord fermé."
            audio_b64 = await text_to_speech_base64(clean_text_for_tts(reply))
            total_ms = int((time.time() - start_t) * 1000)
            stt_ms = int((t_stt - t_read) * 1000)
            print(f"[CHAT/AUDIO|intent] stt={stt_ms}ms total={total_ms}ms transcription={transcription[:80]!r}")
            _fire_log_event(session_id, "voice_command", house_id, {
                "transcription": transcription, "tool_called": f"{intent}_dashboard",
                "success": True, "latency_ms": total_ms,
            })
            return {"status": "ok", "transcription": transcription, "reply": reply,
                    "layout": {}, "audio": audio_b64}

        # 3. Build position context (player position + 3m ahead) + house scoping
        build_x, build_z = calculate_build_position(player_x, player_z, player_angle)
        set_build_position(build_x, build_z)
        set_house_id(house_id)
        position_block = (
            f"[POSITION: x={player_x:.1f}, z={player_z:.1f}, angle={player_angle:.0f}°]\n"
            f"[BUILD_AHEAD: x={build_x:.1f}, z={build_z:.1f}]\n"
            "\n"
        )

        # NOTE: get_analytics_context_line() injection disabled — it added
        # 0.2-2 s per call against a populated demo collection. Dashboard
        # still works (voice intent + /api/analytics/dashboard route);
        # we just no longer inject stats into the LLM prompt.

        # 4. LLM — process transcription with position context (identical to beautiful-hertz)
        base = f"[CONTEXTE SCÈNE: {scene_context}]\n\n{transcription}" if scene_context.strip() else transcription
        message = position_block + base

        t_llm_start = time.time()
        pre_count = _side_effect_count()
        res = await _run_agent_with_fallback(message)
        t_llm = time.time()
        reply = res.content or res.get_content_as_string() or ""

        # Defensive recovery: if LLM wrote tool call as text, execute it —
        # but ONLY if no side effect already happened. See chat_endpoint note.
        recovered_tool = None
        if _side_effect_count() == pre_count:
            recovered_tool, recovered_reply = _recover_tool_from_text(reply)
            if recovered_reply:
                reply = recovered_reply
        layout = extract_json_from_response(reply)

        # 5. TTS — convert reply to audio (edge-tts is async-native)
        cleaned = clean_text_for_tts(reply)
        audio_b64 = await text_to_speech_base64(cleaned)
        t_tts = time.time()

        stt_ms = int((t_stt - t_read) * 1000)
        llm_ms = int((t_llm - t_llm_start) * 1000)
        tts_ms = int((t_tts - t_llm) * 1000)
        total_ms = int((t_tts - start_t) * 1000)
        print(f"[CHAT/AUDIO] stt={stt_ms}ms llm={llm_ms}ms tts={tts_ms}ms total={total_ms}ms "
              f"transcription={transcription[:60]!r} reply={reply[:60]!r}")
        _fire_log_event(session_id, "voice_command", house_id, {
            "transcription": transcription,
            "tool_called": recovered_tool or _infer_tool_from_layout(layout),
            "success": True,
            "latency_ms": int((time.time() - start_t) * 1000),
        })
        return {
            "status": "ok",
            "transcription": transcription,
            "reply": reply,
            "layout": layout,
            "audio": audio_b64,
        }
    except Exception as e:
        print(f"[CHAT/AUDIO] Error: {e}")
        _fire_log_event(session_id, "voice_command", house_id, {
            "transcription": transcription, "tool_called": None,
            "success": False, "latency_ms": int((time.time() - start_t) * 1000),
            "error": str(e)[:200],
        })
        return {"status": "error", "message": str(e)[:200]}


# ==========================================
# LAYOUT — current rooms from DB
# ==========================================
@app.get("/api/layout/{house_id}")
async def get_layout(house_id: str):
    try:
        rooms = [clean_doc(r) for r in rooms_col.find({}, projection={"_id": 0})]
        house = houses_col.find_one({"house_id": house_id}, projection={"_id": 0})
        # Empty-state is NOT an error: a fresh house with no rooms yet is a
        # valid state (user placing furniture before calling initialiser_maison).
        # Returning 404 here spammed Unity logs every 2s with no-op polls.
        return {"status": "ok", "house": clean_doc(house), "rooms": rooms}
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


# ==========================================
# CLEAR — wipe house from DB
# ==========================================
@app.post("/api/clear/{house_id}")
async def clear_house(house_id: str):
    """Wipe everything the AI added for this house_id from MongoDB.

    Scope is intentionally MongoDB-only — the baked-in scene GameObjects
    (houses/roads/fences/grounds present in MaMaison.unity at edit time)
    live in the .unity file and are NOT touched by this endpoint.
    """
    try:
        rooms_deleted = rooms_col.delete_many({}).deleted_count
        houses_deleted = houses_col.delete_many({"house_id": house_id}).deleted_count
        # Placements were untagged before the house_id field was added; clean both
        # rows matching this house and legacy untagged rows so Supprimer feels complete.
        placements_deleted = placements_col.delete_many(
            {"$or": [{"house_id": house_id}, {"house_id": {"$exists": False}}]}
        ).deleted_count
        return {
            "status": "ok",
            "deleted_rooms": rooms_deleted,
            "deleted_houses": houses_deleted,
            "deleted_placements": placements_deleted,
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


# ==========================================
# STATUS — health check
# ==========================================
@app.get("/api/status")
async def status_endpoint():
    # Mirror the key-priority used by _pick_llm_model() in llm_agent.py.
    # After the Lever-1 quota split, LLM_API_KEY is the dedicated LLM account
    # and GROQ_API_KEY is the STT-dedicated account. Reading them in the wrong
    # order made /api/status falsely attribute the LLM provider to the STT key.
    groq_key = os.getenv("LLM_API_KEY") or os.getenv("GROQ_API_KEY")
    gemini_key = os.getenv("GEMINI_API_KEY")
    return {
        "status": "ok",
        "provider": "groq" if groq_key else ("gemini" if gemini_key else "none"),
        "model": "llama-3.3-70b-versatile" if groq_key else "gemini-2.5-flash-lite",
        "fallback_tiers": [m for m in [
            "llama-3.1-8b-instant" if groq_key else None,
            "gemini-2.5-flash-lite" if gemini_key else None,
        ] if m] or None,
    }


# ==========================================
# OBJECTS — spawnable registry
# ==========================================
@app.get("/api/objects/available")
async def get_available_objects():
    """Returns the list of spawnable Unity GameObjects.
    Reads from spawnable_objects.json in the project root.
    Unity's PrefabPlacer.cs uses this to know what it can place."""
    import json as _json
    from pathlib import Path
    config_path = Path(__file__).parent / "spawnable_objects.json"
    try:
        with open(config_path, "r", encoding="utf-8") as f:
            data = _json.load(f)
        return {"status": "ok", "objects": data.get("objects", [])}
    except FileNotFoundError:
        return {"status": "ok", "objects": []}
    except Exception as e:
        return {"status": "error", "objects": [], "error": str(e)[:100]}


# ==========================================
# PLACEMENTS — prefab objects (PrefabPlacer.cs)
# ==========================================
def _house_scope(house_id: str) -> dict:
    """Mongo filter that matches docs for this house OR legacy untagged docs."""
    return {"$or": [{"house_id": house_id}, {"house_id": {"$exists": False}}]}


@app.get("/api/placements/pending")
async def get_pending_placements(house_id: str = Query(default="maison_001")):
    try:
        q = {"status": "pending", **_house_scope(house_id)}
        pending = [clean_doc(p) for p in placements_col.find(q, projection={"_id": 0})]
        return {"status": "ok", "placements": pending}
    except Exception as e:
        return {"status": "error", "placements": [], "error": str(e)[:160]}


@app.get("/api/placements/furniture")
async def get_pending_furniture(house_id: str = Query(default="maison_001")):
    """Pending placements filtered to furniture only. Unity can poll this
    endpoint separately to apply floor-aware spawning."""
    try:
        q = {"status": "pending", "category": "furniture", **_house_scope(house_id)}
        pending = [clean_doc(p) for p in placements_col.find(q, projection={"_id": 0})]
        return {"status": "ok", "placements": pending}
    except Exception as e:
        return {"status": "error", "placements": [], "error": str(e)[:160]}


@app.get("/api/placements/all")
async def get_all_placements(house_id: str = Query(default="maison_001")):
    """Every saved placement for this house (pending + placed).
    Called once by PrefabPlacer.Start() to re-instantiate AI-spawned objects
    after Unity reloads."""
    try:
        rows = [clean_doc(p) for p in placements_col.find(_house_scope(house_id), projection={"_id": 0})]
        return {"status": "ok", "placements": rows}
    except Exception as e:
        return {"status": "error", "placements": [], "error": str(e)[:160]}


@app.get("/api/catalog")
async def get_catalog():
    """Return the full spawnable catalog grouped by category.
    Consumed by the Unity CatalogMenu UI."""
    try:
        from prefab_catalog import PREFAB_CATALOG, FURNITURE_CATALOG

        def _group(catalog: dict) -> list[dict]:
            seen_prefabs: set[str] = set()
            items: list[dict] = []
            for name, info in catalog.items():
                if info["prefab"] in seen_prefabs:
                    continue  # skip aliases pointing to same prefab
                seen_prefabs.add(info["prefab"])
                items.append({
                    "name": name,
                    "prefab": info["prefab"],
                    "category": info["category"],
                })
            return sorted(items, key=lambda x: x["name"])

        return {
            "status": "ok",
            "buildings": _group(PREFAB_CATALOG),
            "furniture": _group(FURNITURE_CATALOG),
        }
    except Exception as e:
        return {"status": "error", "buildings": [], "furniture": [], "error": str(e)[:160]}


@app.post("/api/placements/{placement_id}/confirm")
async def confirm_placement(placement_id: str):
    try:
        placements_col.update_one({"id": placement_id}, {"$set": {"status": "placed"}})
        return {"status": "ok", "placement_id": placement_id}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


@app.delete("/api/placements/{placement_id}")
async def delete_placement(placement_id: str):
    try:
        result = placements_col.delete_one({"id": placement_id})
        return {"status": "ok", "deleted": result.deleted_count > 0}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


@app.delete("/api/rooms/{room_id}")
async def delete_room(room_id: str):
    try:
        result = rooms_col.delete_one({"id": room_id})
        return {"status": "ok", "deleted": result.deleted_count > 0}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


# ==========================================
# TELEPORT — AI-driven player teleportation
# ==========================================

class SceneObjectsRequest(BaseModel):
    objects: list  # [{name, x, z}]


@app.post("/api/scene/register")
async def register_scene_objects(req: SceneObjectsRequest):
    """Unity calls this on Play start to register baked-in scene object positions.
    Upserts by name so re-entering Play Mode is idempotent."""
    import re as _re
    try:
        for obj in req.objects:
            name = str(obj.get("name", ""))
            name_lower = _re.sub(r'\s+', '', name.lower()).replace('maison', 'house')
            scene_objects_col.update_one(
                {"name_lower": name_lower},
                {"$set": {"name": name, "name_lower": name_lower,
                          "x": float(obj.get("x", 0)), "z": float(obj.get("z", 0))}},
                upsert=True
            )
        return {"status": "ok", "registered": len(req.objects)}
    except Exception as e:
        return {"status": "error", "error": str(e)[:160]}


@app.get("/api/teleport/pending")
async def get_pending_teleport(house_id: str = Query(default="maison_001")):
    """Returns the oldest unconfirmed teleport request for this house_id."""
    try:
        doc = clean_doc(teleports_col.find_one(
            {"status": "pending", "house_id": house_id},
            projection={"_id": 0},
            sort=[("created_at", 1)]
        ))
        return {"status": "ok", "teleport": doc or None}
    except Exception as e:
        return {"status": "error", "teleport": None, "error": str(e)[:160]}


@app.post("/api/teleport/{teleport_id}/confirm")
async def confirm_teleport(teleport_id: str):
    """Unity calls this after executing the teleport."""
    try:
        teleports_col.update_one({"id": teleport_id}, {"$set": {"status": "done"}})
        return {"status": "ok", "teleport_id": teleport_id}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


# ==========================================
# ANALYTICS — VR interaction pipeline
# ==========================================

class EventsBatch(BaseModel):
    session_id: str = ""
    house_id: str = "maison_001"
    events: list  # [{event_type, data, timestamp?}, ...]


@app.post("/api/events")
async def ingest_events(batch: EventsBatch):
    """Bulk ingest events from Unity AnalyticsTracker (every 5s).
    Runs the blocking pymongo insert in a worker thread so /api/chat/audio
    isn't queued behind us on the same event loop."""
    try:
        docs = []
        for ev in batch.events or []:
            docs.append({
                "session_id": ev.get("session_id") or batch.session_id,
                "event_type": ev.get("event_type", "unknown"),
                "timestamp": ev.get("timestamp"),
                "house_id": ev.get("house_id") or batch.house_id,
                "data": ev.get("data", {}),
            })
        inserted = await asyncio.to_thread(analytics.log_events_batch, docs)
        return {"status": "ok", "inserted": inserted}
    except Exception as e:
        return {"status": "error", "inserted": 0, "error": str(e)[:160]}


@app.get("/api/analytics/dashboard")
async def analytics_dashboard(session_id: str = Query(default="")):
    """Full stats for the in-world dashboard panel.

    Flat response shape (status + stat fields at top level) so Unity's
    JsonUtility can deserialize directly into DashboardStats without an
    extra envelope DTO.
    """
    try:
        stats = await asyncio.to_thread(analytics.get_dashboard_stats, session_id)
        return {"status": "ok", **stats}
    except Exception as e:
        return {"status": "error", "error": str(e)[:160]}


@app.get("/api/ui/pending")
async def ui_pending(house_id: str = Query(default="maison_001")):
    """Unity polls this to learn when to open/close the dashboard panel."""
    try:
        cmds = await asyncio.to_thread(analytics.get_pending_ui_commands, house_id)
        return {"status": "ok", "commands": cmds}
    except Exception as e:
        return {"status": "error", "commands": [], "error": str(e)[:160]}


if __name__ == "__main__":
    import uvicorn
    uvicorn.run("api:app", host="0.0.0.0", port=8000, reload=True)
