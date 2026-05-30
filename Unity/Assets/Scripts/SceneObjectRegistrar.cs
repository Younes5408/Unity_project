using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// On Play start, finds all top-level House* GameObjects and POSTs their world
/// positions to /api/scene/register so the AI can resolve teleport destinations.
/// Attach to the Player or any persistent GameObject.
/// </summary>
public class SceneObjectRegistrar : MonoBehaviour
{
    [Header("API")]
    public string apiBaseUrl = "http://127.0.0.1:8000";

    [Header("Scope")]
    [Tooltip("Only register objects whose names start with these prefixes.")]
    public string[] prefixes = new string[] { "House", "Road", "Fence" };

    void Start()
    {
        StartCoroutine(RegisterObjects());
    }

    private IEnumerator RegisterObjects()
    {
        // Short delay so the scene is fully loaded
        yield return new WaitForSeconds(1.5f);

        var entries = new List<SceneObjectEntry>();
        // Recursively walk the scene: collect any GameObject whose name starts with a prefix.
        // This catches both root-level matches AND houses nested inside a "Houses" container.
        foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            CollectMatching(root.transform, entries);
        }
        // Filter out plural container names (Houses/Roads/Fences) — keep only individual items.
        entries.RemoveAll(e => e.name.Equals("Houses", System.StringComparison.OrdinalIgnoreCase)
                             || e.name.Equals("Roads",  System.StringComparison.OrdinalIgnoreCase)
                             || e.name.Equals("Fences", System.StringComparison.OrdinalIgnoreCase));

        if (entries.Count == 0)
        {
            Debug.Log("[SceneObjectRegistrar] No matching objects found to register.");
            yield break;
        }

        string json = "{\"objects\":[";
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            json += $"{{\"name\":\"{e.name}\",\"x\":{e.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},\"z\":{e.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}}}";
            if (i < entries.Count - 1) json += ",";
        }
        json += "]}";

        string url = apiBaseUrl + "/api/scene/register";
        byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(bodyBytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
                Debug.Log($"[SceneObjectRegistrar] Registered {entries.Count} objects: {req.downloadHandler.text}");
            else
                Debug.LogWarning($"[SceneObjectRegistrar] Registration failed: {req.error}");
        }
    }

    private bool MatchesPrefix(string name)
    {
        foreach (var prefix in prefixes)
            if (name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void CollectMatching(Transform t, List<SceneObjectEntry> entries)
    {
        if (MatchesPrefix(t.name) && !t.name.Contains("("))
        {
            // Skip Unity duplicate suffixes "(1)", "(2)" — they'd collide on unique index.
            entries.Add(new SceneObjectEntry
            {
                name = t.name,
                x = t.position.x,
                z = t.position.z
            });
        }
        foreach (Transform child in t)
            CollectMatching(child, entries);
    }

    [System.Serializable]
    private class SceneObjectEntry
    {
        public string name;
        public float x;
        public float z;
    }
}
