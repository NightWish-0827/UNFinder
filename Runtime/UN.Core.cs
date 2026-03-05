using UnityEngine;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public static partial class UN
{
    // Main bucket for fast O(1) lookup.
    private static readonly Dictionary<int, List<GameObject>> _fastBucket = new Dictionary<int, List<GameObject>>(5000);

    // ConditionalWeakTable for component caching (to prevent memory leaks).
    private static class ComponentCache<T> where T : Component
    {
        public static readonly ConditionalWeakTable<GameObject, T> Map = new ConditionalWeakTable<GameObject, T>();
    }

#if UNITY_EDITOR
    // When domain reload is disabled in the editor, initialize the garbage data from the previous play session.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticData()
    {
        _fastBucket.Clear();
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHash(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        unchecked
        {
            int hash = (int)2166136261;
            for (int i = 0; i < name.Length; i++)
            {
                hash = (hash ^ name[i]) * 16777619;
            }
            return hash;
        }
    }

    // Internal API for Tracker only.
    internal static void RegisterToCache(int hash, GameObject obj)
    {
        if (!_fastBucket.TryGetValue(hash, out var list))
        {
            list = new List<GameObject>(4);
            _fastBucket[hash] = list;
        }
        list.Add(obj);
    }

    internal static void RemoveFromCache(int hash, GameObject obj)
    {
        if (_fastBucket.TryGetValue(hash, out var list))
        {
            list.Remove(obj);
        }
    }
}