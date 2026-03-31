using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// <see cref="UNQuery.Execute()"/>가 반환하는 풀링 결과 컨테이너입니다.
///
/// 사용 후 풀에 반환되도록 반드시 <c>using</c> 구문에서 사용하십시오.
/// 범위를 벗어난 참조를 보관하면 안 됩니다.
/// <code>
/// using var result = UN.Query().WithComponent&lt;IEnemy&gt;().Execute();
/// foreach (var go in result) { ... }
/// </code>
/// </summary>
public sealed class UNQueryResult : IDisposable, IEnumerable<GameObject>
{
    // ── 풀 ───────────────────────────────────────────────────────────────────

    private static readonly Stack<UNQueryResult> _pool = new Stack<UNQueryResult>(8);

    internal static UNQueryResult Rent()
    {
        if (_pool.Count > 0)
        {
            var inst     = _pool.Pop();
            inst._disposed = false;
            return inst;
        }
        return new UNQueryResult();
    }

    // 도메인 리로드 후 UN.ResetStaticData()에서 호출.
    // 이전 도메인의 내부 상태를 가진 객체가 재사용되지 않도록 풀 전체를 비운다.
    internal static void ResetPool() => _pool.Clear();

    // ── 상태 ─────────────────────────────────────────────────────────────────

    // 내부 리스트. 초기 용량 16은 대부분의 쿼리 크기를 과할당 없이 커버합니다.
    private const int kInitialCapacity = 16;
    private readonly List<GameObject> _items = new List<GameObject>(kInitialCapacity);
    private bool _disposed;

    private UNQueryResult() { }

    // ── 내부 쓰기 API (UN.ExecuteQuery 전용) ────────────────────────────────

    internal void Add(GameObject go) => _items.Add(go);

    // ── 공개 읽기 API ────────────────────────────────────────────────────────

    /// <summary>일치한 GameObject 개수입니다.</summary>
    public int Count
    {
        get { ThrowIfDisposed(); return _items.Count; }
    }

    /// <summary>인덱스로 결과에 접근합니다.</summary>
    public GameObject this[int index]
    {
        get { ThrowIfDisposed(); return _items[index]; }
    }

    // 덕 타이핑된 struct 열거자.
    // 일반 foreach는 이 오버로드를 직접 사용하므로 boxing/힙 할당이 없습니다.
    public List<GameObject>.Enumerator GetEnumerator()
    {
        ThrowIfDisposed();
        return _items.GetEnumerator();
    }

    // LINQ / IEnumerable<T> 소비자를 위한 명시적 인터페이스 구현.
    // struct 열거자가 boxing되지만, 정적 타입이 IEnumerable<GameObject>일 때만 호출됩니다.
    // 일반적인 foreach 경로에서는 사용되지 않습니다.
    IEnumerator<GameObject> IEnumerable<GameObject>.GetEnumerator()
    {
        ThrowIfDisposed();
        return _items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        ThrowIfDisposed();
        return _items.GetEnumerator();
    }

    // ── 생명주기 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 결과 리스트를 비우고 이 컨테이너를 풀에 반환합니다.
    /// <c>using</c> 블록 종료 시 자동으로 호출됩니다.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // _typeBucket / _tagBucket 의 자동 트림 정책과 대칭:
        //   (사용량) * 4 < (할당 용량)  → 75 % 이상 유휴
        //   (할당 용량) > kInitialCapacity → 초기 크기 이하면 트림 불필요
        // Clear() 전에 검사해야 Count 가 이번 사이클의 실제 사용량을 반영한다.
        if (_items.Capacity > kInitialCapacity && _items.Count * 4 < _items.Capacity)
            _items.TrimExcess();

        _items.Clear();
        _pool.Push(this);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UNQueryResult),
                "UNQueryResult는 이미 Dispose되었습니다. using 범위를 벗어난 참조를 보관하지 마십시오.");
    }
}