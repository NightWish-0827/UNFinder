using UnityEngine;

public sealed class UNExample_DuplicateNameBehavior : MonoBehaviour
{
    [SerializeField] private string duplicateName = "EX_DUP";

    [ContextMenu("EX/UNFinder/Create Duplicate Names")]
    private void CreateDuplicates()
    {
        var a = new GameObject(duplicateName);
        a.transform.SetParent(transform, worldPositionStays: false);

        var b = new GameObject(duplicateName);
        b.transform.SetParent(transform, worldPositionStays: false);

        UnityEngine.Debug.Log($"[UNFinder PoC] Created 2 objects with same name '{duplicateName}'. hash={UN.GetHash(duplicateName)}");
    }

    [ContextMenu("EX/UNFinder/UN.Find Duplicate (shows 'first' behavior)")]
    private void FindDuplicate()
    {
        var found = UN.Find(duplicateName);
        if (found == null)
        {
            UnityEngine.Debug.Log($"[UNFinder PoC] UN.Find('{duplicateName}') => null (no object in scene)");
            return;
        }

        UnityEngine.Debug.Log(
            $"[UNFinder PoC] UN.Find('{duplicateName}') => instanceId={found.GetInstanceID()}\n" +
            $"warning: if there are multiple objects with the same name, the returned object is not guaranteed to be the 'first' one. (this SDK treats names as addresses, so it is recommended to use unique naming)");
    }
}

