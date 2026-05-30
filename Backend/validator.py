from typing import List, Dict
from config import MIN_ROOM_GAP, EPSILON_COLLISION, log_debug

# ==========================================
# 4. VALIDATEUR DE LAYOUT
# ==========================================
class HouseValidator:
    """Validation complète des layouts architecturaux."""
    
    def __init__(self, total_surface: float):
        self.total_surface = total_surface
        self.errors = []
        self.warnings = []
    
    def reset(self):
        self.errors = []
        self.warnings = []
    
    def validate_collisions(self, rooms: List[Dict]) -> bool:
        """Vérifie les collisions AABB avec marge."""
        for i, r1 in enumerate(rooms):
            for r2 in rooms[i+1:]:
                if self._aabb_collision(r1, r2):
                    self.errors.append(f"COLLISION: {r1['id']} ↔ {r2['id']}")
                    return False
        return True
    
    def _aabb_collision(self, r1: Dict, r2: Dict) -> bool:
        """Détecte collision AABB (chevauchement réel physique sans marge de conception)."""
        x1, z1 = r1["position"]["x"], r1["position"]["z"]
        w1, d1 = r1["dimensions"]["width"], r1["dimensions"]["depth"]
        
        x2, z2 = r2["position"]["x"], r2["position"]["z"]
        w2, d2 = r2["dimensions"]["width"], r2["dimensions"]["depth"]
        
        # Tolérance epsilon pour ignorer les contacts de bords (float precision limit)
        gap = EPSILON_COLLISION
        
        # Collision ssi intersection stricte (admet le contact parfait des murs)
        if (x1 < x2 + w2 - gap and x1 + w1 > x2 + gap and 
            z1 < z2 + d2 - gap and z1 + d1 > z2 + gap):
            log_debug("COLLISION", f"Overlap détecté! {r1['id']}(x:{x1:.2f}, z:{z1:.2f}, w:{w1:.2f}, d:{d1:.2f}) vs {r2['id']}(x:{x2:.2f}, z:{z2:.2f}, w:{w2:.2f}, d:{d2:.2f})")
            return True  # Collision !
            
        return False  # Libre
    
    def validate_surface(self, rooms: List[Dict]) -> bool:
        """Vérifie que la surface utilisée ne dépasse pas le total."""
        used = sum(r["metadata"]["surface"] for r in rooms)
        if used > self.total_surface * 0.95:  # Tolérance 5%
            self.errors.append(f"Surface dépassée: {used:.2f}m² > {self.total_surface}m²")
            return False
        return True
    
    def validate_accessibility(self, rooms: List[Dict]) -> bool:
        """Vérifie que toutes les pièces sont accessibles."""
        couloir = next((r for r in rooms if r["type"] == "couloir"), None)
        if not couloir:
            self.errors.append("Pas de couloir trouvé")
            return False
        
        # Chaque pièce (sauf la première) doit avoir au moins une porte/adjacence
        for room in rooms:
            if room["type"] == "couloir":
                continue
            if not room.get("doors") and not room["metadata"].get("adjacent_to"):
                self.errors.append(f"{room['id']} n'a pas d'accès (aucune porte/adjacence)")
                return False
        
        return True
    
    def validate_zones(self, rooms: List[Dict]) -> bool:
        """Vérifie la séparation des zones public/private."""
        public_rooms = [r for r in rooms if r.get("zone") == "public"]
        private_rooms = [r for r in rooms if r.get("zone") == "private"]
        
        if not public_rooms:
            self.warnings.append("Aucune pièce publique")
        if not private_rooms:
            self.warnings.append("Aucune pièce privée")
        
        return True
    
    def validate_all(self, rooms: List[Dict]) -> bool:
        """Validation complète."""
        self.reset()
        
        checks = [
            ("Collisions", self.validate_collisions(rooms)),
            ("Surface", self.validate_surface(rooms)),
            ("Accessibilité", self.validate_accessibility(rooms)),
            ("Zones", self.validate_zones(rooms)),
        ]
        
        all_valid = all(result for _, result in checks)
        
        for name, result in checks:
            status = "✅" if result else "❌"
            log_debug("VALIDATION", f"{status} {name}")
        
        return all_valid

def score_layout(rooms: List[Dict]) -> float:
    """Évalue la qualité architecturale du layout."""
    score = 100.0
    
    # Pénalité: couloirs trop longs
    couloirs = [r for r in rooms if r["type"] == "couloir"]
    for couloir in couloirs:
        if couloir["metadata"]["surface"] > 15.0:
            score -= 10
            log_debug("SCORE", "⚠️ Couloir trop grand")
    
    # Bonus: bonnes adjacences
    for room in rooms:
        if room["type"] == "chambre":
            # Les chambres doivent être près de salle de bain
            salle_bain_adj = any("salle" in adj_id for adj_id in room["metadata"]["adjacent_to"])
            if salle_bain_adj:
                score += 5
                log_debug("SCORE", "✅ Chambre bien adjacente à SDB")
    
    return min(100.0, max(0.0, score))
