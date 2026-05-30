using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Reads the controller grip axis (0–1) and proportionally curls all
/// five fingers into a fist. Attach alongside XRHandFollower on the
/// LeftHand / RightHand GameObject.
///
/// Bone names must match the SimpleHands rig:
///   Wrist → Hand → Thumb/Thumb2
///                → IndexFinger/Index2/Index3
///                → MiddleFinger/Middle2/Middle3
///                → RingFinger/Ring2/Ring3
///                → LittleFinger/Little2/Little3
///
/// Curl axis: local Z (matches the FBX export from Blender).
/// </summary>
public class HandFingerCurl : MonoBehaviour
{
    [Header("XR Input")]
    public XRNode hand = XRNode.RightHand;

    [Header("Curl Angles (degrees at full grip)")]
    public float fingerCurl  = 75f;   // Index / Middle / Ring / Little
    public float thumbCurl   = 45f;   // Thumb curls less (different anatomy)
    [Tooltip("Lerp speed — higher = snappier response to grip changes.")]
    public float curlSpeed   = 15f;

    // ── Bone references ──────────────────────────────────────────────────
    private Transform[] _thumbBones;
    private Transform[] _indexBones;
    private Transform[] _middleBones;
    private Transform[] _ringBones;
    private Transform[] _littleBones;

    // ── Rest poses (captured at Start) ──────────────────────────────────
    private Quaternion[] _thumbRest;
    private Quaternion[] _indexRest;
    private Quaternion[] _middleRest;
    private Quaternion[] _ringRest;
    private Quaternion[] _littleRest;

    // ── Target curled poses ─────────────────────────────────────────────
    private Quaternion[] _thumbCurl;
    private Quaternion[] _indexCurl;
    private Quaternion[] _middleCurl;
    private Quaternion[] _ringCurl;
    private Quaternion[] _littleCurl;

    // ── Current blend weight ─────────────────────────────────────────────
    private float _currentGrip;

    // ────────────────────────────────────────────────────────────────────

    void Start()
    {
        // Walk children to find the rig root (HandRig/Wrist)
        var wrist = FindDeep(transform, "Wrist");
        if (wrist == null)
        {
            Debug.LogWarning($"[HandFingerCurl] 'Wrist' bone not found under {name}. Check rig names.");
            enabled = false;
            return;
        }

        // Collect bones for each finger
        _thumbBones  = Collect(wrist, "Thumb",       "Thumb2");
        _indexBones  = Collect(wrist, "IndexFinger",  "Index2",  "Index3");
        _middleBones = Collect(wrist, "MiddleFinger", "Middle2", "Middle3");
        _ringBones   = Collect(wrist, "RingFinger",   "Ring2",   "Ring3");
        _littleBones = Collect(wrist, "LittleFinger", "Little2", "Little3");

        // Capture rest pose and pre-compute the fully-curled pose
        _thumbRest  = Rests(_thumbBones);
        _indexRest  = Rests(_indexBones);
        _middleRest = Rests(_middleBones);
        _ringRest   = Rests(_ringBones);
        _littleRest = Rests(_littleBones);

        _thumbCurl  = CurlTargets(_thumbRest,  thumbCurl);
        _indexCurl  = CurlTargets(_indexRest,  fingerCurl);
        _middleCurl = CurlTargets(_middleRest, fingerCurl);
        _ringCurl   = CurlTargets(_ringRest,   fingerCurl);
        _littleCurl = CurlTargets(_littleRest, fingerCurl);
    }

    /// Set to 0–1 to override the XR grip value from code (e.g. PC keyboard test).
    /// Set to -1 (default) to read from the actual XR controller.
    [HideInInspector] public float gripOverride = -1f;

    void Update()
    {
        float targetGrip = 0f;
        if (gripOverride >= 0f)
        {
            targetGrip = Mathf.Clamp01(gripOverride);
        }
        else
        {
            var device = InputDevices.GetDeviceAtXRNode(hand);
            if (device.isValid)
                device.TryGetFeatureValue(CommonUsages.grip, out targetGrip);
        }

        // Smooth toward target so grip feel isn't instant (reduces jitter)
        _currentGrip = Mathf.Lerp(_currentGrip, targetGrip, Time.deltaTime * curlSpeed);

        ApplyBlend(_thumbBones,  _thumbRest,  _thumbCurl,  _currentGrip);
        ApplyBlend(_indexBones,  _indexRest,  _indexCurl,  _currentGrip);
        ApplyBlend(_middleBones, _middleRest, _middleCurl, _currentGrip);
        ApplyBlend(_ringBones,   _ringRest,   _ringCurl,   _currentGrip);
        ApplyBlend(_littleBones, _littleRest, _littleCurl, _currentGrip);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// Collect named bones from the hierarchy (order matters — proximal first).
    private Transform[] Collect(Transform root, params string[] names)
    {
        var result = new Transform[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            result[i] = FindDeep(root, names[i]);
            if (result[i] == null)
                Debug.LogWarning($"[HandFingerCurl] Bone '{names[i]}' not found under {root.name}");
        }
        return result;
    }

    /// Snapshot localRotations.
    private Quaternion[] Rests(Transform[] bones)
    {
        var q = new Quaternion[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            q[i] = bones[i] != null ? bones[i].localRotation : Quaternion.identity;
        return q;
    }

    /// Compute the fully-curled target: rest rotated by -angle around local Z.
    /// Negative Z = fingers curl toward palm (validated against SimpleHands FBX).
    private Quaternion[] CurlTargets(Quaternion[] rests, float angle)
    {
        var curled = new Quaternion[rests.Length];
        var curl = Quaternion.Euler(0f, 0f, -angle);
        for (int i = 0; i < rests.Length; i++)
            curled[i] = rests[i] * curl;
        return curled;
    }

    /// Lerp all bones from rest to curled by t (0 = open, 1 = fist).
    private void ApplyBlend(Transform[] bones, Quaternion[] rest, Quaternion[] curled, float t)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            bones[i].localRotation = Quaternion.Lerp(rest[i], curled[i], t);
        }
    }

    /// Depth-first search by name.
    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var result = FindDeep(root.GetChild(i), name);
            if (result != null) return result;
        }
        return null;
    }
}
