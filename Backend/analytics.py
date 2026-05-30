"""VR Interaction Analytics — event ingestion, aggregation, and LLM context injection.

Non-blocking by design: any MongoDB failure is swallowed with a warning so that
analytics never breaks the main chat / placement pipeline.
"""

import os
import time
from datetime import datetime, timedelta, timezone
from typing import Any, Optional

from pymongo import MongoClient, DESCENDING, ASCENDING

# ----- MongoDB setup -----
_mongo_uri = os.getenv("MONGO_URI", "mongodb://localhost:27017/")
try:
    _client = MongoClient(_mongo_uri, serverSelectionTimeoutMS=5000)
    _db = _client["house_design"]
    events_col = _db["events"]
    sessions_col = _db["sessions"]
    ui_pending_col = _db["ui_pending"]

    # Indexes (idempotent)
    try:
        events_col.create_index([("session_id", ASCENDING), ("timestamp", DESCENDING)], background=True)
        events_col.create_index([("event_type", ASCENDING), ("timestamp", DESCENDING)], background=True)
        events_col.create_index([("timestamp", DESCENDING)], background=True)
        sessions_col.create_index("session_id", unique=True, background=True)
        ui_pending_col.create_index([("status", ASCENDING), ("created_at", ASCENDING)], background=True)
    except Exception as idx_err:
        print(f"[ANALYTICS] index creation skipped: {idx_err}")
except Exception as e:
    print(f"[ANALYTICS] MongoDB unavailable: {e}")
    events_col = None
    sessions_col = None
    ui_pending_col = None


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ----- Public API -----

def log_event(session_id: str, event_type: str, house_id: str, data: Optional[dict] = None) -> None:
    """Insert a single event. Swallow errors — analytics is non-blocking."""
    if events_col is None or not session_id:
        return
    try:
        events_col.insert_one({
            "session_id": session_id,
            "event_type": event_type,
            "timestamp": _now(),
            "house_id": house_id or "",
            "data": data or {},
        })
        # Maintain a session record (upsert on first event)
        if event_type == "session_start":
            sessions_col.update_one(
                {"session_id": session_id},
                {"$setOnInsert": {
                    "session_id": session_id,
                    "started_at": _now(),
                    "house_id": house_id or "",
                    "total_commands": 0,
                    "total_placements": 0,
                }},
                upsert=True,
            )
        elif event_type == "session_end":
            sessions_col.update_one(
                {"session_id": session_id},
                {"$set": {"ended_at": _now()}},
            )
        elif event_type == "voice_command":
            sessions_col.update_one(
                {"session_id": session_id},
                {"$inc": {"total_commands": 1},
                 "$setOnInsert": {"started_at": _now(), "house_id": house_id or ""}},
                upsert=True,
            )
        elif event_type == "placement":
            sessions_col.update_one(
                {"session_id": session_id},
                {"$inc": {"total_placements": 1},
                 "$setOnInsert": {"started_at": _now(), "house_id": house_id or ""}},
                upsert=True,
            )
    except Exception as e:
        print(f"[ANALYTICS] log_event failed: {e}")


def log_events_batch(events: list[dict]) -> int:
    """Bulk insert. Each item: {session_id, event_type, house_id, data, [timestamp]}.
    Returns number inserted."""
    if events_col is None or not events:
        return 0
    try:
        docs = []
        for ev in events:
            ts = ev.get("timestamp")
            if isinstance(ts, str):
                try:
                    ts = datetime.fromisoformat(ts.replace("Z", "+00:00"))
                except Exception:
                    ts = _now()
            elif not isinstance(ts, datetime):
                ts = _now()
            docs.append({
                "session_id": ev.get("session_id", ""),
                "event_type": ev.get("event_type", "unknown"),
                "timestamp": ts,
                "house_id": ev.get("house_id", ""),
                "data": ev.get("data", {}),
            })
        if docs:
            events_col.insert_many(docs, ordered=False)
        return len(docs)
    except Exception as e:
        print(f"[ANALYTICS] batch insert failed: {e}")
        return 0


def get_dashboard_stats(session_id: str) -> dict:
    """Return aggregated stats for the dashboard panel."""
    empty = {
        "session_id": session_id,
        "total_commands": 0,
        "total_placements": 0,
        "avg_latency_ms": 0,
        "duration_min": 0,
        "top_objects": [],
        "heatmap": [],
        "failed_commands": 0,
    }
    if events_col is None or not session_id:
        return empty

    try:
        scope = {"session_id": session_id}

        total_commands = events_col.count_documents({**scope, "event_type": "voice_command"})
        total_placements = events_col.count_documents({**scope, "event_type": "placement"})

        # Avg latency + failures
        lat_agg = list(events_col.aggregate([
            {"$match": {**scope, "event_type": "voice_command"}},
            {"$group": {
                "_id": None,
                "avg_latency": {"$avg": "$data.latency_ms"},
                "failed": {"$sum": {"$cond": [{"$eq": ["$data.success", False]}, 1, 0]}},
            }},
        ]))
        avg_latency = int(lat_agg[0]["avg_latency"] or 0) if lat_agg else 0
        failed = int(lat_agg[0]["failed"] or 0) if lat_agg else 0

        # Top objects placed
        top_agg = list(events_col.aggregate([
            {"$match": {**scope, "event_type": "placement"}},
            {"$group": {"_id": "$data.object_type", "count": {"$sum": 1}}},
            {"$sort": {"count": -1}},
            {"$limit": 5},
        ]))
        top_objects = [{"object_type": str(r["_id"] or "unknown"), "count": int(r["count"])} for r in top_agg]

        # Heatmap — 16x16 grid over x,z bucketed positions
        # Assumes player x/z roughly in [-50, +50] range. Larger world rescales naturally because we bucket by relative range.
        pos_agg = list(events_col.aggregate([
            {"$match": {**scope, "event_type": "position_update"}},
            {"$project": {"x": "$data.player.x", "z": "$data.player.z"}},
        ]))
        heatmap = _bucket_heatmap(pos_agg, grid=16)

        # Duration
        sess = sessions_col.find_one({"session_id": session_id})
        duration_min = 0
        if sess:
            start = sess.get("started_at")
            end = sess.get("ended_at") or _now()
            # pymongo returns naive datetimes by default; _now() is aware (UTC).
            # Mixing the two raises TypeError on subtraction. Normalize both to UTC.
            if start and start.tzinfo is None:
                start = start.replace(tzinfo=timezone.utc)
            if end and end.tzinfo is None:
                end = end.replace(tzinfo=timezone.utc)
            if start:
                duration_min = int((end - start).total_seconds() // 60)

        return {
            "session_id": session_id,
            "total_commands": total_commands,
            "total_placements": total_placements,
            "avg_latency_ms": avg_latency,
            "duration_min": duration_min,
            "top_objects": top_objects,
            "heatmap": heatmap,
            "failed_commands": failed,
        }
    except Exception as e:
        print(f"[ANALYTICS] dashboard stats failed: {e}")
        return empty


def _bucket_heatmap(positions: list[dict], grid: int = 16) -> list[list[int]]:
    """Bucket a list of {x, z} points into a grid×grid heatmap matrix.
    Returns matrix[row][col] = count, where row=z bucket, col=x bucket."""
    if not positions:
        return []
    xs = [p["x"] for p in positions if p.get("x") is not None]
    zs = [p["z"] for p in positions if p.get("z") is not None]
    if not xs or not zs:
        return []
    x_min, x_max = min(xs), max(xs)
    z_min, z_max = min(zs), max(zs)
    x_span = max(x_max - x_min, 1e-6)
    z_span = max(z_max - z_min, 1e-6)
    matrix = [[0] * grid for _ in range(grid)]
    for p in positions:
        x = p.get("x")
        z = p.get("z")
        if x is None or z is None:
            continue
        col = min(int((x - x_min) / x_span * grid), grid - 1)
        row = min(int((z - z_min) / z_span * grid), grid - 1)
        matrix[row][col] += 1
    return matrix


def get_analytics_context_line(session_id: str) -> str:
    """One-line compressed summary for system-prompt injection. ~30 tokens."""
    if events_col is None or not session_id:
        return ""
    try:
        # Fast aggregations (indexed)
        total_commands = events_col.count_documents({"session_id": session_id, "event_type": "voice_command"})
        if total_commands == 0:
            # First-turn — no stats yet, skip injection
            return ""

        top_agg = list(events_col.aggregate([
            {"$match": {"session_id": session_id, "event_type": "placement"}},
            {"$group": {"_id": "$data.object_type", "count": {"$sum": 1}}},
            {"$sort": {"count": -1}},
            {"$limit": 3},
        ]))
        objects_str = " ".join(f"{r['_id']}×{r['count']}" for r in top_agg if r.get("_id")) or "aucun"

        lat_agg = list(events_col.aggregate([
            {"$match": {"session_id": session_id, "event_type": "voice_command"}},
            {"$group": {
                "_id": None,
                "avg_latency": {"$avg": "$data.latency_ms"},
                "failed": {"$sum": {"$cond": [{"$eq": ["$data.success", False]}, 1, 0]}},
            }},
        ]))
        avg_latency = int(lat_agg[0]["avg_latency"] or 0) if lat_agg else 0
        failed = int(lat_agg[0]["failed"] or 0) if lat_agg else 0

        total_placements = events_col.count_documents({"session_id": session_id, "event_type": "placement"})

        return (
            f"[Analytics] {total_commands} commandes | "
            f"{total_placements} placements ({objects_str}) | "
            f"latence moy {avg_latency}ms | {failed} échecs"
        )
    except Exception as e:
        print(f"[ANALYTICS] context line failed: {e}")
        return ""


# ----- Dashboard UI command queue (open/close panel) -----

def queue_ui_command(house_id: str, action: str) -> Optional[str]:
    """Push a UI command for Unity to consume. action ∈ {open_dashboard, close_dashboard}."""
    if ui_pending_col is None:
        return None
    try:
        import uuid
        cmd_id = str(uuid.uuid4())
        ui_pending_col.insert_one({
            "id": cmd_id,
            "house_id": house_id or "",
            "action": action,
            "status": "pending",
            "created_at": _now(),
        })
        return cmd_id
    except Exception as e:
        print(f"[ANALYTICS] ui queue failed: {e}")
        return None


def get_pending_ui_commands(house_id: str) -> list[dict]:
    """Return pending UI commands for this house and mark them delivered.

    Normalises the legacy stale "maison1" value Unity's UICommandPoller used
    to ship with — some scenes still serialize that default and Unity won't
    pick up the self-heal code change until it recompiles. Treating it as
    "maison_001" here makes the dashboard work without requiring an Editor
    recompile / scene reload on the user's side. Safe because nothing in the
    project legitimately uses "maison1" as a distinct house.
    """
    if ui_pending_col is None:
        return []
    if house_id == "maison1":
        house_id = "maison_001"
    try:
        scope = {"status": "pending",
                 "$or": [{"house_id": house_id}, {"house_id": ""}]}
        rows = list(ui_pending_col.find(scope, projection={"_id": 0}))
        if rows:
            ui_pending_col.update_many(scope, {"$set": {"status": "delivered"}})
        # Cast datetimes to iso strings so FastAPI can json-encode them
        for r in rows:
            ts = r.get("created_at")
            if isinstance(ts, datetime):
                r["created_at"] = ts.isoformat()
        return rows
    except Exception as e:
        print(f"[ANALYTICS] ui fetch failed: {e}")
        return []


# ----- Voice command intent detection (dashboard open/close) -----
#
# Conservative by design: must contain an UNAMBIGUOUS dashboard reference.
# We dropped previous loose subjects ("tableau", "stat", "panneau",
# "métrique", "analyse") because they are common French words that also
# show up in furniture/scene phrases ("le tableau au mur", "le panneau
# de cuisine", "fais une analyse"), short-circuiting the LLM on requests
# that have nothing to do with the dashboard.

import re

# Unambiguous subjects only — full words / multi-word phrases that
# only realistically mean "dashboard panel".
_DASH_SUBJECTS = (
    r"\bdashboard\b",
    r"\bstatistiques?\b",        # statistique / statistiques (singular & plural)
    r"\btableau de bord\b",
    r"\banalytics\b",
)

# Strong open verbs — must appear alongside a subject. We removed
# "voir/vois/donne/peux/pouvez/regarde" — they're too common.
_OPEN_CUES = (
    r"\bmontr(?:e|er|es|ez)\b",
    r"\baffich(?:e|er|es|ez)\b",
    r"\bouvr(?:e|ir|es|ez)\b",
    r"\bshow\b",
    r"\bopen\b",
    r"\bdisplay\b",
)

# Close verbs — same shape, stricter than before.
_CLOSE_CUES = (
    r"\bferm(?:e|er|es|ez)\b",
    r"\bcach(?:e|er|es|ez)\b",
    r"\bclose\b",
    r"\bhide\b",
)

_DASH_SUBJECT_RE = re.compile("|".join(_DASH_SUBJECTS), re.IGNORECASE)
_OPEN_CUE_RE = re.compile("|".join(_OPEN_CUES), re.IGNORECASE)
_CLOSE_CUE_RE = re.compile("|".join(_CLOSE_CUES), re.IGNORECASE)

# Bare phrases — entire transcription is just one of these.
_BARE_OPEN = {"dashboard", "statistiques", "statistique",
              "tableau de bord", "analytics"}
_BARE_CLOSE = {"ferme le dashboard", "ferme dashboard",
               "close dashboard", "ferme les statistiques"}


def detect_dashboard_intent(transcription: str) -> Optional[str]:
    """Return 'open' / 'close' / None based on transcription keywords.

    Conservative: requires an UNAMBIGUOUS dashboard subject ("dashboard",
    "statistiques", "tableau de bord", "analytics") to avoid eating
    ordinary build commands. Plain "tableau" / "stat" / "panneau" /
    "analyse" do NOT trigger — they're too common in French.
    """
    if not transcription:
        return None
    t = transcription.lower().strip().rstrip("?.! ")

    # Bare exact-match shortcuts
    if t in _BARE_OPEN:
        return "open"
    if t in _BARE_CLOSE:
        return "close"

    has_subject = bool(_DASH_SUBJECT_RE.search(t))
    if not has_subject:
        return None

    # Subject present — pick close if a close cue is there, otherwise
    # default to open. Subjects are unambiguous (dashboard / statistiques
    # / tableau de bord / analytics) so a bare mention is treated as a
    # request to see it.
    if _CLOSE_CUE_RE.search(t):
        return "close"
    return "open"
