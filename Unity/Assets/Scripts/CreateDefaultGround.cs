using UnityEngine;

// Small helper: if no ground-like collider is found in the scene at start,
// this script will create a large Plane at y=0 so the player has a floor to walk on.
[ExecuteAlways]
public class CreateDefaultGround : MonoBehaviour
{
    [Tooltip("Half-size multiplier for the auto ground plane (scale will be X:scale, 1, Z:scale)")]
    public float groundScale = 10f;

    [Tooltip("Minimum Y position to consider a ground present. Objects with colliders above this Y are ignored.")]
    public float groundYThreshold = 0.1f;

    void Awake()
    {
        // Only run in play mode to avoid modifying editor scenes unexpectedly
        if (!Application.isPlaying) return;

        if (HasGroundNearby())
        {
            Debug.Log("[CreateDefaultGround] Ground detected in scene — no auto-ground created.");
            return;
        }

        CreateGroundPlane();
    }

    bool HasGroundNearby()
    {
        // Look for any Collider whose top is near Y=0 (simple heuristic)
        Collider[] all = FindObjectsOfType<Collider>();
        foreach (var c in all)
        {
            if (c == null) continue;
            // estimate top Y of collider bounds
            float topY = c.bounds.max.y;
            if (topY >= -groundYThreshold && topY <= groundYThreshold + 5f)
            {
                // treat as ground-like object
                return true;
            }
        }

        return false;
    }

    void CreateGroundPlane()
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "AutoGround";
        ground.transform.position = new Vector3(0f, 0f, 0f);
        ground.transform.localScale = new Vector3(groundScale, 1f, groundScale);

        // Keep the collider so CharacterController can stand on it
        var col = ground.GetComponent<Collider>();
        if (col == null) ground.AddComponent<BoxCollider>();

        // Optional: give it a neutral material color
        var rend = ground.GetComponent<Renderer>();
        if (rend != null)
        {
            rend.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            rend.sharedMaterial.color = new Color(0.6f, 0.6f, 0.6f);
        }

        Debug.Log($"[CreateDefaultGround] Auto ground created at Y=0 (scale={groundScale}).");
    }
}
