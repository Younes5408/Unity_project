using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// VR in-world analytics dashboard panel.
///
/// Polls /api/ui/pending every second to learn when to open or close.
/// When opened, fetches /api/analytics/dashboard, renders stat cards,
/// bar charts of top placed objects, and a 16×16 player-position heatmap.
///
/// Layout is built at runtime — you only need to drop this script on a
/// WorldSpace Canvas GameObject in the scene; it creates its own children.
/// </summary>
[RequireComponent(typeof(Canvas), typeof(CanvasGroup))]
public class AnalyticsDashboardPanel : MonoBehaviour
{
    [Header("API Settings")]
    public string apiBaseUrl = "http://127.0.0.1:8000";
    public string houseId = "maison_001";

    [Header("Behaviour")]
    [Tooltip("Distance in front of the player when the panel opens.")]
    public float openDistance = 1.5f;
    [Tooltip("Vertical offset from camera (eye level adjustment).")]
    public float openHeight = 0.0f;
    [Tooltip("Seconds between /api/ui/pending polls.")]
    public float pollIntervalSeconds = 1f;
    [Tooltip("Fade duration in seconds.")]
    public float fadeSeconds = 0.3f;

    private CanvasGroup _group;
    private Camera _cam;

    // UI references built at runtime
    private TMP_Text _txtCommands;
    private TMP_Text _txtPlacements;
    private TMP_Text _txtLatency;
    private TMP_Text _txtDuration;
    private Transform _barsParent;
    private RawImage _heatmapImage;
    private Texture2D _heatmapTex;

    private bool _isOpen;
    private bool _isFading;

    void Awake()
    {
        _group = GetComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.interactable = false;
        _group.blocksRaycasts = false;
        gameObject.SetActive(true);

        _cam = Camera.main;
        BuildUi();
        _heatmapTex = new Texture2D(16, 16, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        if (_heatmapImage != null) _heatmapImage.texture = _heatmapTex;
    }

    void Start()
    {
        StartCoroutine(PollLoop());
    }

    void Update()
    {
        // Close on Escape (PC convenience)
        if (_isOpen && Input.GetKeyDown(KeyCode.Escape)) Close();

        // Billboard towards the camera while open
        if (_isOpen && _cam != null)
        {
            transform.LookAt(transform.position + _cam.transform.forward);
        }
    }

    // -------- Public API --------

    public void Open()
    {
        if (_isOpen) return;
        PositionInFrontOfPlayer();
        StartCoroutine(FadeTo(1f));
        StartCoroutine(FetchAndRender());
        _isOpen = true;
    }

    public void Close()
    {
        if (!_isOpen) return;
        StartCoroutine(FadeTo(0f));
        _isOpen = false;
    }

    // -------- Polling --------

    private IEnumerator PollLoop()
    {
        var wait = new WaitForSeconds(pollIntervalSeconds);
        while (true)
        {
            yield return StartCoroutine(CheckPendingCommand());
            yield return wait;
        }
    }

    private IEnumerator CheckPendingCommand()
    {
        string url = $"{apiBaseUrl}/api/ui/pending?house_id={UnityWebRequest.EscapeURL(houseId)}";
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) yield break;

            string body = req.downloadHandler.text;
            if (body.Contains("\"open_dashboard\"")) Open();
            else if (body.Contains("\"close_dashboard\"")) Close();
        }
    }

    // -------- Fetch + render --------

    private IEnumerator FetchAndRender()
    {
        string sid = AnalyticsTracker.Instance != null ? AnalyticsTracker.Instance.SessionId : "";
        string url = $"{apiBaseUrl}/api/analytics/dashboard?session_id={UnityWebRequest.EscapeURL(sid)}";
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 8;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Dashboard] fetch failed: {req.error}");
                yield break;
            }
            Render(req.downloadHandler.text);
        }
    }

    private void Render(string json)
    {
        // Lightweight extraction — avoids needing a full JSON parser
        int commands = ExtractInt(json, "total_commands");
        int placements = ExtractInt(json, "total_placements");
        int latency = ExtractInt(json, "avg_latency_ms");
        int duration = ExtractInt(json, "duration_min");
        int failed = ExtractInt(json, "failed_commands");

        if (_txtCommands) _txtCommands.text = commands.ToString();
        if (_txtPlacements) _txtPlacements.text = placements.ToString();
        if (_txtLatency) _txtLatency.text = $"{latency}ms";
        if (_txtDuration) _txtDuration.text = $"{duration} min";

        RenderBars(json);
        RenderHeatmap(json);
    }

    private void RenderBars(string json)
    {
        if (_barsParent == null) return;
        foreach (Transform t in _barsParent) Destroy(t.gameObject);

        var entries = ExtractTopObjects(json);
        int max = 1;
        foreach (var e in entries) if (e.count > max) max = e.count;

        foreach (var e in entries)
        {
            var row = new GameObject($"Bar_{e.objectType}", typeof(RectTransform));
            row.transform.SetParent(_barsParent, false);
            var rt = row.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(0, 28);

            var hl = row.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 8;
            hl.childForceExpandWidth = false;
            hl.childForceExpandHeight = true;

            AddLabel(row.transform, e.objectType, 120, TextAlignmentOptions.Left);
            var barBg = CreateImage(row.transform, "BarBg", new Color(0.2f, 0.2f, 0.25f, 0.8f), 280, 18);
            var fill = CreateImage(barBg.transform, "Fill", new Color(0.35f, 0.85f, 0.55f, 1f),
                                   280f * (e.count / (float)max), 18);
            var fillRt = fill.GetComponent<RectTransform>();
            fillRt.anchorMin = new Vector2(0, 0.5f);
            fillRt.anchorMax = new Vector2(0, 0.5f);
            fillRt.pivot = new Vector2(0, 0.5f);
            fillRt.anchoredPosition = Vector2.zero;
            AddLabel(row.transform, e.count.ToString(), 40, TextAlignmentOptions.Right);
        }
    }

    private void RenderHeatmap(string json)
    {
        if (_heatmapTex == null) return;
        // Extract heatmap matrix: "heatmap":[[..],[..],...]
        int hStart = json.IndexOf("\"heatmap\"");
        if (hStart < 0) { FillHeatmap(null); return; }
        int bStart = json.IndexOf("[[", hStart);
        int bEnd = json.IndexOf("]]", bStart);
        if (bStart < 0 || bEnd < 0) { FillHeatmap(null); return; }

        string matrix = json.Substring(bStart + 1, bEnd - bStart);
        // Now parse rows: split on "],["
        var rows = matrix.Trim('[', ']').Split(new[] { "],[" }, StringSplitOptions.None);
        var grid = new int[16, 16];
        int maxV = 1;
        for (int r = 0; r < Math.Min(16, rows.Length); r++)
        {
            var cells = rows[r].Trim('[', ']').Split(',');
            for (int c = 0; c < Math.Min(16, cells.Length); c++)
            {
                int.TryParse(cells[c].Trim(), out int v);
                grid[r, c] = v;
                if (v > maxV) maxV = v;
            }
        }
        FillHeatmap(grid, maxV);
    }

    private void FillHeatmap(int[,] grid, int maxV = 1)
    {
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int v = grid?[y, x] ?? 0;
                float t = v / (float)maxV;
                Color col = Color.Lerp(new Color(0.05f, 0.1f, 0.4f, 1f), new Color(1f, 0.2f, 0.1f, 1f), t);
                if (v == 0) col = new Color(0.08f, 0.08f, 0.12f, 1f);
                _heatmapTex.SetPixel(x, y, col);
            }
        }
        _heatmapTex.Apply();
    }

    // -------- Helpers --------

    private static int ExtractInt(string json, string key)
    {
        int i = json.IndexOf($"\"{key}\"");
        if (i < 0) return 0;
        int colon = json.IndexOf(':', i);
        if (colon < 0) return 0;
        int j = colon + 1;
        while (j < json.Length && (json[j] == ' ' || json[j] == '"')) j++;
        int k = j;
        while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '-' || json[k] == '.')) k++;
        int.TryParse(json.Substring(j, k - j), out int val);
        return val;
    }

    private struct TopEntry { public string objectType; public int count; }

    private static List<TopEntry> ExtractTopObjects(string json)
    {
        var list = new List<TopEntry>();
        int start = json.IndexOf("\"top_objects\"");
        if (start < 0) return list;
        int arrStart = json.IndexOf('[', start);
        int arrEnd = json.IndexOf(']', arrStart);
        if (arrStart < 0 || arrEnd < 0) return list;
        string arr = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
        int p = 0;
        while (p < arr.Length)
        {
            int oStart = arr.IndexOf('{', p);
            if (oStart < 0) break;
            int oEnd = arr.IndexOf('}', oStart);
            if (oEnd < 0) break;
            string obj = arr.Substring(oStart, oEnd - oStart + 1);
            int oi = obj.IndexOf("\"object_type\"");
            int ci = obj.IndexOf("\"count\"");
            string objectType = "";
            int count = 0;
            if (oi >= 0)
            {
                int q1 = obj.IndexOf('"', oi + 13);
                int q2 = obj.IndexOf('"', q1 + 1);
                int q3 = obj.IndexOf('"', q2 + 1);
                if (q1 > 0 && q2 > q1 && q3 > q2)
                    objectType = obj.Substring(q2 + 1, q3 - q2 - 1);
            }
            if (ci >= 0)
            {
                int colon = obj.IndexOf(':', ci);
                int k = colon + 1;
                while (k < obj.Length && (obj[k] == ' ' || obj[k] == '"')) k++;
                int m = k;
                while (m < obj.Length && char.IsDigit(obj[m])) m++;
                int.TryParse(obj.Substring(k, m - k), out count);
            }
            list.Add(new TopEntry { objectType = objectType, count = count });
            p = oEnd + 1;
        }
        return list;
    }

    private void PositionInFrontOfPlayer()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;
        Vector3 fwd = _cam.transform.forward;
        transform.position = _cam.transform.position + fwd * openDistance + Vector3.up * openHeight;
        transform.LookAt(transform.position + fwd);
    }

    private IEnumerator FadeTo(float target)
    {
        if (_isFading) yield break;
        _isFading = true;
        float from = _group.alpha;
        float t = 0f;
        while (t < fadeSeconds)
        {
            t += Time.deltaTime;
            _group.alpha = Mathf.Lerp(from, target, t / fadeSeconds);
            yield return null;
        }
        _group.alpha = target;
        _group.interactable = target > 0.5f;
        _group.blocksRaycasts = target > 0.5f;
        _isFading = false;
    }

    // -------- UI build (runtime, no prefab required) --------

    private void BuildUi()
    {
        var canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = (RectTransform)transform;
        rt.sizeDelta = new Vector2(1200, 900);
        rt.localScale = Vector3.one * 0.001f;

        // Background
        var bg = CreateImage(transform, "Background", new Color(0.08f, 0.09f, 0.15f, 0.92f), 1200, 900);
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        // Title
        AddLabel(transform, "Statistiques de Session", 1100, TextAlignmentOptions.Center,
                 fontSize: 36, anchoredPos: new Vector2(0, 410));

        // Close button (top right)
        var closeBtn = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
        closeBtn.transform.SetParent(transform, false);
        var cbRt = closeBtn.GetComponent<RectTransform>();
        cbRt.sizeDelta = new Vector2(60, 60);
        cbRt.anchoredPosition = new Vector2(560, 410);
        closeBtn.GetComponent<Image>().color = new Color(0.6f, 0.2f, 0.2f, 0.9f);
        closeBtn.GetComponent<Button>().onClick.AddListener(Close);
        AddLabel(closeBtn.transform, "X", 60, TextAlignmentOptions.Center, fontSize: 32);

        // Stat cards row
        _txtCommands = CreateCard("Commandes", new Vector2(-450, 250));
        _txtPlacements = CreateCard("Placements", new Vector2(-150, 250));
        _txtLatency = CreateCard("Lat. moy", new Vector2(150, 250));
        _txtDuration = CreateCard("Durée", new Vector2(450, 250));

        // Bars section
        AddLabel(transform, "Objets les plus placés", 1100, TextAlignmentOptions.Left,
                 fontSize: 22, anchoredPos: new Vector2(-450, 80));

        var barsContainer = new GameObject("Bars", typeof(RectTransform));
        barsContainer.transform.SetParent(transform, false);
        var bcRt = barsContainer.GetComponent<RectTransform>();
        bcRt.sizeDelta = new Vector2(1100, 200);
        bcRt.anchoredPosition = new Vector2(0, -50);
        var vlg = barsContainer.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 6;
        vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperLeft;
        _barsParent = barsContainer.transform;

        // Heatmap section
        AddLabel(transform, "Carte de chaleur — Positions joueur", 1100, TextAlignmentOptions.Left,
                 fontSize: 22, anchoredPos: new Vector2(-450, -200));

        var heatGo = new GameObject("Heatmap", typeof(RectTransform), typeof(RawImage));
        heatGo.transform.SetParent(transform, false);
        var hRt = heatGo.GetComponent<RectTransform>();
        hRt.sizeDelta = new Vector2(300, 300);
        hRt.anchoredPosition = new Vector2(0, -340);
        _heatmapImage = heatGo.GetComponent<RawImage>();
        _heatmapImage.color = Color.white;
    }

    private TMP_Text CreateCard(string title, Vector2 anchoredPos)
    {
        var card = CreateImage(transform, title + "Card", new Color(0.15f, 0.18f, 0.28f, 0.95f), 240, 140);
        var rt = card.GetComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        AddLabel(card.transform, title, 240, TextAlignmentOptions.Center,
                 fontSize: 20, anchoredPos: new Vector2(0, 45));
        var valueGo = new GameObject("Value", typeof(RectTransform));
        valueGo.transform.SetParent(card.transform, false);
        var vRt = valueGo.GetComponent<RectTransform>();
        vRt.sizeDelta = new Vector2(240, 80);
        vRt.anchoredPosition = new Vector2(0, -15);
        var tmp = valueGo.AddComponent<TextMeshProUGUI>();
        tmp.text = "0";
        tmp.fontSize = 52;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = new Color(0.55f, 0.95f, 0.85f, 1f);
        return tmp;
    }

    private static Image CreateImage(Transform parent, string name, Color color, float w, float h)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(w, h);
        var img = go.GetComponent<Image>();
        img.color = color;
        return img;
    }

    private static TMP_Text AddLabel(Transform parent, string text, float width,
                                     TextAlignmentOptions align,
                                     int fontSize = 18,
                                     Vector2? anchoredPos = null)
    {
        var go = new GameObject("Label_" + text, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(width, fontSize + 14);
        if (anchoredPos.HasValue) rt.anchoredPosition = anchoredPos.Value;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.alignment = align;
        tmp.color = Color.white;
        return tmp;
    }
}
