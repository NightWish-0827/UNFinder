#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

public class UNAutoBaker : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (!scene.IsValid()) return;

        // [FIX-2] 취약한 string.Contains("Prefab") 대신 Unity 공식 API를 사용한다.
        //         PrefabStageUtility는 씬 경로와 무관하게 프리팹 편집 모드를 정확히 식별한다.
        if (PrefabStageUtility.GetCurrentPrefabStage() != null) return;

        var allTrackers = new List<UNTracker>();

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "~UN_AutoRegistry") continue;

                // [FIX-2] 옵트인 방식: [UNBake] 지정 컴포넌트가 없는 오브젝트는 건너뛴다.
                //         이전에는 씬의 모든 Transform을 무조건 베이크해
                //         대형 씬에서 불필요한 MonoBehaviour가 대량 생성되었다.
                if (!HasUNBakeMarker(t)) continue;

                string name = t.name;
                int    hash = UN.GetHash(name);

                // [FIX Reflection] BakeAttach는 UNTracker의 internal static 팩토리다.
                //                  FieldInfo/Reflection 없이 컴파일러 검증 가능하며 IL2CPP에서도 안전하다.
                var tracker = UNTracker.BakeAttach(t.gameObject, hash, name);
                EditorUtility.SetDirty(tracker);
                allTrackers.Add(tracker);
            }
        }

        var registryObj = new GameObject("~UN_AutoRegistry");
        registryObj.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor;

        var registry = registryObj.AddComponent<UNSceneRegistry>();
        registry.bakedTrackers = allTrackers.ToArray();
    }

    // 이 Transform의 MonoBehaviour 중 [UNBake]가 하나라도 있으면 true를 반환한다.
    // 빌드 시점 전용 경로이므로 여기서의 리플렉션 비용은 실질적으로 문제되지 않는다.
    private static bool HasUNBakeMarker(Transform t)
    {
        foreach (var comp in t.GetComponents<MonoBehaviour>())
        {
            if (comp != null &&
                comp.GetType().IsDefined(typeof(UNBakeAttribute), inherit: true))
                return true;
        }
        return false;
    }
}
#endif