using UnityEngine;
using UnityEngine.SceneManagement;

[DefaultExecutionOrder(-32000)]
public class UNSceneRegistry : MonoBehaviour
{
    [HideInInspector] public UNTracker[] bakedTrackers;

    // 이 레지스트리가 속한 씬 — 어디티브 씬 디버깅용
    private Scene _owningScene;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // ── [FIX-11] 씬별 레지스트리 카운터 ──────────────────────────────────────
    //
    // 이전 코드: Awake 마다 FindObjectsByType<UNSceneRegistry> 를 호출.
    //   - FindObjectsByType 은 씬 전체를 순회하는 O(n) 연산이다.
    //   - UNSceneRegistry 의 [DefaultExecutionOrder(-32000)] 로 인해
    //     모든 Awake 중 가장 먼저 실행되는 시점에 씬 전체를 순회한다.
    //   - 씬에 오브젝트가 많을수록 초기화 스파이크가 커진다.
    //
    // 수정: Scene.handle(int) → 카운터 Dictionary 로 교체.
    //   - Awake 에서 카운터를 1 증가, OnDestroy 에서 1 감소.
    //   - 카운터가 2 이상이면 중복 레지스트리가 존재한다.
    //   - FindObjectsByType 호출 없이 O(1) 로 중복을 감지한다.
    //
    // Scene.handle 을 키로 사용하는 이유:
    //   - scene.name 은 중복될 수 있고(Additive 로 같은 씬 두 번 로드 등),
    //     scene 구조체 자체는 Dictionary 키로 사용하기 어렵다.
    //   - handle 은 로드된 씬마다 Unity 가 부여하는 고유 int 이다.

    private static readonly System.Collections.Generic.Dictionary<int, int> _sceneRegistryCount
        = new System.Collections.Generic.Dictionary<int, int>(4);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticData()
    {
        _sceneRegistryCount.Clear();
    }
#endif

    private void Awake()
    {
        _owningScene = gameObject.scene;

        if (bakedTrackers != null)
        {
            for (int i = 0; i < bakedTrackers.Length; i++)
            {
                var tracker = bakedTrackers[i];
                if (tracker != null)
                    tracker.Initialize(tracker.hash, tracker.registeredName);
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // 카운터 증가 후 중복 여부 검사.
        int handle = _owningScene.handle;
        _sceneRegistryCount.TryGetValue(handle, out var prev);
        int current = prev + 1;
        _sceneRegistryCount[handle] = current;

        if (current > 1)
            Debug.LogWarning(
                $"[UNFinder] 씬 '{_owningScene.name}'에 " +
                $"UNSceneRegistry가 {current}개 감지되었습니다. " +
                $"UNAutoBaker가 동일 씬을 두 번 처리했을 수 있습니다.");
#endif
    }

    private void OnDestroy()
    {
        // 씬 언로드 시 트래커 OnDestroy 순서가 레지스트리보다 늦을 경우를 대비한 보험.
        // 정상 흐름에서는 각 UNTracker.OnDestroy가 개별적으로 캐시를 제거하므로
        // 중복 호출은 UNTracker._isRegistered 가드에 의해 무시된다.
        if (bakedTrackers != null)
        {
            for (int i = 0; i < bakedTrackers.Length; i++)
            {
                var tracker = bakedTrackers[i];
                // 트래커가 아직 OnDestroy를 타지 않은 경우에만 수동 정리
                // (Unity는 같은 프레임 내 소멸 순서를 보장하지 않으므로)
                if (tracker != null)
                    tracker.ForceUnregister();
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // 씬 언로드 시 카운터 감소.
        // 씬이 완전히 언로드되면 handle 이 재사용될 수 있으므로
        // 카운터를 정확하게 유지하는 것이 중요하다.
        int handle = _owningScene.handle;
        if (_sceneRegistryCount.TryGetValue(handle, out var count))
        {
            if (count <= 1)
                _sceneRegistryCount.Remove(handle);  // 마지막 레지스트리 — 항목 제거
            else
                _sceneRegistryCount[handle] = count - 1;
        }
#endif
    }
}