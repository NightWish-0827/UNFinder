using UnityEngine;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public static partial class UN
{
    // 이름 기반 고속 조회: FNV-1a 해시 → [(name, go), ...]
    private static readonly Dictionary<int, List<(string name, GameObject go)>> _fastBucket
        = new Dictionary<int, List<(string name, GameObject go)>>(5000);

    // 컴포넌트 캐시 - 키 GO가 GC되면 ConditionalWeakTable 항목도 자동 만료됩니다.
    private static class ComponentCache<T> where T : Component
    {
        public static readonly ConditionalWeakTable<GameObject, T> Map
            = new ConditionalWeakTable<GameObject, T>();
    }

    // ── 설정 ─────────────────────────────────────────────────────────────────
    //
    // [FIX-12] 버킷 경고 임계치를 하드코딩에서 공개 설정값으로 분리한다.
    //
    // 기본값(16)은 소규모 프로젝트 기준이다.
    // 동일 이름 오브젝트를 수십~수백 개 사용하는 대규모 프로젝트에서는
    // 이 값을 프로젝트 초기화 시점에 조정하면 불필요한 경고를 억제할 수 있다.
    //
    // 설정 예시 (게임 초기화 코드):
    //   UN.BucketOverflowThreshold = 64;
    //
    // 0 이하로 설정하면 경고가 완전히 비활성화된다.

    /// <summary>
    /// 동일 이름 버킷의 경고 임계치.
    /// 버킷 크기가 이 값을 초과하면 <c>UN.Find()</c> 대신
    /// <c>UN.Query().WithComponent&lt;T&gt;()</c> 사용을 권장하는 경고가 발생합니다.
    ///
    /// 기본값: 16. 0 이하로 설정하면 경고가 비활성화됩니다.
    /// <b>DEVELOPMENT_BUILD / Editor 에서만 평가됩니다.</b>
    /// </summary>
    public static int BucketOverflowThreshold = 16;

    // ── 메인 스레드 구조적 보장 ──────────────────────────────────────────────
    //
    // UNFinder 의 모든 정적 상태(버킷, 캐시, 공유 버퍼)는 동기화 프리미티브 없이
    // 설계되었으며, 메인 스레드 단독 접근을 전제로 한다.
    //
    // 이전에는 이 전제를 XML 주석과 코드 컨벤션으로만 강제했다.
    // AssertMainThread는 에디터/개발 빌드에서 위반 시 즉시 예외를 발생시켜
    // 이 전제를 구조적으로 보장한다.
    //
    // [Conditional] 에 의해 릴리즈 빌드에서는 호출 자체가 제거되므로
    // 런타임 비용은 0 이다.

    private static readonly int _mainThreadId
        = System.Threading.Thread.CurrentThread.ManagedThreadId;

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    private static void AssertMainThread()
    {
        if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            throw new System.InvalidOperationException(
                $"[UNFinder] 메인 스레드 외부에서 호출되었습니다. " +
                $"현재 스레드={System.Threading.Thread.CurrentThread.ManagedThreadId}, " +
                $"메인 스레드={_mainThreadId}. " +
                $"UNFinder 의 모든 API 는 메인 스레드에서만 호출해야 합니다.");
    }

    // ── 도메인 리로드/씬 리로드 초기화 ──────────────────────────────────────
    //
    // [FIX-A] 외부 조건을 UNITY_EDITOR → UNITY_EDITOR || DEVELOPMENT_BUILD 로 확장.
    //         이전 코드는 에디터에서만 실행되어, 실기기 Development Build에서
    //         씬 전환 후 정적 버킷이 초기화되지 않는 문제가 있었다.
    //
    // [FIX-B] 쿼리 풀 3종 초기화 추가.
    //         UNQuery / UNCachedQuery / UNQueryResult 의 정적 풀은
    //         이전 코드에서 Reset 대상에 포함되지 않았다.
    //         도메인 리로드 후 풀에 잔존하는 객체를 꺼내 쓰면
    //         이전 도메인의 참조를 가진 채로 재사용되어 무음 오염이 발생한다.
    //
    // [FIX-C] 내부 중첩 #if UNITY_EDITOR || DEVELOPMENT_BUILD 제거.
    //         외부 조건과 동일하므로 항상 참 — 중첩 자체가 불필요했다.
    //         _typeRegisterBusy 초기화는 외부 조건 하나로 보호하면 충분하다.

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticData()
    {
        // ── 이름 · 타입 · 태그 버킷 ──────────────────────────────────────────
        _fastBucket.Clear();
        _typeBucket.Clear();
        _tagBucket.Clear();
        _prefabTypeCache.Clear();

        // ── 피크 카운터 ──────────────────────────────────────────────────────
        // _typeBucket / _tagBucket 과 함께 초기화해야 한다.
        // 구 도메인의 피크 값이 남아있으면 새 도메인에서 TrimExcess 판단이 잘못된다.
        _typeBucketPeak.Clear();
        _tagBucketPeak.Clear();

        // ── 프레임 쿼리 캐시 ─────────────────────────────────────────────────
        UNQueryCache.Reset();

        // ── 쿼리 파이프라인 풀 ───────────────────────────────────────────────
        // 도메인 리로드 후 풀에 남은 객체는 이전 도메인의 내부 상태를 보유한다.
        // 풀을 통째로 비워 새 도메인에서 항상 fresh 객체를 사용하도록 보장한다.
        UNQuery.ResetPool();
        UNCachedQuery.ResetPool();
        UNQueryResult.ResetPool();

        // ── 재진입 가드 ──────────────────────────────────────────────────────
        // 전 빌드에서 항상 컴파일되는 플래그이므로 #if 없이 직접 초기화한다.
        // 도메인 리로드 시 정적 필드 초기화 순서가 보장되지 않으므로 명시적으로 리셋.
        _typeRegisterBusy = false;
        _trimBusy = false;

        // ── [FIX-15b] 펜딩 큐 ─────────────────────────────────────────────
        _pendingTypeDuringTrim.Clear();
        _pendingTagDuringTrim.Clear();
    }
#endif

    // ── 해시 ─────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHash(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        unchecked
        {
            int hash = (int)2166136261;
            for (int i = 0; i < name.Length; i++)
                hash = (hash ^ name[i]) * 16777619;
            return hash;
        }
    }

    // ── 캐시 기본 연산 ───────────────────────────────────────────────────────

    internal static void RegisterToCache(int hash, string name, GameObject obj)
    {
        if (!_fastBucket.TryGetValue(hash, out var list))
        {
            list = new List<(string, GameObject)>(4);
            _fastBucket[hash] = list;
        }
        list.Add((name, obj));

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // 버킷 크기 경계 감시.
        // FNV-1a는 충돌률이 낮지만, 동일 이름 오브젝트가 밀집되면
        // Find()의 내부 선형 탐색이 O(n)으로 퇴화한다.
        // 임계치(BucketOverflowThreshold)를 초과하면 경고.
        // 임계치가 0 이하면 경고 자체를 비활성화한다.
        if (BucketOverflowThreshold > 0 && list.Count > BucketOverflowThreshold)
            Debug.LogWarning(
                $"[UNFinder] 버킷 과밀: 이름='{name}', hash={hash}, " +
                $"엔트리 수={list.Count} (임계치={BucketOverflowThreshold}). " +
                $"동일 이름 오브젝트가 많으면 UN.Find() 대신 " +
                $"UN.Query().WithComponent<T>()를 사용하십시오.",
                obj);
#endif

        UNQueryCache.MarkDirty();
    }

    // Swap-and-pop 기반 O(1) 제거.
    internal static void RemoveFromCache(int hash, GameObject obj)
    {
        if (!_fastBucket.TryGetValue(hash, out var list)) return;

        int idx = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i].go, obj)) { idx = i; break; }
        }
        if (idx < 0) return;

        int last = list.Count - 1;
        if (idx != last) list[idx] = list[last];
        list.RemoveAt(last);

        // 이름 버킷이 비었으면 내부 배열을 즉시 반환한다.
        // _typeBucket / _tagBucket 과 동일한 자동 트림 정책.
        // 다음 동명 오브젝트 등록 시 초기 용량(4)으로 재할당되므로 비용 최소.
        if (list.Count == 0)
            list.TrimExcess();

        // 씬 오브젝트 구성이 바뀌었으므로 프레임 쿼리 캐시를 무효화합니다.
        UNQueryCache.MarkDirty();
    }
}