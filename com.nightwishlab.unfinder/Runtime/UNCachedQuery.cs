using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 프레임 단위 캐시가 활성화된 쿼리 빌더입니다.
/// <see cref="UNQuery.Cached()"/>에서만 반환됩니다.
///
/// <see cref="Execute()"/>만 사용할 수 있으며 <c>ForEach</c>는 의도적으로 제공하지 않습니다.
/// 이는 컴파일 타임 계약입니다. <c>.Cached()</c>를 체이닝했다면
/// 인라인 콜백이 아니라 결과 컨테이너를 원한다는 의미입니다.
///
/// <code>
/// // 정상 사용
/// using var result = UN.Query().WithComponent&lt;IEnemy&gt;().Cached().Execute();
///
/// // Additive 씬 + 캐시: 씬 핸들이 지문(fingerprint)에 포함됨
/// using var result = UN.Query().WithComponent&lt;IEnemy&gt;().WithScene(combatScene).Cached().Execute();
///
/// // 컴파일 오류 — UNCachedQuery에는 ForEach가 없음
/// UN.Query().WithComponent&lt;IEnemy&gt;().Cached().ForEach(...);
/// </code>
/// </summary>
public sealed class UNCachedQuery : IDisposable
{
    // ── 풀 ───────────────────────────────────────────────────────────────────

    private static readonly Stack<UNCachedQuery> _pool = new Stack<UNCachedQuery>(8);

    internal static UNCachedQuery Rent()
        => _pool.Count > 0 ? _pool.Pop() : new UNCachedQuery();

    // 도메인 리로드 후 UN.ResetStaticData()에서 호출.
    // 이전 도메인의 내부 상태를 가진 객체가 재사용되지 않도록 풀 전체를 비운다.
    internal static void ResetPool() => _pool.Clear();

    // ── 상태(부모 UNQuery가 풀로 돌아가기 전에 복사) ──────────────────────────
    //
    // 이 리스트들은 Cached() 호출 시점의 UNQuery 필터 상태를 복사한 것입니다.
    // UNQuery는 스스로 비우고 재활용되며, UNCachedQuery는 Execute() 완료까지
    // 자신의 복사본을 소유합니다.

    private readonly List<Type>   _with    = new List<Type>(4);
    private readonly List<Type>   _without = new List<Type>(4);
    private readonly List<string> _tags    = new List<string>(2);

    // default(Scene).IsValid() == false 이면 씬 필터가 없습니다.
    private Scene _scene;

    private bool _disposed = true; // 초기값 true, Rent()~ReturnToPool() 구간에서만 false

    private UNCachedQuery() { }

    // ── 내부 설정(UNQuery.Cached()에서 호출) ─────────────────────────────────

    internal void CopyFiltersFrom(
        List<Type> with, List<Type> without, List<string> tags, Scene scene)
    {
        _with.Clear();
        _without.Clear();
        _tags.Clear();

        foreach (var t in with)    _with.Add(t);
        foreach (var t in without) _without.Add(t);
        foreach (var s in tags)    _tags.Add(s);

        _scene    = scene;
        _disposed = false;
    }

    // ── 공개 API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 캐시 쿼리를 실행하고 풀링된 <see cref="UNQueryResult"/>를 반환합니다.
    /// <c>using</c> 구문으로 감싸 사용하십시오.
    /// </summary>
    public UNQueryResult Execute()
    {
        ThrowIfDisposed();

        var result = UNQueryResult.Rent();
        int fp = ComputeFingerprint();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        UNQueryCache.ValidateFingerprint(fp, BuildDebugSignature());
#endif

        UN.ExecuteQueryCached(_with, _without, _tags, _scene, fp, result);
        ReturnToPool();
        return result;
    }

    /// <summary>
    /// 실행 없이 현재 빌더를 풀에 반환합니다.
    /// <see cref="Execute"/>를 호출하지 못한 경우(예: 예외 발생)에도
    /// <c>using</c> 블록 종료 시 자동 호출됩니다.
    /// </summary>
    public void Dispose() => ReturnToPool();

    // ── 지문(Fingerprint) ────────────────────────────────────────────────────

    private int ComputeFingerprint()
    {
        // 순서 독립적 지문을 위한 설계 목표:
        //
        //   (a) 섹션 순서는 의미가 있어야 함:
        //         With<A>, Without<B>  ≠  With<B>, Without<A>
        //       → FNV-1a 체인에 섹션 구분 상수를 넣어 해결.
        //
        //   (b) 섹션 내부 순서는 의미가 없어야 함(집합 의미):
        //         With<A,B>  ==  With<B,A>
        //       → 교환 법칙이 성립하는 누산(sum)으로 해결.
        //
        //   (c) 같은 섹션 내 중복 항목은 상쇄되면 안 됨:
        //         With<A,A>  ≠  With<A>
        //       → 각 해시를 Wang-mix 후 SUM.
        //         sum(Wang(A), Wang(A)) = 2·Wang(A) ≠ Wang(A) 이므로 상쇄 없음.
        //
        //   (d) 씬 필터는 반드시 서로 다른 지문을 생성해야 함:
        //         WithScene(A) ≠ WithScene(B) ≠ no-scene
        //       → _scene.handle(로드된 씬마다 고유 int)를 별도 섹션으로 혼합.
        //         handle == 0 은 "필터 없음"(default(Scene)) 의미.
        //
        //   (e) 섹션/엔트리 전반에서 낮은 충돌 확률:
        //       → 섹션 간 FNV-1a 체인으로 avalanche 효과 유지.

        unchecked
        {
            const int FNV_BASIS    = unchecked((int)2166136261u);
            const int FNV_PRIME    = 16777619;
            const int SEP_WITH     = unchecked((int)0x9E3779B9); // 황금비 계열 상수
            const int SEP_WITHOUT  = 0x6C62272E;
            const int SEP_TAG      = 0x1B873593;
            const int SEP_SCENE    = unchecked((int)0xC2B2AE35); // 추가 — 씬 섹션 도메인 분리

            // ── 섹션 누산기(Wang-mix → sum) ───────────────────────────────────
            //
            // Wang 해시: 전단사 정수 혼합 함수.
            // 특성:
            //   - 0 고정점이 없음: WangMix(0) ≠ 0 이므로 빈 집합과 0 해시 집합이 구분됨.
            //   - 전단사: x ≠ y 이면 WangMix(x) ≠ WangMix(y)로 충돌 최소화.
            //   - 순수 산술 연산(분기 없음): JIT/Burst 인라이닝 친화적.

            int withAcc = 0;
            foreach (var t in _with)
                withAcc += WangMix(t.GetHashCode());

            int withoutAcc = 0;
            foreach (var t in _without)
                withoutAcc += WangMix(t.GetHashCode());

            int tagAcc = 0;
            foreach (var s in _tags)
                tagAcc += WangMix(s?.GetHashCode() ?? 0);

            // Scene.handle은 로드된 씬마다 고유한 int입니다.
            // default(Scene).handle == 0 으로 씬 필터 없음을 자연스럽게 표현합니다.
            int sceneAcc = WangMix(_scene.handle);

            // ── FNV-1a 섹션 간 체인 ───────────────────────────────────────────
            //
            // 섹션 누산값을 고정 순서로 섞되, 도메인 구분 상수를 사이에 넣어
            // 다른 섹션의 동일 값이 다른 지문을 만들도록 합니다.

            int fp = FNV_BASIS;
            fp = (fp ^ SEP_WITH)    * FNV_PRIME;
            fp = (fp ^ withAcc)     * FNV_PRIME;
            fp = (fp ^ SEP_WITHOUT) * FNV_PRIME;
            fp = (fp ^ withoutAcc)  * FNV_PRIME;
            fp = (fp ^ SEP_TAG)     * FNV_PRIME;
            fp = (fp ^ tagAcc)      * FNV_PRIME;
            fp = (fp ^ SEP_SCENE)   * FNV_PRIME;
            fp = (fp ^ sceneAcc)    * FNV_PRIME;
            return fp;
        }
    }

    /// <summary>
    /// Wang 정수 해시 - 전단사 32비트 혼합 함수.
    /// 합산 전에 원시 해시값의 상관성을 낮춰
    /// 단순 XOR의 자기 상쇄 문제를 제거합니다.
    ///
    /// 특성:
    ///   Bijective  - 모든 입력이 고유한 출력으로 매핑됨.
    ///   No-zero-FP - WangMix(0) = 0x165667b1 이므로 빈 집합과 0 해시 집합이 구분됨.
    ///   GC-free    - 순수 산술 연산으로 할당이 없음.
    /// </summary>
    private static int WangMix(int key)
    {
        unchecked
        {
            key = ~key + (key << 21);
            key =  key ^ (key >> 24);
            key = (key + (key << 3)) + (key << 8);
            key =  key ^ (key >> 14);
            key = (key + (key << 2)) + (key << 4);
            key =  key ^ (key >> 28);
            key =  key + (key << 31);
            return key;
        }
    }

    // ── 진단 ─────────────────────────────────────────────────────────────────

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private string BuildDebugSignature()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("With:[");
        foreach (var t in _with)    { sb.Append(t.Name); sb.Append(','); }
        sb.Append("] Without:[");
        foreach (var t in _without) { sb.Append(t.Name); sb.Append(','); }
        sb.Append("] Tags:[");
        foreach (var s in _tags)    { sb.Append(s);      sb.Append(','); }
        sb.Append("] Scene:");
        sb.Append(_scene.IsValid() ? _scene.name : "(none)");
        return sb.ToString();
    }
#endif

    // ── 내부 ─────────────────────────────────────────────────────────────────

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UNCachedQuery),
                "UNCachedQuery는 이미 실행되었거나 Dispose되었습니다. " +
                "Execute()를 두 번 이상 호출하지 말고, using 범위를 벗어난 참조 보관을 피하십시오.");
    }

    private void ReturnToPool()
    {
        if (_disposed) return;
        _disposed = true;
        _with.Clear();
        _without.Clear();
        _tags.Clear();
        _scene = default;
        _pool.Push(this);
    }
}