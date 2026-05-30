using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Polls /api/teleport/pending every 1.5s. When a teleport request is found,
/// fades the screen to black, moves the player, then fades back in.
/// Attach to the Player GameObject alongside FirstPersonController.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class TeleportReceiver : MonoBehaviour
{
    [Header("API")]
    public string apiBaseUrl = "http://127.0.0.1:8000";
    public string houseId = "maison_001";

    [Header("Polling")]
    public float pollInterval = 1.5f;

    [Header("Fade")]
    [Tooltip("Camera used for the fade overlay. Auto-found if null.")]
    public Camera playerCamera;
    public float fadeDuration = 0.25f;

    // ---- internal ----
    private CharacterController cc;
    private float _nextPoll;
    private bool _isPolling;
    private float _fadeAlpha = 0f;   // 0 = transparent, 1 = black

    void Start()
    {
        cc = GetComponent<CharacterController>();
        if (playerCamera == null)
            playerCamera = GetComponentInChildren<Camera>();
    }

    void Update()
    {
        if (Time.time >= _nextPoll && !_isPolling)
        {
            _nextPoll = Time.time + pollInterval;
            StartCoroutine(PollTeleport());
        }
    }

    void OnGUI()
    {
        if (_fadeAlpha <= 0f) return;
        // Draw a full-screen black overlay using the current fade alpha
        GUI.color = new Color(0f, 0f, 0f, _fadeAlpha);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private IEnumerator PollTeleport()
    {
        _isPolling = true;
        string url = $"{apiBaseUrl}/api/teleport/pending?house_id={UnityWebRequest.EscapeURL(houseId)}";

        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var res = JsonUtility.FromJson<TeleportResponse>(req.downloadHandler.text);
                if (res != null && res.teleport != null && !string.IsNullOrEmpty(res.teleport.id))
                {
                    yield return StartCoroutine(ExecuteTeleport(res.teleport));
                }
            }
            // Silently ignore errors — retry on next poll
        }
        _isPolling = false;
    }

    private IEnumerator ExecuteTeleport(TeleportData data)
    {
        Debug.Log($"[TeleportReceiver] Teleporting to '{data.destination}' at ({data.target_x:F1}, {data.target_z:F1})");

        // Fade to black
        yield return StartCoroutine(Fade(0f, 1f));

        // Move player — disable CC temporarily to avoid physics fighting
        cc.enabled = false;
        transform.position = new Vector3(data.target_x, transform.position.y, data.target_z);
        cc.enabled = true;

        // Let physics settle for one frame
        yield return null;

        // Fade back in
        yield return StartCoroutine(Fade(1f, 0f));

        // ACK the teleport
        StartCoroutine(ConfirmTeleport(data.id));
    }

    private IEnumerator Fade(float from, float to)
    {
        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            _fadeAlpha = Mathf.Lerp(from, to, elapsed / fadeDuration);
            yield return null;
        }
        _fadeAlpha = to;
    }

    private IEnumerator ConfirmTeleport(string teleportId)
    {
        string url = $"{apiBaseUrl}/api/teleport/{teleportId}/confirm";
        using (var req = UnityWebRequest.PostWwwForm(url, ""))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[TeleportReceiver] Confirm failed: {req.error}");
        }
    }

    // ---- JSON DTOs ----
    [System.Serializable]
    private class TeleportResponse
    {
        public string status;
        public TeleportData teleport;
    }

    [System.Serializable]
    private class TeleportData
    {
        public string id;
        public string destination;
        public float target_x;
        public float target_z;
        public string status;
    }
}
