using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Gives the Archi-Agent "eyes" — performs raycasts and proximity scans
/// to describe what the player can see and what's around them in the scene.
/// Output is a compact JSON-ready string sent with each voice command.
/// Now includes nearby_objects with positions/bounds and free_directions.
/// </summary>
public class SceneInspector : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Player transform. Auto-found via 'Player' tag at Awake() if null.")]
    public Transform playerTransform;
    [Tooltip("Camera used as player's eyes (forward direction). Auto-found if null.")]
    public Camera playerCamera;

    [Header("Vision Settings")]
    [Tooltip("Max distance for forward raycast (what's in front of player).")]
    public float forwardRayDistance = 30f;
    [Tooltip("Radius around player to scan for nearby objects (houses, roads, rooms).")]
    public float proximityRadius = 25f;
    [Tooltip("Max number of significant objects to include in nearby_objects array.")]
    public int maxNearbyObjects = 12;

    void Awake()
    {
        if (playerTransform == null)
        {
            GameObject p = GameObject.FindWithTag("Player");
            if (p == null) p = GameObject.Find("Player");
            if (p != null) playerTransform = p.transform;
        }
        if (playerCamera == null)
        {
            playerCamera = Camera.main;
            if (playerCamera == null && playerTransform != null)
                playerCamera = playerTransform.GetComponentInChildren<Camera>();
        }
    }

    /// <summary>
    /// Generates a French-language scene context summary for the LLM.
    /// Sent as a form field with each voice command.
    /// </summary>
    public string GetSceneContextJson()
    {
        if (playerTransform == null)
            return "{\"error\": \"no_player\"}";

        Vector3 pos = playerTransform.position;
        Vector3 fwd = playerCamera != null ? playerCamera.transform.forward : playerTransform.forward;

        // ---- Forward raycast: what's directly ahead? ----
        string looking_at = "rien (terrain libre)";
        float looking_at_distance = -1f;
        Vector3 rayOrigin = pos + Vector3.up * 1.4f; // eye level
        if (Physics.Raycast(rayOrigin, fwd, out RaycastHit hit, forwardRayDistance))
        {
            looking_at = ClassifyObject(hit.collider.gameObject);
            looking_at_distance = hit.distance;
        }

        // ---- Proximity scan: count and classify nearby objects ----
        Collider[] nearby = Physics.OverlapSphere(pos, proximityRadius);
        int houseCount = 0, roadCount = 0, fenceCount = 0, groundCount = 0, roomCount = 0, otherCount = 0;
        string nearestHouse = null;
        float nearestHouseDist = float.MaxValue;
        var nearbyRooms = new List<string>();

        // Track unique significant objects with position/bounds for the LLM
        var significantObjects = new List<NearbyObjectInfo>();
        var seenRoots = new HashSet<int>(); // prevent duplicates by instance ID

        foreach (var c in nearby)
        {
            if (c == null || c.gameObject == null) continue;
            string cls = ClassifyObject(c.gameObject);
            float d = Vector3.Distance(pos, c.transform.position);

            if (cls.StartsWith("maison House"))
            {
                houseCount++;
                if (d < nearestHouseDist) { nearestHouseDist = d; nearestHouse = cls; }
                // Add to significant objects (use the House root)
                GameObject root = FindSignificantRoot(c.gameObject, "house");
                if (root != null && seenRoots.Add(root.GetInstanceID()))
                    significantObjects.Add(BuildObjectInfo(root, "house", d));
            }
            else if (cls.StartsWith("route"))
            {
                roadCount++;
                GameObject root = FindSignificantRoot(c.gameObject, "road");
                if (root != null && seenRoots.Add(root.GetInstanceID()))
                    significantObjects.Add(BuildObjectInfo(root, "road", d));
            }
            else if (cls.StartsWith("clôture"))
            {
                fenceCount++;
                GameObject root = FindSignificantRoot(c.gameObject, "fence");
                if (root != null && seenRoots.Add(root.GetInstanceID()))
                    significantObjects.Add(BuildObjectInfo(root, "fence", d));
            }
            else if (cls.StartsWith("pelouse")) groundCount++;
            else if (cls.StartsWith("pièce")) { roomCount++; if (nearbyRooms.Count < 6) nearbyRooms.Add(cls); }
            else otherCount++;
        }

        // Sort by distance ascending, cap at maxNearbyObjects
        significantObjects.Sort((a, b) => a.dist.CompareTo(b.dist));
        if (significantObjects.Count > maxNearbyObjects)
            significantObjects.RemoveRange(maxNearbyObjects, significantObjects.Count - maxNearbyObjects);

        // ---- Free directions: distance to first solid obstacle in N/S/E/W ----
        float freeNorth = CastDirectionDistance(pos, Vector3.forward);
        float freeSouth = CastDirectionDistance(pos, Vector3.back);
        float freeEast = CastDirectionDistance(pos, Vector3.right);
        float freeWest = CastDirectionDistance(pos, Vector3.left);

        // ---- Compose JSON manually (avoid Newtonsoft dependency) ----
        StringBuilder sb = new StringBuilder();
        sb.Append("{");
        sb.AppendFormat("\"player_pos\":[{0:F1},{1:F1},{2:F1}],", pos.x, pos.y, pos.z);
        sb.AppendFormat("\"facing\":[{0:F2},{1:F2}],", fwd.x, fwd.z);
        sb.AppendFormat("\"looking_at\":\"{0}\",", EscapeJson(looking_at));
        sb.AppendFormat("\"looking_at_distance_m\":{0:F1},", looking_at_distance);
        sb.AppendFormat("\"proximity_radius_m\":{0:F1},", proximityRadius);
        sb.Append("\"counts\":{");
        sb.AppendFormat("\"houses\":{0},\"roads\":{1},\"fences\":{2},\"grounds\":{3},\"custom_rooms\":{4},\"other\":{5}",
            houseCount, roadCount, fenceCount, groundCount, roomCount, otherCount);
        sb.Append("},");
        if (nearestHouse != null)
            sb.AppendFormat("\"nearest_house\":\"{0}\",\"nearest_house_distance_m\":{1:F1},", EscapeJson(nearestHouse), nearestHouseDist);
        sb.Append("\"nearby_custom_rooms\":[");
        for (int i = 0; i < nearbyRooms.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.AppendFormat("\"{0}\"", EscapeJson(nearbyRooms[i]));
        }
        sb.Append("],");

        // nearby_objects array with position and bounding box
        sb.Append("\"nearby_objects\":[");
        for (int i = 0; i < significantObjects.Count; i++)
        {
            if (i > 0) sb.Append(",");
            var obj = significantObjects[i];
            sb.Append("{");
            sb.AppendFormat("\"name\":\"{0}\",", EscapeJson(obj.name));
            sb.AppendFormat("\"type\":\"{0}\",", obj.type);
            sb.AppendFormat("\"pos\":[{0:F1},{1:F1},{2:F1}],", obj.pos.x, obj.pos.y, obj.pos.z);
            sb.AppendFormat("\"size\":[{0:F1},{1:F1},{2:F1}],", obj.size.x, obj.size.y, obj.size.z);
            sb.AppendFormat("\"dist\":{0:F1}", obj.dist);
            sb.Append("}");
        }
        sb.Append("],");

        // free_directions: distance to nearest obstacle in each cardinal direction
        sb.Append("\"free_directions\":{");
        sb.AppendFormat("\"north\":{0:F1},\"south\":{1:F1},\"east\":{2:F1},\"west\":{3:F1}",
            freeNorth, freeSouth, freeEast, freeWest);
        sb.Append("}}");
        return sb.ToString();
    }

    // ---- Helper: find the significant root GameObject for an object ----
    private GameObject FindSignificantRoot(GameObject go, string expectedType)
    {
        Transform cur = go.transform;
        for (int i = 0; i < 5 && cur != null; i++)
        {
            string pn = cur.name;
            if (expectedType == "house" && pn.StartsWith("House") && pn.Length <= 8)
                return cur.gameObject;
            if (expectedType == "road" && pn.StartsWith("Road_"))
                return cur.gameObject;
            if (expectedType == "fence" && pn.StartsWith("Fence_"))
                return cur.gameObject;
            cur = cur.parent;
        }
        return go; // fallback to the hit object itself
    }

    // ---- Helper: compute aggregate bounds of a root object ----
    private NearbyObjectInfo BuildObjectInfo(GameObject root, string type, float dist)
    {
        Bounds bounds = new Bounds(root.transform.position, Vector3.zero);
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        foreach (var r in renderers)
            bounds.Encapsulate(r.bounds);

        return new NearbyObjectInfo
        {
            name = root.name,
            type = type,
            pos = bounds.center,
            size = bounds.size,
            dist = dist
        };
    }

    // ---- Helper: cast a ray in a direction to find free distance ----
    private float CastDirectionDistance(Vector3 origin, Vector3 direction)
    {
        float maxDist = proximityRadius;
        Vector3 rayStart = origin + Vector3.up * 0.5f; // slightly above ground
        if (Physics.Raycast(rayStart, direction, out RaycastHit h, maxDist))
        {
            // Only count solid objects (houses, fences, roads) — not ground
            string cls = ClassifyObject(h.collider.gameObject);
            if (cls.StartsWith("pelouse") || cls.StartsWith("dalle"))
            {
                // Hit ground — try again ignoring ground layer (cast further)
                // Simple approach: return maxDist since ground is not an obstacle
                return maxDist;
            }
            return h.distance;
        }
        return maxDist;
    }

    /// <summary>
    /// Classifies a GameObject into a human-readable French label based on naming conventions.
    /// </summary>
    private string ClassifyObject(GameObject go)
    {
        string n = go.name;
        Transform t = go.transform;

        // Walk up parent chain to find the group (Roads, Fences, Grounds, Houses)
        Transform cur = t;
        for (int i = 0; i < 5 && cur != null; i++)
        {
            string pn = cur.name;
            if (pn == "Roads" || pn.StartsWith("Road_")) return "route (asphalte)";
            if (pn == "Fences" || pn.StartsWith("Fence_")) return "clôture";
            if (pn == "Grounds" || pn.StartsWith("Ground")) return "pelouse";
            if (pn == "Tiles" || pn.StartsWith("Tiles")) return "dalle pavée";
            if (pn.StartsWith("House") && pn.Length <= 8)
                return "maison " + pn; // e.g. "maison House3"
            cur = cur.parent;
        }

        // Custom rooms spawned by LayoutReceiver follow {type}_{id} pattern
        if (n.Contains("_") && (n.StartsWith("salon") || n.StartsWith("chambre") ||
            n.StartsWith("cuisine") || n.StartsWith("couloir") || n.StartsWith("salle") ||
            n.StartsWith("bureau") || n.StartsWith("wc")))
            return "pièce " + n;

        // Walls / floors / roofs / doors created by LayoutReceiver
        if (n.StartsWith("Wall_") || n.StartsWith("Floor") || n.StartsWith("Roof") || n.StartsWith("Door"))
        {
            Transform room = t.parent;
            if (room != null) return "pièce " + room.name + " (" + n + ")";
            return "élément " + n;
        }

        if (n == "Player" || n.Contains("Player")) return "joueur";
        if (n.Contains("Streetlight") || n.Contains("Lamp")) return "lampadaire";
        return "objet " + n;
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ---- Data struct for nearby objects ----
    private struct NearbyObjectInfo
    {
        public string name;
        public string type;
        public Vector3 pos;
        public Vector3 size;
        public float dist;
    }
}
