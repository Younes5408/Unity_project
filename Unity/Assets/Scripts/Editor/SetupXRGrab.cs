using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// One-shot Editor menu: configures XR Grab Interactable on every chair/table
/// prefab and adds XR Direct Interactor + grab trigger to both Hand objects in
/// the current scene. Idempotent — safe to re-run after adding new furniture.
///
/// Run via:  Tools → XR → Setup Hand Grab
/// </summary>
public static class SetupXRGrab
{
    private const string FurnitureFolder = "Assets/Resources/Furniture";
    private const float HandGrabRadius = 0.08f;     // sphere trigger on each hand
    private const string MovableTag = "Movable";

    [MenuItem("Tools/XR/Setup Hand Grab")]
    public static void Run()
    {
        EnsureTag(MovableTag);

        int hands = WireHands();
        int prefabs = WirePrefabs();

        AssetDatabase.SaveAssets();
        if (EditorSceneManager.GetActiveScene().IsValid())
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        EditorUtility.DisplayDialog(
            "XR Grab Setup",
            $"Wired {hands} hand(s) and {prefabs} furniture prefab(s).\n\n" +
            "Hand Direct Interactors radius = " + HandGrabRadius + " m.\n" +
            "Chairs/tables are kinematic until grabbed.",
            "OK");
    }

    // ---------- Hands ----------

    private static int WireHands()
    {
        int count = 0;
        foreach (var name in new[] { "LeftHand", "RightHand" })
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                Debug.LogWarning($"[SetupXRGrab] '{name}' not found in scene — skipped.");
                continue;
            }

            // Sphere collider as the grab volume (must be a trigger)
            var sphere = go.GetComponent<SphereCollider>();
            if (sphere == null) sphere = go.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = HandGrabRadius;
            sphere.center = Vector3.zero;

            // XR Direct Interactor — grabs anything tagged Interactable inside the sphere
            // when the controller's selectAction (grip) is pressed.
            var interactor = go.GetComponent<NearFarInteractor>();
            // NearFarInteractor is the XRIT 3.x replacement for XRDirectInteractor +
            // XRRayInteractor combined. For pure direct grab we use the older
            // XRDirectInteractor — simpler config, no ray needed.
            if (interactor == null)
            {
                var direct = go.GetComponent<XRDirectInteractor>();
                if (direct == null) go.AddComponent<XRDirectInteractor>();
            }

            count++;
        }
        return count;
    }

    // ---------- Furniture Prefabs ----------

    private static int WirePrefabs()
    {
        int count = 0;
        var guids = AssetDatabase.FindAssets("t:Prefab", new[] { FurnitureFolder });
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            // Only target chairs/tables — skip Kitchen_Set* and other complex props
            var fname = System.IO.Path.GetFileNameWithoutExtension(path).ToLower();
            if (!(fname.StartsWith("chair") || fname.StartsWith("table"))) continue;

            var prefab = PrefabUtility.LoadPrefabContents(path);
            try
            {
                bool changed = ConfigurePrefab(prefab);
                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefab, path);
                    Debug.Log($"[SetupXRGrab] Wired: {path}");
                    count++;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefab);
            }
        }
        return count;
    }

    private static bool ConfigurePrefab(GameObject prefab)
    {
        bool changed = false;

        // Tag for downstream queries (analytics / AI tool callbacks)
        if (prefab.tag != MovableTag) { prefab.tag = MovableTag; changed = true; }

        // Rigidbody — kinematic at rest so chairs don't fall through floors.
        // XRGrabInteractable temporarily switches kinematic off during grab and
        // restores the authored state on release.
        var rb = prefab.GetComponent<Rigidbody>();
        if (rb == null) { rb = prefab.AddComponent<Rigidbody>(); changed = true; }
        if (!rb.isKinematic) { rb.isKinematic = true; changed = true; }
        if (rb.useGravity) { rb.useGravity = false; changed = true; }

        // Ensure at least one collider exists on the root or children. Most
        // furniture FBX have child mesh colliders already — only add a default
        // BoxCollider if the prefab has NO collider anywhere.
        if (prefab.GetComponentInChildren<Collider>() == null)
        {
            var box = prefab.AddComponent<BoxCollider>();
            box.size = new Vector3(0.6f, 0.8f, 0.6f); // generic chair/table-ish
            changed = true;
        }

        // XR Grab Interactable
        var grab = prefab.GetComponent<XRGrabInteractable>();
        if (grab == null)
        {
            grab = prefab.AddComponent<XRGrabInteractable>();
            // Smoother motion: track position/rotation rather than physics impulses
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.throwOnDetach = false;  // furniture shouldn't fly across the room
            changed = true;
        }

        return changed;
    }

    // ---------- Tag helper ----------

    private static void EnsureTag(string tag)
    {
        var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (asset == null || asset.Length == 0) return;
        var so = new SerializedObject(asset[0]);
        var tagsProp = so.FindProperty("tags");
        for (int i = 0; i < tagsProp.arraySize; i++)
            if (tagsProp.GetArrayElementAtIndex(i).stringValue == tag) return;

        tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
        tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tag;
        so.ApplyModifiedProperties();
    }
}
