using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 플루언트 쿼리 빌더입니다. <see cref="UN.Query()"/>로 획득합니다.
///
/// 빌더 객체 자체는 풀링되므로 참조를 장기 보관하면 안 됩니다.
/// 필터 메서드를 체이닝한 뒤 <see cref="Execute()"/> 또는 <see cref="ForEach"/>로 종료하십시오.
///
/// <code>
/// // 기본 모드: 항상 재조회
/// using var result = UN.Query()
///     .WithComponent&lt;IDamageable&gt;()
///     .WithoutComponent&lt;IFrozen&gt;()
///     .Execute();
///
/// // Additive 씬 필터: 특정 씬의 오브젝트만 조회
/// using var result = UN.Query()
///     .WithComponent&lt;IEnemy&gt;()
///     .WithScene(combatScene)
///     .Execute();
///
/// // 프레임 캐시 모드: UNCachedQuery 반환, Execute()만 사용 가능
/// // ForEach는 UNCachedQuery에서 의도적으로 제거됨(컴파일 타임 계약)
/// using var result = UN.Query()
///     .WithComponent&lt;IDamageable&gt;()
///     .Cached()
///     .Execute();
///
/// // GC-free 인라인 순회(비캐시 모드 전용)
/// UN.Query().WithComponent&lt;ITickable&gt;().ForEach(go => go.GetComponent&lt;ITickable&gt;().Tick());
/// </code>
/// </summary>
public sealed class UNQuery
{
    // ── 풀 ───────────────────────────────────────────────────────────────────

    private static readonly Stack<UNQuery> _pool = new Stack<UNQuery>(8);

    internal static UNQuery Rent()
        => _pool.Count > 0 ? _pool.Pop() : new UNQuery();

    // 도메인 리로드 후 UN.ResetStaticData()에서 호출.
    // 이전 도메인의 내부 상태를 가진 객체가 재사용되지 않도록 풀 전체를 비운다.
    internal static void ResetPool() => _pool.Clear();

    // ── 필터 상태 ────────────────────────────────────────────────────────────

    private readonly List<Type>   _with    = new List<Type>(4);
    private readonly List<Type>   _without = new List<Type>(4);
    private readonly List<string> _tags    = new List<string>(2);

    // default(Scene).IsValid() == false 는 "필터 없음"을 의미합니다.
    // WithScene()을 호출하면 유효한 씬 핸들이 설정됩니다.
    private Scene _scene;

    private UNQuery() { }

    // ── 필터 API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <typeparamref name="T"/>에 할당 가능한 컴포넌트를 가진 GameObject만 포함합니다.
    /// <typeparamref name="T"/>는 구체 타입, 추상 베이스 클래스, 인터페이스 모두 가능합니다.
    /// </summary>
    public UNQuery WithComponent<T>() where T : class
    {
        _with.Add(typeof(T));
        return this;
    }

    /// <summary><typeparamref name="T"/>에 할당 가능한 컴포넌트를 가진 GameObject를 제외합니다.</summary>
    public UNQuery WithoutComponent<T>() where T : class
    {
        _without.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Unity Tag가 <paramref name="tag"/>와 같은 GameObject만 포함합니다.
    /// 런타임 태그 변경은 <see cref="UN.SetTag"/>를 통해 수행하십시오.
    /// </summary>
    public UNQuery WithTag(string tag)
    {
        _tags.Add(tag);
        return this;
    }

    /// <summary>
    /// Include only GameObjects that belong to <paramref name="scene"/>.
    ///
    /// 어디티브 씬 환경에서 동일 타입의 오브젝트를 씬 단위로 구분할 때 사용합니다.
    /// 호출하지 않으면 모든 씬의 오브젝트를 반환합니다 (기존 동작).
    ///
    /// <b>구현 특성:</b>
    /// 씬 필터는 별도 인덱스 없이 <c>go.scene == scene</c> 비교로 동작합니다.
    /// <c>FindSmallestBucket</c> 이 타입·태그로 후보를 먼저 좁힌 뒤 씬을 검사하므로
    /// 전체 씬 순회 비용은 발생하지 않습니다.
    ///
    /// <b>Cached() 와 함께 사용 시:</b>
    /// 씬 핸들이 지문에 포함되므로 동일 타입·다른 씬 쿼리는 별도 캐시 슬롯을 사용합니다.
    /// </summary>
    public UNQuery WithScene(Scene scene)
    {
        _scene = scene;
        return this;
    }

    /// <summary>
    /// 프레임 단위 캐시 모드로 전환하고 <see cref="UNCachedQuery"/>를 반환합니다.
    ///
    /// <see cref="UNCachedQuery"/>는 <c>Execute()</c>만 노출합니다.
    /// <c>Cached()</c> 이후에는 <c>ForEach</c>를 의도적으로 사용할 수 없으며
    /// 이는 반환 타입으로 컴파일 타임에 강제됩니다.
    ///
    /// 같은 프레임의 동일 쿼리는 버킷 재순회 없이 캐시를 히트합니다.
    /// 캐시는 다음 프레임 경계에서 자동 무효화되며,
    /// 프레임 중간에 GO 등록/제거가 발생해도 자동 무효화됩니다.
    /// </summary>
    public UNCachedQuery Cached()
    {
        // 필터 상태를 UNCachedQuery로 전달한 뒤 현재 빌더를 풀에 반환합니다.
        var cached = UNCachedQuery.Rent();
        cached.CopyFiltersFrom(_with, _without, _tags, _scene);
        ReturnToPool();
        return cached;
    }

    // ── 종료 연산 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 쿼리를 실행하고 풀링된 <see cref="UNQueryResult"/>를 반환합니다.
    /// <c>using</c> 구문으로 감싸 사용하십시오.
    /// </summary>
    public UNQueryResult Execute()
    {
        var result = UNQueryResult.Rent();
        UN.ExecuteQuery(_with, _without, _tags, _scene, result);
        ReturnToPool();
        return result;
    }

    /// <summary>
    /// 쿼리를 실행하고 일치하는 각 GameObject에 대해 <paramref name="action"/>을 호출합니다.
    /// <paramref name="action"/>이 변수를 캡처하지 않으면 GC-free입니다.
    ///
    /// 조기 종료가 필요하면 <see cref="TryForEach"/> 를 사용하십시오.
    /// </summary>
    public void ForEach(Action<GameObject> action)
    {
        UN.ExecuteQueryForEach(_with, _without, _tags, _scene, action);
        ReturnToPool();
    }

    /// <summary>
    /// 쿼리를 실행하고 일치하는 각 GameObject에 대해 <paramref name="action"/>을 호출합니다.
    /// <paramref name="action"/> 이 <c>false</c> 를 반환하면 순회를 즉시 중단합니다.
    ///
    /// <paramref name="action"/>이 변수를 캡처하지 않으면 GC-free입니다.
    ///
    /// <code>
    /// // 처음 발견한 활성 Enemy만 처리하고 중단
    /// UN.Query().WithComponent&lt;IEnemy&gt;().TryForEach(go =>
    /// {
    ///     if (!go.activeInHierarchy) return true;  // continue
    ///     go.GetComponent&lt;IEnemy&gt;().Alert();
    ///     return false;                             // break
    /// });
    /// </code>
    /// </summary>
    /// <returns>
    /// <c>true</c>  — 모든 오브젝트 순회를 완료함.<br/>
    /// <c>false</c> — <paramref name="action"/>이 중단을 요청해 조기 종료됨.
    /// </returns>
    public bool TryForEach(Func<GameObject, bool> action)
    {
        bool completed = UN.ExecuteQueryTryForEach(_with, _without, _tags, _scene, action);
        ReturnToPool();
        return completed;
    }

    /// <summary>
    /// 쿼리를 만족하는 첫 번째 GameObject 를 반환합니다.
    /// 일치하는 오브젝트가 없으면 <c>null</c> 을 반환합니다.
    ///
    /// 내부적으로 첫 번째 오브젝트 발견 즉시 순회를 중단하므로
    /// 전체 결과가 필요하지 않을 때 <c>Execute()[0]</c> 보다 효율적입니다.
    /// GC-free — delegate 할당 및 <see cref="UNQueryResult"/> 할당이 없습니다.
    ///
    /// <code>
    /// // 플레이어와 가장 먼저 인덱싱된 Enemy 를 찾음
    /// GameObject firstEnemy = UN.Query().WithComponent&lt;IEnemy&gt;().First();
    /// </code>
    /// </summary>
    public GameObject First()
    {
        var found = UN.ExecuteQueryFirst(_with, _without, _tags, _scene);
        ReturnToPool();
        return found;
    }

    // ── 내부 ─────────────────────────────────────────────────────────────────

    private void ReturnToPool()
    {
        _with.Clear();
        _without.Clear();
        _tags.Clear();
        _scene = default; // IsValid() == false 이므로 다음 사용 시 씬 필터 없음
        _pool.Push(this);
    }
}