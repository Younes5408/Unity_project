using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Drives a hand GameObject's local pose from the XR controller for the
/// chosen XRNode (LeftHand / RightHand). Uses the same legacy XR Input
/// API VoiceRecorder already uses — no InputAction setup required, works
/// out of the box with the Oculus XR loader.
///
/// Place this on a child of the Player (sibling of Main Camera) and parent
/// the hand visual mesh underneath. In PC mode (no headset), the controller
/// is invalid and the script does nothing — hands stay parked at their
/// scene-authored local position so they're invisible / out of view.
/// </summary>
public class XRHandFollower : MonoBehaviour
{
    [Tooltip("Which controller to follow.")]
    public XRNode hand = XRNode.LeftHand;

    [Header("Visual Offset")]
    [Tooltip("Local position offset applied AFTER tracking (tweak so the model sits correctly in the controller's grip).")]
    public Vector3 positionOffset = Vector3.zero;

    [Tooltip("Local rotation offset applied AFTER tracking (Euler degrees). Quest controllers point along their own +Z; use this to align the hand model's forward.")]
    public Vector3 rotationOffsetEuler = Vector3.zero;

    [Header("Behaviour")]
    [Tooltip("Hide the hand visual when the controller is not tracked (e.g. PC mode, controller off).")]
    public bool hideWhenUntracked = true;

    [Tooltip("Optional child GameObject to enable/disable. If null, uses this GameObject's first child.")]
    public GameObject handVisual;

    private bool _wasTracked;

    void Reset()
    {
        // Auto-pick the first child as the visual on component add.
        if (transform.childCount > 0) handVisual = transform.GetChild(0).gameObject;
    }

    void Update()
    {
        var device = InputDevices.GetDeviceAtXRNode(hand);
        bool tracked = false;

        if (device.isValid)
        {
            // isTracked is the canonical "controller is currently being seen
            // by the headset" check. Position/rotation can be stale when the
            // controller is out of view — gate visibility on isTracked.
            if (!device.TryGetFeatureValue(CommonUsages.isTracked, out tracked))
                tracked = false;

            if (tracked)
            {
                if (device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos))
                    transform.localPosition = pos + positionOffset;
                if (device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
                    transform.localRotation = rot * Quaternion.Euler(rotationOffsetEuler);
            }
        }

        // Toggle visual on/off based on tracking state. Only update when the
        // state changes to avoid hammering SetActive every frame.
        if (hideWhenUntracked && handVisual != null && tracked != _wasTracked)
        {
            handVisual.SetActive(tracked);
            _wasTracked = tracked;
        }
    }
}
