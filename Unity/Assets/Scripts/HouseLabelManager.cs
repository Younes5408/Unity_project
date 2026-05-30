using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using TMPro;

/// <summary>
/// Auto-generates a floating WorldSpace "Maison X" label above every House1-8
/// in the scene. Labels hide when the player is within <hideDistance> metres
/// so the interior view is uncluttered.
/// Attach to any persistent GameObject (e.g. an empty "Managers" GO).
/// </summary>
public class HouseLabelManager : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Height above the house's top bounds edge.")]
    public float heightAboveBounds = 2.5f;

    [Tooltip("Hide the label when the player is closer than this distance.")]
    public float hideDistance = 8f;

    [Tooltip("Font size of the house name label.")]
    public float fontSize = 3f;

    [Header("References")]
    [Tooltip("Player Transform used for distance check. Auto-found if null.")]
    public Transform playerTransform;

    private struct LabelEntry
    {
        public GameObject labelGo;
        public Canvas canvas;
    }

    private List<LabelEntry> labels = new List<LabelEntry>();

    void Start()
    {
        if (playerTransform == null)
        {
            var fpc = FindObjectOfType<FirstPersonController>();
            if (fpc != null) playerTransform = fpc.transform;
        }

        SpawnLabels();
    }

    void Update()
    {
        if (playerTransform == null) return;
        Vector3 playerPos = playerTransform.position;

        foreach (var entry in labels)
        {
            if (entry.labelGo == null) continue;
            float dist = Vector3.Distance(playerPos, entry.labelGo.transform.position);
            entry.labelGo.SetActive(dist > hideDistance);
        }
    }

    private void SpawnLabels()
    {
        // Match House1 … House8 (and any AI-added ones like House3_abc123)
        var regex = new Regex(@"^House(\d+)", RegexOptions.IgnoreCase);

        // Search all root GameObjects
        var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        foreach (var root in roots)
        {
            var match = regex.Match(root.name);
            if (!match.Success) continue;

            string number = match.Groups[1].Value;
            string labelText = "Maison " + number;

            // Compute top of house bounds
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            float topY = root.transform.position.y + 5f; // fallback
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
                topY = b.max.y;
            }

            // Create WorldSpace Canvas
            GameObject canvasGo = new GameObject("Label_" + root.name);
            canvasGo.transform.position = new Vector3(
                root.transform.position.x,
                topY + heightAboveBounds,
                root.transform.position.z
            );
            canvasGo.transform.rotation = Quaternion.identity;

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();

            RectTransform canvasRect = canvasGo.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(8f, 2f);

            // TMP text
            GameObject textGo = new GameObject("Text");
            textGo.transform.SetParent(canvasGo.transform, false);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = labelText;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.fontStyle = FontStyles.Bold;
            // Outline for readability against any background
            tmp.outlineWidth = 0.2f;
            tmp.outlineColor = new Color32(0, 0, 0, 200);

            RectTransform textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            labels.Add(new LabelEntry { labelGo = canvasGo, canvas = canvas });
            Debug.Log($"[HouseLabelManager] Label '{labelText}' at y={topY + heightAboveBounds:F1}");
        }

        Debug.Log($"[HouseLabelManager] Created {labels.Count} house labels.");
    }
}
