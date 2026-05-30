using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    [Header("Movement")]
    public float walkSpeed = 5.0f;

    [Header("Mouse")]
    public float mouseSensitivity = 2.0f;

    [Header("References")]
    public Camera playerCamera; // assign the camera child in inspector

    // internal state
    private CharacterController controller;
    private Vector3 velocity = Vector3.zero;
    private float gravity = -9.81f;

    // look state
    private float pitch = 0f; // vertical rotation
    private float yaw = 0f;   // horizontal rotation accumulator

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>();
        }
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // Place player above the nearest floor if we start inside the void
        EnsureOnFloorAtStart();
    }

    void EnsureOnFloorAtStart()
    {
        // If already grounded, do nothing
        if (controller.isGrounded)
        {
            PositionCamera();
            return;
        }

        // Raycast downward to find a surface under the player
        RaycastHit hit;
        float maxDistance = 50f;
        Vector3 origin = transform.position;

        if (Physics.Raycast(origin, Vector3.down, out hit, maxDistance))
        {
            float desiredY = hit.point.y + (controller.height / 2.0f) + 0.05f;
            transform.position = new Vector3(transform.position.x, desiredY, transform.position.z);
            Debug.Log($"[FirstPersonController] Positioned player above detected floor at y={hit.point.y:0.00}");
        }
        else if (Physics.Raycast(origin, Vector3.up, out hit, maxDistance))
        {
            // Found a ceiling; place slightly below it
            float desiredY = hit.point.y - (controller.height / 2.0f) - 0.05f;
            transform.position = new Vector3(transform.position.x, desiredY, transform.position.z);
            Debug.Log($"[FirstPersonController] No floor below; placed player below detected ceiling at y={hit.point.y:0.00}");
        }
        else
        {
            // Fallback: place at y = controller.height/2 + 0.1
            float desiredY = (controller.height / 2.0f) + 0.1f;
            transform.position = new Vector3(transform.position.x, desiredY, transform.position.z);
            Debug.LogWarning("[FirstPersonController] No geometry found nearby — placing player at default height.");
        }

        PositionCamera();
    }

    void PositionCamera()
    {
        if (playerCamera != null)
        {
            // Place camera at roughly eye height inside the character
            float eyeHeight = controller.height * 0.5f - 0.05f;
            playerCamera.transform.localPosition = new Vector3(0f, eyeHeight, 0f);
        }
    }

    void Update()
    {
        HandleCursorLock();
        HandleMouseLook_NewInputSystem();
        HandleMovement_NewInputSystem();
        HandleDebugInputs();
    }

    void HandleCursorLock()
    {
        // Re-lock cursor on left click if it got unlocked (e.g. after web request / window focus loss)
        if (Cursor.lockState != CursorLockMode.Locked)
        {
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }

    void HandleDebugInputs()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.rKey.wasPressedThisFrame)
        {
            Debug.Log("[FirstPersonController] Respawn requested (R)");
            EnsureOnFloorAtStart();
        }
    }

    void HandleMouseLook_NewInputSystem()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        Vector2 delta = mouse.delta.ReadValue();
        // scale similar to old Input.GetAxis behaviour
        float mx = delta.x * mouseSensitivity * 0.02f;
        float my = delta.y * mouseSensitivity * 0.02f;

        // accumulate yaw/pitch
        yaw += mx;
        pitch -= my;
        pitch = Mathf.Clamp(pitch, -90f, 90f);

        // apply rotations
        transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        if (playerCamera != null)
        {
            playerCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }
    }

    void HandleMovement_NewInputSystem()
    {
        Vector2 moveInput = Vector2.zero;
        var kb = Keyboard.current;
        if (kb != null)
        {
            // forward/back: W or Z (AZERTY)
            if (kb.wKey.isPressed || kb.zKey.isPressed) moveInput.y += 1f;
            if (kb.sKey.isPressed) moveInput.y -= 1f;

            // left/right: A or Q for left, D for right
            if (kb.aKey.isPressed || kb.qKey.isPressed) moveInput.x -= 1f;
            if (kb.dKey.isPressed) moveInput.x += 1f;
        }

        Vector3 move = (transform.right * moveInput.x + transform.forward * moveInput.y);
        if (move.sqrMagnitude > 1f) move.Normalize();

        Vector3 horizontal = move * walkSpeed;

        // grounded handling
        if (controller.isGrounded && velocity.y < 0f)
        {
            velocity.y = -1f; // keep a small downward force to stay grounded
        }

        velocity.y += gravity * Time.deltaTime;

        Vector3 total = horizontal + new Vector3(0f, velocity.y, 0f);

        // IMPORTANT: use controller.Move() so collisions with walls are handled
        controller.Move(total * Time.deltaTime);
    }

    // allow unlocking cursor at runtime
    public void ReleaseCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
