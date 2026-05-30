using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Unified hand controller for the Animated Hands asset.
///
/// One component per hand GameObject. In VR, follows the matching XR
/// controller and drives the Animator's Grip/Trigger floats from real
/// controller input. In PC mode (no headset), positions the hand relative
/// to the camera and drives the same Animator floats from keyboard keys.
///
/// Expected GameObject layout:
///   ├─ LeftHand   (this component, handedness=Left,  XRNode=LeftHand)
///   │   └─ Left Hand Model  (animated-hands prefab instance, has Animator)
///   └─ RightHand  (this component, handedness=Right, XRNode=RightHand)
///       └─ Right Hand Model (animated-hands prefab instance, has Animator)
///
/// The Animator is auto-found via GetComponentInChildren<Animator>().
/// </summary>
public class HandPresence : MonoBehaviour
{
    public enum Handedness { Right, Left }

    [Header("Identity")]
    [Tooltip("Which hand this drives. Used both for XR node selection and PC-mode mirroring.")]
    public Handedness handedness = Handedness.Right;

    [Header("References (auto-discovered if blank)")]
    [Tooltip("Animator that drives Grip/Trigger floats. If blank, found in children.")]
    public Animator handAnimator;
    [Tooltip("Camera used for PC-mode positioning. If blank, Camera.main → any active Camera.")]
    public Camera cameraOverride;

    [Header("VR Mode (when XR headset is active)")]
    [Tooltip("Local position offset applied AFTER tracking so the hand sits inside the controller grip.")]
    public Vector3 vrPositionOffset = Vector3.zero;
    [Tooltip("Local rotation offset (Euler) applied AFTER tracking, to align the model's forward with the controller.")]
    public Vector3 vrRotationOffsetEuler = Vector3.zero;

    [Header("PC Mode (no headset)")]
    [Tooltip("Position relative to the camera. RIGHT-hand values; X is auto-mirrored for Left.")]
    public Vector3 pcHandOffset = new Vector3(0.55f, -0.35f, 0.65f);
    [Tooltip("Euler offset applied AFTER aligning with camera. Y/Z auto-mirrored for Left hand. Default (-90,0,0) → palm down, fingers forward (natural resting first-person view).")]
    public Vector3 pcRotationOffsetEuler = new Vector3(-90f, 0f, 0f);
    [Tooltip("Right-hand grip/trigger key. Hold to close the fist (also grabs nearby Rigidbody).")]
    public KeyCode pcRightGripKey = KeyCode.J;
    [Tooltip("Left-hand grip/trigger key. Hold to close the fist (also grabs nearby Rigidbody).")]
    public KeyCode pcLeftGripKey  = KeyCode.H;
    [Tooltip("Sphere radius (m) around the hand used to find a grabbable Rigidbody when the grip key is pressed.")]
    public float pcGrabRadius = 0.35f;

    [Header("Animator Parameters")]
    public string gripParam    = "Grip";
    public string triggerParam = "Trigger";

    [Header("Debug")]
    public bool verboseLogs = false;

    // ── internal ─────────────────────────────────────────────────────
    private int        _gripHash;
    private int        _triggerHash;
    private bool       _haveAnimParams;
    private bool       _warnedNoCamera;
    private GameObject _pcHeld;        // currently grabbed in PC mode

    void Awake()
    {
        if (handAnimator == null) handAnimator = GetComponentInChildren<Animator>();
        if (handAnimator != null)
        {
            _gripHash    = Animator.StringToHash(gripParam);
            _triggerHash = Animator.StringToHash(triggerParam);
            _haveAnimParams = true;
        }
    }

    void Start()
    {
        if (cameraOverride == null)
        {
            cameraOverride = Camera.main;
            if (cameraOverride == null) cameraOverride = FindFirstObjectByType<Camera>();
        }
        if (verboseLogs)
            Debug.Log($"[HandPresence/{handedness}] camera={(cameraOverride!=null?cameraOverride.name:"NULL")}, animator={(handAnimator!=null?handAnimator.name:"NULL")}");
    }

    void Update()
    {
        if (XRSettings.isDeviceActive)
            UpdateVR();
        else
            UpdatePC();
    }

    // ── VR mode ──────────────────────────────────────────────────────
    void UpdateVR()
    {
        XRNode node = handedness == Handedness.Right ? XRNode.RightHand : XRNode.LeftHand;
        var device = InputDevices.GetDeviceAtXRNode(node);

        if (!device.isValid)
        {
            SetAnim(0f, 0f);
            return;
        }

        // Position / rotation
        bool tracked = false;
        device.TryGetFeatureValue(CommonUsages.isTracked, out tracked);
        if (tracked)
        {
            if (device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos))
                transform.localPosition = pos + vrPositionOffset;
            if (device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
                transform.localRotation = rot * Quaternion.Euler(vrRotationOffsetEuler);
        }

        // Animator inputs — Grip squeeze and Trigger pull, both 0..1
        float grip = 0f, trig = 0f;
        device.TryGetFeatureValue(CommonUsages.grip, out grip);
        device.TryGetFeatureValue(CommonUsages.trigger, out trig);
        SetAnim(grip, trig);
    }

    // ── PC mode ──────────────────────────────────────────────────────
    void UpdatePC()
    {
        if (cameraOverride == null)
        {
            if (!_warnedNoCamera) { Debug.LogError($"[HandPresence/{handedness}] No camera found — tag your camera MainCamera or set cameraOverride."); _warnedNoCamera = true; }
            return;
        }

        // Mirror for left
        Vector3 offs = pcHandOffset;
        Vector3 rotOffs = pcRotationOffsetEuler;
        if (handedness == Handedness.Left)
        {
            offs.x    = -offs.x;
            rotOffs.y = -rotOffs.y;
            rotOffs.z = -rotOffs.z;
        }

        Transform camT = cameraOverride.transform;
        transform.position = camT.TransformPoint(offs);
        transform.rotation = camT.rotation * Quaternion.Euler(rotOffs);

        // Animator: which key for this hand?
        KeyCode key = handedness == Handedness.Right ? pcRightGripKey : pcLeftGripKey;
        float closed = Input.GetKey(key) ? 1f : 0f;
        // Drive both Grip and Trigger so any blend-tree branch responds —
        // result is the closed-fist pose. If you want pinch instead, set
        // triggerParam alone via a different keybind.
        SetAnim(closed, closed);

        // Grab nearest Rigidbody on key down, release on key up.
        // VR mode gets grab "for free" via XRDirectInteractor reacting to
        // the controller grip — PC mode has no XR controller so we do this
        // manually here, matching the old PCHandSimulator behaviour.
        if (Input.GetKeyDown(key)) TryGrabPC();
        if (Input.GetKeyUp(key))   ReleasePC();
    }

    void TryGrabPC()
    {
        if (_pcHeld != null) return; // already holding something

        var hits = Physics.OverlapSphere(transform.position, pcGrabRadius);
        Rigidbody best = null;
        float bestDist = float.MaxValue;
        foreach (var col in hits)
        {
            var rb = col.GetComponentInParent<Rigidbody>();
            if (rb == null) continue;
            // Skip our own hand colliders if any RB ended up on the hand rig
            if (rb.transform.IsChildOf(transform)) continue;
            float d = Vector3.Distance(transform.position, rb.transform.position);
            if (d < bestDist) { bestDist = d; best = rb; }
        }
        if (best == null) return;

        _pcHeld = best.gameObject;
        best.isKinematic = true;
        _pcHeld.transform.SetParent(transform);
        if (verboseLogs) Debug.Log($"[HandPresence/{handedness}] grabbed {_pcHeld.name}");
    }

    void ReleasePC()
    {
        if (_pcHeld == null) return;
        _pcHeld.transform.SetParent(null);
        var rb = _pcHeld.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = true; // freeze where dropped (no gravity flop)
        if (verboseLogs) Debug.Log($"[HandPresence/{handedness}] released {_pcHeld.name}");
        _pcHeld = null;
    }

    void SetAnim(float grip, float trigger)
    {
        if (!_haveAnimParams || handAnimator == null) return;
        handAnimator.SetFloat(_gripHash, Mathf.Clamp01(grip));
        handAnimator.SetFloat(_triggerHash, Mathf.Clamp01(trigger));
    }
}
