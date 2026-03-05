#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Reflection;

public class UNAutoBaker : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    private static readonly FieldInfo TrackerHashField =
        typeof(UNTracker).GetField("hash", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (!scene.IsValid() || scene.path.Contains("Prefab")) return;

        List<UNTracker> allTrackers = new List<UNTracker>();
        GameObject[] rootObjects = scene.GetRootGameObjects();

        // Iterate through all Transform in the scene.
        foreach (var root in rootObjects)
        {
            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in children)
            {
                // The registry itself is not baked.
                if (t.name == "~UN_AutoRegistry") continue;

                int hash = UN.GetHash(t.name);
                
                // At the time of scene build, physically embed the Tracker.
                UNTracker tracker = t.GetComponent<UNTracker>();
                if (tracker == null)
                {
                    tracker = t.gameObject.AddComponent<UNTracker>();
                }
                
                SetTrackerHash(tracker, hash);
                tracker.hideFlags = HideFlags.HideInInspector; // Hide from the developer's eyes.
                
                allTrackers.Add(tracker);
            }
        }

        // Create a dummy object for the registry in the scene.
        GameObject registryObj = new GameObject("~UN_AutoRegistry");
        registryObj.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor;

        UNSceneRegistry registry = registryObj.AddComponent<UNSceneRegistry>();
        
        // Assign all trackers collected to the registry.
        registry.bakedTrackers = allTrackers.ToArray();
    }

    private static void SetTrackerHash(UNTracker tracker, int hash)
    {
        if (tracker == null) return;
        if (TrackerHashField == null)
        {
            Debug.LogError("UNAutoBaker: Failed to locate UNTracker.hash field. Baking aborted for this tracker.");
            return;
        }

        TrackerHashField.SetValue(tracker, hash);
        EditorUtility.SetDirty(tracker);
    }
}
#endif