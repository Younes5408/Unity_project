"""
Action history tracking for undo/redo functionality.
Stores snapshots of room and house state for reverting changes.
"""

from datetime import datetime
from typing import Optional, List, Dict
from database import rooms_collection, house_collection, clean_doc
from config import log_debug

try:
    _history_collection = None
    if rooms_collection is not None:
        # Access the same MongoDB database as rooms/houses
        from database import mongo_client
        _history_collection = mongo_client["house_design"]["actions_history"]
except Exception as e:
    log_debug("HISTORY", f"⚠️ History collection unavailable: {e}")
    _history_collection = None


def record_action(action_type: str, metadata: Optional[Dict] = None, undoable: bool = True) -> bool:
    """
    Record an action to the history for potential undo.
    Stores current room and house state as a snapshot.
    """
    if _history_collection is None:
        return False

    try:
        # Capture current state
        rooms = [clean_doc(r) for r in rooms_collection.find({}, projection={"_id": 0})]
        house = clean_doc(house_collection.find_one({}, projection={"_id": 0}))

        snapshot = {
            "timestamp": datetime.now().isoformat(),
            "action": action_type,
            "undoable": undoable,
            "metadata": metadata or {},
            "rooms": rooms,
            "house": house
        }

        result = _history_collection.insert_one(snapshot)
        log_debug("HISTORY", f"✅ Recorded: {action_type}")
        return True
    except Exception as e:
        log_debug("HISTORY", f"❌ Failed to record: {e}")
        return False


def get_last_n_actions(n: int = 10) -> List[Dict]:
    """Retrieve the last n actions from history."""
    if _history_collection is None:
        return []

    try:
        actions = list(_history_collection.find(
            {},
            projection={"_id": 0, "rooms": 0, "house": 0},  # Exclude large payloads
            sort=[("timestamp", -1)],
            limit=n
        ))
        return actions
    except Exception as e:
        log_debug("HISTORY", f"❌ Failed to retrieve: {e}")
        return []


def pop_last_undoable_action() -> Optional[Dict]:
    """
    Pop the last undoable action from history and return its snapshot.
    Used for undo functionality.
    """
    if _history_collection is None:
        return None

    try:
        # Find the last undoable action
        action = _history_collection.find_one(
            {"undoable": True},
            sort=[("timestamp", -1)]
        )

        if action:
            _history_collection.delete_one({"_id": action["_id"]})
            log_debug("HISTORY", f"✅ Popped undoable action: {action.get('action', 'unknown')}")
            return clean_doc(action)
        else:
            log_debug("HISTORY", "⚠️ No undoable actions found")
            return None
    except Exception as e:
        log_debug("HISTORY", f"❌ Failed to pop: {e}")
        return None


def clear_history() -> bool:
    """Clear all history (for testing or reset)."""
    if _history_collection is None:
        return False

    try:
        _history_collection.delete_many({})
        log_debug("HISTORY", "✅ History cleared")
        return True
    except Exception as e:
        log_debug("HISTORY", f"❌ Failed to clear: {e}")
        return False
