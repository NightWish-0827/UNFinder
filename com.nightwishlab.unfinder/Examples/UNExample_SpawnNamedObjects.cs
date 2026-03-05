using UnityEngine;

public sealed class UNExample_SpawnNamedObjects : MonoBehaviour
{
    [Header("Spawn")]
    [SerializeField] private string baseName = "EX_Target";
    [SerializeField, Min(1)] private int count = 10;
    [SerializeField] private bool createDuplicateNamePair = true;

    [ContextMenu("EX/UNFinder/Spawn Named Objects")]
    private void Spawn()
    {
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject($"{baseName}_{i:000}");
            go.transform.SetParent(transform, worldPositionStays: false);
        }

        if (createDuplicateNamePair)
        {
            var a = new GameObject($"{baseName}_DUP");
            a.transform.SetParent(transform, worldPositionStays: false);
            var b = new GameObject($"{baseName}_DUP");
            b.transform.SetParent(transform, worldPositionStays: false);
        }
    }
}

