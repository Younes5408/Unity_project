import os
from dotenv import load_dotenv

# ==========================================
# 0. CONFIGURATION & MODE DEBUG
# ==========================================
load_dotenv()
api_key = os.getenv("LLM_API_KEY")
DEBUG_MODE = True

def log_debug(category: str, message: str):
    """Log formaté avec catégorie."""
    if DEBUG_MODE:
        try:
            print(f"[{category:12}] {message}")
        except UnicodeEncodeError:
            print(f"[{category:12}] {message.encode('ascii', 'replace').decode()}")

# ==========================================
# 1. CONSTANTES GLOBALES
# ==========================================
GRID_SIZE = 0.5
MIN_ROOM_GAP = 0.0
WALL_THICKNESS = 0.15
EPSILON_COLLISION = 0.01
DEFAULT_HEIGHT = 3.0
ROOF_THICKNESS = 0.1

# Room dimensions
ROOM_RATIOS = {
    "salon": 0.25,
    "chambre": 0.15,
    "cuisine": 0.12,
    "hall_nuit": 0.08,
    "couloir": 0.10,
    "salle de bain": 0.06,
}

MIN_ROOM_SIZES = {
    "salon": 20.0,
    "chambre": 10.0,
    "cuisine": 8.0,
    "salle de bain": 4.0,
    "hall_nuit": 3.0,
    "couloir": 2.0,
}

MAX_ROOM_SIZES = {
    "salon": 60.0,
    "chambre": 25.0,
    "cuisine": 20.0,
    "salle de bain": 12.0,
    "hall_nuit": 10.0,
    "couloir": 50.0,
}

# Zones architecturales
ROOM_ZONES = {
    "salon": "public",
    "cuisine": "public",
    "couloir": "circulation",
    "chambre": "private",
    "salle de bain": "private",
    "hall_nuit": "private",
}

# Règles architecturales
ARCHITECTURAL_RULES = {
    "cuisine": {"must_be_near": ["salon"], "must_not_be_near": ["chambre"]},
    "chambre": {"must_be_near": ["salle de bain", "hall_nuit"], "must_not_be_near": ["salon"]},
    "salle de bain": {"must_be_near": ["chambre"], "must_not_be_near": ["salon", "cuisine"]},
    "salon": {"is_central": True, "must_be_near": ["cuisine"]},
}

# Windows/Doors
MIN_WINDOWS_PER_BEDROOM = 1
WINDOW_WIDTH = 1.5
DOOR_WIDTH = 0.9

def get_room_zone(room_type: str) -> str:
    """Retourne la zone d'une pièce."""
    return ROOM_ZONES.get(room_type, "unknown")
