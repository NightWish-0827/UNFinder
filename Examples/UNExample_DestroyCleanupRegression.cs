using UnityEngine;

public sealed class UNExample_DestroyCleanupRegression : MonoBehaviour
{
    [SerializeField] private string targetName = "EX_Destroy_Target";

    [ContextMenu("EX/UNFinder/Destroy Cleanup Test")]
    private void Run()
    {
        var go = new GameObject(targetName);

        // Track it
        var found1 = UN.Find(targetName);

        // Destroy it and try finding again
        DestroyImmediate(go);
        var found2 = UN.Find(targetName);

        UnityEngine.Debug.Log(
            $"[UNFinder PoC] Destroy cleanup\n" +
            $"first find => {(found1 == null ? "null" : found1.GetInstanceID().ToString())}\n" +
            $"after destroy find => {(found2 == null ? "null" : found2.GetInstanceID().ToString())}");
    }
}

