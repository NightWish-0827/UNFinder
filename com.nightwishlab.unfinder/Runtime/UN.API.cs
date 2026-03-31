using UnityEngine;
using UnityEngine.SceneManagement;

public static partial class UN
{
    // ── 이름 기반 조회 ───────────────────────────────────────────────────────

    /// <summary>
    /// 이름으로 오브젝트를 O(1) 비용으로 조회합니다.
    /// 
    /// <b>충돌 특성:</b>
    /// 내부 버킷은 FNV-1a 해시로 분리됩니다. 동일 해시를 공유하는 이름이
    /// 여러 개 존재하면 버킷 내부에서 선형 탐색이 발생하며, 최악의 경우 O(n)이 될 수 있습니다.
    /// 동일 이름 오브젝트가 수백 개 이상인 환경에서는
    /// <see cref="Query"/> 사용을 권장합니다.
    /// 캐시 히트 경로: 순수 C# Dictionary + 문자열 비교만 수행하며, Unity 브리지 호출과 GC 할당이 없습니다.
    /// 지연 폴백: <c>GameObject.Find</c> 는 이름별로 최대 1회만 호출되고 이후 캐시에 저장됩니다.
    /// </summary>
    public static GameObject Find(string name)
    {
        AssertMainThread();
        int hash = GetHash(name);

        if (_fastBucket.TryGetValue(hash, out var list))
        {
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                // [FIX-7] entry.go != null 으로 Unity pseudo-null 감지.
                //         ReferenceEquals(entry.go, null) 은 C# 참조 null 만 검사하므로
                //         Destroy 된 GameObject 를 살아있는 것으로 잘못 반환한다.
                //         Unity 의 == null 연산자 오버로드는 네이티브 오브젝트 소멸을 감지한다.
                if (entry.name == name && entry.go != null)
                    return entry.go;
            }
        }

        GameObject found = GameObject.Find(name);
        if (found != null)
            EnsureTracker(found, hash, name);
        return found;
    }

    /// <summary>
    /// 이름으로 오브젝트를 찾고 캐시된 컴포넌트를 반환합니다.
    ///
    /// <b>메인 스레드 전용.</b>
    /// ComponentCache 는 단일 스레드 접근을 전제로 설계되었다.
    /// Job / async 컨텍스트에서 호출하지 말 것.
    /// </summary>
    public static T FindComponent<T>(string name) where T : Component
    {
        AssertMainThread();
        GameObject go = Find(name);
        if (go == null) return null;

        // [FIX-7] cached != null 으로 Unity pseudo-null 감지.
        //         이전 코드의 !ReferenceEquals(cached, null) 은 C# 참조만 검사하므로
        //         컴포넌트가 Destroy 된 경우에도 유효한 것으로 반환했다.
        //         Unity 의 == 연산자 오버로드는 네이티브 소멸을 올바르게 감지한다.
        if (ComponentCache<T>.Map.TryGetValue(go, out var cached) && cached != null)
            return cached;

        if (go.TryGetComponent<T>(out var component))
            WriteComponentCache(ComponentCache<T>.Map, go, component);

        return component;
    }

    /// <summary>
    /// 특정 씬에 속한 오브젝트 중 이름으로 검색합니다.
    /// 어디티브 씬 환경에서 동일 이름 충돌을 해소할 때 사용하십시오.
    ///
    /// 주의: 씬 필터링은 bucket 전체를 순회한 뒤 <c>go.scene</c>을 비교하므로
    /// 동일 이름 충돌이 없다면 <see cref="Find(string)"/>을 우선 사용할 것.
    /// </summary>
    public static GameObject FindInScene(Scene scene, string name)
    {
        AssertMainThread();
        int hash = GetHash(name);

        if (_fastBucket.TryGetValue(hash, out var list))
        {
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (entry.name == name
                    && entry.go != null          // [FIX-7] Unity pseudo-null 감지
                    && entry.go.scene == scene)
                    return entry.go;
            }
        }
        return null;
    }

    /// <summary>
    /// 특정 씬에 속한 오브젝트 중 이름으로 컴포넌트를 검색합니다.
    ///
    /// <b>메인 스레드 전용.</b>
    /// </summary>
    public static T FindComponentInScene<T>(Scene scene, string name) where T : Component
    {
        AssertMainThread();
        GameObject go = FindInScene(scene, name);
        if (go == null) return null;

        if (ComponentCache<T>.Map.TryGetValue(go, out var cached) && cached != null)
            return cached;

        if (go.TryGetComponent<T>(out var component))
            WriteComponentCache(ComponentCache<T>.Map, go, component);

        return component;
    }

    /// <summary>오브젝트를 지정한 이름으로 명시적으로 등록합니다.</summary>
    public static void Bind(GameObject obj, string name)
    {
        AssertMainThread();
        if (obj == null) return;
        EnsureTracker(obj, GetHash(name), name);
    }

    /// <summary>
    /// 오브젝트 이름을 변경하면서 캐시 정합성을 유지합니다.
    /// 캐시 불일치를 막기 위해 이름 변경은 반드시 이 API를 통해 수행해야 합니다.
    /// </summary>
    public static void Rename(GameObject obj, string newName)
    {
        AssertMainThread();
        if (obj == null) return;
        obj.name = newName;
        EnsureTracker(obj, GetHash(newName), newName);
    }

    // ── 생성(Instantiate) ────────────────────────────────────────────────────

    /// <summary>
    /// 프리팹을 생성하고 즉시 O(1) 캐시에 등록합니다.
    /// Unity가 기본으로 붙이는 "(Clone)" 접미사는 제거합니다.
    ///
    /// [A] 프리팹 타입 캐시: 각 프리팹의 첫 인스턴스에서만 Type[] 스냅샷을 계산합니다.
    /// 이후 인스턴스는 캐시된 배열을 재사용하여 GetComponents()와 ToArray()를 건너뜁니다.
    /// </summary>
    public static GameObject Instantiate(
        GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
    {
        AssertMainThread();
        GameObject instance = UnityEngine.Object.Instantiate(prefab, position, rotation, parent);
        string instanceName = prefab.name;
        instance.name = instanceName;

        int hash = GetHash(instanceName);
        EnsureTrackerWithPrefabCache(instance, hash, instanceName, prefab);
        return instance;
    }

    /// <summary>
    /// 컴포넌트 프리팹을 생성하고 컴포넌트를 캐시한 뒤 반환합니다.
    ///
    /// <b>메인 스레드 전용.</b>
    /// </summary>
    public static T Instantiate<T>(
        T prefab, Vector3 position, Quaternion rotation, Transform parent = null)
        where T : Component
    {
        AssertMainThread();
        T instance = UnityEngine.Object.Instantiate(prefab, position, rotation, parent);
        string instanceName = prefab.gameObject.name;
        instance.gameObject.name = instanceName;

        int hash = GetHash(instanceName);
        EnsureTrackerWithPrefabCache(instance.gameObject, hash, instanceName, prefab.gameObject);

        // 새 인스턴스이므로 기존 항목이 없는 것이 정상입니다.
        // WriteComponentCache를 통해 Remove+Add 중복 패턴을 일관되게 처리합니다.
        WriteComponentCache(ComponentCache<T>.Map, instance.gameObject, instance);
        return instance;
    }

    // ── 파괴(Destroy) ────────────────────────────────────────────────────────

    /// <summary>
    /// GameObject 를 파괴하고 캐시에서 제거합니다.
    /// <see cref="UN.Instantiate(GameObject,Vector3,Quaternion,Transform)"/> 의 대칭 API.
    ///
    /// <b>캐시 정리 시점:</b>
    /// Unity 는 <paramref name="delay"/> == 0 일 때 프레임 말에,
    /// delay &gt; 0 일 때 지정 시간 후에 GameObject 를 파괴합니다.
    /// UNTracker.OnDestroy 가 실제 파괴 시점에 캐시를 자동으로 정리합니다.
    /// delay 기간 동안 캐시 항목이 유효하게 남는 것은 정상 동작입니다
    /// — GO 는 그 시간 동안 실제로 살아있기 때문입니다.
    ///
    /// <b>Object.Destroy(go) 를 직접 호출해도 캐시는 올바르게 정리됩니다.</b>
    /// 이 API 는 UN.Instantiate 와의 대칭성 및 코드베이스 일관성을 위해 제공됩니다.
    /// </summary>
    public static void Destroy(GameObject go, float delay = 0f)
    {
        AssertMainThread();
        if (go == null) return;
        Object.Destroy(go, delay);
        // UNTracker.OnDestroy 가 실제 파괴 시점에 모든 캐시를 정리한다.
        // 이 메서드에서 직접 캐시를 건드릴 필요 없음.
    }

    /// <summary>
    /// 컴포넌트만 파괴하고 파괴 완료 후 타입 인덱스를 자동으로 재빌드합니다.
    ///
    /// <b>Object.Destroy(component) 와의 차이:</b>
    /// <c>Object.Destroy(component)</c> 를 직접 호출하면 UNTracker 는 해당 컴포넌트가
    /// 타입 인덱스에서 제거됐음을 알 수 없어 쿼리가 잘못된 결과를 반환할 수 있습니다.
    /// 이 API 는 파괴를 예약한 뒤 파괴가 완료된 직후의 LateUpdate 에서 타입 인덱스를
    /// 자동으로 재빌드합니다. <see cref="NotifyComponentChanged"/> 를 수동으로
    /// 호출할 필요가 없습니다.
    ///
    /// <b>주의:</b> 컴포넌트가 속한 GameObject 전체를 파괴하려면
    /// <see cref="Destroy(GameObject, float)"/> 를 사용하십시오.
    /// </summary>
    public static void Destroy(Component component, float delay = 0f)
    {
        AssertMainThread();
        if (component == null) return;

        var go = component.gameObject;
        Object.Destroy(component, delay);

        // [FIX-8] Object.Destroy(component, delay) 는 delay 후 프레임 말에 실행된다.
        //         지금 즉시 RebuildTypeIndex 를 호출하면 컴포넌트가 아직 살아있어
        //         타입 목록에서 제거되지 않는다.
        //         → UNTracker 에 파괴 시간을 기록하고,
        //           해당 시간 경과 후 LateUpdate 에서 재빌드한다.
        if (go.TryGetComponent<UNTracker>(out var tracker))
            tracker.ScheduleTypeRebuild(delay);
    }

    // ── 쿼리(Query) ───────────────────────────────────────────────────────────

    /// <summary>
    /// 플루언트(연쇄 호출) 방식의 타입 기반 쿼리를 시작합니다.
    ///
    /// <code>
    /// // 기본 모드 — 매번 재조회:
    /// using var result = UN.Query()
    ///     .WithComponent&lt;IDamageable&gt;()
    ///     .WithoutComponent&lt;IFrozen&gt;()
    ///     .Execute();
    ///
    /// // 프레임 캐시 모드 — 같은 프레임에서 반복 호출 시 결과 재사용:
    /// using var result = UN.Query()
    ///     .WithComponent&lt;IDamageable&gt;()
    ///     .Cached()
    ///     .Execute();
    /// </code>
    /// </summary>
    public static UNQuery Query()
    {
        AssertMainThread();
        return UNQuery.Rent();
    }

    // ── 컴포넌트 변경 보조 API ───────────────────────────────────────────────

    /// <summary>
    /// 컴포넌트를 추가하고 UNFinder 타입 인덱스를 갱신합니다.
    /// 트래킹 중인 GameObject라면 <c>go.AddComponent&lt;T&gt;()</c> 대신 이 API 사용을 권장합니다.
    /// </summary>
    public static T AddComponent<T>(GameObject go) where T : Component
    {
        AssertMainThread();
        if (go == null) return null;
        var comp = go.AddComponent<T>();
        if (go.TryGetComponent<UNTracker>(out var tracker))
        {
            RebuildTypeIndex(tracker);

            // [FIX-14] 프리팹 캐시 무효화.
            //
            // 이 인스턴스가 UN.Instantiate 로 생성됐다면 tracker.prefabSource 가
            // 원본 프리팹을 가리킨다.
            //
            // AddComponent 로 컴포넌트 구성이 바뀐 인스턴스의 타입 배열은
            // 이제 프리팹의 구성과 다르다.
            // 캐시를 그대로 두면 이후 같은 프리팹으로 생성한 인스턴스가
            // "변형된" 타입 배열을 사용해 잘못 등록될 수 있다.
            //
            // 무효화 후 다음 Instantiate 는 슬로우 패스(GetComponents)를 거쳐
            // 프리팹 본래의 타입 배열을 새로 캐싱한다.
            if (tracker.prefabSource != null)
            {
                _prefabTypeCache.Remove(tracker.prefabSource);
                tracker.prefabSource = null; // 이 인스턴스는 더 이상 캐시와 연결되지 않음
            }
        }
        return comp;
    }

    /// <summary>
    /// <see cref="AddComponent{T}"/>를 거치지 않고 컴포넌트가 추가/제거되었음을
    /// UNFinder에 알립니다. Destroy(component) 호출 후 다음 프레임에 호출하십시오.
    /// </summary>
    public static void NotifyComponentChanged(GameObject go)
    {
        AssertMainThread();
        if (go == null) return;
        if (!go.TryGetComponent<UNTracker>(out var tracker)) return;

        RebuildTypeIndex(tracker);

        // [FIX-14] AddComponent 경로와 동일하게 프리팹 캐시를 무효화한다.
        // 컴포넌트 구성이 프리팹과 달라졌을 수 있으므로 캐시를 신뢰할 수 없다.
        if (tracker.prefabSource != null)
        {
            _prefabTypeCache.Remove(tracker.prefabSource);
            tracker.prefabSource = null;
        }
    }

    /// <summary>
    /// Unity Tag를 변경하고 UNFinder 태그 인덱스를 갱신합니다.
    /// 트래킹된 오브젝트의 태그 변경은 반드시 이 API를 통해 수행해야 합니다.
    /// 트래커가 없는 오브젝트도 실제 Unity 태그는 정상 변경됩니다.
    /// </summary>
    public static void SetTag(GameObject go, string newTag)
    {
        AssertMainThread();
        if (go == null) return;

        string oldTag = go.tag;
        if (oldTag == newTag) return;

        go.tag = newTag;

        if (!go.TryGetComponent<UNTracker>(out var tracker))
        {
#if UNITY_EDITOR
            Debug.Log(
                $"[UNFinder] SetTag: '{go.name}'은 트래킹되지 않는 오브젝트입니다. " +
                $"Unity 태그는 '{newTag}'로 변경되었지만, WithTag 쿼리에는 " +
                $"UN.Bind() 호출 후 반영됩니다.", go);
#endif
            return;
        }

        UN.ReregisterTag(tracker.registeredTag, newTag, go);
        tracker.registeredTag = newTag;
    }

    // ── 내부 보조 함수 ────────────────────────────────────────────────────────

    private static void EnsureTracker(GameObject obj, int hash, string name)
    {
        if (!obj.TryGetComponent<UNTracker>(out var tracker))
        {
            tracker = obj.AddComponent<UNTracker>();
            tracker.hideFlags = HideFlags.HideInInspector;
        }
        tracker.Initialize(hash, name);
    }

    // [A] Instantiate 경로: 프리팹 타입 캐시를 사용해 중복 작업을 줄입니다.
    private static void EnsureTrackerWithPrefabCache(
        GameObject obj, int hash, string name, GameObject prefab)
    {
        if (!obj.TryGetComponent<UNTracker>(out var tracker))
        {
            tracker = obj.AddComponent<UNTracker>();
            tracker.hideFlags = HideFlags.HideInInspector;
        }

        // 트래커의 Initialize를 호출하되, 타입 등록 경로는
        // 프리팹 캐시 대응 버전으로 우회합니다.
        tracker.InitializeWithPrefabCache(hash, name, prefab);
    }

    // ── ComponentCache 쓰기 보조 함수 ────────────────────────────────────────
    //
    // [FIX-7] Remove + Add 패턴을 한 곳으로 통합합니다.
    //
    // 이 패턴의 전제:
    //   ConditionalWeakTable의 개별 연산(Add, Remove, TryGetValue)은 각각 스레드 안전합니다.
    //   하지만 Remove → Add 두 연산 시퀀스 자체는 원자적이지 않습니다.
    //   UNFinder의 모든 경로는 Unity 메인 스레드에서만 호출되므로
    //   시퀀스 중간에 다른 스레드가 개입하지 않습니다.
    //   → 메인 스레드 전용 전제가 깨지면 이 헬퍼를 lock으로 보호해야 합니다.
    //
    // Remove를 먼저 호출하는 이유:
    //   ConditionalWeakTable.Add()는 키가 이미 존재하면 ArgumentException을 던집니다.
    //   캐시 갱신(예: 컴포넌트 교체 후 재등록) 시 기존 항목을 먼저 제거해야 합니다.

    private static void WriteComponentCache<T>(
        System.Runtime.CompilerServices.ConditionalWeakTable<GameObject, T> map,
        GameObject go,
        T component)
        where T : class
    {
        map.Remove(go);
        map.Add(go, component);
    }

    /// <summary>
    /// 모든 등록된 오브젝트의 캐시 정합성을 검사합니다.
    /// DEVELOPMENT_BUILD 에서만 활성화됩니다.
    ///
    /// <b>주의:</b> 전체 버킷을 순회하므로 매 프레임 호출은 금지.
    /// 디버그 패널, 테스트 픽스처, 씬 전환 직후에만 사용할 것.
    /// </summary>
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void ValidateCacheIntegrity()
    {
        int driftCount = 0;
        foreach (var bucket in _fastBucket.Values)
        {
            // 버킷 순회 중 수정을 피하기 위해 읽기 전용으로 접근
            for (int i = 0; i < bucket.Count; i++)
            {
                var (registeredName, go) = bucket[i];
                if (go == null) continue;     // [FIX-7] Unity pseudo-null 감지

                if (go.name != registeredName)
                {
                    Debug.LogWarning(
                        $"[UNFinder] 이름 드리프트: " +
                        $"등록='{registeredName}' / 현재='{go.name}'. " +
                        $"UN.Rename()을 사용하십시오.", go);
                    driftCount++;
                }
            }
        }

        if (driftCount == 0)
            Debug.Log("[UNFinder] ValidateCacheIntegrity: 드리프트 없음. 캐시 정상.");
        else
            Debug.LogWarning($"[UNFinder] ValidateCacheIntegrity: {driftCount}개 드리프트 발견.");
    }
}