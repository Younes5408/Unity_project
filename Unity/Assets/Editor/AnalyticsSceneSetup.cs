using UnityEditor;
using UnityEngine;

/// <summary>
/// Menu helper: Tools → Analytics → Add to current scene.
/// Creates the two GameObjects the runtime needs (AnalyticsManager + DashboardPanel)
/// so you don't have to wire them by hand in MaMaison.unity.
/// </summary>
public static class AnalyticsSceneSetup
{
    [MenuItem("Tools/Analytics/Add to current scene")]
    public static void AddToScene()
    {
        // 1. AnalyticsManager — tracker singleton
        var trackerGo = GameObject.Find("AnalyticsManager");
        if (trackerGo == null)
        {
            trackerGo = new GameObject("AnalyticsManager");
            trackerGo.AddComponent<AnalyticsTracker>();
            Debug.Log("[AnalyticsSetup] Created AnalyticsManager");
        }
        else
        {
            Debug.Log("[AnalyticsSetup] AnalyticsManager already exists");
        }

        // 2. AnalyticsDashboard — WorldSpace canvas, hidden by default
        var dashGo = GameObject.Find("AnalyticsDashboard");
        if (dashGo == null)
        {
            dashGo = new GameObject("AnalyticsDashboard");
            var canvas = dashGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            dashGo.AddComponent<UnityEngine.UI.CanvasScaler>();
            dashGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            dashGo.AddComponent<CanvasGroup>();
            dashGo.AddComponent<AnalyticsDashboardPanel>();
            // Place ~1.5 m in front of origin at eye height; runtime repositions on Open
            dashGo.transform.position = new Vector3(0, 1.6f, 1.5f);
            Debug.Log("[AnalyticsSetup] Created AnalyticsDashboard");
        }
        else
        {
            Debug.Log("[AnalyticsSetup] AnalyticsDashboard already exists");
        }

        EditorUtility.SetDirty(trackerGo);
        EditorUtility.SetDirty(dashGo);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

        EditorUtility.DisplayDialog("Analytics Setup",
            "AnalyticsManager + AnalyticsDashboard added to the current scene.\n" +
            "Save the scene (Ctrl+S) and press Play to test.\n" +
            "Voice trigger: 'montre les statistiques' to open, 'ferme' to close.",
            "OK");
    }
}
