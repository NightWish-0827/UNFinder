using UnityEngine;

// Run before all logic (including Awake) to prepare the cache.
[DefaultExecutionOrder(-32000)]
public class UNSceneRegistry : MonoBehaviour
{
    [HideInInspector] public UNTracker[] bakedTrackers;

    private void Awake()
    {
        if (bakedTrackers == null) return;

        // At the time of scene start, activate all trackers and push them into _fastBucket.
        for (int i = 0; i < bakedTrackers.Length; i++)
        {
            var tracker = bakedTrackers[i];
            if (tracker != null)
            {
                tracker.Initialize(tracker.hash);
            }
        }
    }
}