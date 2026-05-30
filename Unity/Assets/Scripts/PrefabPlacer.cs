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
                if (InstantiatePrefab(p))
                {
                    _placedIds.Add(p.id);
                    // Re-ACK pending replays so they don't keep showing in /pending.
                    if (p.status == "pending") StartCoroutine(ConfirmPlacement(p.id));
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

                        bool success = InstantiatePrefab(placement);
                        if (success)
                        {
                            _placedIds.Add(placement.id);
                            StartCoroutine(ConfirmPlacement(placement.id));
                        }
                    }
                }
            }
            // Silently ignore errors — will retry on next poll
        }
        _isPolling = false;
    }

    private bool InstantiatePrefab(PlacementData placement)
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
            return false;
        }

        // Position and rotation
        Vector3 position = new Vector3(placement.position.x, placement.position.y, placement.position.z);
        Quaternion rotation = Quaternion.Euler(0f, placement.rotation_y, 0f);

        // Floor-aware raycast: for furniture, raycast down from a high position to find ground
        if (placement.category == "furniture")
        {
            Vector3 rayStart = new Vector3(position.x, 50f, position.z); // start high up
            RaycastHit hit;
            if (Physics.Raycast(rayStart, Vector3.down, out hit, 100f))
            {
                position.y = hit.point.y; // place on the ground where raycast hit
            }
            else
            {
                position.y = 0.1f; // fallback: just above y=0 if no ground found
            }
        }

        // Instantiate under the correct container
        Transform container = GetContainerForCategory(placement.category);
        GameObject instance = Instantiate(prefab, position, rotation, container);
        instance.name = $"{placement.object_type}_{placement.id}";

        Debug.Log($"[PrefabPlacer] Placed {placement.object_type} at ({position.x:F1}, {position.y:F1}, {position.z:F1}) id={placement.id}");

        // Fire analytics so total_placements + top_objects populate in the
        // dashboard. Static helper no-ops if AnalyticsTracker is missing.
        AnalyticsTracker.LogPlacement(placement.object_type, placement.prefab ?? "", position);

        return true;
    }

    private IEnumerator ConfirmPlacement(string placementId)
    {
        string url = $"{apiBaseUrl}/api/placements/{placementId}/confirm";

        using (UnityWebRequest req = UnityWebRequest.PostWwwForm(url, ""))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[PrefabPlacer] Failed to confirm placement {placementId}: {req.error}");
            }
        }
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
        public float rotation_y;
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
