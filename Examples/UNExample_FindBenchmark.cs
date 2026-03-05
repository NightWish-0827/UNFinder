using System.Diagnostics;
using UnityEngine;

public sealed class UNExample_FindBenchmark : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private string targetName = "EX_Target_000";

    [Header("Benchmark")]
    [SerializeField, Min(1)] private int iterations = 20000;
    [SerializeField] private bool warmup = true;

    private void Awake()
    {
        if (GameObject.Find(targetName) == null)
        {
            new GameObject(targetName);
        }
    }

    private void Start()
    {
        if (warmup)
        {
            _ = UN.Find(targetName);
        }

        var sw = new Stopwatch();

        // GameObject.Find baseline
        sw.Restart();
        for (int i = 0; i < iterations; i++)
        {
            _ = GameObject.Find(targetName);
        }
        sw.Stop();
        long goFindMs = sw.ElapsedMilliseconds;

        // UN.Find (first call may have fallback cost if not warmed up)
        sw.Restart();
        for (int i = 0; i < iterations; i++)
        {
            _ = UN.Find(targetName);
        }
        sw.Stop();
        long unFindMs = sw.ElapsedMilliseconds;

        UnityEngine.Debug.Log(
            $"[UNFinder PoC] target='{targetName}', iter={iterations}, warmup={warmup}\n" +
            $"GameObject.Find: {goFindMs} ms\n" +
            $"UN.Find:         {unFindMs} ms");
    }
}

