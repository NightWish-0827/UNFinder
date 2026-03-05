using UnityEngine;

public static partial class UN
{
    /// <summary>
    /// Find an object with O(1) cost. If it is not in the cache, perform GameObject.Find only once.
    /// Cache-hit path guarantees extreme performance with no Unity fake-null bridging and no GC allocation.
    /// </summary>
    public static GameObject Find(string name)
    {
        int hash = GetHash(name);

        // 1. Fast Path: Pure C# memory-based O(1) search (no null check).
        if (_fastBucket.TryGetValue(hash, out var list) && list.Count > 0)
        {
            return list[0]; 
        }

        // 2. Lazy Fallback: Perform native search only if it is not in the cache.
        GameObject found = GameObject.Find(name);
        if (!ReferenceEquals(found, null))
        {
            EnsureTracker(found, hash);
        }
        return found;
    }

    /// <summary>
    /// Find an object with O(1) cost and return the cached component.
    /// </summary>
    public static T FindComponent<T>(string name) where T : Component
    {
        GameObject go = Find(name);
        if (ReferenceEquals(go, null)) return null;

        // Component cache lookup (no memory leak with ConditionalWeakTable).
        if (ComponentCache<T>.Map.TryGetValue(go, out var cached) && !ReferenceEquals(cached, null))
        {
            return cached;
        }

        // Micro-optimization: Use TryGetComponent instead of GetComponent (no GC allocation).
        if (go.TryGetComponent<T>(out var component))
        {
            ComponentCache<T>.Map.Remove(go);
            ComponentCache<T>.Map.Add(go, component);
        }

        return component;
    }

    /// <summary>
    /// Explicitly register an object with a specific name in the cache.
    /// </summary>
    public static void Bind(GameObject obj, string name)
    {
        if (ReferenceEquals(obj, null)) return;

        int hash = GetHash(name);
        EnsureTracker(obj, hash);
    }

    /// <summary>
    /// Change the name of the object and maintain cache integrity.
    /// The name change must be performed only through this API.
    /// </summary>
    public static void Rename(GameObject obj, string newName)
    {
        if (ReferenceEquals(obj, null)) return;

        int newHash = GetHash(newName);
        
        // Note: name property assignment causes C++ bridging, but since it is a one-time setting, it is allowed.
        obj.name = newName; 
        
        EnsureTracker(obj, newHash);
    }

    /// <summary>
    /// Create an object and immediately register it in the O(1) search cache.
    /// </summary>
    public static GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
    {
        GameObject instance = UnityEngine.Object.Instantiate(prefab, position, rotation, parent);
        
        // Remove the "(Clone)" text and synchronize the name.
        string instanceName = prefab.name; 
        instance.name = instanceName; 

        int hash = GetHash(instanceName);
        EnsureTracker(instance, hash);

        return instance;
    }

    /// <summary>
    /// Create an object and immediately cache the component and return it.
    /// </summary>
    public static T Instantiate<T>(T prefab, Vector3 position, Quaternion rotation, Transform parent = null) where T : Component
    {
        T instance = UnityEngine.Object.Instantiate(prefab, position, rotation, parent);
        
        string instanceName = prefab.gameObject.name;
        instance.gameObject.name = instanceName;

        int hash = GetHash(instanceName);
        EnsureTracker(instance.gameObject, hash);

        ComponentCache<T>.Map.Remove(instance.gameObject);
        ComponentCache<T>.Map.Add(instance.gameObject, instance);
        
        return instance;
    }

    // Internal helper method to secretly attach the Tracker to the object and initialize it.
    private static void EnsureTracker(GameObject obj, int hash)
    {
        // Optimization: TryGetComponent is slightly faster and safer.
        if (!obj.TryGetComponent<UNTracker>(out var tracker))
        {
            tracker = obj.AddComponent<UNTracker>();
            tracker.hideFlags = HideFlags.HideInInspector;
        }
        tracker.Initialize(hash);
    }
}