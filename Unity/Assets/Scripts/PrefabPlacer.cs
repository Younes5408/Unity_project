using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Polls the backend for pending prefab placements and instantiates them in the scene.
/// Works alongside LayoutReceiver — this handles whole prefab objects (houses, roads, fences)
/// while LayoutReceiver handles procedural room geometry.
/// </summary>
public class PrefabPlacer : MonoBehaviour
{
    [Header("API Settings")]
    [Tooltip("Base URL of the Python backend.")]
    public string apiBaseUrl = "http://127.0.0.1:8000";

    [Header("Polling")]
    [Tooltip("How often to check for pending placements (seconds).")]
    public float pollInterval = 2f;

    [Header("Prefab Base Path")]
    [Tooltip("Path inside Resources/ where ModularHousePack1 prefabs are located.")]
    public string prefabBasePath = "ModularHousePack1";

    [Header("House Scoping")]
    [Tooltip("Scope all placement queries to this house_id (must match VoiceRecorder.houseId).")]
    public string houseId = "maison_001";

    [Header("Scene Containers (auto-found if null)")]
    public Transform housesContainer;
    public Transform roadsContainer;
    public Transform fencesContainer;
    public Transform groundsContainer;

    private float _nextPollTime;
    private bool _isPolling;
    private HashSet<string> _placedIds = new HashSet<string>();

    void Start()
    {
        // Auto-find containers in the scene hierarchy
        if (housesContainer == null) housesContainer = FindOrCreateContainer("Houses");
        if (roadsContainer == null) roadsContainer = FindOrCreateContainer("Roads");
        if (fencesContainer == null) fencesContainer = FindOrCreateContainer("Fences");
        if (groundsContainer == null) groundsContainer = FindOrCreateContainer("Grounds");

        // Replay everything the AI has saved for this house from previous sessions.
        // Baked-in scene GameObjects are loaded by Unity from the .unity file — this
        // only adds AI-created prefabs on top, instantiated as children of the
        // containers (siblings of, but distinct from, the originals).
        StartCoroutine(ReplaySavedPlacements());

        _nextPollTime = Time.time + 1f; // start polling after 1 second
    }

    private IEnumerator ReplaySavedPlacements()
    {
        string url = $"{apiBaseUrl}/api/placements/all?house_id={UnityWebRequest.EscapeURL(houseId)}";
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.timeout = 8;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[PrefabPlacer] Replay failed: " + req.error);
                yield break;
            }
            var response = JsonUtility.FromJson<PlacementsResponse>(req.downloadHandler.text);
            if (response == null || response.placements == null) yield break;

            int replayed = 0;
            foreach (var p in response.placements)
            {
                if (string.IsNullOrEmpty(p.id) || _placedIds.Contains(p.id)) continue;
                GameObject instance = InstantiatePrefab(p);
                if (instance != null)
                {
                    _placedIds.Add(p.id);
                    // Re-ACK pending replays so they don't keep showing in /pending.
                    if (p.status == "pending") StartCoroutine(ConfirmPlacement(p.id, instance.transform.position, instance.transform.eulerAngles));
                    replayed++;
                }
            }
            Debug.Log($"[PrefabPlacer] Replayed {replayed} saved placements for house={houseId}");
        }
    }

    void Update()
    {
        if (Time.time >= _nextPollTime && !_isPolling)
        {
            _nextPollTime = Time.time + pollInterval;
            StartCoroutine(PollPendingPlacements());
        }
    }

    private IEnumerator PollPendingPlacements()
    {
        _isPolling = true;
        string url = $"{apiBaseUrl}/api/placements/pending?house_id={UnityWebRequest.EscapeURL(houseId)}";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                string json = req.downloadHandler.text;
                PlacementsResponse response = JsonUtility.FromJson<PlacementsResponse>(json);

                if (response != null && response.placements != null)
                {
                    foreach (var placement in response.placements)
                    {
                        if (string.IsNullOrEmpty(placement.id)) continue;
                        if (_placedIds.Contains(placement.id)) continue;

                        GameObject instance = InstantiatePrefab(placement);
                        if (instance != null)
                        {
                            _placedIds.Add(placement.id);
                            StartCoroutine(ConfirmPlacement(placement.id, instance.transform.position, instance.transform.eulerAngles));
                        }
                    }
                }
            }
            // Silently ignore errors — will retry on next poll
        }
        _isPolling = false;
    }

    private GameObject InstantiatePrefab(PlacementData placement)
    {
        // Build the Resources path: e.g. "ModularHousePack1/Houses/House3"
        string resourcePath = $"{prefabBasePath}/{placement.prefab}";
        GameObject prefab = Resources.Load<GameObject>(resourcePath);

        if (prefab == null)
        {
            // Fallback: try without base path
            prefab = Resources.Load<GameObject>(placement.prefab);
        }

        if (prefab == null)
        {
            Debug.LogWarning($"[PrefabPlacer] Prefab not found at Resources/{resourcePath}. " +
                           $"Ensure prefabs are copied to Assets/Resources/{prefabBasePath}/");
            return null;
        }

        // Position and rotation
        Vector3 position = new Vector3(placement.position.x, placement.position.y, placement.position.z);
        Quaternion rotation = Quaternion.Euler(placement.rotation_x, placement.rotation_y, placement.rotation_z);

        Debug.Log($"[PrefabPlacer] InstantiatePrefab: '{placement.object_type}' strategy='{placement.placement_strategy}' category='{placement.category}' status='{placement.status}'");

        // Raycast-based placement strategies (only run for pending placements to find dynamic anchors)
        if (placement.status == "pending")
        {
            if (placement.placement_strategy == "wall_aware")
            {
                Camera mainCam = Camera.main;
                if (mainCam != null)
                {
                    Vector3 rayStart = mainCam.transform.position;
                    Vector3 rayDir = mainCam.transform.forward;
                    RaycastHit hit;
                    if (Physics.Raycast(rayStart, rayDir, out hit, 15f))
                    {
                        position = hit.point;
                        // Align rotation so local up points along wall normal
                        rotation = Quaternion.FromToRotation(Vector3.up, hit.normal) * Quaternion.Euler(0f, placement.rotation_y, 0f);
                        Debug.Log($"[PrefabPlacer] Wall-aware hit '{hit.collider.gameObject.name}' at {position} normal={hit.normal}");
                    }
                    else
                    {
                        position = rayStart + rayDir * 3f;
                        Debug.Log($"[PrefabPlacer] Wall-aware missed, using fallback {position}");
                    }
                }
            }
            else if (placement.placement_strategy == "ceiling_aware")
            {
                Camera mainCam = Camera.main;
                Vector3 rayStart = position;
                if (mainCam != null)
                {
                    // Start raycast at player's horizontal coordinates at head level to find ceiling above head
                    rayStart = new Vector3(mainCam.transform.position.x, mainCam.transform.position.y, mainCam.transform.position.z);
                }
                
                RaycastHit hit;
                if (Physics.Raycast(rayStart, Vector3.up, out hit, 20f))
                {
                    position = hit.point; // exactly on the ceiling directly above the player's head
                    // Face down from ceiling
                    rotation = Quaternion.FromToRotation(Vector3.up, hit.normal) * Quaternion.Euler(0f, placement.rotation_y, 0f);
                    Debug.Log($"[PrefabPlacer] Ceiling-aware hit '{hit.collider.gameObject.name}' at {position} normal={hit.normal}");
                }
                else
                {
                    position.y = 3.0f; // fallback ceiling height
                    Debug.Log($"[PrefabPlacer] Ceiling-aware missed, using fallback Y=3.0");
                }
            }
            else if (placement.placement_strategy == "floor_aware" || placement.category == "furniture")
            {
                float startY = 50f; // default fallback
                Camera mainCam = Camera.main;
                if (mainCam != null)
                {
                    startY = mainCam.transform.position.y + 0.2f; // start just above player head level but below ceiling
                }
                
                // Determine horizontal offset directions relative to the player's gaze
                Vector3 localRight = Vector3.right;
                Vector3 localForward = Vector3.forward;
                if (mainCam != null)
                {
                    localRight = mainCam.transform.right;
                    localRight.y = 0f;
                    localRight.Normalize();
                    
                    localForward = mainCam.transform.forward;
                    localForward.y = 0f;
                    localForward.Normalize();
                }

                // Check candidates for a free spot on the floor
                Vector3[] candidates = new Vector3[]
                {
                    position,                       // Candidate 0: original center
                    position + localRight * 0.8f,   // Candidate 1: to the right
                    position - localRight * 0.8f,   // Candidate 2: to the left
                    position - localForward * 0.8f,  // Candidate 3: in front (closer to player)
                    position + localForward * 0.8f   // Candidate 4: behind (further from player)
                };

                Vector3 bestPos = position;
                bool foundSpot = false;

                foreach (var cand in candidates)
                {
                    Vector3 rayStart = new Vector3(cand.x, startY, cand.z);
                    RaycastHit[] hits = Physics.RaycastAll(rayStart, Vector3.down, 100f);
                    System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    
                    float candidateFloorY = 0f;
                    bool hitValidFloor = false;
                    
                    foreach (var hit in hits)
                    {
                        if (IsFurniture(hit.collider.gameObject)) continue;
                        candidateFloorY = hit.point.y;
                        hitValidFloor = true;
                        break;
                    }
                    
                    if (hitValidFloor)
                    {
                        // Check if this candidate position is already occupied by another furniture item
                        Vector3 testCenter = new Vector3(cand.x, candidateFloorY + 0.25f, cand.z);
                        Collider[] overlaps = Physics.OverlapSphere(testCenter, 0.35f);
                        bool isOccupied = false;
                        foreach (var col in overlaps)
                        {
                            if (IsFurniture(col.gameObject))
                            {
                                isOccupied = true;
                                break;
                            }
                        }

                        if (!isOccupied)
                        {
                            bestPos = new Vector3(cand.x, candidateFloorY, cand.z);
                            foundSpot = true;
                            Debug.Log($"[PrefabPlacer] Found unoccupied floor spot for '{placement.object_type}' at {bestPos}");
                            break;
                        }
                    }
                }

                if (!foundSpot)
                {
                    // Fallback to original raycast if no clean spot found
                    Vector3 rayStart = new Vector3(position.x, startY, position.z);
                    RaycastHit[] hits = Physics.RaycastAll(rayStart, Vector3.down, 100f);
                    System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    float finalY = 0.1f;
                    foreach (var hit in hits)
                    {
                        if (IsFurniture(hit.collider.gameObject)) continue;
                        finalY = hit.point.y;
                        break;
                    }
                    position.y = finalY;
                    Debug.Log($"[PrefabPlacer] No unoccupied spot found, using fallback position {position}");
                }
                else
                {
                    position = bestPos;
                }
            }
        }

        // Instantiate under the correct container
        Transform container = GetContainerForCategory(placement.category);
        GameObject instance = Instantiate(prefab, position, rotation, container);
        instance.name = $"{placement.object_type}_{placement.id}";

        // Apply default scaling to prevent huge objects (like tables) from taking up the entire space
        instance.transform.localScale = GetDefaultScaleForObject(placement.object_type);

        // If it is a house, disable its door colliders so the player can walk through them
        if (placement.category == "house" || placement.object_type.Contains("maison"))
        {
            int disabledCount = DisableDoorColliders(instance.transform);
            Debug.Log($"[PrefabPlacer] Disabled {disabledCount} door collider(s) in house '{instance.name}'");
        }

        // Add PointLight component dynamically if it is a light source and doesn't already have one
        if (placement.object_type.Contains("lampadaire") || placement.object_type.Contains("lampe") || placement.object_type.Contains("light"))
        {
            Light existingLight = instance.GetComponentInChildren<Light>();
            if (existingLight == null)
            {
                GameObject lightGo = new GameObject("DynamicLightSource");
                lightGo.transform.parent = instance.transform;

                float heightOffset = 0.5f; // default for table lamps
                float range = 8f;
                float intensity = 1.5f;
                Color lightColor = new Color(1f, 0.95f, 0.8f); // Warm light

                if (placement.object_type.Contains("lampadaire"))
                {
                    heightOffset = 3.5f; // high up for streetlights
                    range = 15f;
                    intensity = 2f;
                }
                else if (placement.object_type.Contains("fluorescent"))
                {
                    heightOffset = 0.1f;
                    range = 10f;
                    intensity = 1.2f;
                }

                lightGo.transform.localPosition = new Vector3(0f, heightOffset, 0f);

                Light lightComp = lightGo.AddComponent<Light>();
                lightComp.type = LightType.Point;
                lightComp.range = range;
                lightComp.intensity = intensity;
                lightComp.color = lightColor;
                lightComp.shadows = LightShadows.Soft; // Enable soft shadows for realism!

                Debug.Log($"[PrefabPlacer] Added dynamic {lightComp.type} light to '{instance.name}' at local Y={heightOffset}");
            }
        }

        Debug.Log($"[PrefabPlacer] Placed {placement.object_type} at ({position.x:F1}, {position.y:F1}, {position.z:F1}) id={placement.id}");

        // Fire analytics so total_placements + top_objects populate in the
        // dashboard. Static helper no-ops if AnalyticsTracker is missing.
        AnalyticsTracker.LogPlacement(placement.object_type, placement.prefab ?? "", position);

        return instance;
    }

    private IEnumerator ConfirmPlacement(string placementId, Vector3 finalPos, Vector3 finalEuler)
    {
        string url = $"{apiBaseUrl}/api/placements/{placementId}/confirm";

        // Serialize actual position and rotation to JSON
        string json = $"{{\n  \"x\": {finalPos.x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},\n  \"y\": {finalPos.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},\n  \"z\": {finalPos.z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},\n  \"rotation_x\": {finalEuler.x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},\n  \"rotation_y\": {finalEuler.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},\n  \"rotation_z\": {finalEuler.z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}\n}}";
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 5;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[PrefabPlacer] Failed to confirm placement {placementId}: {req.error}");
            }
        }
    }

    private int DisableDoorColliders(Transform root)
    {
        int count = 0;
        string[] doorKeywords = new string[] { "door", "porte", "doorframe", "portail" };
        foreach (Transform child in root)
        {
            string lowerName = child.name.ToLowerInvariant();
            bool isDoor = false;
            foreach (string kw in doorKeywords)
            {
                if (lowerName.Contains(kw))
                {
                    isDoor = true;
                    break;
                }
            }

            if (isDoor)
            {
                foreach (Collider col in child.GetComponentsInChildren<Collider>(true))
                {
                    if (col.enabled)
                    {
                        col.enabled = false;
                        count++;
                    }
                }
            }
            else
            {
                count += DisableDoorColliders(child);
            }
        }
        return count;
    }

    private bool IsFurniture(GameObject go)
    {
        string lowerName = go.name.ToLowerInvariant();
        if (lowerName.Contains("table") || 
            lowerName.Contains("chaise") || 
            lowerName.Contains("chair") || 
            lowerName.Contains("cuisine") || 
            lowerName.Contains("kitchen") || 
            lowerName.Contains("furniture") ||
            lowerName.Contains("ustensiles"))
        {
            return true;
        }
        
        Transform current = go.transform;
        while (current != null)
        {
            string parentName = current.name.ToLowerInvariant();
            if (parentName.Contains("table") || 
                parentName.Contains("chaise") || 
                parentName.Contains("chair") || 
                parentName.Contains("cuisine") || 
                parentName.Contains("kitchen") ||
                parentName.Contains("furniture"))
            {
                return true;
            }
            if (current == this.transform)
            {
                // Under PrefabPlacer itself
                return true;
            }
            current = current.parent;
        }
        return false;
    }

    private Vector3 GetDefaultScaleForObject(string objectType)
    {
        string lower = objectType.ToLowerInvariant();
        if (lower.Contains("table") || lower.Contains("chaise") || lower.Contains("chair"))
        {
            return new Vector3(0.6f, 0.6f, 0.6f); // Scale down tables and chairs to 60% size
        }
        return Vector3.one;
    }

    private Transform GetContainerForCategory(string category)
    {
        switch (category)
        {
            case "house": return housesContainer;
            case "road": return roadsContainer;
            case "fence": return fencesContainer;
            case "ground": return groundsContainer;
            default: return transform; // fallback to this GameObject
        }
    }

    private Transform FindOrCreateContainer(string name)
    {
        GameObject existing = GameObject.Find(name);
        if (existing != null) return existing.transform;
        // Create a new empty container
        GameObject container = new GameObject(name);
        return container.transform;
    }

    // ---- JSON Data Classes ----
    [System.Serializable]
    private class PlacementsResponse
    {
        public string status;
        public PlacementData[] placements;
    }

    [System.Serializable]
    private class PlacementData
    {
        public string id;
        public string object_type;
        public string prefab;
        public string label;
        public PlacementPosition position;
        public float rotation_x;
        public float rotation_y;
        public float rotation_z;
        public string category;
        public string status;
        public string placement_strategy; // "default" or "floor_aware"
    }

    [System.Serializable]
    private class PlacementPosition
    {
        public float x;
        public float y;
        public float z;
    }
}
