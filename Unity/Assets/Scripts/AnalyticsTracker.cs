using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// VR Interaction Analytics — client-side event tracker.
///
/// Singleton, DontDestroyOnLoad. Buffers events and POSTs them to /api/events
/// every 5 seconds. Also samples player position every 5s and emits
/// session_start / session_end events.
///
/// Other scripts can fire events via the static helpers:
///   AnalyticsTracker.LogVoiceCommand(transcription, tool, success, latencyMs)
///   AnalyticsTracker.LogPlacement(objectType, prefab, position)
///   AnalyticsTracker.LogToolCall(toolName, argsSummary, resultStatus)
/// </summary>
public class AnalyticsTracker : MonoBehaviour
{
    [Header("API Settings")]
    public string apiBaseUrl = "http://127.0.0.1:8000";
    public string houseId = "maison_001";

    [Header("Sampling")]
    [Tooltip("Seconds between event flushes to backend.")]
    public float flushIntervalSeconds = 5f;
    [Tooltip("Seconds between player position samples.")]
    public float positionSampleSeconds = 5f;
    [Tooltip("Player Transform to sample positions from. Leave null to auto-find Main Camera.")]
    public Transform playerTransform;

    public static AnalyticsTracker Instance { get; private set; }
    public string SessionId { get; private set; }

    private readonly List<AnalyticsEvent> _buffer = new List<AnalyticsEvent>();
    private readonly object _bufferLock = new object();
    private bool _sessionStarted;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        SessionId = Guid.NewGuid().ToString();
    }

    void Start()
    {
        if (playerTransform == null && Camera.main != null)
            playerTransform = Camera.main.transform;

        EmitSessionStart();
        StartCoroutine(FlushLoop());
        StartCoroutine(PositionSampleLoop());
    }

    void OnApplicationQuit()
    {
        EmitSessionEnd();
        // Best-effort synchronous flush — can't run a coroutine on quit
        StartCoroutine(FlushOnce());
    }

    // -------- Public logging helpers --------

    public static void LogVoiceCommand(string transcription, string toolCalled, bool success, int latencyMs)
    {
        if (Instance == null) return;
        Instance.Enqueue("voice_command", new Dictionary<string, object>
        {
            { "transcription", transcription ?? "" },
            { "tool_called", toolCalled ?? "" },
            { "success", success },
            { "latency_ms", latencyMs },
        });
    }

    public static void LogPlacement(string objectType, string prefab, Vector3 position)
    {
        if (Instance == null) return;
        Instance.Enqueue("placement", new Dictionary<string, object>
        {
            { "object_type", objectType ?? "" },
            { "prefab", prefab ?? "" },
            { "position", new Dictionary<string, object> {
                { "x", position.x }, { "y", position.y }, { "z", position.z } } },
        });
    }

    public static void LogToolCall(string toolName, string argsSummary, string resultStatus)
    {
        if (Instance == null) return;
        Instance.Enqueue("tool_call", new Dictionary<string, object>
        {
            { "tool_name", toolName ?? "" },
            { "args_summary", argsSummary ?? "" },
            { "result_status", resultStatus ?? "" },
        });
    }

    // -------- Internals --------

    private void EmitSessionStart()
    {
        if (_sessionStarted) return;
        _sessionStarted = true;
        Enqueue("session_start", new Dictionary<string, object>
        {
            { "session_id", SessionId },
            { "house_id", houseId },
        });
    }

    private void EmitSessionEnd()
    {
        if (!_sessionStarted) return;
        _sessionStarted = false;
        Enqueue("session_end", new Dictionary<string, object>
        {
            { "session_id", SessionId },
            { "house_id", houseId },
        });
    }

    private void Enqueue(string eventType, Dictionary<string, object> data)
    {
        var ev = new AnalyticsEvent
        {
            session_id = SessionId,
            event_type = eventType,
            house_id = houseId,
            timestamp = DateTime.UtcNow.ToString("o"),
            data = data,
        };
        lock (_bufferLock) { _buffer.Add(ev); }
    }

    private IEnumerator PositionSampleLoop()
    {
        var wait = new WaitForSeconds(positionSampleSeconds);
        while (true)
        {
            if (playerTransform != null)
            {
                var p = playerTransform.position;
                var a = playerTransform.eulerAngles.y;
                Enqueue("position_update", new Dictionary<string, object>
                {
                    { "player", new Dictionary<string, object> {
                        { "x", p.x }, { "y", p.y }, { "z", p.z }, { "angle", a } } },
                });
            }
            yield return wait;
        }
    }

    private IEnumerator FlushLoop()
    {
        var wait = new WaitForSeconds(flushIntervalSeconds);
        while (true)
        {
            yield return wait;
            yield return FlushOnce();
        }
    }

    private IEnumerator FlushOnce()
    {
        List<AnalyticsEvent> snapshot;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0) yield break;
            snapshot = new List<AnalyticsEvent>(_buffer);
            _buffer.Clear();
        }

        string json = SerializeBatch(snapshot);
        string url = $"{apiBaseUrl}/api/events";

        int attempt = 0;
        bool sent = false;
        while (attempt < 3 && !sent)
        {
            attempt++;
            using (var req = new UnityWebRequest(url, "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    sent = true;
                }
                else
                {
                    Debug.LogWarning($"[Analytics] flush attempt {attempt} failed: {req.error}");
                    yield return new WaitForSeconds(1f);
                }
            }
        }
        if (!sent)
        {
            Debug.LogWarning($"[Analytics] dropped {snapshot.Count} events after 3 retries");
        }
    }

    private string SerializeBatch(List<AnalyticsEvent> events)
    {
        // Manual JSON build — Unity's JsonUtility cannot serialize Dictionary<string,object>.
        var sb = new StringBuilder();
        sb.Append("{\"session_id\":\"").Append(EscapeJson(SessionId)).Append("\",");
        sb.Append("\"house_id\":\"").Append(EscapeJson(houseId)).Append("\",");
        sb.Append("\"events\":[");
        for (int i = 0; i < events.Count; i++)
        {
            if (i > 0) sb.Append(",");
            AppendEvent(sb, events[i]);
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private void AppendEvent(StringBuilder sb, AnalyticsEvent ev)
    {
        sb.Append("{");
        sb.Append("\"session_id\":\"").Append(EscapeJson(ev.session_id)).Append("\",");
        sb.Append("\"event_type\":\"").Append(EscapeJson(ev.event_type)).Append("\",");
        sb.Append("\"house_id\":\"").Append(EscapeJson(ev.house_id)).Append("\",");
        sb.Append("\"timestamp\":\"").Append(EscapeJson(ev.timestamp)).Append("\",");
        sb.Append("\"data\":");
        AppendValue(sb, ev.data);
        sb.Append("}");
    }

    private void AppendValue(StringBuilder sb, object v)
    {
        if (v == null) { sb.Append("null"); return; }
        if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
        if (v is int i) { sb.Append(i); return; }
        if (v is long l) { sb.Append(l); return; }
        if (v is float f) { sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
        if (v is double d) { sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
        if (v is string s) { sb.Append("\"").Append(EscapeJson(s)).Append("\""); return; }
        if (v is Dictionary<string, object> dict)
        {
            sb.Append("{");
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\"").Append(EscapeJson(kv.Key)).Append("\":");
                AppendValue(sb, kv.Value);
            }
            sb.Append("}");
            return;
        }
        if (v is IEnumerable<object> list)
        {
            sb.Append("[");
            bool first = true;
            foreach (var item in list)
            {
                if (!first) sb.Append(",");
                first = false;
                AppendValue(sb, item);
            }
            sb.Append("]");
            return;
        }
        sb.Append("\"").Append(EscapeJson(v.ToString())).Append("\"");
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private class AnalyticsEvent
    {
        public string session_id;
        public string event_type;
        public string house_id;
        public string timestamp;
        public Dictionary<string, object> data;
    }
}
