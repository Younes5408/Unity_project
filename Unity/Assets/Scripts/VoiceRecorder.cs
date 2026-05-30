using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.InputSystem;
using UnityEngine.XR;

// DTO for /api/chat/audio response (full voice pipeline)
[System.Serializable]
public class VoiceResponse
{
    public string status;
    public string transcription;
    public string reply;
    public string audio;  // base64 MP3
    public object layout; // JSON object
}

public class VoiceRecorder : MonoBehaviour
{
    /// <summary>Fires the moment push-to-talk is pressed (V key or Quest B).
    /// DashboardController subscribes so the dashboard auto-closes when the
    /// user asks the AI anything new — feels more natural than leaving the
    /// panel stuck on screen during the next request.</summary>
    public static event System.Action OnPushToTalkPressed;


    [Header("Configuration")]
    public string apiBaseUrl = "http://127.0.0.1:8000";
    public string houseId = "maison_001";
    public int maxRecordingSeconds = 60;
    public int sampleRate = 16000;
    public float minRecordingSeconds = 0.3f;
    public float requestTimeoutSeconds = 60f;

    [Header("Components")]
    public LayoutReceiver layoutReceiver;
    public AudioSource audioSource;

    [Header("Player Position")]
    [Tooltip("Drag the Player GameObject here to send live position with each request")]
    public Transform playerTransform;

    [Header("Audio Cues")]
    public AudioClip recordStartSound;
    public AudioClip recordStopSound;
    [Range(0f, 1f)]
    public float cueVolume = 0.5f;

    [Header("Debug")]
    public bool showDebugUI = true;

    // State machine
    private enum State { Ready, Recording, Sending, Talking, Done }
    private State currentState = State.Ready;

    private AudioClip recording;
    private string lastReply = "";
    private string lastTranscription = "";
    private float sendingTimer = 0f;
    private float doneTimer = 0f;
    private const float DoneDisplayDuration = 5f;

    // XR controller B-button tracking
    private bool xrBButtonHeld = false;

    // Interrupt support — track in-flight request and coroutine
    private UnityWebRequest currentRequest;
    private Coroutine activeVoiceCoroutine;

    // GUI styles
    private GUIStyle statusStyle;
    private GUIStyle replyStyle;
    private GUIStyle boxStyle;
    private GUIStyle hintStyle;
    private bool stylesInitialized = false;

    void Update()
    {
        bool pressedThisFrame = false;
        bool releasedThisFrame = false;

        // Keyboard: V key
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.vKey.wasPressedThisFrame)  pressedThisFrame = true;
            if (kb.vKey.wasReleasedThisFrame) releasedThisFrame = true;
        }

        // Meta Quest: B button (right controller)
        var rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (rightHand.isValid)
        {
            bool bDown = false;
            if (rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bDown))
            {
                if (bDown && !xrBButtonHeld)  pressedThisFrame = true;
                if (!bDown && xrBButtonHeld)  releasedThisFrame = true;
                xrBButtonHeld = bDown;
            }
        }

        // INTERRUPT: pressing V while AI is Sending/Talking → abort + restart
        if (pressedThisFrame && (currentState == State.Sending || currentState == State.Talking))
        {
            InterruptCurrent();
            StartRecording();
            return;
        }

        // Track timer in Sending state
        if (currentState == State.Sending)
        {
            sendingTimer += Time.deltaTime;
            return;
        }
        sendingTimer = 0f;

        // Talking state: locked out from starting a new recording while TTS plays
        if (currentState == State.Talking)
            return;

        if (pressedThisFrame && (currentState == State.Ready || currentState == State.Done))
        {
            currentState = State.Ready;
            StartRecording();
        }
        else if (releasedThisFrame && currentState == State.Recording)
        {
            StopAndSend();
        }

        if (currentState == State.Done)
        {
            doneTimer -= Time.deltaTime;
            if (doneTimer <= 0f)
                currentState = State.Ready;
        }
    }

    void InterruptCurrent()
    {
        Debug.Log("[VoiceRecorder] Interrupting current request/playback.");

        // Abort in-flight web request
        if (currentRequest != null)
        {
            try { currentRequest.Abort(); } catch { }
            currentRequest = null;
        }

        // Stop the active voice coroutine (covers SendVoiceCoroutine + PlayAudioBase64)
        if (activeVoiceCoroutine != null)
        {
            StopCoroutine(activeVoiceCoroutine);
            activeVoiceCoroutine = null;
        }

        // Stop any TTS audio mid-playback
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
            audioSource.clip = null;
        }

        // Avatar: snap back to WakeUp — same as a fresh push-to-talk press
        AvatarEventBridge.Instance?.OnAvatarWakeUp();

        currentState = State.Ready;
        sendingTimer = 0f;
    }

    void StartRecording()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[VoiceRecorder] No microphone detected!");
            return;
        }

        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
            audioSource.clip = null;
        }

        if (Microphone.IsRecording(null))
            Microphone.End(null);

        if (recordStartSound != null && audioSource != null)
            audioSource.PlayOneShot(recordStartSound, cueVolume);

        // Avatar: wake up when push-to-talk pressed
        AvatarEventBridge.Instance?.OnAvatarWakeUp();

        // Notify listeners (e.g. DashboardController auto-closes the panel
        // so the player isn't looking at stale stats while issuing a new
        // command). Errors in handlers must not break recording.
        try { OnPushToTalkPressed?.Invoke(); } catch (System.Exception e) {
            Debug.LogWarning($"[VoiceRecorder] PushToTalk handler threw: {e.Message}");
        }

        recording = Microphone.Start(null, false, maxRecordingSeconds, sampleRate);
        if (recording == null)
        {
            Debug.LogError("[VoiceRecorder] Microphone.Start() returned null.");
            currentState = State.Ready;
            return;
        }

        currentState = State.Recording;
        Debug.Log("[VoiceRecorder] Recording started...");
    }

    void StopAndSend()
    {
        int samplesRecorded = Microphone.GetPosition(null);
        Microphone.End(null);

        if (recording == null || samplesRecorded <= 0)
        {
            Debug.LogWarning("[VoiceRecorder] No audio captured.");
            currentState = State.Ready;
            return;
        }

        float duration = (float)samplesRecorded / sampleRate;
        if (duration < minRecordingSeconds)
        {
            Debug.LogWarning($"[VoiceRecorder] Recording too short ({duration:F2}s), ignoring.");
            currentState = State.Ready;
            return;
        }

        if (recordStopSound != null && audioSource != null)
            audioSource.PlayOneShot(recordStopSound, cueVolume);

        // Trim AudioClip
        int channels = recording.channels;
        float[] data = new float[samplesRecorded * channels];
        recording.GetData(data, 0);
        AudioClip trimmed = AudioClip.Create("RecordedClip", samplesRecorded, channels, sampleRate, false);
        trimmed.SetData(data, 0);

        byte[] wavData = WavEncoder.FromAudioClip(trimmed);

        // Send the recorded audio to the backend for STT -> LLM -> TTS pipeline
        activeVoiceCoroutine = StartCoroutine(SendVoiceCoroutine(wavData));
    }

IEnumerator SendVoiceCoroutine(byte[] wavData)
    {
        currentState = State.Sending;
        sendingTimer = 0f;

        // Avatar: AI is thinking while we wait for the backend
        AvatarEventBridge.Instance?.OnGenerationStarted();

        if (layoutReceiver != null)
            layoutReceiver.NotifyCommandSent();

        float playerX     = playerTransform != null ? playerTransform.position.x    : 0f;
        float playerZ     = playerTransform != null ? playerTransform.position.z    : 0f;
        float playerAngle = playerTransform != null ? playerTransform.eulerAngles.y : 0f;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string sessionId = AnalyticsTracker.Instance != null ? AnalyticsTracker.Instance.SessionId : "";
        string url = apiBaseUrl + "/api/chat/audio"
                   + "?house_id=" + System.Uri.EscapeDataString(houseId)
                   + "&player_x=" + playerX.ToString("F2", inv)
                   + "&player_z=" + playerZ.ToString("F2", inv)
                   + "&player_angle=" + playerAngle.ToString("F2", inv)
                   + "&session_id=" + System.Uri.EscapeDataString(sessionId);

        var form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("file", wavData, "recording.wav", "audio/wav"),
        };

        using (var req = UnityWebRequest.Post(url, form))
        {
            currentRequest = req;
            req.timeout = (int)requestTimeoutSeconds;
            yield return req.SendWebRequest();
            currentRequest = null;

            if (req.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("[VoiceRecorder] Voice response received.");
                VoiceResponse res = null;
                try
                {
                    res = JsonUtility.FromJson<VoiceResponse>(req.downloadHandler.text);
                    lastTranscription = res.transcription ?? "";
                    lastReply = res.reply ?? "";
                    Debug.Log("[VoiceRecorder] Transcription: " + lastTranscription);
                    Debug.Log("[VoiceRecorder] Agent: " + lastReply);
                }
                catch (System.Exception e)
                {
                    Debug.LogError("[VoiceRecorder] Failed to parse response: " + e.Message);
                }

                if (res != null && !string.IsNullOrEmpty(res.audio))
                    yield return StartCoroutine(PlayAudioBase64(res.audio));
            }
            else if (req.result == UnityWebRequest.Result.ConnectionError && string.IsNullOrEmpty(req.error))
            {
                // Aborted by InterruptCurrent — silent
                yield break;
            }
            else
            {
                Debug.LogError("[VoiceRecorder] Request failed: " + req.error);
                lastReply = "Erreur: " + req.error;
                // Avatar: backend failed — go straight back to Sleep so it doesn't stay Thinking forever.
                AvatarEventBridge.Instance?.OnBotFinished();
            }
        }

        activeVoiceCoroutine = null;
        currentState = State.Done;
        doneTimer = DoneDisplayDuration;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    IEnumerator PlayAudioBase64(string base64mp3)
    {
        byte[] mp3Bytes = null;
        try
        {
            mp3Bytes = System.Convert.FromBase64String(base64mp3);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[VoiceRecorder] Invalid base64 audio: {e.Message}");
            yield break;
        }

        string tmpPath = System.IO.Path.Combine(Application.persistentDataPath, "reply.mp3");
        try
        {
            System.IO.File.WriteAllBytes(tmpPath, mp3Bytes);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[VoiceRecorder] Failed to write audio file: {e.Message}");
            yield break;
        }

        using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + tmpPath, AudioType.MPEG))
        {
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (audioSource != null && clip != null)
                {
                    audioSource.clip = clip;

                    // Avatar: TTS audio is starting — go to Talking state
                    AvatarEventBridge.Instance?.OnBotStartedResponding();

                    // UI: switch status from SENDING to TALKING while audio plays
                    currentState = State.Talking;

                    audioSource.Play();
                    Debug.Log("[VoiceRecorder] Audio playback started.");

                    // Wait for audio to finish, then notify avatar bridge
                    while (audioSource != null && audioSource.isPlaying)
                        yield return null;

                    // Avatar: TTS audio finished — return to Sleep
                    AvatarEventBridge.Instance?.OnBotFinished();
                }
                else
                {
                    // No audio source/clip — still notify finish so avatar doesn't stay stuck.
                    AvatarEventBridge.Instance?.OnBotFinished();
                }
            }
            else
            {
                Debug.LogWarning($"[VoiceRecorder] Audio playback failed: {req.error}");
                AvatarEventBridge.Instance?.OnBotFinished();
            }
        }
    }

IEnumerator SendChatCoroutine(string message)
    {
        currentState = State.Sending;
        sendingTimer = 0f;

        float playerX     = playerTransform != null ? playerTransform.position.x    : 0f;
        float playerZ     = playerTransform != null ? playerTransform.position.z    : 0f;
        float playerAngle = playerTransform != null ? playerTransform.eulerAngles.y : 0f;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string sessionIdChat = AnalyticsTracker.Instance != null ? AnalyticsTracker.Instance.SessionId : "";
        string url = apiBaseUrl + "/api/chat";
        string jsonBody = "{\"message\":\"" + EscapeJson(message) + "\","
                        + "\"house_id\":\"" + houseId + "\","
                        + "\"player_x\":" + playerX.ToString("F2", inv) + ","
                        + "\"player_z\":" + playerZ.ToString("F2", inv) + ","
                        + "\"player_angle\":" + playerAngle.ToString("F2", inv) + ","
                        + "\"session_id\":\"" + EscapeJson(sessionIdChat) + "\"}";
        byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(bodyBytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = (int)requestTimeoutSeconds;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("[VoiceRecorder] Chat response received.");
                try
                {
                    VoiceResponse res = JsonUtility.FromJson<VoiceResponse>(req.downloadHandler.text);
                    if (!string.IsNullOrEmpty(res.reply))
                    {
                        lastReply = res.reply;
                        Debug.Log("[VoiceRecorder] Agent: " + res.reply);
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError("[VoiceRecorder] Failed to parse response: " + e.Message);
                }
            }
            else
            {
                Debug.LogError("[VoiceRecorder] Request failed: " + req.error);
                lastReply = "Erreur: " + req.error;
            }
        }

        currentState = State.Done;
        doneTimer = DoneDisplayDuration;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    // =========================================
    // UI
    // =========================================
    void InitStyles()
    {
        if (stylesInitialized) return;
        statusStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22, fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft, richText = true
        };
        replyStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14, wordWrap = true, richText = true
        };
        boxStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(12, 12, 8, 8)
        };
        hintStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12, fontStyle = FontStyle.Italic
        };
        stylesInitialized = true;
    }

    void OnGUI()
    {
        if (!showDebugUI) return;
        InitStyles();

        float panelWidth = Mathf.Min(500f, Screen.width - 20f);
        float x = 10f;
        float y = 10f;

        string statusText = "";
        Color statusColor = Color.white;

        switch (currentState)
        {
            case State.Ready:
                statusText = "READY [Hold V to talk]";
                statusColor = Color.green;
                break;
            case State.Recording:
                statusText = "RECORDING...";
                statusColor = Color.red;
                break;
            case State.Sending:
                string dots = new string('.', (int)(sendingTimer % 3) + 1);
                statusText = $"SENDING{dots} ({sendingTimer:0}s)";
                statusColor = Color.yellow;
                break;
            case State.Talking:
                statusText = "TALKING...";
                statusColor = new Color(0.8f, 0.4f, 1f);
                break;
            case State.Done:
                statusText = "DONE";
                statusColor = new Color(0.3f, 0.9f, 1f);
                break;
        }

        GUI.color = statusColor;
        GUI.Label(new Rect(x, y, panelWidth, 30f), statusText, statusStyle);
        y += 35f;

        if (!string.IsNullOrEmpty(lastTranscription))
        {
            GUI.color = new Color(1f, 1f, 1f);
            GUI.Label(new Rect(x, y, panelWidth, 20f), $"Heard: {lastTranscription.Substring(0, Mathf.Min(100, lastTranscription.Length))}", hintStyle);
            y += 25f;
        }

        if (!string.IsNullOrEmpty(lastReply))
        {
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            float panelHeight = 20f + Mathf.Min(120f, lastReply.Length * 0.5f + 30f);
            GUI.Box(new Rect(x, y, panelWidth, panelHeight), "", boxStyle);

            GUI.color = new Color(0.7f, 0.9f, 1f);
            string display = lastReply.Length > 300 ? lastReply.Substring(0, 300) + "..." : lastReply;
            GUI.Label(new Rect(x + 12f, y + 8f, panelWidth - 24f, 100f),
                $"Agent: {display}", replyStyle);
        }

        if (Cursor.lockState != CursorLockMode.Locked)
        {
            GUI.color = new Color(1f, 0.5f, 0f);
            GUI.Label(new Rect(x, Screen.height - 30f, panelWidth, 25f),
                "[ Click to regain mouse control ]", hintStyle);
        }

        GUI.color = Color.white;
    }
}

/// <summary>Encodes a Unity AudioClip into a WAV byte array (16-bit PCM).</summary>
public static class WavEncoder
{
    public static byte[] FromAudioClip(AudioClip clip)
    {
        int sampleCount = clip.samples;
        int channels    = clip.channels;
        int sampleRate  = clip.frequency;

        float[] floatData = new float[sampleCount * channels];
        clip.GetData(floatData, 0);

        short[] pcmData = new short[floatData.Length];
        for (int i = 0; i < floatData.Length; i++)
        {
            float clamped = Mathf.Clamp(floatData[i], -1f, 1f);
            pcmData[i] = (short)(clamped * 32767f);
        }

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            int bitsPerSample = 16;
            int byteRate   = sampleRate * channels * (bitsPerSample / 8);
            int blockAlign = channels * (bitsPerSample / 8);
            int dataSize   = pcmData.Length * (bitsPerSample / 8);

            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);
            foreach (short s in pcmData) writer.Write(s);

            return stream.ToArray();
        }
    }
}
