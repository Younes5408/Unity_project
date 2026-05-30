using UnityEngine;

/// <summary>
/// Disables colliders on door GameObjects inside the Houses container so the
/// player can walk through them regardless of their original prefab state.
/// Attach to the "Houses" container GameObject (auto-found if not set).
/// Non-destructive: colliders still exist, just disabled — re-enable to revert.
/// </summary>
public class AutoOpenDoors : MonoBehaviour
{
    [Tooltip("Root transform to search for door colliders. Auto-found if null.")]
    public Transform housesRoot;

    [Tooltip("Keywords in GameObject names that identify door objects (case-insensitive).")]
    public string[] doorKeywords = new string[] { "door", "porte", "doorframe", "portail" };

    void Awake()
    {
        if (housesRoot == null)
        {
            GameObject found = GameObject.Find("Houses");
            if (found != null) housesRoot = found.transform;
        }

        if (housesRoot == null)
        {
            Debug.LogWarning("[AutoOpenDoors] 'Houses' container not found. Attach this script to it or assign housesRoot.");
            return;
        }

        int disabled = DisableDoorColliders(housesRoot);
        Debug.Log($"[AutoOpenDoors] Disabled {disabled} door collider(s) under '{housesRoot.name}'.");
    }

    private int DisableDoorColliders(Transform root)
    {
        int count = 0;
        foreach (Transform child in root)
        {
            if (IsDoorObject(child.name))
            {
                // Disable all colliders on this object and its children
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
                // Recurse into non-door children
                count += DisableDoorColliders(child);
            }
        }
        return count;
    }

    private bool IsDoorObject(string name)
    {
        string lower = name.ToLowerInvariant();
        foreach (string kw in doorKeywords)
        {
            if (lower.Contains(kw.ToLowerInvariant()))
                return true;
        }
        return false;
    }
}
