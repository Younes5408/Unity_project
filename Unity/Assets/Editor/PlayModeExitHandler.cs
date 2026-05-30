using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Shows a Save/Delete dialog when exiting Play mode.
/// Lets the player choose whether to keep or discard the house layout in MongoDB.
/// </summary>
[InitializeOnLoad]
public static class PlayModeExitHandler
{
    private static bool _wasPlaying = false;

    static PlayModeExitHandler()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        // Detect transition: was playing -> now editing
        if (state == PlayModeStateChange.EnteredEditMode && _wasPlaying)
        {
            _wasPlaying = false;
            ShowSaveDeleteDialog();
        }
        else if (state == PlayModeStateChange.EnteredPlayMode)
        {
            _wasPlaying = true;
        }
    }

    private static void ShowSaveDeleteDialog()
    {
        // 3-button dialog: Save / Delete / (Cancel = do nothing, same as Save)
        int choice = EditorUtility.DisplayDialogComplex(
            "Session Terminée",
            "Voulez-vous sauvegarder ou supprimer la maison créée pendant cette session ?",
            "Sauvegarder",   // button 0
            "Supprimer",     // button 1
            "Annuler"        // button 2
        );

        if (choice == 1) // Delete
        {
            DeleteHouseData();
        }
        else
        {
            Debug.Log("[ArchiAgent] Données sauvegardées dans MongoDB.");
        }
    }

    private static void DeleteHouseData()
    {
        string url = "http://localhost:8000/api/clear/maison_001";

        using (UnityWebRequest req = UnityWebRequest.PostWwwForm(url, ""))
        {
            req.timeout = 5;
            req.downloadHandler = new DownloadHandlerBuffer();
            var op = req.SendWebRequest();

            // Block briefly since we're in edit mode
            while (!op.isDone) { }

            if (req.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("[ArchiAgent] Données supprimées avec succès. " + req.downloadHandler.text);
            }
            else
            {
                Debug.LogWarning($"[ArchiAgent] Impossible de supprimer: {req.error}");
            }
        }
    }
}
