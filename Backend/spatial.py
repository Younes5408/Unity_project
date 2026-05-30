import math
import unicodedata
from typing import List, Dict, Tuple, Set
from config import (GRID_SIZE, MIN_ROOM_GAP, EPSILON_COLLISION, ROOM_RATIOS, MIN_ROOM_SIZES, MAX_ROOM_SIZES, 
                    DEFAULT_HEIGHT, DOOR_WIDTH, WINDOW_WIDTH, WALL_THICKNESS, log_debug)

# ==========================================
# 3. GRILLE & GÉOMÉTRIE & PLACEMENT
# ==========================================
class GridMap:
    """Gestion intelligente de la grille pour éviter les chevauchements."""
    
    def __init__(self, world_width: float, world_depth: float):
        self.world_width = world_width
        self.world_depth = world_depth
        self.grid_width = int(world_width / GRID_SIZE) + 1
        self.grid_depth = int(world_depth / GRID_SIZE) + 1
        self.occupied_cells: Set[Tuple[int, int]] = set()
        log_debug("GRID", f"Grille créée: {self.grid_width}x{self.grid_depth} ({GRID_SIZE}m cells)")
    
    def world_to_grid(self, x: float, z: float) -> Tuple[int, int]:
        """Convertit coordonnées monde en coordonnées grille."""
        gx = math.floor(x / GRID_SIZE)
        gz = math.floor(z / GRID_SIZE)
        return (gx, gz)

    def reserve_point(self, x: float, z: float) -> None:
        """Réserve la cellule contenant le point (x,z) pour empêcher les placements dessus."""
        gx, gz = self.world_to_grid(x, z)
        self.occupied_cells.add((gx, gz))
    
    def grid_to_world(self, gx: int, gz: int) -> Tuple[float, float]:
        """Convertit coordonnées grille en coordonnées monde."""
        x = gx * GRID_SIZE
        z = gz * GRID_SIZE
        return (x, z)
    
    def reserve_cells(self, x: float, z: float, width: float, depth: float) -> bool:
        """Réserve les cellules pour une pièce."""
        gx1, gz1 = self.world_to_grid(x, z)
        gx2, gz2 = self.world_to_grid(x + width - EPSILON_COLLISION, z + depth - EPSILON_COLLISION)
        
        cells_to_reserve = []
        for gx in range(gx1, gx2 + 1):
            for gz in range(gz1, gz2 + 1):
                if (gx, gz) in self.occupied_cells:
                    log_debug("COLLISION", f"Cellule ({gx},{gz}) déjà occupée")
                    return False
                cells_to_reserve.append((gx, gz))
        
        for cell in cells_to_reserve:
            self.occupied_cells.add(cell)
        
        log_debug("GRID", f"✅ {len(cells_to_reserve)} cellules réservées")
        return True
    
    def is_area_free(self, x: float, z: float, width: float, depth: float) -> bool:
        """Vérifie si une zone est libre."""
        gx1, gz1 = self.world_to_grid(x, z)
        gx2, gz2 = self.world_to_grid(x + width - EPSILON_COLLISION, z + depth - EPSILON_COLLISION)
        
        for gx in range(gx1, gx2 + 1):
            for gz in range(gz1, gz2 + 1):
                if (gx, gz) in self.occupied_cells:
                    return False
        return True

class RoomPlacement:
    """Placement intelligent des pièces avec grille."""

    @staticmethod
    def _normalize_placement_hint(placement_hint: str) -> str:
        if not placement_hint:
            return ""

        hint = unicodedata.normalize("NFKD", placement_hint.lower().strip())
        hint = "".join(ch for ch in hint if not unicodedata.combining(ch))

        if any(token in hint for token in ["droite", "right"]):
            return "east"
        if any(token in hint for token in ["gauche", "left"]):
            return "west"
        if any(token in hint for token in ["devant", "avant", "front"]):
            return "north"
        if any(token in hint for token in ["derriere", "arriere", "back", "behind"]):
            return "south"
        if "north" in hint:
            return "north"
        if "south" in hint:
            return "south"
        if "east" in hint:
            return "east"
        if "west" in hint:
            return "west"

        return ""
    
    @staticmethod
    def get_dynamic_dimensions(room_type: str, total_surface: float) -> Dict:
        """Calcule les dimensions dynamiques avec contraintes min/max."""
        ratio = ROOM_RATIOS.get(room_type, 0.10)
        target_surface = total_surface * ratio
        
        # Appliquer les contraintes
        min_size = MIN_ROOM_SIZES.get(room_type, 2.0)
        max_size = MAX_ROOM_SIZES.get(room_type, 100.0)
        target_surface = max(min_size, min(max_size, target_surface))
        
        # Calculer dimensions
        width = round(target_surface ** 0.5, 2)
        depth = round(target_surface / width, 2)
        
        # Arrondir à la grille
        width = round(width / GRID_SIZE) * GRID_SIZE
        depth = round(depth / GRID_SIZE) * GRID_SIZE
        
        return {
            "width": float(width),
            "depth": float(depth),
            "height": DEFAULT_HEIGHT,
            "surface": round(width * depth, 2)
        }
    
    @staticmethod
    def find_position(room_type: str, dims: Dict, existing_rooms: List[Dict], grid: GridMap, placement_hint: str = "", connect_to: str = None, strict: bool = False) -> Tuple[float, float]:
        """Place la pièce organiquement contre les pièces existantes."""
        if not existing_rooms:
            return 0.0, 0.0
            
        w, d = dims["width"], dims["depth"]
        gap = MIN_ROOM_GAP
        preferred_side = RoomPlacement._normalize_placement_hint(placement_hint)
        
        # Prioriser le couloir, puis les pièces publiques, puis les privées
        sorted_rooms = sorted(existing_rooms, key=lambda r: 0 if r["type"] == "couloir" else (1 if r.get("zone") == "public" else 2))
        
        log_debug("PLACEMENT", f"Placement {room_type} ({w}x{d}m) - Organique")

        def build_candidates(rx: float, rz: float, rw: float, rd: float):
            base = [
                ("north", rx + rw / 2 - w / 2, rz + rd + gap),
                ("south", rx + rw / 2 - w / 2, rz - d - gap),
                ("east", rx + rw + gap, rz + rd / 2 - d / 2),
                ("west", rx - w - gap, rz + rd / 2 - d / 2),
            ]

            if preferred_side:
                if strict:
                    return [c for c in base if c[0] == preferred_side]
                ordered = [c for c in base if c[0] == preferred_side]
                ordered.extend(c for c in base if c[0] != preferred_side)
                return ordered

            return base
        
        # If connect_to is specified, only try that anchor (strict adjacency)
        anchors_to_try = sorted_rooms
        if connect_to:
            anchors_to_try = [r for r in sorted_rooms if r["id"] == connect_to]
            if not anchors_to_try:
                log_debug("PLACEMENT", f"⚠️ connect_to '{connect_to}' introuvable, fallback normal")
                anchors_to_try = sorted_rooms

        for anchor in anchors_to_try:
            rx, rz = anchor["position"]["x"], anchor["position"]["z"]
            rw, rd = anchor["dimensions"]["width"], anchor["dimensions"]["depth"]
            
            for _, tx, tz in build_candidates(rx, rz, rw, rd):
                if grid.is_area_free(tx, tz, w, d):
                    if grid.reserve_cells(tx, tz, w, d):
                        log_debug("PLACEMENT", f"✅ {room_type} placé près de {anchor['id']}: ({tx:.2f}, {tz:.2f})")
                        return tx, tz
        
        # FALLBACK : Chercher une zone libre n'importe où
        # Anchor the fallback search around the first sorted room (which is the
        # couloir if one exists — see sorted_rooms ordering above). Previously
        # this block referenced `hx`/`hz`/`hw` which were never defined in this
        # scope — leftover from an old refactor that removed a house-anchor var.
        # That would NameError on every fallback hit. Safe now because
        # `existing_rooms` is guaranteed non-empty (we early-returned at line
        # 127 if it was).
        log_debug("PLACEMENT", f"⚠️ Fallback recherche pour {room_type}")
        anchor = sorted_rooms[0]
        hx = anchor["position"]["x"]
        hz = anchor["position"]["z"]
        hw = anchor["dimensions"]["width"]

        search_x = hx - 20.0
        search_z = hz - 10.0
        while search_z < hz + 30.0:
            if grid.is_area_free(search_x, search_z, w, d):
                if grid.reserve_cells(search_x, search_z, w, d):
                    log_debug("PLACEMENT", f"✅ {room_type} placé en fallback: ({search_x:.2f}, {search_z:.2f})")
                    return search_x, search_z
            search_z += GRID_SIZE

        # Dernier recours (ne devrait presque jamais arriver ici)
        log_debug("PLACEMENT", f"❌ IMPOSSIBLE de placer {room_type}, fallback extrême")
        return hx + hw + 10.0, hz
    
    @staticmethod
    def find_adjacent_rooms(new_x: float, new_z: float, new_w: float, new_d: float, 
                           existing_rooms: List[Dict], tolerance: float = 0.2) -> List[str]:
        """Trouve les pièces adjacentes avec marge architecturale."""
        adjacent = []
        for r in existing_rooms:
            rx, rz = r["position"]["x"], r["position"]["z"]
            rw, rd = r["dimensions"]["width"], r["dimensions"]["depth"]
            
            # Adjacence horizontale
            if (abs(new_x - (rx + rw)) < tolerance or abs((new_x + new_w) - rx) < tolerance):
                if not (new_z + new_d < rz or new_z > rz + rd):
                    adjacent.append(r["id"])
            
            # Adjacence verticale
            elif (abs(new_z - (rz + rd)) < tolerance or abs((new_z + new_d) - rz) < tolerance):
                if not (new_x + new_w < rx or new_x > rx + rw):
                    adjacent.append(r["id"])
        
        return adjacent

def calculate_door_position(new_room: Dict, adj_room: Dict) -> Dict:
    """Calcule le point central exact du mur partagé pour y placer une porte."""
    x1, z1 = new_room["position"]["x"], new_room["position"]["z"]
    w1, d1 = new_room["dimensions"]["width"], new_room["dimensions"]["depth"]
    x2, z2 = adj_room["position"]["x"], adj_room["position"]["z"]
    w2, d2 = adj_room["dimensions"]["width"], adj_room["dimensions"]["depth"]
    
    ix_min = max(x1, x2)
    ix_max = min(x1 + w1, x2 + w2)
    iz_min = max(z1, z2)
    iz_max = min(z1 + d1, z2 + d2)
    
    if ix_max > ix_min:  # Chevauchement horizontal (faces nord/sud connectées)
        return {"x": float((ix_min + ix_max) / 2), "z": float(max(z1, z2))}
    if iz_max > iz_min:  # Chevauchement vertical (faces est/ouest connectées)
        return {"x": float(max(x1, x2)), "z": float((iz_min + iz_max) / 2)}
        
    return {"x": float(x1), "z": float(z1)}

def generate_doors(rooms: List[Dict]) -> Dict[str, List[Dict]]:
    """Génère les portes entre pièces adjacentes."""
    doors = {}
    for room in rooms:
        doors[room["id"]] = []
        for adjacent_id in room["metadata"]["adjacent_to"]:
            doors[room["id"]].append({
                "id": f"door_{room['id']}_to_{adjacent_id}",
                "connected_to": adjacent_id,
                "width": DOOR_WIDTH,
                "type": "internal_door"
            })
    return doors

def generate_windows(rooms: List[Dict]) -> Dict[str, List[Dict]]:
    """Génère les fenêtres sur les murs extérieurs uniquement."""
    windows = {}
    
    for room in rooms:
        windows[room["id"]] = []

        # Un couloir ne doit pas recevoir de fenêtres.
        if room.get("type") == "couloir" or room.get("zone") == "circulation":
            continue
        
        # Déterminer les murs extérieurs (non adjacents)
        rx = room["position"]["x"]
        rz = room["position"]["z"]
        rw = room["dimensions"]["width"]
        rd = room["dimensions"]["depth"]
        rh = room["dimensions"]["height"]
        
        # Trouver les murs adjacents
        north_adjacent = False
        south_adjacent = False
        east_adjacent = False
        west_adjacent = False
        
        for other in rooms:
            if other["id"] == room["id"]:
                continue
            
            ox = other["position"]["x"]
            oz = other["position"]["z"]
            ow = other["dimensions"]["width"]
            od = other["dimensions"]["depth"]
            
            eps = 0.1
            
            # Vérifier si c'est adjacent au nord
            if abs(rz + rd - oz) <= eps:
                ix0 = max(rx, ox)
                ix1 = min(rx + rw, ox + ow)
                if ix1 - ix0 > 0.5:
                    north_adjacent = True
            
            # Vérifier si c'est adjacent au sud
            if abs(oz + od - rz) <= eps:
                ix0 = max(rx, ox)
                ix1 = min(rx + rw, ox + ow)
                if ix1 - ix0 > 0.5:
                    south_adjacent = True
            
            # Vérifier si c'est adjacent à l'est
            if abs(rx + rw - ox) <= eps:
                iz0 = max(rz, oz)
                iz1 = min(rz + rd, oz + od)
                if iz1 - iz0 > 0.5:
                    east_adjacent = True
            
            # Vérifier si c'est adjacent à l'ouest
            if abs(ox + ow - rx) <= eps:
                iz0 = max(rz, oz)
                iz1 = min(rz + rd, oz + od)
                if iz1 - iz0 > 0.5:
                    west_adjacent = True
        
        # Placer les fenêtres sur les murs extérieurs
        window_count = 1
        
        # Nord (extérieur)
        if not north_adjacent and rd > 2.0:
            window_pos = {
                "x": float(rx + rw / 2.0),
                "y": float(rh / 2.0 + 0.5),
                "z": float(rz + rd)
            }
            windows[room["id"]].append({
                "id": f"window_{room['id']}_{window_count}",
                "width": WINDOW_WIDTH,
                "height": 1.2,
                "position": window_pos,
                "wall": "north",
                "type": "double_window"
            })
            window_count += 1
        
        # Sud (extérieur)
        if not south_adjacent and rd > 2.0:
            window_pos = {
                "x": float(rx + rw / 2.0),
                "y": float(rh / 2.0 + 0.5),
                "z": float(rz)
            }
            windows[room["id"]].append({
                "id": f"window_{room['id']}_{window_count}",
                "width": WINDOW_WIDTH,
                "height": 1.2,
                "position": window_pos,
                "wall": "south",
                "type": "double_window"
            })
            window_count += 1
        
        # Est (extérieur)
        if not east_adjacent and rw > 2.0:
            window_pos = {
                "x": float(rx + rw),
                "y": float(rh / 2.0 + 0.5),
                "z": float(rz + rd / 2.0)
            }
            windows[room["id"]].append({
                "id": f"window_{room['id']}_{window_count}",
                "width": WINDOW_WIDTH,
                "height": 1.2,
                "position": window_pos,
                "wall": "east",
                "type": "double_window"
            })
            window_count += 1
        
        # Ouest (extérieur)
        if not west_adjacent and rw > 2.0:
            window_pos = {
                "x": float(rx),
                "y": float(rh / 2.0 + 0.5),
                "z": float(rz + rd / 2.0)
            }
            windows[room["id"]].append({
                "id": f"window_{room['id']}_{window_count}",
                "width": WINDOW_WIDTH,
                "height": 1.2,
                "position": window_pos,
                "wall": "west",
                "type": "double_window"
            })
    
    return windows

def generate_walls(room: Dict, existing_rooms: List[Dict]) -> List[Dict]:
    """Génère les murs d'une pièce."""
    walls = []
    rx, rz = room["position"]["x"], room["position"]["z"]
    rw, rd = room["dimensions"]["width"], room["dimensions"]["depth"]
    
    # 4 murs
    walls.append({
        "id": f"wall_{room['id']}_north",
        "orientation": "north",
        "length": rw,
        "thickness": WALL_THICKNESS,
        "position": {"x": rx, "z": rz + rd}
    })
    walls.append({
        "id": f"wall_{room['id']}_south",
        "orientation": "south",
        "length": rw,
        "thickness": WALL_THICKNESS,
        "position": {"x": rx, "z": rz}
    })
    walls.append({
        "id": f"wall_{room['id']}_east",
        "orientation": "east",
        "length": rd,
        "thickness": WALL_THICKNESS,
        "position": {"x": rx + rw, "z": rz}
    })
    walls.append({
        "id": f"wall_{room['id']}_west",
        "orientation": "west",
        "length": rd,
        "thickness": WALL_THICKNESS,
        "position": {"x": rx, "z": rz}
    })
    
    return walls
