using UnityEngine;

public sealed class UNExample_RuntimeRenameRegression : MonoBehaviour
{
    [SerializeField] private string initialName = "EX_Rename_Target";
    [SerializeField] private string renamedTo = "EX_Rename_Target_NEW";

    [ContextMenu("EX/UNFinder/Rename Regression Test")]
    private void Run()
    {
        var go = GameObject.Find(initialName);
        if (go == null)
        {
            go = new GameObject(initialName);
        }

        // register once
        _ = UN.Find(initialName);

        go.name = renamedTo;

        var findOld = UN.Find(initialName);
        var findNew = UN.Find(renamedTo);

        UnityEngine.Debug.Log(
            $"[UNFinder PoC] Rename regression\n" +
            $"old='{initialName}', new='{renamedTo}'\n" +
            $"UN.Find(old) => {(findOld == null ? "null" : findOld.GetInstanceID().ToString())}\n" +
            $"UN.Find(new) => {(findNew == null ? "null" : findNew.GetInstanceID().ToString())}");
    }
}

