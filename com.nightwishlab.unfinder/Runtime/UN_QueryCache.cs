using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 프레임 범위 쿼리 결과 캐시입니다.
///
/// 캐시된 쿼리 결과는 다음 중 하나가 발생할 때까지 유효합니다.
///   1. 프레임 경계 - Time.frameCount가 증가함.
///   2. 프레임 중간 변경 - RegisterToCache / RemoveFromCache 호출로
///      _dirtyVersion이 증가해 영향 쿼리가 재실행됨.
///
/// _dirtyVersion 생명주기:
///   MarkDirty()  - RegisterToCache / RemoveFromCache에서 호출.
///                  _dirtyVersion을 증가시킵니다.
///   Commit()     - 캐시 미스 후 재조회가 끝난 뒤 호출.
///                  엔트리에 커밋 시점 버전을 기록합니다.
///                  이후 TryGet 히트에는 정확한 버전 일치가 필요합니다.
///
/// [이전 bool 설계 대비 수정점]
///   bool _globalDirty 방식의 문제:
///     같은 프레임에 쿼리 A/B가 있고, 사이에 Instantiate가 발생하면
///       1. MarkDirty()  -> _globalDirty = true
///       2. TryGet(A)    -> MISS(더티). 재조회로 A 채움.
///       3. Commit(A)    -> _globalDirty = false         <- 전역 리셋 발생
///       4. TryGet(B)    -> HIT(_globalDirty가 false)
///                          B 리스트는 Instantiate 이전 데이터라 stale 발생.
///
///   해결 - 버전 카운터:
///     각 엔트리는 Commit 시점의 _dirtyVersion을 기록합니다.
///     Commit(A)는 _dirtyVersion을 건드리지 않고 A 엔트리만 스탬프합니다.
///     TryGet(B)는 entry.version == _dirtyVersion을 비교합니다.
///     Instantiate 이후 B는 재조회되지 않았으므로 구버전 상태이고,
///     _dirtyVersion보다 작아 MISS가 되어 올바르게 재조회됩니다.
///
/// 옵트인 방식으로, <see cref="UNQuery.Cached()"/>를 호출한 쿼리만 이 시스템을 사용합니다.
/// </summary>
public static class UNQueryCache
{
    // ── 저장소 ────────────────────────────────────────────────────────────────

    internal struct CacheEntry
    {
        public int              frame;
        public int              version;   // Commit() 시점 _dirtyVersion 스냅샷
        public List<GameObject> items;
    }

    private static readonly Dictionary<int, CacheEntry> _store
        = new Dictionary<int, CacheEntry>(32);

    private static readonly Stack<List<GameObject>> _listPool
        = new Stack<List<GameObject>>(8);

    // ── 버전 카운터(bool _globalDirty 대체) ─────────────────────────────────
    //
    // MarkDirty()에서 단조 증가합니다.
    // 세션 중간에는 리셋하지 않고 Reset()(도메인 리로드)에서만 0으로 초기화합니다.
    //
    // CacheEntry 유효 조건(둘 다 만족):
    //   entry.frame   == Time.frameCount   (같은 프레임)
    //   entry.version == _dirtyVersion     (커밋 후 변경 없음)

    private static int _dirtyVersion = 0;

    /// <summary>
    /// 씬 오브젝트 구성이 변경되었음을 알립니다.
    /// UN.RegisterToCache와 UN.RemoveFromCache에서 호출됩니다.
    /// 토글이 아닌 증가 방식을 사용하므로 각 Commit()은 자신의 스냅샷만 기록하며,
    /// 한 쿼리 커밋이 다른 쿼리의 stale 리스트를 실수로 유효화하지 않습니다.
    /// </summary>
    internal static void MarkDirty() => _dirtyVersion++;

    // ── 캐시 API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 지정된 fingerprint에 대해 유효한 캐시 결과를 조회합니다.
    ///
    /// HIT  - 같은 프레임이고 entry.version == _dirtyVersion.
    ///        true를 반환하며 <paramref name="cached"/>는 현재 리스트를 가리킵니다.
    ///
    /// MISS - 프레임 변경 또는 버전 불일치(마지막 커밋 이후 씬 변경).
    ///        false를 반환하며 <paramref name="cached"/>는 호출자가 채울 수 있도록
    ///        비워진 리스트를 가리킵니다.
    /// </summary>
    internal static bool TryGet(int fingerprint, out List<GameObject> cached)
    {
        if (_store.TryGetValue(fingerprint, out var entry))
        {
            bool sameFrame      = entry.frame   == Time.frameCount;
            bool currentVersion = entry.version == _dirtyVersion;

            if (sameFrame && currentVersion)
            {
                cached = entry.items;
                return true; // ── 캐시 HIT ──
            }

            // 오래된 엔트리: 기존 리스트 할당을 재사용하기 위해 clear 후 갱신.
            entry.items.Clear();
            entry.frame   = Time.frameCount;
            entry.version = _dirtyVersion - 1; // 현재 버전 기준 미커밋 상태 표시
            _store[fingerprint] = entry;

            cached = entry.items;
            return false; // ── 캐시 MISS, 재조회 필요 ──
        }

        // 신규 fingerprint: 새 슬롯 할당.
        var list = _listPool.Count > 0 ? _listPool.Pop() : new List<GameObject>(16);
        _store[fingerprint] = new CacheEntry
        {
            frame   = Time.frameCount,
            version = _dirtyVersion - 1, // 아직 커밋되지 않음
            items   = list,
        };

        cached = list;
        return false; // ── 캐시 MISS, 재조회 필요 ──
    }

    /// <summary>
    /// 재조회로 엔트리가 채워진 뒤 해당 fingerprint를 커밋 상태로 기록합니다.
    ///
    /// 엔트리에 현재 _dirtyVersion을 기록합니다.
    /// 이후 _dirtyVersion이 증가하지 않는 한(추가 GO 변경이 없는 한)
    /// 해당 fingerprint의 TryGet은 HIT합니다.
    ///
    /// 이전 bool 설계와 달리 _dirtyVersion 자체는 변경하지 않으므로
    /// 다른 엔트리의 유효성은 이 커밋의 영향을 받지 않습니다.
    /// </summary>
    internal static void Commit(int fingerprint)
    {
        if (_store.TryGetValue(fingerprint, out var entry))
        {
            entry.version = _dirtyVersion; // 현재 전역 버전으로 스탬프
            _store[fingerprint] = entry;
        }
        // _dirtyVersion은 의도적으로 여기서 리셋하지 않습니다.
        // 각 엔트리가 자체 버전 스냅샷을 갖습니다.
    }

    // ── 진단 ─────────────────────────────────────────────────────────────────

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // 충돌 감시용 서명 저장 — 릴리즈에서는 완전히 제거됨.
    // 같은 지문에 다른 서명이 들어오면 즉시 경고.
    private static readonly Dictionary<int, string> _fingerprintSignatures
        = new Dictionary<int, string>(32);

    internal static void ValidateFingerprint(int fingerprint, string querySignature)
    {
        if (_fingerprintSignatures.TryGetValue(fingerprint, out var stored))
        {
            if (stored != querySignature)
                Debug.LogError(
                    $"[UNFinder] 지문 충돌 감지! " +
                    $"fingerprint={fingerprint}\n" +
                    $"기존 서명: {stored}\n" +
                    $"신규 서명: {querySignature}\n" +
                    $"ComputeFingerprint() 알고리즘을 점검하십시오.");
        }
        else
        {
            _fingerprintSignatures[fingerprint] = querySignature;
        }
    }
#endif

    // ── 생명주기 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 풀링 리스트를 모두 반환하고 저장소를 비웁니다.
    /// UN.ResetStaticData(도메인 리로드/에디터 리셋)에서 호출됩니다.
    /// </summary>
    internal static void Reset()
    {
        foreach (var entry in _store.Values)
        {
            entry.items.Clear();
            _listPool.Push(entry.items);
        }
        _store.Clear();
        _dirtyVersion = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        _fingerprintSignatures.Clear();
#endif
    }
}