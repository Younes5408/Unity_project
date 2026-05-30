import json
import math
import os
import re
from agno.agent import Agent
from agno.models.groq import Groq
from agno.models.google import Gemini
from config import api_key, log_debug
from tools import initialiser_maison, ajouter_piece_unity, verifier_espace_disponible, consulter_normes, placer_objet, teleporter_joueur  # noqa: F401 — verifier_espace_disponible kept importable for future re-enable (see WARNING below)


# ============================================================================
# ⚠️  DO NOT RE-ADD `verifier_espace_disponible` TO THE AGENT TOOL LISTS  ⚠️
# ----------------------------------------------------------------------------
# It is intentionally commented out of every Agent(tools=[...]) below because:
#   1. The agent prompt explicitly tells the LLM NEVER to call it unless the
#      user explicitly asks about available space — so in practice it is never
#      invoked, yet its JSON schema still ships in every system prompt
#      (~20 tokens × every call = quota waste on Groq free tier).
#   2. Having it visible to the LLM occasionally tempts the cheaper models
#      (8b) to call it spuriously before a real build tool, eating into the
#      tool_call_limit=3 budget and producing slow/confusing responses.
# The import above is kept so the function remains reachable if a future
# feature needs it — DO NOT delete the import or uncomment the tools entries
# without first re-introducing it deliberately with prompt updates.
# ============================================================================


def calculate_build_position(player_x: float, player_z: float, player_angle: float, distance: float = 3.0):
    """Calculate position 3m in front of the player's facing direction.

    Uses Unity's coordinate convention:
      - eulerAngles.y = 0°   → facing +Z (forward)
      - eulerAngles.y = 90°  → facing +X (right)
      - eulerAngles.y = 180° → facing -Z (back)
      - eulerAngles.y = 270° → facing -X (left)

    So forward = (sin(yaw), cos(yaw)) in the XZ plane.

    Returns (build_x, build_z)
    """
    angle_rad = math.radians(player_angle)
    build_x = player_x + distance * math.sin(angle_rad)
    build_z = player_z + distance * math.cos(angle_rad)
    return round(build_x, 2), round(build_z, 2)

# ==========================================
# 8. AGENT LLM
# ==========================================
def _pick_llm_model():
    """Primary: Groq llama-3.3-70b-versatile.

    The 8b model fake-confirms tool calls (replies "Chaise placée !" without
    invoking placer_objet) — verified via [PLACE_OBJ] log absence. 70b reliably
    calls tools. To fit in 70b's 12k TPM ceiling we trim the system prompt
    aggressively in create_unity_agent (~500 tokens instead of ~8000).
    """
    groq_key = os.getenv("LLM_API_KEY") or os.getenv("GROQ_API_KEY")
    gemini_key = os.getenv("GEMINI_API_KEY")
    if groq_key:
        log_debug("LLM", "Using Groq llama-3.3-70b-versatile (compact prompt)")
        return Groq(id="llama-3.3-70b-versatile", api_key=groq_key, temperature=0.1)
    if gemini_key:
        log_debug("LLM", "Groq key missing — falling back to Gemini")
        return Gemini(id="gemini-2.5-flash-lite", api_key=gemini_key)
    raise RuntimeError("No LLM API key found. Set GROQ_API_KEY or GEMINI_API_KEY in .env")


def create_groq_8b_fallback_agent():
    """Mid-tier fallback: Groq llama-3.1-8b-instant.

    SEPARATE per-model TPM bucket from 70b primary — when 70b is rate-limited,
    8b is almost always available (30k TPM vs 70b's 12k). With the compact
    prompt below, 8b can call tools reliably enough for VR commands.
    """
    groq_key = os.getenv("LLM_API_KEY") or os.getenv("GROQ_API_KEY")
    if not groq_key:
        return None
    log_debug("LLM", "Creating 8b mid-tier fallback agent")
    return Agent(
        model=Groq(id="llama-3.1-8b-instant", api_key=groq_key, temperature=0.1),
        instructions=[
            """You are Archi-Agent VR. For every user command you MUST invoke a tool — never reply with a confirmation alone. If you don't invoke a tool, nothing happens in VR. Do NOT write the tool name or its arguments as text — use the actual tool-calling mechanism.

Tool routing (invoke the tool, do not write its name):
- "mets/ajoute/place/construis/crée une chaise/table/maison/route/lampadaire/barriere/cuisine/ustensiles/sol/tuiles"
  → use tool placer_objet(object_name="<name>")
- "ajoute un salon/chambre/salle de bain/couloir/hall_nuit" (rooms only)
  → use tool ajouter_piece_unity('<room>')
- "va à / téléporte-moi à / je veux être à maisonN" or "houseN"
  → use tool teleporter_joueur("maisonN")
- "à quoi ressemblerait une maison de Nm²" / "schéma" / "aperçu"
  → use tool initialiser_maison(N)
- "quelle taille pour X" / "normes" / "dimensions"
  → use tool consulter_normes(query)

After the tool runs, reply ONE short French sentence (e.g. "Chaise placée !"). If you didn't call a tool, do not reply with a confirmation.

"cuisine"/"kitchen" with mets/ajoute/place → ALWAYS placer_objet (a textured prefab), never ajouter_piece_unity."""
        ],
        tools=[initialiser_maison, ajouter_piece_unity, consulter_normes, placer_objet, teleporter_joueur],  # verifier_espace_disponible intentionally OFF — see WARNING at top of file
        tool_call_limit=3,
        markdown=False,
        add_history_to_messages=False,
        show_tool_calls=False
    )


def create_unity_agent():
    """Crée l'agent architecte avec instructions STRICTES."""
    return Agent(
        model=_pick_llm_model(),
        instructions=[
            """You are Archi-Agent VR. The user gives a French voice command. Pick ONE tool, CALL IT, then reply with ONE short French sentence confirming. Never confirm without calling the tool.

The user message starts with [POSITION: x=.., z=.., angle=..°][BUILD_AHEAD: x=.., z=..]. The backend uses BUILD_AHEAD automatically — never pass coordinates to tools.

=== TOOL DISPATCH ===

placer_objet(object_name) — textured PREFABS (the only build path for these names):
  chaise, table, maison (or maison1..8), route (or route1..5), lampadaire,
  barriere (or barriere1..7), sol, tuiles, cuisine (or cuisine1..3),
  ustensiles, kitchen (or kitchen1..3)
  Trigger verbs: mets, ajoute, place, pose, construis, crée, fais, installe,
  rajoute, build, add, put.
  Examples:
    "mets une chaise"           → placer_objet(object_name="chaise")
    "mets la chaise 3"          → placer_objet(object_name="chaise3")
    "ajoute une table"          → placer_objet(object_name="table")
    "construis une maison"      → placer_objet(object_name="maison")
    "construis-moi une maison de 100m²" → placer_objet(object_name="maison")
    "mets une cuisine"          → placer_objet(object_name="cuisine")
    "ajoute des ustensiles"     → placer_objet(object_name="ustensiles")
    "ajoute une route"          → placer_objet(object_name="route")
    "place un lampadaire"       → placer_objet(object_name="lampadaire")

ajouter_piece_unity(room_type, placement_hint?) — ROOM types only:
  salon, chambre, cuisine, salle de bain, hall_nuit, couloir
  ONLY when user says "ajoute UN/UNE [room]" (singular room context). Pass
  placement_hint if user says "à droite/gauche/devant/derrière".
  Examples:
    "ajoute un salon"           → ajouter_piece_unity('salon')
    "ajoute une chambre à droite" → ajouter_piece_unity('chambre', placement_hint='right')

initialiser_maison(surface_totale) — WHITE schematic preview ONLY (visualization), never construction:
  Triggers: "à quoi ressemblerait", "montre-moi à quoi ressemble", "aperçu",
  "schéma", "visualise", "preview".
  Example: "à quoi ressemblerait une maison de 200m²?" → initialiser_maison(200)

teleporter_joueur(destination) — when user wants to BE AT / GO TO a named house:
  Triggers: "va à", "téléporte-moi", "amène-moi", "emmène-moi", "je veux être à",
  "je veux aller", "mets-moi dans", "take me to", "go to".
  Pass the destination name verbatim (maison1..8, house1..8).
  Examples:
    "va à la maison 2"          → teleporter_joueur("maison2")
    "téléporte-moi à house 5"   → teleporter_joueur("house5")
    "je veux être dans la maison 3" → teleporter_joueur("maison3")

consulter_normes(query) — questions about room sizes, norms, dimensions:
  Triggers: "quelle taille", "combien de m²", "normes", "dimensions", "superficie".
  Example: "quelle taille pour une chambre?" → consulter_normes("dimensions chambre superficie")

=== DISAMBIGUATION ===

"cuisine" is BOTH a prefab AND a room. Rule:
  - "mets/ajoute/construis/place une cuisine" → placer_objet("cuisine") [PREFAB]
  - Use ajouter_piece_unity('cuisine') ONLY if user explicitly says "ajoute une PIÈCE cuisine" or asks for a room of the house.

"maison" is ALWAYS placer_objet("maison") for construction. Only initialiser_maison for preview phrasing.

=== POSITION ===
If user asks "où suis-je", "quelles sont mes coordonnées", read POSITION from the message and reply in plain text — no tool call.

=== REPLY FORMAT ===
After the tool call succeeds, reply with ONE short French sentence:
  placer_objet → "Chaise placée !" / "Table placée !" / "Maison ajoutée !"
  ajouter_piece_unity → "Salon ajouté !"
  teleporter_joueur → "Téléportation vers maison2 en cours !"
  consulter_normes → 1-2 sentence summary of the result
No JSON, no markdown headers, no extra explanation."""
        ],
        tools=[initialiser_maison, ajouter_piece_unity, consulter_normes, placer_objet, teleporter_joueur],  # verifier_espace_disponible intentionally OFF — see WARNING at top of file
        tool_call_limit=3,
        markdown=False,
        add_history_to_messages=False,
        show_tool_calls=False
    )


def create_gemini_fallback_agent():
    """Agent de secours utilisant Gemini (déclenché si Groq échoue/timeout/quota)."""
    gemini_key = os.getenv("GEMINI_API_KEY")
    if not gemini_key:
        return None
    log_debug("LLM", "Creating Gemini fallback agent")
    return Agent(
        model=Gemini(id="gemini-2.5-flash-lite", api_key=gemini_key),
        instructions=[
            """You are Archi-Agent VR. Execute EXACTLY what the user asks. Reply briefly in French (1 short sentence).

Tool routing (CRITICAL):
- "mets/ajoute/place/construis une chaise/table/maison/route/lampadaire/barriere/cuisine/ustensiles"
  → placer_objet(object_name="<name>")  — these are TEXTURED PREFABS, not rooms.
- "ajoute un salon/chambre/salle de bain/couloir/hall_nuit"
  → ajouter_piece_unity('<room>')  — these ARE rooms.
- "à quoi ressemblerait une maison de Nm²" → initialiser_maison(N).
- "va à / téléporte-moi à maisonN / houseN" → teleporter_joueur("maisonN").
- "quelle taille pour une chambre" / norms questions → consulter_normes(query).

NEVER refuse a placer_objet call because "the house isn't initialised" — placer_objet works independently of any house.
After a tool call, reply with one short French sentence (e.g. "Cuisine placée !")."""
        ],
        tools=[initialiser_maison, ajouter_piece_unity, consulter_normes, placer_objet, teleporter_joueur],  # verifier_espace_disponible intentionally OFF — see WARNING at top of file
        tool_call_limit=3,
        markdown=False,
        add_history_to_messages=False,
        show_tool_calls=False
    )

# ==========================================
# 9. EXTRACTION JSON SÉCURISÉE
# ==========================================
def extract_json_from_response(text: str) -> dict:
    """Extrait JSON de manière sécurisée."""
    if not text:
        return {}
    
    # Chercher bloc ```json
    match = re.search(r'```json\s*([\s\S]*?)\s*```', text)
    if match:
        json_str = match.group(1).strip()
        log_debug("JSON", "✅ JSON trouvé dans bloc ```json```")
    else:
        # Fallback: chercher premier { au dernier }
        start = text.find('{')
        end = text.rfind('}')
        if start == -1 or end == -1 or end <= start:
            log_debug("JSON", "❌ Pas de JSON trouvé")
            return {}
        json_str = text[start:end+1]
        log_debug("JSON", "⚠️ JSON extrait avec fallback")
    
    try:
        return json.loads(json_str)
    except json.JSONDecodeError as e:
        log_debug("JSON", f"❌ Erreur parse: {str(e)}")
        return {}
