using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// PC keyboard fallback for testing hand grab without a VR headset.
/// Automatically activates when no XR device is connected.
///
/// Behaviour:
///   - Positions RightHand in front-right of the camera (where your real
///     right hand would roughly be).
///   - Hold J  →  hand "grabs" the nearest chair/table within grabRadius,
///               fingers curl closed.
///   - Release J  →  object dropped, fingers open.
///
/// Attach this to the same GameObject as XRHandFollower (i.e. RightHand).
/// It does nothing when a real XR headset is active.
/// </summary>
public class PCHandSimulator : MonoBehaviour
{
    [Header("PC Controls")]
    public KeyCode grabKey = KeyCode.J;

    [Header("Hand Position (relative to camera)")]
    [Tooltip("Where the simulated hand floats relative to the camera. Tweak to taste.")]
    public Vector3 handOffset = new Vector3(0.35f, -0.25f, 0.7f); // right, down, forward

    [Header("Grab Settings")]
    public float grabRadius = 0.6f;

    // ── internal ──────────────────────────────────────────────────
    private HandFingerCurl _curl;
    private GameObject     _held;
    private Vector3        _heldLocalOffset;
    private bool           _pcMode;

    void Start()
    {
        _curl = GetComponent<HandFingerCurl>();
    }

    void Update()
    {
        // Only take over when no real XR headset is active
        _pcMode = !XRSettings.isDeviceActive;
        if (!_pcMode)
        {
            if (_curl != null) _curl.gripOverride = -1f; // hand back to XR control
            return;
        }

        // ── Position hand in front of camera ──
        var cam = Camera.main;
        if (cam != null)
        {
            transform.position = cam.transform.TransformPoint(handOffset);
            transform.rotation = cam.transform.rotation;
        }

        // ── J key: grab / release ──
        if (Input.GetKeyDown(grabKey))  TryGrab();
        if (Input.GetKeyUp(grabKey))    Release();

        // ── Curl fingers while holding J ──
        if (_curl != null)
            _curl.gripOverride = Input.GetKey(grabKey) ? 1f : 0f;
    }

    private void TryGrab()
    {
        if (_held != null) return; // already holding something

        // Find the nearest Rigidbody within grabRadius
        var hits = Physics.OverlapSphere(transform.position, grabRadius);
        Rigidbody best = null;
        float bestDist = float.MaxValue;

        foreach (var col in hits)
        {
            var rb = col.GetComponentInParent<Rigidbody>();
            if (rb == null) continue;
            float d = Vector3.Distance(transform.position, rb.transform.position);
            if (d < bestDist) { bestDist = d; best = rb; }
        }

        if (best == null) return;

        _held = best.gameObject;
        best.isKinematic = true;
        _held.transform.SetParent(transform);

        Debug.Log($"[PCHandSimulator] Grabbed: {_held.name}");
    }

    private void Release()
    {
        if (_held == null) return;

        _held.transform.SetParent(null);
        var rb = _held.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = true; // stay where dropped

        Debug.Log($"[PCHandSimulator] Released: {_held.name}");
        _held = null;
    }

    // Draw a wire sphere in the editor so you can see the grab radius
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.35f);
        Gizmos.DrawSphere(transform.position, grabRadius);
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.9f);
        Gizmos.DrawWireSphere(transform.position, grabRadius);
    }
}
