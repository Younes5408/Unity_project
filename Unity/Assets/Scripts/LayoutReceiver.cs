using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

// === CLASSES DE SÉRIALISATION ===
[System.Serializable]
public class ApiLayoutResponse
{
    public string status;
    public List<RoomDto> rooms;
    public HouseDto house;
    public bool building_in_progress;
}

[System.Serializable]
public class HouseDto
{
    public string house_id;
    public float surface_totale;
    public float surface_utilisee;
}

[System.Serializable]
public class RoomDoor
{
    public string id;
    public string connected_to;
    public float width;
    public string type;
    public Vec3 position;
}

[System.Serializable]
public class RoomWindow
{
    public string id;
    public float width;
    public float height;
    public Vec3 position;
    public string wall;
    public string type;
}

[System.Serializable]
public class RoomDto
{
    public string id;
    public string type;
    public string label;
    public Vec3 position;
    public Dims dimensions;
    public List<RoomDoor> doors;
    public List<RoomWindow> windows;
}

[System.Serializable]
public class Vec3
{
    public float x;
    public float y;
    public float z;
}

[System.Serializable]
public class Dims
{
    public float width;
    public float depth;
    public float height;
}

public class LayoutReceiver : MonoBehaviour
{
    [Header("Configuration API")]
    public string apiBaseUrl = "http://127.0.0.1:8000";
    public string houseId = "maison_001";

    [Header("Auto-Refresh Temps Réel")]
    public bool enableAutoRefresh = true;
    public float fastRefreshInterval = 1f;   // Après une commande vocale
    public float idleRefreshInterval = 10f;  // Quand rien ne se passe

    [Header("Options")]
    public GameObject roomPrefab;

    private Dictionary<string, GameObject> spawnedRooms = new Dictionary<string, GameObject>();
    private float lastPollTime = 0f;
    private int lastRoomCount = 0;
    private float lastSurface = 0f;
    private string lastLayoutSignature = "";

    // Adaptive polling state
    private float activeModeTimer = 0f;        // Countdown after voice command
    private const float ActiveModeDuration = 12f; // Stay fast for 12s after send
    private float CurrentInterval => activeModeTimer > 0 ? fastRefreshInterval : idleRefreshInterval;

    /// <summary>Call this from VoiceRecorder when a request is sent to activate fast polling.</summary>
    public void NotifyCommandSent()
    {
        activeModeTimer = ActiveModeDuration;
    }

    void Start()
    {
        if (enableAutoRefresh)
            Debug.Log($"[LayoutReceiver] Auto-refresh: fast={fastRefreshInterval}s / idle={idleRefreshInterval}s");
    }

    void Update()
    {
        if (activeModeTimer > 0f)
            activeModeTimer -= Time.deltaTime;

        if (enableAutoRefresh)
        {
            lastPollTime += Time.deltaTime;
            if (lastPollTime >= CurrentInterval)
            {
                FetchLayoutSilent();
                lastPollTime = 0f;
            }
        }
    }

    // Version silencieuse pour l'auto-refresh
    private void FetchLayoutSilent()
    {
        StartCoroutine(GetLayoutCoroutine(false));
    }

    // Refresh manuel (si besoin)
    [ContextMenu("Rafraîchir maintenant")]
    public void FetchLayout()
    {
        StartCoroutine(GetLayoutCoroutine(true));
    }

    IEnumerator GetLayoutCoroutine(bool showLogs = true)
    {
        string url = $"{apiBaseUrl}/api/layout/{houseId}";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    ApiLayoutResponse response = JsonUtility.FromJson<ApiLayoutResponse>(req.downloadHandler.text);

                    // Skip rendering if the backend is still executing LLM tools
                    if (response.building_in_progress || response.status == "building")
                    {
                        if (showLogs) Debug.Log("[LayoutReceiver] Build in progress, skipping render...");
                        yield break;
                    }

                    if (response.rooms != null && response.rooms.Count > 0)
                    {
                        // Validate layout has at least 2 rooms (corridor + 1 real room)
                        // to avoid rendering a partial init-only state
                        if (response.rooms.Count < 2 && response.house != null
                            && response.house.surface_totale > 0
                            && response.house.surface_utilisee < response.house.surface_totale * 0.1f)
                        {
                            if (showLogs) Debug.Log("[LayoutReceiver] Layout looks incomplete (only corridor), waiting...");
                            yield break;
                        }

                        // Détecte les changements sur le layout complet pour éviter les rerenders répétés.
                        string currentSignature = BuildLayoutSignature(response.rooms, response.house);
                        bool hasChanged = currentSignature != lastLayoutSignature;

                        if (hasChanged)
                        {
                            RenderRooms(response.rooms);
                            lastRoomCount = response.rooms.Count;
                            lastSurface = response.house != null ? response.house.surface_utilisee : 0f;
                            lastLayoutSignature = currentSignature;

                            if (showLogs)
                            {
                                Debug.Log($"[LayoutReceiver] {response.rooms.Count} piece(s) mise(s) a jour");
                                if (response.house != null)
                                {
                                    Debug.Log($"[LayoutReceiver] Surface: {response.house.surface_utilisee}/{response.house.surface_totale} m2");
                                }
                            }
                        }
                    }
                }
                catch (System.Exception e)
                {
                    if (showLogs) Debug.LogError($"[LayoutReceiver] Erreur JSON: {e.Message}");
                }
            }
            else if (req.responseCode == 404)
            {
                // Silencieux en 404 (maison pas encore créée)
            }
        }
    }

    void RenderRooms(List<RoomDto> rooms)
    {
        // Nettoyage
        foreach (var go in spawnedRooms.Values)
        {
            if (go != null) Destroy(go);
        }
        spawnedRooms.Clear();

        // Générer des portes implicites pour les pièces adjacentes si l'API n'envoie pas la liste
        float eps = 0.01f;
        Dictionary<string, List<RoomDoor>> implicitDoors = new Dictionary<string, List<RoomDoor>>();
        for (int i = 0; i < rooms.Count; i++)
        {
            for (int j = i + 1; j < rooms.Count; j++)
            {
                var A = rooms[i];
                var B = rooms[j];
                float ax0 = A.position.x;
                float ax1 = A.position.x + A.dimensions.width;
                float az0 = A.position.z;
                float az1 = A.position.z + A.dimensions.depth;

                float bx0 = B.position.x;
                float bx1 = B.position.x + B.dimensions.width;
                float bz0 = B.position.z;
                float bz1 = B.position.z + B.dimensions.depth;

                // East/West adjacency (A right edge touches B left edge)
                if (Mathf.Abs(ax1 - bx0) <= eps)
                {
                    // overlap on Z?
                    float iz0 = Mathf.Max(az0, bz0);
                    float iz1 = Mathf.Min(az1, bz1);
                    if (iz1 - iz0 > 0.5f)
                    {
                        float doorZ = (iz0 + iz1) / 2.0f;
                        float doorX = ax1;
                        RoomDoor dA = new RoomDoor { id = $"door_{A.id}_to_{B.id}", connected_to = B.id, width = 1.0f, type = "implicit", position = new Vec3 { x = doorX, y = 0f, z = doorZ } };
                        RoomDoor dB = new RoomDoor { id = $"door_{B.id}_to_{A.id}", connected_to = A.id, width = 1.0f, type = "implicit", position = new Vec3 { x = doorX, y = 0f, z = doorZ } };
                        if (!implicitDoors.ContainsKey(A.id)) implicitDoors[A.id] = new List<RoomDoor>();
                        if (!implicitDoors.ContainsKey(B.id)) implicitDoors[B.id] = new List<RoomDoor>();
                        implicitDoors[A.id].Add(dA);
                        implicitDoors[B.id].Add(dB);
                    }
                }

                // West/ East (A left edge touches B right edge)
                if (Mathf.Abs(bx1 - ax0) <= eps)
                {
                    float iz0 = Mathf.Max(az0, bz0);
                    float iz1 = Mathf.Min(az1, bz1);
                    if (iz1 - iz0 > 0.5f)
                    {
                        float doorZ = (iz0 + iz1) / 2.0f;
                        float doorX = bx1;
                        RoomDoor dA = new RoomDoor { id = $"door_{A.id}_to_{B.id}", connected_to = B.id, width = 1.0f, type = "implicit", position = new Vec3 { x = doorX, y = 0f, z = doorZ } };
                        RoomDoor dB = new RoomDoor { id = $"door_{B.id}_to_{A.id}", connected_to = A.id, width = 1.0f, type = "implicit", position = new Vec3 { x = doorX, y = 0f, z = doorZ } };
                        if (!implicitDoors.ContainsKey(A.id)) implicitDoors[A.id] = new List<RoomDoor>();
                        if (!implicitDoors.ContainsKey(B.id)) implicitDoors[B.id] = new List<RoomDoor>();
                        implicitDoors[A.id].Add(dA);
                        implicitDoors[B.id].Add(dB);
                    }
                }

                // North/South adjacency (A top touches B bottom)
                if (Mathf.Abs(az1 - bz0) <= eps)
                {
                    float ix0 = Mathf.Max(ax0, bx0);
                    float ix1 = Mathf.Min(ax1, bx1);
                    if (ix1 - ix0 > 0.5f)
                    {
                        float doorX = (ix0 + ix1) / 2.0f;
                        float doorZ = az1;
                        RoomDoor dA = new RoomDoor { id = $"door_{A.id}_to_{B.id}", connected_to = B.id, width = 1.0f, type = "implicit", position = new Vec3 { x = doorX, y = 0f, z = doorZ } };
                        RoomDoor dB = new RoomDoor { id = $"door_{B.id}_to_{A.id}", connected_to = A.id, width = 1.0f, type = "implicit", position = new Vec3 { x = doorX, y = 0f, z = doorZ } };
                        if (!implicitDoors.ContainsKey(A.id)) implicitDoors[A.id] = new List<RoomDoor>();
                        if (!implicitDoors.ContainsKey(B.id)) implicitDoors[B.id] = new List<RoomDoor>();
                        implicitDoors[A.id].Add(dA);
                        implicitDoors[B.id].Add(dB);
                    }
                }

                // South/North adjacency (B top touches A bottom)
                if (Mathf.Abs(bz1 - az0) <= eps)
                {
                    float ix0 = Mathf.Max(ax0, bx0);
                    float ix1 = Mathf.Min(ax1, bx1);
                    if (ix1 - ix0 > 0.5f)
                    {
                        float doorX = (ix0 + ix1) / 2.0f;
                        float doorZ = bz1;
                        RoomDoor dA = new RoomDoor { id = $"door_{A.id}_to_{B.id}", connected_to = B.id, width = 1.0f, type = "implicit", position = new Vec3 { x = doorX, y = 0f, z = doorZ } };
                        RoomDoor dB = new RoomDoor { id = $"door_{B.id}_to_{A.id}", connected_to = A.id, width = 1.0f, type = "implicit", position = new Vec3 { x = doorX, y = 0f, z = doorZ } };
                        if (!implicitDoors.ContainsKey(A.id)) implicitDoors[A.id] = new List<RoomDoor>();
                        if (!implicitDoors.ContainsKey(B.id)) implicitDoors[B.id] = new List<RoomDoor>();
                        implicitDoors[A.id].Add(dA);
                        implicitDoors[B.id].Add(dB);
                    }
                }
            }
        }

        // Génération
        foreach (var room in rooms)
        {
            // if API did not provide doors, inject implicit doors
            if ((room.doors == null || room.doors.Count == 0) && implicitDoors.ContainsKey(room.id))
            {
                room.doors = implicitDoors[room.id];
            }

            CreateRoomFull(room);
        }

        Debug.Log($"🏠 Scène mise à jour : {rooms.Count} pièce(s)");
    }

    string BuildLayoutSignature(List<RoomDto> rooms, HouseDto house)
    {
        List<string> parts = new List<string>();

        if (house != null)
        {
            parts.Add($"house:{house.house_id}:{house.surface_totale:0.00}:{house.surface_utilisee:0.00}");
        }

        List<RoomDto> orderedRooms = new List<RoomDto>(rooms);
        orderedRooms.Sort((a, b) => string.CompareOrdinal(a.id, b.id));

        foreach (var room in orderedRooms)
        {
            parts.Add($"room:{room.id}:{room.type}:{room.position.x:0.00}:{room.position.z:0.00}:{room.dimensions.width:0.00}:{room.dimensions.depth:0.00}:{room.dimensions.height:0.00}");

            if (room.doors != null)
            {
                foreach (var door in room.doors)
                {
                    parts.Add($"door:{room.id}:{door.id}:{door.connected_to}:{door.position.x:0.00}:{door.position.z:0.00}");
                }
            }

            if (room.windows != null)
            {
                foreach (var window in room.windows)
                {
                    parts.Add($"window:{room.id}:{window.id}:{window.wall}:{window.position.x:0.00}:{window.position.y:0.00}:{window.position.z:0.00}");
                }
            }
        }

        return string.Join("|", parts);
    }

    void CreateRoomFull(RoomDto room)
    {
        GameObject roomObj = new GameObject($"{room.type}_{room.id}");
        // La position locale est le coin inférieur gauche dans notre logique, 
        // Unity a son pivot au centre des primitifs.
        float cx = room.position.x + (room.dimensions.width / 2.0f);
        float cz = room.position.z + (room.dimensions.depth / 2.0f);
        roomObj.transform.position = new Vector3(cx, 0, cz);

        // Floor
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.parent = roomObj.transform;
        floor.transform.localPosition = new Vector3(0, 0, 0);
        // Plane is 10x10 units in Unity by default
        floor.transform.localScale = new Vector3(room.dimensions.width / 10.0f, 1, room.dimensions.depth / 10.0f);
        
        Renderer floorRend = floor.GetComponent<Renderer>();
        if (floorRend != null) floorRend.material.color = GetColorByType(room.type);

        // Walls
        float wallThickness = 0.2f;
        float h = room.dimensions.height;
        float w = room.dimensions.width;
        float d = room.dimensions.depth;

        // Collecte des portes par mur avec leur position locale (axe X pour North/South, axe Z pour East/West)
        float? northDoorCenterLocal = null;
        float? southDoorCenterLocal = null;
        float? eastDoorCenterLocal = null;
        float? westDoorCenterLocal = null;

        if (room.doors != null)
        {
            foreach (var door in room.doors)
            {
                float dx = door.position.x;
                float dz = door.position.z;

                float rLeft = room.position.x;
                float rRight = room.position.x + room.dimensions.width;
                float rBottom = room.position.z;
                float rTop = room.position.z + room.dimensions.depth;

                float epsilon = 0.5f;

                if (Mathf.Abs(dz - rTop) <= epsilon)
                {
                    // position X locale centré : doorX - (room.position.x + width/2)
                    float localX = dx - (room.position.x + room.dimensions.width / 2.0f);
                    northDoorCenterLocal = localX;
                }
                else if (Mathf.Abs(dz - rBottom) <= epsilon)
                {
                    float localX = dx - (room.position.x + room.dimensions.width / 2.0f);
                    southDoorCenterLocal = localX;
                }
                else if (Mathf.Abs(dx - rRight) <= epsilon)
                {
                    float localZ = dz - (room.position.z + room.dimensions.depth / 2.0f);
                    eastDoorCenterLocal = localZ;
                }
                else if (Mathf.Abs(dx - rLeft) <= epsilon)
                {
                    float localZ = dz - (room.position.z + room.dimensions.depth / 2.0f);
                    westDoorCenterLocal = localZ;
                }
            }
        }

        int doorCount = (northDoorCenterLocal.HasValue ? 1 : 0) + (southDoorCenterLocal.HasValue ? 1 : 0) +
                        (eastDoorCenterLocal.HasValue ? 1 : 0) + (westDoorCenterLocal.HasValue ? 1 : 0);
        Debug.Log($"Room {room.id}: {doorCount} porte(s) détectée(s)");

        // Collecte des fenêtres par mur
        RoomWindow northWindow = null, southWindow = null, eastWindow = null, westWindow = null;
        
        if (room.windows != null)
        {
            foreach (var window in room.windows)
            {
                float wx = window.position.x;
                float wz = window.position.z;

                float rLeft = room.position.x;
                float rRight = room.position.x + room.dimensions.width;
                float rBottom = room.position.z;
                float rTop = room.position.z + room.dimensions.depth;

                float epsilon = 0.5f;

                if (Mathf.Abs(wz - rTop) <= epsilon && northWindow == null) northWindow = window;
                else if (Mathf.Abs(wz - rBottom) <= epsilon && southWindow == null) southWindow = window;
                else if (Mathf.Abs(wx - rRight) <= epsilon && eastWindow == null) eastWindow = window;
                else if (Mathf.Abs(wx - rLeft) <= epsilon && westWindow == null) westWindow = window;
            }
        }

        // North
        if (northDoorCenterLocal.HasValue) CreateWallWithDoor(roomObj, "Wall_N", new Vector3(0, h/2, d/2), new Vector3(w, h, wallThickness), true, northDoorCenterLocal.Value);
        else if (northWindow != null) CreateWallWithWindow(roomObj, "Wall_N", new Vector3(0, h/2, d/2), new Vector3(w, h, wallThickness), true, northWindow);
        else CreateWall(roomObj, "Wall_N", new Vector3(0, h/2, d/2), new Vector3(w, h, wallThickness));

        // South
        if (southDoorCenterLocal.HasValue) CreateWallWithDoor(roomObj, "Wall_S", new Vector3(0, h/2, -d/2), new Vector3(w, h, wallThickness), true, southDoorCenterLocal.Value);
        else if (southWindow != null) CreateWallWithWindow(roomObj, "Wall_S", new Vector3(0, h/2, -d/2), new Vector3(w, h, wallThickness), true, southWindow);
        else CreateWall(roomObj, "Wall_S", new Vector3(0, h/2, -d/2), new Vector3(w, h, wallThickness));

        // East
        if (eastDoorCenterLocal.HasValue) CreateWallWithDoor(roomObj, "Wall_E", new Vector3(w/2, h/2, 0), new Vector3(wallThickness, h, d), false, eastDoorCenterLocal.Value);
        else if (eastWindow != null) CreateWallWithWindow(roomObj, "Wall_E", new Vector3(w/2, h/2, 0), new Vector3(wallThickness, h, d), false, eastWindow);
        else CreateWall(roomObj, "Wall_E", new Vector3(w/2, h/2, 0), new Vector3(wallThickness, h, d));

        // West
        if (westDoorCenterLocal.HasValue) CreateWallWithDoor(roomObj, "Wall_W", new Vector3(-w/2, h/2, 0), new Vector3(wallThickness, h, d), false, westDoorCenterLocal.Value);
        else if (westWindow != null) CreateWallWithWindow(roomObj, "Wall_W", new Vector3(-w/2, h/2, 0), new Vector3(wallThickness, h, d), false, westWindow);
        else CreateWall(roomObj, "Wall_W", new Vector3(-w/2, h/2, 0), new Vector3(wallThickness, h, d));

        // Ajout du toit
        CreateRoof(room, roomObj);

        spawnedRooms[room.id] = roomObj;
    }
    
    void CreateRoof(RoomDto room, GameObject roomContainer)
    {
        GameObject roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
        roof.name = "Roof";
        
        // Supprimer le collider qui bloquerait le passage
        Collider roofCollider = roof.GetComponent<Collider>();
        if (roofCollider != null) DestroyImmediate(roofCollider);
        
        roof.transform.parent = roomContainer.transform;
        
        float h = room.dimensions.height;
        float w = room.dimensions.width;
        float d = room.dimensions.depth;
        float thickness = 0.1f;
        
        // Placé exactement au sommet des murs (Y = height)
        roof.transform.localPosition = new Vector3(0, h, 0);
        roof.transform.localScale = new Vector3(w, thickness, d);
        
        Renderer rend = roof.GetComponent<Renderer>();
        if (rend != null)
        {
            rend.material.color = new Color(0.85f, 0.85f, 0.85f); // Blanc cassé / Gris très clair
        }
    }

    void CreateWall(GameObject parent, string name, Vector3 pos, Vector3 scale)
    {
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = name;
        
        // CRUCIAL : Supprimer le collider qui bloque physiquement le passage
        Collider wallCollider = wall.GetComponent<Collider>();
        if (wallCollider != null) DestroyImmediate(wallCollider);
        
        wall.transform.parent = parent.transform;
        wall.transform.localPosition = pos;
        wall.transform.localScale = scale;
        wall.GetComponent<Renderer>().material.color = Color.white;
    }

    void CreateWallWithWindow(GameObject parent, string name, Vector3 pos, Vector3 scale, bool isHorizontal, RoomWindow window)
    {
        float windowWidth = Mathf.Max(0.01f, window.width > 0f ? window.width : 1.5f);
        float windowHeight = Mathf.Max(0.01f, window.height > 0f ? window.height : 1.2f);
        float minSegment = 0.01f;

        float roomCenterX = parent.transform.position.x;
        float roomCenterZ = parent.transform.position.z;
        float windowCenterLocal = isHorizontal
            ? window.position.x - roomCenterX
            : window.position.z - roomCenterZ;

        float wallMin = isHorizontal ? -scale.x / 2.0f : -scale.z / 2.0f;
        float wallMax = isHorizontal ? scale.x / 2.0f : scale.z / 2.0f;
        float holeLeft = windowCenterLocal - windowWidth / 2.0f;
        float holeRight = windowCenterLocal + windowWidth / 2.0f;

        float wallBottom = -scale.y / 2.0f;
        float wallTop = scale.y / 2.0f;
        float windowCenterYLocal = window.position.y - parent.transform.position.y - scale.y / 2.0f;
        float windowBottom = windowCenterYLocal - windowHeight / 2.0f;
        float windowTop = windowCenterYLocal + windowHeight / 2.0f;

        float leftSpan = Mathf.Max(0f, holeLeft - wallMin);
        float rightSpan = Mathf.Max(0f, wallMax - holeRight);
        float sillHeight = Mathf.Max(0f, windowBottom - wallBottom);
        float lintelHeight = Mathf.Max(0f, wallTop - windowTop);
        float wallThickness = isHorizontal ? scale.z : scale.x;

        if (leftSpan > minSegment)
        {
            float center = wallMin + leftSpan / 2.0f;
            CreateWall(parent, name + "_Left", pos + (isHorizontal ? new Vector3(center, 0, 0) : new Vector3(0, 0, center)),
                isHorizontal ? new Vector3(leftSpan, scale.y, wallThickness) : new Vector3(wallThickness, scale.y, leftSpan));
        }

        if (rightSpan > minSegment)
        {
            float center = holeRight + rightSpan / 2.0f;
            CreateWall(parent, name + "_Right", pos + (isHorizontal ? new Vector3(center, 0, 0) : new Vector3(0, 0, center)),
                isHorizontal ? new Vector3(rightSpan, scale.y, wallThickness) : new Vector3(wallThickness, scale.y, rightSpan));
        }

        if (sillHeight > minSegment)
        {
            float centerY = wallBottom + sillHeight / 2.0f;
            CreateWall(parent, name + "_Sill", pos + new Vector3(0, centerY, 0),
                isHorizontal ? new Vector3(windowWidth, sillHeight, wallThickness) : new Vector3(wallThickness, sillHeight, windowWidth));
        }

        if (lintelHeight > minSegment)
        {
            float centerY = windowTop + lintelHeight / 2.0f;
            CreateWall(parent, name + "_Lintel", pos + new Vector3(0, centerY, 0),
                isHorizontal ? new Vector3(windowWidth, lintelHeight, wallThickness) : new Vector3(wallThickness, lintelHeight, windowWidth));
        }

        // L'ouverture reste vide pour conserver une vraie vue vers l'extérieur.
    }

    void CreateWallWithDoor(GameObject parent, string name, Vector3 pos, Vector3 scale, bool isHorizontal, float doorCenterLocal)
    {
        // doorCenterLocal is the center of the door in the wall local axis (X for horizontal walls, Z for vertical walls)
        float doorWidth = 1.0f;
        float minSegment = 0.01f;

        if (isHorizontal)
        {
            // Wall spans from -scale.x/2 .. +scale.x/2 in local X
            float x1 = -scale.x / 2.0f;
            float holeLeft = doorCenterLocal - doorWidth / 2.0f;
            float holeRight = doorCenterLocal + doorWidth / 2.0f;

            float leftLength = holeLeft - x1;
            float rightLength = (scale.x / 2.0f) - holeRight;

            if (leftLength > minSegment)
            {
                float center = (x1 + holeLeft) / 2.0f;
                CreateWall(parent, name + "_L", pos + new Vector3(center, 0, 0), new Vector3(leftLength, scale.y, scale.z));
            }

            if (rightLength > minSegment)
            {
                float center = (holeRight + (scale.x / 2.0f)) / 2.0f;
                CreateWall(parent, name + "_R", pos + new Vector3(center, 0, 0), new Vector3(rightLength, scale.y, scale.z));
            }

            // Create a simple door frame (left jamb, right jamb, lintel) for visual clarity
            float jambThickness = 0.06f;
            float jambDepth = Mathf.Max(0.02f, scale.z * 0.6f);
            // Pour obtenir une porte affleurante au plafond, on retire le lintel visible
            float lintelHeight = 0.0f; // pas de lintel visible
            // jambHeight remplit presque toute la hauteur du mur (petit jeu pour éviter z-fighting)
            float jambHeight = Mathf.Max(0.01f, scale.y - 0.02f);

            // Left jamb - position depuis le sol local (Y = -scale.y/2)
            Vector3 leftJambPos = pos + new Vector3(holeLeft - (jambThickness / 2.0f), -scale.y / 2.0f + jambHeight / 2.0f, 0);
            GameObject lj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lj.name = name + "_Jamb_L";
            DestroyImmediate(lj.GetComponent<Collider>());
            lj.transform.parent = parent.transform;
            lj.transform.localPosition = leftJambPos;
            lj.transform.localScale = new Vector3(jambThickness, jambHeight, jambDepth);
            lj.GetComponent<Renderer>().material.color = new Color(0.55f, 0.33f, 0.07f);

            // Right jamb
            Vector3 rightJambPos = pos + new Vector3(holeRight + (jambThickness / 2.0f), -scale.y / 2.0f + jambHeight / 2.0f, 0);
            GameObject rj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rj.name = name + "_Jamb_R";
            DestroyImmediate(rj.GetComponent<Collider>());
            rj.transform.parent = parent.transform;
            rj.transform.localPosition = rightJambPos;
            rj.transform.localScale = new Vector3(jambThickness, jambHeight, jambDepth);
            rj.GetComponent<Renderer>().material.color = new Color(0.55f, 0.33f, 0.07f);

            // Pas de lintel visible (ou très fin si lintelHeight > 0)
            if (lintelHeight > 0.001f)
            {
                Vector3 lintelPos = pos + new Vector3(doorCenterLocal, -scale.y / 2.0f + jambHeight + lintelHeight / 2.0f, 0);
                GameObject lintel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                lintel.name = name + "_Lintel";
                DestroyImmediate(lintel.GetComponent<Collider>());
                lintel.transform.parent = parent.transform;
                lintel.transform.localPosition = lintelPos;
                lintel.transform.localScale = new Vector3(doorWidth + jambThickness * 2.0f, lintelHeight, jambDepth);
                lintel.GetComponent<Renderer>().material.color = new Color(0.55f, 0.33f, 0.07f);
            }
        }
        else
        {
            // Vertical wall (East/West) spans -scale.z/2 .. +scale.z/2 in local Z
            float z1 = -scale.z / 2.0f;
            float holeLeft = doorCenterLocal - doorWidth / 2.0f; // along Z
            float holeRight = doorCenterLocal + doorWidth / 2.0f;

            float leftLength = holeLeft - z1;
            float rightLength = (scale.z / 2.0f) - holeRight;

            if (leftLength > minSegment)
            {
                float center = (z1 + holeLeft) / 2.0f;
                CreateWall(parent, name + "_L", pos + new Vector3(0, 0, center), new Vector3(scale.x, scale.y, leftLength));
            }

            if (rightLength > minSegment)
            {
                float center = (holeRight + (scale.z / 2.0f)) / 2.0f;
                CreateWall(parent, name + "_R", pos + new Vector3(0, 0, center), new Vector3(scale.x, scale.y, rightLength));
            }

            // Door frame
            float jambThickness = 0.06f;
            float jambDepth = Mathf.Max(0.02f, scale.x * 0.6f);
            // Suppression du lintel visible pour avoir une ouverture de porte jusqu'au plafond
            float lintelHeight = 0.0f;
            float jambHeight = Mathf.Max(0.01f, scale.y - 0.02f);

            // Left jamb (closer to negative Z) - position depuis le sol local (Y = -scale.y/2)
            Vector3 leftJambPos = pos + new Vector3(0, -scale.y / 2.0f + jambHeight / 2.0f, holeLeft - (jambThickness / 2.0f));
            GameObject lj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lj.name = name + "_Jamb_L";
            DestroyImmediate(lj.GetComponent<Collider>());
            lj.transform.parent = parent.transform;
            lj.transform.localPosition = leftJambPos;
            lj.transform.localScale = new Vector3(jambDepth, jambHeight, jambThickness);
            lj.GetComponent<Renderer>().material.color = new Color(0.55f, 0.33f, 0.07f);

            // Right jamb
            Vector3 rightJambPos = pos + new Vector3(0, -scale.y / 2.0f + jambHeight / 2.0f, holeRight + (jambThickness / 2.0f));
            GameObject rj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rj.name = name + "_Jamb_R";
            DestroyImmediate(rj.GetComponent<Collider>());
            rj.transform.parent = parent.transform;
            rj.transform.localPosition = rightJambPos;
            rj.transform.localScale = new Vector3(jambDepth, jambHeight, jambThickness);
            rj.GetComponent<Renderer>().material.color = new Color(0.55f, 0.33f, 0.07f);

            // Pas de lintel visible (ou très fin si lintelHeight > 0)
            if (lintelHeight > 0.001f)
            {
                Vector3 lintelPos = pos + new Vector3(0, -scale.y / 2.0f + jambHeight + lintelHeight / 2.0f, doorCenterLocal);
                GameObject lintel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                lintel.name = name + "_Lintel";
                DestroyImmediate(lintel.GetComponent<Collider>());
                lintel.transform.parent = parent.transform;
                lintel.transform.localPosition = lintelPos;
                lintel.transform.localScale = new Vector3(jambDepth, lintelHeight, doorWidth + jambThickness * 2.0f);
                lintel.GetComponent<Renderer>().material.color = new Color(0.55f, 0.33f, 0.07f);
            }
        }
    }

    Color GetColorByType(string type)
    {
        if (string.IsNullOrEmpty(type)) return Color.white;
        string t = type.ToLowerInvariant();
        
        if (t.Contains("salon")) return new Color(0.9f, 0.8f, 0.7f);
        if (t.Contains("chambre")) return new Color(0.7f, 0.8f, 0.9f);
        if (t.Contains("cuisine")) return new Color(0.8f, 0.9f, 0.7f);
        if (t.Contains("bain")) return new Color(0.9f, 0.7f, 0.8f);
        if (t.Contains("couloir")) return new Color(0.6f, 0.6f, 0.6f);
        
        return Color.white;
    }
}