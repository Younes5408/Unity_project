import os
from pymongo import MongoClient
from bson import ObjectId
from typing import Dict, Optional
from config import log_debug

# ==========================================
# 2. CONNEXION DB & UTILITAIRES
# ==========================================
try:
    mongo_uri = os.getenv("MONGO_URI", "mongodb://localhost:27017/")
    mongo_client = MongoClient(
        mongo_uri,
        serverSelectionTimeoutMS=5000,
        socketTimeoutMS=10000,
        connectTimeoutMS=5000
    )
    db = mongo_client["house_design"]
    rooms_collection = db["rooms"]
    house_collection = db["houses"]
    placements_collection = db["placements"]
    teleports_collection = db["teleports"]
    scene_objects_collection = db["scene_objects"]

    # Indexes — make tool queries (find_one by id / house_id) and pending-placement
    # polling fast. create_index is idempotent: no-op if the index already exists.
    try:
        rooms_collection.create_index("id", background=True)
        house_collection.create_index("house_id", background=True)
        placements_collection.create_index(
            [("status", 1), ("category", 1)], background=True
        )
        teleports_collection.create_index("status", background=True)
        scene_objects_collection.create_index("name_lower", unique=True, background=True)
    except Exception as idx_err:
        print(f"[DATABASE] index creation skipped: {idx_err}")

    log_debug("DATABASE", "MongoDB connecte OK")
except Exception as e:
    print(f"Erreur MongoDB: {e}")
    rooms_collection = None
    house_collection = None
    placements_collection = None
    teleports_collection = None
    scene_objects_collection = None

def clear_database():
    """Vide les collections rooms et houses."""
    if rooms_collection is not None and house_collection is not None:
        rooms_collection.delete_many({})
        house_collection.delete_many({})

def clean_doc(doc: Optional[Dict]) -> Dict:
    """Nettoie un document MongoDB."""
    if doc is None:
        return {}
    cleaned = {}
    for k, v in doc.items():
        if k == "_id":
            continue
        if isinstance(v, ObjectId):
            cleaned[k] = str(v)
        elif isinstance(v, dict):
            cleaned[k] = clean_doc(v)
        elif isinstance(v, list):
            cleaned[k] = [clean_doc(i) if isinstance(i, dict) else i for i in v]
        else:
            cleaned[k] = v
    return cleaned
