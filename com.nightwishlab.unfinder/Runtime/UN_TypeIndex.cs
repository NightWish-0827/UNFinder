using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public static partial class UN
{
    // ── 버킷 ─────────────────────────────────────────────────────────────────

    internal static readonly Dictionary<Type, HashSet<GameObject>> _typeBucket
        = new Dictionary<Type, HashSet<GameObject>>(64);

    internal static readonly Dictionary<string, HashSet<GameObject>> _tagBucket
        = new Dictionary<string, HashSet<GameObject>>(16);

    // ── [FIX-5] TrimTypeBuckets 휴리스틱용 피크 카운터 ──────────────────────
    //
    // HashSet<T> 은 내부 버킷 배열의 Capacity를 공개 프로퍼티로 노출하지 않는다.
    // TrimExcess() 호출 가치를 판단하려면 피크 크기를 별도로 추적해야 한다.
    //
    // 규칙:
    //   등록 시 — Count가 피크를 넘으면 피크를 갱신한다.
    //   해제 시 — Count * 4 < peak 이면 (75% 이상 감소) TrimExcess() 후 피크를 리셋한다.
    //   자동   — Count == 0 이면 항상 TrimExcess() (공간 전량 반환, 재할당 비용 최소).
    //
    // 피크 카운터 자체의 오버헤드:
    //   Dictionary<Type, int> 조회는 TryGetValue 한 번 — 등록/해제 경로에서
    //   이미 _typeBucket 을 한 번 조회하므로 추가 비용은 사실상 intdict 조회 1회.

    private static readonly Dictionary<Type, int>   _typeBucketPeak
        = new Dictionary<Type, int>(64);

    private static readonly Dictionary<string, int> _tagBucketPeak
        = new Dictionary<string, int>(16);

    // ── 인터페이스 캐시 ──────────────────────────────────────────────────────

    private static readonly Dictionary<Type, Type[]> _ifaceCache
        = new Dictionary<Type, Type[]>(64);

    // ── [A] 프리팹 타입 캐시 ─────────────────────────────────────────────────
    //
    // 동일 프리팹 인스턴스는 동일한 컴포넌트 구성을 공유합니다.
    // 프리팹 소스 기준으로 Type[] 스냅샷을 캐싱하면 다음 비용을 줄일 수 있습니다.
    //   1. GetComponents() C++ 브리지 호출(첫 인스턴스 이후)
    //   2. _tempTypeList.ToArray() 힙 할당(첫 인스턴스 이후)
    //
    // 키:   원본 프리팹 GameObject(인스턴스 아님)
    // 값:   첫 인스턴스에서 RegisterTypeIndex가 만든 Type[] 스냅샷
    //
    // ResetStaticData에서 비웁니다 - 프리팹 참조는 에디터 세션 종속입니다.

    // internal - UNTracker.InitializeWithPrefabCache에서 접근합니다.
    // private이면 동일 어셈블리 내 클래스 간 접근이 불가능합니다.
    internal static readonly Dictionary<GameObject, Type[]> _prefabTypeCache
        = new Dictionary<GameObject, Type[]>(64);

    // ── 공유 단일-프레임 임시 버퍼 ───────────────────────────────────────────
    //
    // RegisterTypeIndex 가 실행되는 동안만 점유되는 단일 프레임 임시 버퍼.
    // 메인 스레드 전용이며, 재진입 금지(_typeRegisterBusy 가드로 보장).

    private static readonly List<Component>  _tempComponents = new List<Component>(16);
    private static readonly List<Type>       _tempTypeList   = new List<Type>(16);
    private static readonly HashSet<Type>    _tempTypeSet    = new HashSet<Type>();

    // ── 재진입 가드 ───────────────────────────────────────────────────────────
    //
    // [FIX-6] 이전 코드: #if UNITY_EDITOR || DEVELOPMENT_BUILD 로만 컴파일됨.
    //         릴리즈 빌드에서 재진입이 발생하면 _tempComponents 등 공유 버퍼가
    //         중간 상태에서 덮어씌워져 tracker.registeredTypes 에 쓰레기값이 들어간다.
    //         이후 쿼리는 오류 없이 잘못된 결과를 반환하는 무음 오염이 된다.
    //
    // 수정 전략 — 역할을 두 층으로 분리한다:
    //
    //   (a) 버퍼 오염 방지   — _typeRegisterBusy + try/finally 를 전 빌드에서 컴파일.
    //                          재진입 시 즉시 반환으로 실행 자체를 차단한다.
    //
    //   (b) 버그 탐지·보고   — throw 메시지는 UNITY_EDITOR || DEVELOPMENT_BUILD 한정.
    //                          개발 중 재진입 원인을 즉시 식별할 수 있도록 유지한다.

    private static volatile bool _typeRegisterBusy; // 전 빌드 - volatile로 메모리 가시성 보장

    // ── [FIX-15] Trim 가드 ──────────────────────────────────────────────────
    //
    // TrimTypeBuckets() 가 _typeBucket / _tagBucket 을 foreach 순회하는 동안
    // AddToTypeBucket / RegisterTagIndex 가 새 키를 삽입하면
    // InvalidOperationException("Collection was modified") 이 발생한다.
    //
    // _trimBusy 는 순회 시작 전에 true, 종료 후 false 로 설정된다.
    // 등록 경로는 _trimBusy == true 이면 조기 리턴하여 Dictionary 변경을 방지한다.

    private static volatile bool _trimBusy;

    // ── [FIX-15b] Trim 중 펜딩 큐 ────────────────────────────────────────
    //
    // _trimBusy == true 인 동안 도착한 등록 요청을 버리지 않고 큐에 보관한다.
    // trim 완료 직후 일괄 등록하여 반쪽 등록 상태(이름 버킷에만 존재)를 방지한다.
    //
    // Unity 의 동기 실행 모델에서 TrimTypeBuckets 순회 중 등록이 끼어들 가능성은
    // 극히 낮지만, 실패 모드가 무감지/무복구이므로 구조적 방어를 제공한다.

    private static readonly List<(Type type, GameObject go)> _pendingTypeDuringTrim
        = new List<(Type, GameObject)>(4);

    private static readonly List<(string tag, GameObject go)> _pendingTagDuringTrim
        = new List<(string, GameObject)>(4);

    // ── 인터페이스 필터 ──────────────────────────────────────────────────────

    private static bool ShouldIndexInterface(Type iface)
    {
        var ns = iface.Namespace;
        if (ns == null)                   return true;
        if (ns.StartsWith("UnityEngine")) return false;
        if (ns.StartsWith("UnityEditor")) return false;
        if (ns.StartsWith("System"))      return false;
        if (ns.StartsWith("Microsoft"))   return false;
        return true;
    }

    private static Type[] GetGameInterfaces(Type concreteType)
    {
        if (_ifaceCache.TryGetValue(concreteType, out var cached))
            return cached;

        var raw   = concreteType.GetInterfaces();
        int count = 0;
        foreach (var iface in raw)
            if (ShouldIndexInterface(iface)) count++;

        var result = new Type[count];
        int idx = 0;
        foreach (var iface in raw)
            if (ShouldIndexInterface(iface)) result[idx++] = iface;

        return _ifaceCache[concreteType] = result;
    }

    // ── 타입 인덱스 등록 ─────────────────────────────────────────────────────

    internal static void RegisterTypeIndex(UNTracker tracker)
    {
        AssertMainThread();

        // (a) 버퍼 오염 방지 - 전 빌드에서 동작.
        if (_typeRegisterBusy)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // (b) 버그 탐지 - 개발 빌드에서만 throw. 원인 파악을 즉시 가능하게 한다.
            throw new InvalidOperationException(
                "[UNFinder] RegisterTypeIndex에 재진입이 감지되었습니다. " +
                "UNSceneRegistry 초기화 중(Awake 범위 안)에서 " +
                "UN.AddComponent / UN.NotifyComponentChanged를 호출하지 마십시오.");
#else
            // 릴리즈에서는 조용히 건너뜁니다 - 버퍼 오염보다 건너뛰기가 항상 안전합니다.
            return;
#endif
        }

        _typeRegisterBusy = true;
        try
        {
            var go = tracker.gameObject;

            _tempComponents.Clear();
            _tempTypeList.Clear();
            _tempTypeSet.Clear();

            go.GetComponents(_tempComponents);

            foreach (var comp in _tempComponents)
            {
                if (comp == null) continue;
                var type = comp.GetType();
                if (_tempTypeSet.Add(type))
                {
                    _tempTypeList.Add(type);
                    AddToTypeBucket(type, go);
                }
                foreach (var iface in GetGameInterfaces(type))
                {
                    if (_tempTypeSet.Add(iface))
                    {
                        _tempTypeList.Add(iface);
                        AddToTypeBucket(iface, go);
                    }
                }
            }
            tracker.registeredTypes = _tempTypeList.ToArray();
        }
        finally
        {
            // 예외가 발생해도 플래그를 반드시 해제해야 다음 호출이 진행됩니다.
            _typeRegisterBusy = false;
        }
    }

    // [A] 프리팹 캐시 변형 - 반복 호출에서 GetComponents + ToArray를 생략합니다.
    // 프리팹 소스를 아는 UN_API.Instantiate 경로에서 호출됩니다.
    internal static void RegisterTypeIndexFromCache(UNTracker tracker, Type[] cachedTypes)
    {
        var go = tracker.gameObject;

        foreach (var type in cachedTypes)
            AddToTypeBucket(type, go);

        // 캐시 배열을 직접 재사용합니다 - 추가 할당이 없습니다.
        tracker.registeredTypes = cachedTypes;
    }

    internal static void UnregisterTypeIndex(UNTracker tracker)
    {
        if (tracker.registeredTypes == null) return;

        var go = tracker.gameObject;
        foreach (var type in tracker.registeredTypes)
        {
            if (!_typeBucket.TryGetValue(type, out var set)) continue;

            set.Remove(go);
            TrimTypeBucketIfNeeded(type, set);
        }
        tracker.registeredTypes = null;
    }

    internal static void RebuildTypeIndex(UNTracker tracker)
    {
        UnregisterTypeIndex(tracker);
        RegisterTypeIndex(tracker);
    }

    // ── 태그 인덱스 ───────────────────────────────────────────────────────────

    internal static void RegisterTagIndex(string tag, GameObject go)
    {
        if (string.IsNullOrEmpty(tag) || tag == "Untagged") return;

        // [FIX-15b] trim 순회 중이면 펜딩 큐에 보관 → trim 완료 후 일괄 등록.
        if (_trimBusy)
        {
            _pendingTagDuringTrim.Add((tag, go));
            return;
        }

        if (!_tagBucket.TryGetValue(tag, out var set))
            _tagBucket[tag] = set = new HashSet<GameObject>();

        set.Add(go);

        // 피크를 갱신합니다.
        if (!_tagBucketPeak.TryGetValue(tag, out var peak) || set.Count > peak)
            _tagBucketPeak[tag] = set.Count;
    }

    internal static void UnregisterTagIndex(string tag, GameObject go)
    {
        if (!_tagBucket.TryGetValue(tag, out var set)) return;

        set.Remove(go);
        TrimTagBucketIfNeeded(tag, set);
    }

    internal static void ReregisterTag(string oldTag, string newTag, GameObject go)
    {
        UnregisterTagIndex(oldTag, go);
        RegisterTagIndex(newTag, go);
    }

    // ── [FIX-5] 공개 Trim API ────────────────────────────────────────────────
    //
    // 씬 전환, 웨이브 종료, 대량 Destroy 이후에 호출한다.
    // 매 프레임 호출 금지 - 전체 버킷 순회로 O(buckets) 비용이 발생합니다.
    //
    // 트림 기준:
    //   Count == 0          → TrimExcess() (내부 배열 전량 반환)
    //   Count * 4 < peak    → TrimExcess() (피크 대비 75% 이상 감소)
    //
    // 반환값: 트림된 버킷 수(디버그/로그 용도).

    /// <summary>
    /// 비어있거나 대규모 소멸 이후 과도하게 할당된 타입·태그 버킷의
    /// 내부 배열 메모리를 회수합니다.
    ///
    /// <b>호출 시점:</b> 씬 전환, 웨이브 종료 등 대량 Destroy 직후.
    /// 매 프레임 호출은 금지합니다.
    /// </summary>
    /// <returns>트림된 버킷 수.</returns>
    public static int TrimTypeBuckets()
    {
        AssertMainThread();

        // [FIX-15] 구조적 방어 - 등록 진행 중이면 Dictionary 순회가 불가능합니다.
        if (_typeRegisterBusy)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogWarning(
                "[UNFinder] TrimTypeBuckets: 타입 등록이 진행 중이므로 트림을 건너뜁니다.");
#endif
            return 0;
        }

        _trimBusy = true;
        try
        {
            int trimmed = 0;

            foreach (var kv in _typeBucket)
            {
                var set  = kv.Value;
                var type = kv.Key;
                int peak = _typeBucketPeak.TryGetValue(type, out var p) ? p : 0;

                if (set.Count == 0 || (peak > 0 && set.Count * 4 < peak))
                {
                    set.TrimExcess();
                    _typeBucketPeak[type] = set.Count;
                    trimmed++;
                }
            }

            foreach (var kv in _tagBucket)
            {
                var set = kv.Value;
                var tag = kv.Key;
                int peak = _tagBucketPeak.TryGetValue(tag, out var p) ? p : 0;

                if (set.Count == 0 || (peak > 0 && set.Count * 4 < peak))
                {
                    set.TrimExcess();
                    _tagBucketPeak[tag] = set.Count;
                    trimmed++;
                }
            }

            return trimmed;
        }
        finally
        {
            _trimBusy = false;
            FlushPendingTrimRegistrations();
        }
    }

    // ── [FIX-15b] 펜딩 큐 플러시 ─────────────────────────────────────────────
    //
    // _trimBusy 가 false 로 해제된 직후 호출된다.
    // 큐에 보관된 등록 요청을 순서대로 실행하여 인덱스 일관성을 복원한다.
    // _trimBusy == false 이므로 AddToTypeBucket / RegisterTagIndex 는 정상 경로를 탄다.

    private static void FlushPendingTrimRegistrations()
    {
        for (int i = 0; i < _pendingTypeDuringTrim.Count; i++)
        {
            var (type, go) = _pendingTypeDuringTrim[i];
            if (go != null) AddToTypeBucket(type, go);
        }
        _pendingTypeDuringTrim.Clear();

        for (int i = 0; i < _pendingTagDuringTrim.Count; i++)
        {
            var (tag, go) = _pendingTagDuringTrim[i];
            if (go != null) RegisterTagIndex(tag, go);
        }
        _pendingTagDuringTrim.Clear();
    }

    // ── 쿼리 실행 ─────────────────────────────────────────────────────────────
    //
    // [FIX-9] 세 Execute 메서드와 PassesFilters 에 Scene scene 파라미터 추가.
    //
    // 씬 필터 동작:
    //   scene.IsValid() == false (default) → 씬 제한 없음, 기존 동작과 동일.
    //   scene.IsValid() == true            → go.scene == scene 인 오브젝트만 통과.
    //
    // 설계 근거:
    //   씬별 별도 버킷을 관리하면 씬 로드·언로드 시 인덱스 일관성 유지 비용이 크다.
    //   FindSmallestBucket 이 타입·태그로 후보를 이미 좁힌 뒤 씬을 비교하므로
    //   추가 비용은 후보 오브젝트당 Scene 구조체 비교 1회에 불과하다.

    internal static void ExecuteQuery(
        List<Type>   with,
        List<Type>   without,
        List<string> tags,
        Scene        scene,
        UNQueryResult result)
    {
        if (with.Count == 0 && tags.Count == 0) return;

        var smallest = FindSmallestBucket(with, tags);
        if (smallest == null) return;

        foreach (var go in smallest)
        {
            if (go == null) continue;
            if (PassesFilters(go, with, without, tags, scene, smallest))
                result.Add(go);
        }
    }

    // [B] 캐시 변형 - 버킷 순회 전에 UNQueryCache를 먼저 확인합니다.
    internal static void ExecuteQueryCached(
        List<Type>   with,
        List<Type>   without,
        List<string> tags,
        Scene        scene,
        int          fingerprint,
        UNQueryResult result)
    {
        if (with.Count == 0 && tags.Count == 0) return;

        // 캐시 히트: 프레임 캐시 리스트를 result로 복사합니다.
        if (UNQueryCache.TryGet(fingerprint, out var cachedList))
        {
            foreach (var go in cachedList)
                result.Add(go);
            return;
        }

        // 캐시 미스: 쿼리를 실행해 캐시 리스트와 result를 함께 채웁니다.
        var smallest = FindSmallestBucket(with, tags);
        if (smallest == null)
        {
            UNQueryCache.Commit(fingerprint);
            return;
        }

        foreach (var go in smallest)
        {
            if (go == null) continue;
            if (PassesFilters(go, with, without, tags, scene, smallest))
            {
                cachedList.Add(go);   // 캐시에 기록
                result.Add(go);       // 결과에 기록
            }
        }

        UNQueryCache.Commit(fingerprint);
    }

    internal static void ExecuteQueryForEach(
        List<Type>         with,
        List<Type>         without,
        List<string>       tags,
        Scene              scene,
        Action<GameObject> action)
    {
        if (with.Count == 0 && tags.Count == 0) return;

        var smallest = FindSmallestBucket(with, tags);
        if (smallest == null) return;

        foreach (var go in smallest)
        {
            if (go == null) continue;
            if (PassesFilters(go, with, without, tags, scene, smallest))
                action(go);
        }
    }

    // [FIX-10] 조기 종료 지원 변형.
    //
    // ForEach는 Action<GO>를 사용하므로 중단 신호를 반환할 수 없습니다.
    // TryForEach는 Func<GO, bool>을 사용합니다.
    //   action(go) == true  → continue (다음 오브젝트로 진행)
    //   action(go) == false → break    (순회 즉시 중단)
    //
    // [FIX-16] First()는 클로저 할당 제거를 위해 전용 ExecuteQueryFirst 경로로 분리되었습니다.
    //
    // 반환값:
    //   true  - 모든 후보 순회 완료(중단 없음)
    //   false - action이 false를 반환해 조기 종료

    internal static bool ExecuteQueryTryForEach(
        List<Type>          with,
        List<Type>          without,
        List<string>        tags,
        Scene               scene,
        Func<GameObject, bool> action)
    {
        if (with.Count == 0 && tags.Count == 0) return true;

        var smallest = FindSmallestBucket(with, tags);
        if (smallest == null) return true;

        foreach (var go in smallest)
        {
            if (go == null) continue;
            if (!PassesFilters(go, with, without, tags, scene, smallest)) continue;

            if (!action(go))
                return false; // 조기 종료 - action이 중단 요청
        }
        return true; // 전체 순회 완료
    }

    // ── [FIX-16] GC-free First() 실행 경로 ──────────────────────────────────
    //
    // UNQuery.First()가 이전에 사용하던 ExecuteQueryTryForEach + 람다 경로는
    // 지역 변수 캡처로 호출마다 클로저(display class) 할당이 발생했습니다.
    //
    // 전용 실행 경로를 제공해 delegate 할당 없이 첫 번째 결과를 반환합니다.

    internal static GameObject ExecuteQueryFirst(
        List<Type>   with,
        List<Type>   without,
        List<string> tags,
        Scene        scene)
    {
        if (with.Count == 0 && tags.Count == 0) return null;

        var smallest = FindSmallestBucket(with, tags);
        if (smallest == null) return null;

        foreach (var go in smallest)
        {
            if (go == null) continue;
            if (PassesFilters(go, with, without, tags, scene, smallest))
                return go;
        }
        return null;
    }

    // ── 내부 보조 함수 ───────────────────────────────────────────────────────

    private static HashSet<GameObject> FindSmallestBucket(List<Type> with, List<string> tags)
    {
        HashSet<GameObject> smallest = null;

        foreach (var type in with)
        {
            if (!_typeBucket.TryGetValue(type, out var set)) return null;
            if (smallest == null || set.Count < smallest.Count) smallest = set;
        }
        foreach (var tag in tags)
        {
            if (!_tagBucket.TryGetValue(tag, out var set)) return null;
            if (smallest == null || set.Count < smallest.Count) smallest = set;
        }
        return smallest;
    }

    private static bool PassesFilters(
        GameObject          go,
        List<Type>          with,
        List<Type>          without,
        List<string>        tags,
        Scene               scene,           // [FIX-9] 씬 필터 추가
        HashSet<GameObject> smallest)
    {
        // ── 타입 필터 ─────────────────────────────────────────────────────────
        foreach (var type in with)
        {
            if (!_typeBucket.TryGetValue(type, out var set)) return false;
            if (!ReferenceEquals(set, smallest) && !set.Contains(go)) return false;
        }
        foreach (var type in without)
        {
            if (_typeBucket.TryGetValue(type, out var set) && set.Contains(go))
                return false;
        }

        // ── 태그 필터 ─────────────────────────────────────────────────────────
        foreach (var tag in tags)
        {
            if (!_tagBucket.TryGetValue(tag, out var set)) return false;
            if (!ReferenceEquals(set, smallest) && !set.Contains(go)) return false;
        }

        // ── 씬 필터 ───────────────────────────────────────────────────────────
        // scene.IsValid() == false(default)면 조건 자체를 평가하지 않습니다.
        // 유효한 씬이 지정된 경우에만 go.scene을 비교합니다.
        if (scene.IsValid() && go.scene != scene) return false;

        return true;
    }

    private static void AddToTypeBucket(Type type, GameObject go)
    {
        // [FIX-15b] trim 순회 중이면 펜딩 큐에 보관 → trim 완료 후 일괄 등록.
        if (_trimBusy)
        {
            _pendingTypeDuringTrim.Add((type, go));
            return;
        }

        if (!_typeBucket.TryGetValue(type, out var set))
            _typeBucket[type] = set = new HashSet<GameObject>();

        set.Add(go);

        // 피크 갱신 - TrimTypeBuckets의 트림 판단 기준으로 사용됩니다.
        if (!_typeBucketPeak.TryGetValue(type, out var peak) || set.Count > peak)
            _typeBucketPeak[type] = set.Count;
    }

    // ── [FIX-5] Remove 단위 Trim 보조 함수 ──────────────────────────────────
    //
    // UnregisterTypeIndex / UnregisterTagIndex에서 Remove 직후 호출됩니다.
    // 매 Remove마다 실행되지만 실제 TrimExcess()는 조건 충족 시에만 호출됩니다.
    //
    // 자동 트림 조건 (TrimTypeBuckets의 명시적 호출 없이 즉시 회수):
    //   Count == 0       → 내부 배열 전량 반환. 다음 Add 시 소용량으로 재할당.
    //   Count * 4 < peak → 피크 대비 75% 이상 감소 — 유의미한 낭비로 판단.

    private static void TrimTypeBucketIfNeeded(Type type, HashSet<GameObject> set)
    {
        if (!_typeBucketPeak.TryGetValue(type, out var peak)) return;

        if (set.Count == 0 || set.Count * 4 < peak)
        {
            set.TrimExcess();
            _typeBucketPeak[type] = set.Count;
        }
    }

    private static void TrimTagBucketIfNeeded(string tag, HashSet<GameObject> set)
    {
        if (!_tagBucketPeak.TryGetValue(tag, out var peak)) return;

        if (set.Count == 0 || set.Count * 4 < peak)
        {
            set.TrimExcess();
            _tagBucketPeak[tag] = set.Count;
        }
    }
}