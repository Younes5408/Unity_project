import json
import contextvars
from datetime import datetime
from typing import Optional
from config import log_debug, DEFAULT_HEIGHT, WALL_THICKNESS, DOOR_WIDTH, WINDOW_WIDTH, get_room_zone
from database import rooms_collection, house_collection, clean_doc, clear_database
from spatial import GridMap, RoomPlacement, generate_walls, calculate_door_position, generate_windows
from validator import HouseValidator, score_layout

# Request-scoped build position (set by api.py before agent.arun, read by initialiser_maison)
build_position_ctx = contextvars.ContextVar("build_position", default=(0.0, 0.0))

# Request-scoped house_id (set by api.py per request, read by placer_objet so every
# placement is tagged with the house it belongs to — enables scoped Supprimer + replay).
house_id_ctx = contextvars.ContextVar("house_id", default="maison_001")


def set_build_position(x: float, z: float) -> None:
    build_position_ctx.set((float(x), float(z)))


def set_house_id(house_id: str) -> None:
    house_id_ctx.set(str(house_id) if house_id else "maison_001")

# ==========================================
# 7. OUTILS DE L'AGENT (TOOLS)
# ==========================================
def initialiser_maison(surface_totale: float) -> str:
    """Initialise une nouvelle maison avec couloir central.

    The corridor position is taken from build_position_ctx (set per-request
    by api.py to the BUILD_AHEAD coordinates — 3m in front of the player).
    The LLM never has to manage coordinates.
    """
    try:
        surface_totale = float(surface_totale)
    except (ValueError, TypeError):
        return json.dumps({"status": "error", "message": "surface_totale doit être un nombre"})

    position_x, position_z = build_position_ctx.get()

    if rooms_collection is None or house_collection is None:
        return json.dumps({"status": "error", "message": "MongoDB non connecté"})

    clear_database()

    log_debug("INIT", f"Création maison {surface_totale}m² à ({position_x}, {position_z})")

    hw = 2.0
    hd = round(surface_totale * 0.12 / hw, 2)
    house_id = "maison_001"

    couloir = {
        "id": "couloir_1",
        "type": "couloir",
        "label": "Couloir Central",
        "position": {"x": position_x, "y": 0.0, "z": position_z},
        "dimensions": {"width": float(hw), "depth": float(hd), "height": DEFAULT_HEIGHT},
        "rotation": {"x": 0.0, "y": 0.0, "z": 0.0},
        "visual": {
            "floor_color": "#A0A0A0",
            "wall_color": "#FFFFFF",
            "opacity": 1.0,
            "wall_thickness": WALL_THICKNESS
        },
        "metadata": {
            "surface": round(hw * hd, 2),
            "adjacent_to": [],
            "has_door": True,
            "privacy_level": 0,
            "natural_light": 0,
            "circulation_score": 100,
            "architectural_score": 0,
            "zone": "circulation"
        },
        "doors": [
            {
                "id": "door_couloir_1_to_outside",
                "connected_to": "outside",
                "width": DOOR_WIDTH,
                "type": "entry_door",
                "position": {"x": float(position_x + hw / 2.0), "z": float(position_z)}
            }
        ],
        "windows": [],
        "walls": generate_walls({"id": "couloir_1", "position": {"x": position_x, "z": position_z},
                                "dimensions": {"width": hw, "depth": hd}}, [])
    }
    
    house_collection.insert_one({
        "house_id": house_id,
        "surface_totale": surface_totale,
        "surface_utilisee": round(hw * hd, 2),
        "created_at": datetime.now().isoformat(),
        "layout_valid": True,
        "validation_errors": [],
        "orientation": "north",
        "style": "modern"
    })
    rooms_collection.insert_one(couloir)
    
    log_debug("INIT", "✅ Maison initialisée")
    
    return json.dumps({
        "status": "success",
        "action": "init_house",
        "house_id": house_id,
        "timestamp": datetime.now().isoformat(),
        "rooms": [clean_doc(couloir)],
        "metadata": {
            "surface_totale": surface_totale,
            "surface_utilisee": round(hw * hd, 2),
            "unite": "meters",
            "layout_valid": True,
            "validation_errors": []
        }
    }, ensure_ascii=False)

def ajouter_piece_unity(room_type: str, label: Optional[str] = None, placement_hint: Optional[str] = None, connect_to: Optional[str] = None) -> str:
    """Ajoute une pièce avec validation complète."""
    if rooms_collection is None or house_collection is None:
        return json.dumps({"status": "error", "message": "MongoDB non connecté"})
    
    house = clean_doc(house_collection.find_one({}, sort=[("created_at", -1)], projection={"_id": 0}))
    if not house:
        return json.dumps({"status": "error", "message": "Initialise d'abord la maison"})
    
    existing = [clean_doc(r) for r in rooms_collection.find({}, projection={"_id": 0})]
    
    log_debug("ROOM_ADD", f"Ajout de {room_type}")
    
    # Créer grille
    grid = GridMap(100.0, 100.0)  # Monde assez grand
    for room in existing:
        x, z = room["position"]["x"], room["position"]["z"]
        w, d = room["dimensions"]["width"], room["dimensions"]["depth"]
        grid.reserve_cells(x, z, w, d)
        # Reserve door coordinates so future placements don't overlap doors
        for door in room.get("doors", []):
            dp = door.get("position")
            if isinstance(dp, dict) and "x" in dp and "z" in dp:
                grid.reserve_point(dp["x"], dp["z"])
    
    # Calculer dimensions
    dims = RoomPlacement.get_dynamic_dimensions(room_type, house["surface_totale"])
    strict_flag = bool(placement_hint or connect_to)
    tx, tz = RoomPlacement.find_position(room_type, dims, existing, grid, placement_hint or "", connect_to=connect_to, strict=strict_flag)
    
    count = len([r for r in existing if r["type"] == room_type]) + 1
    room_id = f"{room_type}_{count}"
    
    colors = {
        "salon": "#E8D5C4",
        "chambre": "#C4D7E8",
        "cuisine": "#D5E8C4",
        "salle de bain": "#E8C4D7",
        "hall_nuit": "#F0F0F0"
    }
    
    adjacent_rooms = RoomPlacement.find_adjacent_rooms(tx, tz, dims["width"], dims["depth"], existing)
    if not adjacent_rooms:
        adjacent_rooms = ["couloir_1"]

    # Idempotency: if the user asked to connect to a specific room and such a
    # room already exists connected to that target, do not create a duplicate.
    if connect_to:
        exists_connected = next((r for r in existing if r.get("type") == room_type and any(d.get("connected_to") == connect_to for d in r.get("doors", []))), None)
        if exists_connected:
            log_debug("ROOM_ADD", f"⚠️ Request ignored — a {room_type} already connected to {connect_to} ({exists_connected['id']})")
            # Return current layout without creating a new room
            updated_rooms = [clean_doc(r) for r in rooms_collection.find({}, projection={"_id": 0})]
            layout_score = score_layout(updated_rooms)
            lightweight_rooms = []
            for room in updated_rooms:
                lightweight_room = {
                    "id": room["id"],
                    "type": room["type"],
                    "label": room["label"],
                    "position": room["position"],
                    "dimensions": room["dimensions"],
                    "visual": room["visual"],
                    "zone": room.get("zone", "unknown"),
                    "metadata": room.get("metadata", {}),
                    "doors": room.get("doors", []),
                    "windows": room.get("windows", []),
                    "num_doors": len(room.get("doors", [])),
                    "num_windows": len(room.get("windows", [])),
                    "num_walls": len(room.get("walls", []))
                }
                lightweight_rooms.append(lightweight_room)

            return json.dumps({
                "status": "success",
                "action": "add_room",
                "house_id": house["house_id"],
                "timestamp": datetime.now().isoformat(),
                "rooms": lightweight_rooms,
                "metadata": {
                    "surface_totale": house["surface_totale"],
                    "surface_utilisee": round(house.get("surface_utilisee", 0), 2),
                    "unite": "meters",
                    "layout_valid": True,
                    "validation_errors": [],
                    "layout_score": round(layout_score, 1),
                    "orientation": house.get("orientation", "north"),
                    "style": house.get("style", "modern")
                }
            }, ensure_ascii=False)
    
    # Créer la pièce
    new_room = {
        "id": room_id,
        "type": room_type,
        "label": label or f"{room_type.capitalize()} {count}",
        "position": {"x": float(tx), "y": 0.0, "z": float(tz)},
        "dimensions": {
            "width": float(dims["width"]),
            "depth": float(dims["depth"]),
            "height": float(dims["height"])
        },
        "rotation": {"x": 0.0, "y": 0.0, "z": 0.0},
        "visual": {
            "floor_color": colors.get(room_type, "#FFFFFF"),
            "wall_color": "#FFFFFF",
            "opacity": 1.0,
            "wall_thickness": WALL_THICKNESS
        },
        "zone": get_room_zone(room_type),
        "metadata": {
            "surface": float(dims["surface"]),
            "adjacent_to": adjacent_rooms,
            "has_door": len(adjacent_rooms) > 0,
            "door_position": "front",
            "privacy_level": 2 if get_room_zone(room_type) == "private" else 0,
            "natural_light": 1 if room_type == "chambre" else 0,
            "circulation_score": 50,
            "architectural_score": 0
        },
        "doors": [],
        "windows": [],
        "walls": []
    }
    
    # Générer portes et fenêtres pour cette pièce
    if adjacent_rooms:
        new_room["doors"] = []
        for adj in adjacent_rooms:
            adj_room = next((r for r in existing if r["id"] == adj), None)
            door_pos = {"x": 0.0, "z": 0.0}
            if adj_room:
                door_pos = calculate_door_position(new_room, adj_room)
                
            new_room["doors"].append({
                "id": f"door_{room_id}_to_{adj}",
                "connected_to": adj,
                "width": DOOR_WIDTH,
                "type": "internal_door",
                "position": door_pos
            })
    
    if room_type == "chambre":
        new_room["windows"] = [
            {
                "id": f"window_{room_id}_1",
                "width": WINDOW_WIDTH,
                "position": "north_wall",
                "type": "double_window"
            }
        ]
    
    new_room["walls"] = generate_walls(new_room, existing)
    
    # Valider
    validator = HouseValidator(house["surface_totale"])
    all_rooms_to_validate = existing + [new_room]
    
    if not validator.validate_all(all_rooms_to_validate):
        log_debug("VALIDATION", f"❌ Validation échouée")
        return json.dumps({
            "status": "error",
            "message": "Placement invalide",
            "errors": validator.errors,
            "warnings": validator.warnings
        })
    
    # Insérer en DB
    try:
        rooms_collection.insert_one(new_room)

        # Ajouter la porte réciproque dans les pièces adjacentes déjà existantes.
        for adj in adjacent_rooms:
            adj_room = next((r for r in existing if r["id"] == adj), None)
            if not adj_room:
                continue

            reciprocal_pos = calculate_door_position(adj_room, new_room)
            reciprocal_door = {
                "id": f"door_{adj}_to_{room_id}",
                "connected_to": room_id,
                "width": DOOR_WIDTH,
                "type": "internal_door",
                "position": reciprocal_pos
            }

            rooms_collection.update_one(
                {"id": adj},
                {
                    "$push": {"doors": reciprocal_door},
                    "$set": {"metadata.has_door": True}
                }
            )

        house_collection.update_one(
            {"house_id": house["house_id"]},
            {
                "$inc": {"surface_utilisee": dims["surface"]},
                "$set": {
                    "layout_valid": True,
                    "validation_errors": []
                }
            }
        )
        log_debug("ROOM_ADD", f"✅ {room_type} ajouté en DB")
    except Exception as e:
        log_debug("ERROR", f"Erreur DB: {str(e)}")
        return json.dumps({"status": "error", "message": f"Erreur DB: {str(e)}"})
    
    # Générer et mettre à jour les fenêtres pour toutes les pièces
    updated_rooms = [clean_doc(r) for r in rooms_collection.find({}, projection={"_id": 0})]
    windows_dict = generate_windows(updated_rooms)
    
    for room_id, room_windows in windows_dict.items():
        rooms_collection.update_one(
            {"id": room_id},
            {"$set": {"windows": room_windows}}
        )
    
    # Récupérer état final
    updated_rooms = [clean_doc(r) for r in rooms_collection.find({}, projection={"_id": 0})]
    updated_surface = house["surface_utilisee"] + dims["surface"]
    layout_score = score_layout(updated_rooms)
    
    # Retourner une réponse LÉGÈRE (sans murs/fenêtres/portes détaillés)
    lightweight_rooms = []
    for room in updated_rooms:
        lightweight_room = {
            "id": room["id"],
            "type": room["type"],
            "label": room["label"],
            "position": room["position"],
            "dimensions": room["dimensions"],
            "visual": room["visual"],
            "zone": room.get("zone", "unknown"),
            "metadata": room["metadata"],
            "doors": room.get("doors", []),
            "windows": room.get("windows", []),
            "num_doors": len(room.get("doors", [])),
            "num_windows": len(room.get("windows", [])),
            "num_walls": len(room.get("walls", []))
        }
        lightweight_rooms.append(lightweight_room)
    
    return json.dumps({
        "status": "success",
        "action": "add_room",
        "house_id": house["house_id"],
        "timestamp": datetime.now().isoformat(),
        "rooms": lightweight_rooms,
        "metadata": {
            "surface_totale": house["surface_totale"],
            "surface_utilisee": round(updated_surface, 2),
            "unite": "meters",
            "layout_valid": True,
            "validation_errors": [],
            "layout_score": round(layout_score, 1),
            "orientation": house.get("orientation", "north"),
            "style": house.get("style", "modern")
        }
    }, ensure_ascii=False)

def supprimer_piece(room_id: str) -> str:
    """Supprime une pièce de la maison."""
    if rooms_collection is None or house_collection is None:
        return json.dumps({"status": "error", "message": "MongoDB non connecté"})

    room = clean_doc(rooms_collection.find_one({"id": room_id}, projection={"_id": 0}))
    if not room:
        return json.dumps({"status": "error", "message": f"Pièce {room_id} non trouvée"})

    if room["type"] == "couloir":
        return json.dumps({"status": "error", "message": "Impossible de supprimer le couloir principal"})

    # Supprimer la pièce
    rooms_collection.delete_one({"id": room_id})

    # Supprimer les portes réciproques dans les pièces adjacentes
    for adj_id in room.get("metadata", {}).get("adjacent_to", []):
        rooms_collection.update_one(
            {"id": adj_id},
            {"$pull": {"doors": {"connected_to": room_id}}}
        )

    # Mettre à jour la surface utilisée
    house = clean_doc(house_collection.find_one({}, projection={"_id": 0}))
    if house:
        house_collection.update_one(
            {"house_id": house["house_id"]},
            {"$inc": {"surface_utilisee": -room.get("metadata", {}).get("surface", 0)}}
        )

    log_debug("ROOM_DEL", f"✅ {room_id} supprimé")

    # Retourner l'état final
    updated_rooms = [clean_doc(r) for r in rooms_collection.find({}, projection={"_id": 0})]
    layout_score = score_layout(updated_rooms)

    return json.dumps({
        "status": "success",
        "action": "delete_room",
        "deleted_room_id": room_id,
        "timestamp": datetime.now().isoformat(),
        "rooms": updated_rooms,
        "metadata": {
            "surface_totale": house.get("surface_totale", 0),
            "surface_utilisee": round(house.get("surface_utilisee", 0) - room.get("metadata", {}).get("surface", 0), 2),
            "unite": "meters",
            "layout_score": round(layout_score, 1)
        }
    }, ensure_ascii=False)

def analyser_conception() -> str:
    """Analyse la conception de la maison et retourne un score."""
    if rooms_collection is None or house_collection is None:
        return json.dumps({"status": "error", "message": "MongoDB non connecté"})

    rooms = [clean_doc(r) for r in rooms_collection.find({}, projection={"_id": 0})]
    house = clean_doc(house_collection.find_one({}, projection={"_id": 0}))

    if not rooms:
        return json.dumps({"status": "error", "message": "Aucune pièce trouvée"})

    # Valider et scorer la conception
    validator = HouseValidator(house.get("surface_totale", 100))
    validator.validate_all(rooms)
    layout_score = score_layout(rooms)

    analysis = {
        "layout_score": round(layout_score, 1),
        "num_rooms": len(rooms),
        "surface_totale": house.get("surface_totale", 0),
        "surface_utilisee": house.get("surface_utilisee", 0),
        "validation_errors": validator.errors,
        "validation_warnings": validator.warnings,
        "room_summary": [{"id": r["id"], "type": r["type"], "surface": r.get("metadata", {}).get("surface", 0)} for r in rooms]
    }

    log_debug("ANALYSIS", f"Score: {layout_score:.1f}/100")

    return json.dumps({
        "status": "success",
        "action": "analyze_design",
        "timestamp": datetime.now().isoformat(),
        "analysis": analysis
    }, ensure_ascii=False)

def verifier_espace_disponible(player_x: float, player_z: float, surface_totale: float) -> str:
    """Vérifie si l'espace est disponible pour une nouvelle maison."""
    if rooms_collection is None:
        return json.dumps({"status": "error", "message": "MongoDB non connecté"})

    try:
        player_x = float(player_x) if player_x is not None else 0.0
        player_z = float(player_z) if player_z is not None else 0.0
        surface_totale = float(surface_totale)
    except (ValueError, TypeError):
        return json.dumps({"status": "error", "message": "Paramètres invalides"})

    # Estimation de l'empreinte au sol (approximation carré)
    estimated_footprint = (surface_totale * 0.7) ** 0.5  # Ratio ~0.7 pour approx carré
    estimated_footprint = round(estimated_footprint / 0.5) * 0.5  # Arrondir à la grille 0.5m

    # Vérifier si la maison tient dans les limites de la scène
    scene_bounds = 40.0  # Limites approximatives de la scène
    if abs(player_x) > scene_bounds - estimated_footprint or abs(player_z) > scene_bounds - estimated_footprint:
        return json.dumps({
            "status": "insufficient",
            "available": False,
            "reason": f"Terrain hors limites (scène ~{scene_bounds*2}m × {scene_bounds*2}m)",
            "player_pos": [player_x, player_z],
            "estimated_footprint": f"{estimated_footprint:.1f}m x {estimated_footprint:.1f}m"
        })

    # Vérifier les pièces existantes à proximité
    existing_rooms = [clean_doc(r) for r in rooms_collection.find({}, projection={"_id": 0})]
    clearance_radius = estimated_footprint / 2.0 + 2.0  # Rayon de sécurité

    for room in existing_rooms:
        room_x = room.get("position", {}).get("x", 0)
        room_z = room.get("position", {}).get("z", 0)
        distance = ((player_x - room_x) ** 2 + (player_z - room_z) ** 2) ** 0.5

        if distance < clearance_radius:
            return json.dumps({
                "status": "insufficient",
                "available": False,
                "reason": f"Zone occupée à {distance:.1f}m ({room.get('type')} {room.get('id')}). Déplacez-vous vers un terrain libre.",
                "player_pos": [player_x, player_z],
                "occupied_room": {"id": room.get("id"), "type": room.get("type"), "distance": round(distance, 1)}
            })

    log_debug("SPACE_CHECK", f"✅ Espace disponible à ({player_x}, {player_z}) pour {surface_totale}m²")

    return json.dumps({
        "status": "ok",
        "available": True,
        "estimated_footprint": f"{estimated_footprint:.1f}m x {estimated_footprint:.1f}m",
        "player_pos": [player_x, player_z],
        "timestamp": datetime.now().isoformat()
    }, ensure_ascii=False)

def placer_objet(object_name: str, rotation_y: float = 0.0) -> str:
    """Place un prefab Unity par son nom à la position BUILD_AHEAD du joueur.

    object_name: nom de l'objet (ex: "maison", "maison3", "route", "barriere", "lampadaire", "sol")
    rotation_y: rotation en degrés autour de l'axe Y (0 par défaut)
    """
    from database import placements_collection
    from prefab_catalog import resolve_prefab, get_available_names

    if placements_collection is None:
        return json.dumps({"status": "error", "message": "MongoDB non connect"})

    if not object_name or not isinstance(object_name, str):
        return json.dumps({"status": "error", "message": "object_name requis"})

    resolved = resolve_prefab(object_name)
    if not resolved:
        available = ", ".join(get_available_names())
        return json.dumps({"status": "error", "message": f"Objet '{object_name}' inconnu. Disponibles: {available}"}, ensure_ascii=False)

    position_x, position_z = build_position_ctx.get()
    placement_id = f"obj_{object_name}_{datetime.now().strftime('%Y%m%d%H%M%S%f')}"

    # Set specific placement strategies for lights and furniture
    if object_name in ("lampe", "lampe1", "lampe2", "lampe3"):
        placement_strategy = "ceiling_aware"
    elif object_name == "lampadaire" or resolved["category"] == "furniture":
        placement_strategy = "floor_aware"
    else:
        placement_strategy = "default"

    placement = {
        "id": placement_id,
        "house_id": house_id_ctx.get(),
        "object_type": object_name,
        "prefab": resolved["prefab"],
        "label": object_name,
        "category": resolved["category"],
        "placement_strategy": placement_strategy,
        "position": {"x": float(position_x), "y": 0.0, "z": float(position_z)},
        "rotation_y": float(rotation_y),
        "status": "pending",
        "created_at": datetime.now().isoformat()
    }

    try:
        placements_collection.insert_one(placement)
    except Exception as e:
        return json.dumps({"status": "error", "message": f"Erreur DB: {str(e)[:100]}"})

    log_debug("PLACE_OBJ", f"{object_name} ({resolved['prefab']}) en attente a ({position_x}, {position_z})")

    return json.dumps({
        "status": "ok",
        "action": "place_object",
        "placement_id": placement_id,
        "object_type": object_name,
        "prefab": resolved["prefab"],
        "position": {"x": position_x, "y": 0.0, "z": position_z},
        "message": f"'{object_name}' sera place a ({position_x:.1f}, {position_z:.1f})"
    }, ensure_ascii=False)


def teleporter_joueur(destination: str) -> str:
    """Téléporte le joueur vers une maison ou une pièce par son nom.

    destination: ex. "maison1", "maison 2", "house3", "salon", "chambre"
    Résout le nom en coordonnées depuis scene_objects (maisons baked-in) ou
    placements (maisons placées par l'IA), puis écrit une demande de téléportation
    en MongoDB pour que Unity l'exécute.
    """
    from database import teleports_collection, scene_objects_collection, placements_collection

    if not destination or not isinstance(destination, str):
        return json.dumps({"status": "error", "message": "destination requise"})

    # Normalise: "maison 2" / "maison2" / "house2" / "House 2" → "house2"
    import re as _re
    norm = destination.strip().lower()
    norm = _re.sub(r'\s+', '', norm)            # remove spaces
    norm = norm.replace('maison', 'house')      # french → english key

    target_x, target_z = None, None
    label = destination

    # 1. Look up baked-in scene objects registered by Unity
    if scene_objects_collection is not None:
        obj = scene_objects_collection.find_one(
            {"name_lower": norm},
            projection={"_id": 0}
        )
        if obj:
            target_x = float(obj["x"])
            target_z = float(obj["z"])
            label = obj.get("name", destination)

    # 2. Fallback: search placements for AI-placed houses
    if target_x is None and placements_collection is not None:
        for p in placements_collection.find({"status": "placed"}, projection={"_id": 0}):
            pname = _re.sub(r'\s+', '', p.get("object_type", "").lower()).replace('maison', 'house')
            if pname == norm or pname.startswith(norm):
                target_x = float(p["position"]["x"])
                target_z = float(p["position"]["z"])
                label = p.get("object_type", destination)
                break

    if target_x is None:
        return json.dumps({
            "status": "error",
            "message": f"Destination '{destination}' inconnue. Essaie 'maison1'…'maison8'."
        }, ensure_ascii=False)

    # Offset: teleport the player 4m in front of the house's center (towards +Z direction)
    # so they appear at the door, not inside a wall.
    teleport_id = f"tp_{datetime.now().strftime('%Y%m%d%H%M%S%f')}"
    house_id = house_id_ctx.get()

    doc = {
        "id": teleport_id,
        "house_id": house_id,
        "destination": destination,
        "label": label,
        "target_x": target_x,
        "target_z": target_z + 5.0,   # 5m offset so player lands outside the door
        "status": "pending",
        "created_at": datetime.now().isoformat()
    }

    if teleports_collection is not None:
        teleports_collection.insert_one(doc)
    else:
        return json.dumps({"status": "error", "message": "MongoDB non connecté"})

    log_debug("TELEPORT", f"Téléportation vers '{label}' à ({target_x:.1f}, {target_z:.1f})")

    return json.dumps({
        "status": "ok",
        "action": "teleport",
        "destination": label,
        "target": {"x": target_x, "z": target_z + 5.0},
        "message": f"Téléportation vers {label}…"
    }, ensure_ascii=False)


def consulter_normes(query: str) -> str:
    """Consulte les normes architecturales via RAG."""
    try:
        from rag import retrieve
    except ImportError:
        return json.dumps({
            "status": "error",
            "message": "Module RAG non disponible"
        })

    if not query or not isinstance(query, str):
        return json.dumps({
            "status": "error",
            "message": "Requête vide ou invalide"
        })

    context = retrieve(query, top_k=3)

    if not context:
        return json.dumps({
            "status": "no_results",
            "query": query,
            "message": "Aucune norme trouvée pour cette requête"
        })

    log_debug("RAG", f"✅ Contexte trouvé pour '{query}'")

    return json.dumps({
        "status": "success",
        "action": "consult_norms",
        "query": query,
        "timestamp": datetime.now().isoformat(),
        "context": context
    }, ensure_ascii=False)

def historique_actions(limit: int = 10) -> str:
    """Retourne l'historique des 'limit' dernières actions."""
    try:
        from history import get_last_n_actions
        actions = get_last_n_actions(limit)
    except ImportError:
        return json.dumps({
            "status": "error",
            "message": "Module historique non disponible"
        })
    except Exception as e:
        return json.dumps({
            "status": "error",
            "message": f"Erreur lecture historique: {str(e)}"
        })

    log_debug("HISTORY", f"✅ {len(actions)} actions trouvées")

    return json.dumps({
        "status": "success",
        "action": "list_history",
        "timestamp": datetime.now().isoformat(),
        "actions": actions,
        "count": len(actions)
    }, ensure_ascii=False)

def annuler_derniere_action() -> str:
    """Annule la dernière action (undo)."""
    try:
        from history import pop_last_undoable_action
    except ImportError:
        return json.dumps({
            "status": "error",
            "message": "Module historique non disponible"
        })

    # Récupérer l'état avant la dernière action
    try:
        previous_state = pop_last_undoable_action()
    except Exception as e:
        return json.dumps({
            "status": "error",
            "message": f"Erreur annulation: {str(e)}"
        })

    if not previous_state:
        return json.dumps({
            "status": "error",
            "message": "Aucune action à annuler"
        })

    # Restaurer l'état précédent
    try:
        rooms_collection.delete_many({})
        house_collection.delete_many({})

        if previous_state.get("rooms"):
            for room in previous_state["rooms"]:
                rooms_collection.insert_one(room)

        if previous_state.get("house"):
            house_collection.insert_one(previous_state["house"])

        log_debug("UNDO", f"✅ Action annulée")
    except Exception as e:
        return json.dumps({
            "status": "error",
            "message": f"Erreur restauration: {str(e)}"
        })

    updated_rooms = [clean_doc(r) for r in rooms_collection.find({}, projection={"_id": 0})]

    return json.dumps({
        "status": "success",
        "action": "undo",
        "timestamp": datetime.now().isoformat(),
        "rooms": updated_rooms,
        "restored_action": previous_state.get("action", "unknown")
    }, ensure_ascii=False)
