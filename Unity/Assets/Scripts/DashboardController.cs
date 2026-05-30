// Assets/Scripts/DashboardController.cs
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

/// <summary>
/// Subscribes to UICommandPoller events.
/// On OpenDashboard: fetches stats, updates labels, shows panel.
/// On CloseDashboard: hides panel.
/// Attach to the same GameObject as UICommandPoller.
/// </summary>
public class DashboardController : MonoBehaviour
{
    [Header("References")]
    public UICommandPoller poller;
    public GameObject dashboardPanel;

    [Header("Labels")]
    public TextMeshProUGUI commandsText;
    public TextMeshProUGUI placementsText;
    public TextMeshProUGUI latencyText;
    public TextMeshProUGUI durationText;

    [Header("Config")]
    public string apiBase = "http://localhost:8000";
    public string sessionId = "";          // Set at runtime by VoiceRecorder (or auto-generate here)

    [Tooltip("Offset from Main Camera in local space (top-right, 50cm forward)")]
    public Vector3 cameraOffset = new Vector3(0.18f, 0.10f, 0.5f);

    private Camera _cam;
    private Transform _originalParent;

    private void Awake()
    {
        _cam = Camera.main;
        // sessionId is resolved at fetch time from AnalyticsTracker.Instance —
        // see ResolveSessionId(). Per-call resolution keeps the dashboard
        // aligned with whatever session VoiceRecorder is logging against.
        if (poller == null) poller = GetComponent<UICommandPoller>();
        if (poller == null) Debug.LogError("[Dashboard] UICommandPoller not found on this GameObject.", this);
    }

    /// <summary>Pull session_id from AnalyticsTracker (the single source of
    /// truth for both client batch events and server-side voice_command
    /// logging). Falls back to the inspector value, then to a device-derived
    /// id so the panel still shows *something* if the tracker is missing.</summary>
    private string ResolveSessionId()
    {
        if (AnalyticsTracker.Instance != null && !string.IsNullOrEmpty(AnalyticsTracker.Instance.SessionId))
            return AnalyticsTracker.Instance.SessionId;
        if (!string.IsNullOrEmpty(sessionId))
            return sessionId;
        return SystemInfo.deviceUniqueIdentifier;
    }

    private void OnEnable()
    {
        if (poller != null)
        {
            poller.OnOpenDashboard += HandleOpen;
            poller.OnCloseDashboard += HandleClose;
        }
        // Push-to-talk auto-close: any new voice command hides the panel so
        // the player doesn't issue commands while staring at stale stats.
        VoiceRecorder.OnPushToTalkPressed += HandlePushToTalkClose;
    }

    private void OnDisable()
    {
        if (poller != null)
        {
            poller.OnOpenDashboard -= HandleOpen;
            poller.OnCloseDashboard -= HandleClose;
        }
        VoiceRecorder.OnPushToTalkPressed -= HandlePushToTalkClose;
    }

    /// <summary>Close the panel on push-to-talk, but only if it's currently
    /// visible — otherwise we'd needlessly reparent + StopAllCoroutines() on
    /// every key press.</summary>
    private void HandlePushToTalkClose()
    {
        if (dashboardPanel != null && dashboardPanel.activeSelf)
        {
            HandleClose();
        }
    }

    private void HandleOpen()
    {
        if (dashboardPanel == null)
        {
            Debug.LogWarning("[Dashboard] dashboardPanel not assigned — open ignored.");
            return;
        }

        // Camera resolution cascade. Camera.main only finds cameras tagged
        // MainCamera — VR rigs frequently don't tag their camera, which would
        // leave the panel stuck at world origin (scale 0.001 → invisible).
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) _cam = FindObjectOfType<Camera>();

        if (_cam != null)
        {
            // Parent the panel to the camera so it follows head movement (like
            // the "READY" overlay). worldPositionStays:false keeps the local
            // scale (0.001 baked into the scene) so the panel doesn't scale
            // to giant by inheriting camera world scale.
            _originalParent = dashboardPanel.transform.parent;
            dashboardPanel.transform.SetParent(_cam.transform, worldPositionStays: false);
            dashboardPanel.transform.localPosition = cameraOffset;
            dashboardPanel.transform.localRotation = Quaternion.identity;
            Debug.Log($"[Dashboard] Attached to camera '{_cam.name}' at local {cameraOffset}");
        }
        else
        {
            // No camera at all — fall back to a fixed offset from world origin
            // so at least something is visible. Better than invisible at (0,0,0).
            dashboardPanel.transform.position = new Vector3(0f, 1.6f, 1.5f);
            dashboardPanel.transform.rotation = Quaternion.identity;
            Debug.LogWarning("[Dashboard] No camera found — using fallback position (0, 1.6, 1.5).");
        }

        dashboardPanel.SetActive(true);
        StartCoroutine(FetchAndDisplay());
    }

    private void HandleClose()
    {
        StopAllCoroutines();
        if (dashboardPanel != null)
        {
            // Restore original parent (usually scene root) before hiding so
            // we don't leave the panel hanging off the camera transform.
            if (dashboardPanel.transform.parent != _originalParent)
                dashboardPanel.transform.SetParent(_originalParent, worldPositionStays: false);
            dashboardPanel.SetActive(false);
        }
    }

    private IEnumerator FetchAndDisplay()
    {
        // Use the same session_id AnalyticsTracker / VoiceRecorder are logging
        // under — otherwise the stats query asks about a session that has no
        // events and returns zeros across the board.
        string sid = ResolveSessionId();
        string url = $"{apiBase}/api/analytics/dashboard?session_id={UnityWebRequest.EscapeURL(sid)}";
        using var req = UnityWebRequest.Get(url);
        req.timeout = 5;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[Dashboard] stats fetch failed: {req.error}");
            SetLabels(0, 0, 0, 0);
            yield break;
        }

        var stats = JsonUtility.FromJson<DashboardStats>(req.downloadHandler.text);
        if (stats == null) { SetLabels(0, 0, 0, 0); yield break; }

        SetLabels(stats.total_commands, stats.total_placements,
                  stats.avg_latency_ms, stats.duration_min);
    }

    private void SetLabels(int commands, int placements, float latencyMs, float durationMin)
    {
        if (commandsText)   commandsText.text   = $"Commandes      {commands}";
        if (placementsText) placementsText.text = $"Placements     {placements}";
        if (latencyText)    latencyText.text    = $"Latence moy   {latencyMs:F0} ms";
        if (durationText)   durationText.text   = $"Durée           {durationMin:F1} min";
    }

    [Serializable]
    private class DashboardStats
    {
        public int total_commands;
        public int total_placements;
        public float avg_latency_ms;
        public float duration_min;
    }
}
