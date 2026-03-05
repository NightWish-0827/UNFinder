using UnityEngine;

public sealed class UNExample_InstantiateAndFind : MonoBehaviour
{
    [Header("Prefab (optional)")]
    [SerializeField] private GameObject prefab;

    [Header("Spawn")]
    [SerializeField, Min(1)] private int spawnCount = 5;
    [SerializeField] private Vector3 startPos = Vector3.zero;
    [SerializeField] private Vector3 step = new Vector3(1.5f, 0f, 0f);

    [ContextMenu("EX/UNFinder/Instantiate + UN.Find")]
    private void Run()
    {
        if (prefab == null)
        {
            prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            prefab.name = "EX_Cube";
            prefab.SetActive(false); // prevent it from being visible as a scene object
        }

        for (int i = 0; i < spawnCount; i++)
        {
            Vector3 pos = startPos + step * i;
            var go = UN.Instantiate(prefab, pos, Quaternion.identity, parent: transform);
            go.SetActive(true);
        }

        var found = UN.Find(prefab.name);
        UnityEngine.Debug.Log(found != null
            ? $"[UNFinder PoC] UN.Find('{prefab.name}') => '{found.name}' (instanceId={found.GetInstanceID()})"
            : $"[UNFinder PoC] UN.Find('{prefab.name}') => null");
    }
}

