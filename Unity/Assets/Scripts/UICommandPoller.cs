// Assets/Scripts/UICommandPoller.cs
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Polls /api/ui/pending every 2s and fires events when the backend
/// queues an open_dashboard or close_dashboard command.
/// Attach to any persistent GameObject in the scene (e.g. GameManager).
///
/// IMPORTANT: house_id must match what VoiceRecorder sends, or the backend
/// won't return commands. We auto-discover VoiceRecorder.houseId at Start()
/// so the two can never drift apart from Inspector misconfiguration.
/// </summary>
public class UICommandPoller : MonoBehaviour
{
    [Header("Backend Settings")]
    [Tooltip("Base URL of the Python backend, e.g. http://localhost:8000")]
    public string apiBase = "http://localhost:8000";

    [Tooltip("Override house_id manually. Leave blank to auto-read from VoiceRecorder at Start().")]
    public string houseId = "";

    [Header("Polling")]
    [Tooltip("Seconds between polls — minimum enforced at 0.5s")]
    public float pollInterval = 2f;

    public event Action OnOpenDashboard;
    public event Action OnCloseDashboard;

    private void Start()
    {
        pollInterval = Mathf.Max(0.5f, pollInterval);

        // ALWAYS sync to VoiceRecorder when present — VoiceRecorder is the
        // canonical source of house_id (it tags every chat/audio request).
        // A stale Inspector value here would silently break dashboard polling,
        // so VoiceRecorder wins regardless of what the Inspector says.
        var voice = FindObjectOfType<VoiceRecorder>();
        if (voice != null && !string.IsNullOrEmpty(voice.houseId))
        {
            if (!string.IsNullOrEmpty(houseId) && houseId != voice.houseId)
            {
                Debug.LogWarning($"[UICommandPoller] Inspector houseId='{houseId}' differs from VoiceRecorder.houseId='{voice.houseId}' — using VoiceRecorder's value to keep queues in sync.");
            }
            houseId = voice.houseId;
            Debug.Log($"[UICommandPoller] houseId='{houseId}' (from VoiceRecorder).");
        }
        else if (string.IsNullOrEmpty(houseId))
        {
            houseId = "maison_001"; // last-resort default — must match VoiceRecorder's default
            Debug.LogWarning($"[UICommandPoller] No VoiceRecorder found and Inspector empty — falling back to houseId='{houseId}'.");
        }

        StartCoroutine(PollLoop());
    }

    private void OnDestroy() => StopAllCoroutines();

    private IEnumerator PollLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(pollInterval);
            yield return FetchPending();
        }
    }

    private IEnumerator FetchPending()
    {
        string url = $"{apiBase}/api/ui/pending?house_id={UnityWebRequest.EscapeURL(houseId)}";
        using var req = UnityWebRequest.Get(url);
        req.timeout = 5;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            // Server offline or network error — silent, don't spam logs
            yield break;
        }

        // API returns {"status":"ok","commands":[...]}
        var response = JsonUtility.FromJson<PendingResponse>(req.downloadHandler.text);
        if (response?.commands == null) yield break;

        // Backend marks these commands as "delivered" on fetch — no double-fire on next poll.
        foreach (var cmd in response.commands)
        {
            if (cmd.action == "open_dashboard")
            {
                Debug.Log("[Dashboard] open command received");
                OnOpenDashboard?.Invoke();
            }
            else if (cmd.action == "close_dashboard")
            {
                Debug.Log("[Dashboard] close command received");
                OnCloseDashboard?.Invoke();
            }
        }
    }

    // Matches {"status":"ok","commands":[...]} returned by /api/ui/pending
    [Serializable] private class PendingResponse { public string status; public UiCommand[] commands; }

    // id/house_id/status are deserialized for completeness; only action is used for dispatch.
    [Serializable]
    private class UiCommand
    {
        public string id;
        public string house_id;
        public string action;
        public string status;
    }
}
