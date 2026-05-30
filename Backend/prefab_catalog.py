"""Mapping from French user-friendly names to Unity Resources prefab paths."""

PREFAB_CATALOG = {
    # Houses
    "maison": {"prefab": "Houses/House1", "category": "house"},
    "maison1": {"prefab": "Houses/House1", "category": "house"},
    "maison2": {"prefab": "Houses/House2", "category": "house"},
    "maison3": {"prefab": "Houses/House3", "category": "house"},
    "maison4": {"prefab": "Houses/House4", "category": "house"},
    "maison5": {"prefab": "Houses/House5", "category": "house"},
    "maison6": {"prefab": "Houses/House6", "category": "house"},
    "maison7": {"prefab": "Houses/House7", "category": "house"},
    "maison8": {"prefab": "Houses/House8", "category": "house"},
    # Roads
    "route": {"prefab": "Modules/Roads/Road_1", "category": "road"},
    "route1": {"prefab": "Modules/Roads/Road_1", "category": "road"},
    "route2": {"prefab": "Modules/Roads/Road_2", "category": "road"},
    "route3": {"prefab": "Modules/Roads/Road_3", "category": "road"},
    "route4": {"prefab": "Modules/Roads/Road_4", "category": "road"},
    "route5": {"prefab": "Modules/Roads/Road_5", "category": "road"},
    "lampadaire": {"prefab": "Modules/Roads/Streetlight", "category": "road"},
    # Fences
    "barriere": {"prefab": "Modules/Fences/Fence_1", "category": "fence"},
    "barriere1": {"prefab": "Modules/Fences/Fence_1", "category": "fence"},
    "barriere2": {"prefab": "Modules/Fences/Fence_2", "category": "fence"},
    "barriere3": {"prefab": "Modules/Fences/Fence_3", "category": "fence"},
    "barriere4": {"prefab": "Modules/Fences/Fence_4", "category": "fence"},
    "barriere5": {"prefab": "Modules/Fences/Fence_5", "category": "fence"},
    "barriere6": {"prefab": "Modules/Fences/Fence_6", "category": "fence"},
    "barriere7": {"prefab": "Modules/Fences/Fence_7", "category": "fence"},
    # Ground
    "sol": {"prefab": "Modules/Ground/Ground", "category": "ground"},
    "tuiles": {"prefab": "Modules/Tiles/Tiles", "category": "ground"},
}


# Furniture — pre-textured prefabs spawned with floor-aware placement (raycast down to ground)
# Prefab names match Assets/Resources/Furniture/ from "3D Game Asset - Table & Chair Sets"
FURNITURE_CATALOG = {
    # Chairs — 5 variants, "chaise" defaults to Chair1
    "chaise":  {"prefab": "Furniture/Chair1", "category": "furniture"},
    "chaise1": {"prefab": "Furniture/Chair1", "category": "furniture"},
    "chaise2": {"prefab": "Furniture/Chair2", "category": "furniture"},
    "chaise3": {"prefab": "Furniture/Chair3", "category": "furniture"},
    "chaise4": {"prefab": "Furniture/Chair4", "category": "furniture"},
    "chaise5": {"prefab": "Furniture/Chair5", "category": "furniture"},
    # Tables — 5 variants, "table" defaults to Table1
    "table":   {"prefab": "Furniture/Table1", "category": "furniture"},
    "table1":  {"prefab": "Furniture/Table1", "category": "furniture"},
    "table2":  {"prefab": "Furniture/Table2", "category": "furniture"},
    "table3":  {"prefab": "Furniture/Table3", "category": "furniture"},
    "table4":  {"prefab": "Furniture/Table4", "category": "furniture"},
    "table5":  {"prefab": "Furniture/Table5", "category": "furniture"},
    # Kitchen sets (Genius Crate Kitchen_Set asset)
    "cuisine":        {"prefab": "Furniture/Kitchen_Set01", "category": "furniture"},
    "cuisine1":       {"prefab": "Furniture/Kitchen_Set01", "category": "furniture"},
    "cuisine2":       {"prefab": "Furniture/Kitchen_Set02", "category": "furniture"},
    "cuisine3":       {"prefab": "Furniture/Kitchen_Set03", "category": "furniture"},
    "ustensiles":     {"prefab": "Furniture/Kitchen_Set_props", "category": "furniture"},
    "kitchen":        {"prefab": "Furniture/Kitchen_Set01", "category": "furniture"},
    "kitchen1":       {"prefab": "Furniture/Kitchen_Set01", "category": "furniture"},
    "kitchen2":       {"prefab": "Furniture/Kitchen_Set02", "category": "furniture"},
    "kitchen3":       {"prefab": "Furniture/Kitchen_Set03", "category": "furniture"},
    "lampe":          {"prefab": "Furniture/Large_round_lamp", "category": "furniture"},
    "lampe1":         {"prefab": "Furniture/Large_round_lamp", "category": "furniture"},
    "lampe2":         {"prefab": "Furniture/Small_roof_lamp", "category": "furniture"},
    "lampe3":         {"prefab": "Furniture/Classic_fluorescent_lamp", "category": "furniture"},
}


def resolve_prefab(name: str) -> dict | None:
    """Resolve a user-friendly name to a prefab path + category.
    Checks PREFAB_CATALOG first, then FURNITURE_CATALOG.
    Returns None if name is not in either catalog."""
    key = name.lower().strip()
    return PREFAB_CATALOG.get(key) or FURNITURE_CATALOG.get(key)


def get_available_names() -> list[str]:
    """Return sorted list of unique available object names (buildings + furniture)."""
    return sorted(set(PREFAB_CATALOG.keys()) | set(FURNITURE_CATALOG.keys()))


def get_building_names() -> list[str]:
    """Return sorted list of building/structure names only."""
    return sorted(PREFAB_CATALOG.keys())


def get_furniture_names() -> list[str]:
    """Return sorted list of furniture names only."""
    return sorted(FURNITURE_CATALOG.keys())
