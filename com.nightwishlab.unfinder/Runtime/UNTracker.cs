using System;
using UnityEngine;

[AddComponentMenu("")]
[ExecuteAlways]
public class UNTracker : MonoBehaviour
{
    // ── 유지 필드 ─────────────────────────────────────────────────────────────

    internal int        hash;
    internal string     registeredName;
    internal string     registeredTag;
    internal Type[]     registeredTypes;

    // [FIX-14] 이 인스턴스를 생성한 프리팹 소스 참조.
    //          UN.Instantiate → InitializeWithPrefabCache 경로에서만 설정된다.
    //          UN.AddComponent 호출 시 이 프리팹의 캐시 항목을 무효화하는 데 사용된다.
    //          프리팹 없이 등록된 오브젝트(씬 오브젝트, BakeAttach 등)는 null.
    internal GameObject prefabSource;

    private bool _isRegistered       = false;

    // ── [FIX-8] 지연 타입 인덱스 재빌드 ──────────────────────────────────────
    //
    // UN.Destroy(Component, delay) 가 호출되면 Unity 는 해당 컴포넌트를 파괴한다.
    //   delay == 0 → 이번 프레임 말에 파괴.
    //   delay >  0 → delay 초 후 프레임 말에 파괴.
    //
    // 파괴 전에 RebuildTypeIndex 를 호출하면 컴포넌트가 아직 살아있어 무의미하다.
    //
    // 해결책 — 펜딩 플래그 + 시간 기반 LateUpdate 훅:
    //   1. ScheduleTypeRebuild(delay) 가 _rebuildAfterTime = Time.time + delay 를 기록하고
    //      enabled = true 로 LateUpdate 를 활성화한다.
    //   2. LateUpdate 는 Time.time > _rebuildAfterTime 이 될 때까지 대기한다.
    //      delay == 0 이면 같은 프레임에서 Time.time <= _rebuildAfterTime 이므로
    //      자연스럽게 한 프레임 지연된다 — Object.Destroy 가 프레임 말에 실행되는 것과 정합.
    //   3. 재빌드 완료 후 enabled = false 로 LateUpdate 를 비활성화한다.

    private bool  _pendingTypeRebuild = false;
    private float _rebuildAfterTime;

    // ── 빌드 시점 팩토리 ──────────────────────────────────────────────────────

    internal static UNTracker BakeAttach(GameObject go, int hash, string name)
    {
        if (!go.TryGetComponent<UNTracker>(out var tracker))
            tracker = go.AddComponent<UNTracker>();

        tracker.hash           = hash;
        tracker.registeredName = name;
        tracker.hideFlags      = HideFlags.HideInInspector;
        return tracker;
    }

    // ── 기본 초기화 ───────────────────────────────────────────────────────────

    internal void Initialize(int hash, string name)
    {
        if (_isRegistered)
        {
            // 해시가 같아도(충돌) 이름이 다를 수 있으므로 이름까지 함께 비교한다.
            if (this.hash != hash ||
                !string.Equals(this.registeredName, name, StringComparison.Ordinal))
            {
                UN.RemoveFromCache(this.hash, gameObject);
                this.hash           = hash;
                this.registeredName = name;
                UN.RegisterToCache(this.hash, this.registeredName, gameObject);
            }
            return;
        }

        this.hash           = hash;
        this.registeredName = name;

        UN.RegisterToCache(this.hash, this.registeredName, gameObject);

        this.registeredTag = gameObject.tag;
        UN.RegisterTagIndex(this.registeredTag, gameObject);

        UN.RegisterTypeIndex(this);

        _isRegistered = true;
    }

    // ── [A] 프리팹 캐시 초기화 ───────────────────────────────────────────────
    //
    // UN_API.EnsureTrackerWithPrefabCache(Instantiate 경로)에서 호출됩니다.
    // 프리팹의 첫 인스턴스에서는 RegisterTypeIndex 경로를 타고
    // 결과 Type[]를 _prefabTypeCache에 저장합니다.
    // 이후 인스턴스에서는 캐시된 Type[]를 재사용하므로
    // GetComponents/ToArray 할당이 발생하지 않습니다.

    internal void InitializeWithPrefabCache(int hash, string name, GameObject prefab)
    {
        if (_isRegistered)
        {
            // 해시가 같아도(충돌) 이름이 다를 수 있으므로 이름까지 함께 비교한다.
            if (this.hash != hash ||
                !string.Equals(this.registeredName, name, StringComparison.Ordinal))
            {
                UN.RemoveFromCache(this.hash, gameObject);
                this.hash           = hash;
                this.registeredName = name;
                UN.RegisterToCache(this.hash, this.registeredName, gameObject);
            }
            return;
        }

        this.hash           = hash;
        this.registeredName = name;

        UN.RegisterToCache(this.hash, this.registeredName, gameObject);

        this.registeredTag = gameObject.tag;
        UN.RegisterTagIndex(this.registeredTag, gameObject);

        // [FIX-14] 프리팹 소스를 기억해 둔다.
        //          UN.AddComponent 호출 시 이 참조를 통해 캐시를 무효화한다.
        this.prefabSource = prefab;

        // 프리팹 타입 캐시 조회.
        if (prefab != null && UN._prefabTypeCache.TryGetValue(prefab, out var cachedTypes))
        {
            // 빠른 경로: 캐시된 Type[] 재사용 - GetComponents/ToArray 생략.
            UN.RegisterTypeIndexFromCache(this, cachedTypes);
        }
        else
        {
            // 느린 경로: 첫 인스턴스에 대한 전체 등록.
            UN.RegisterTypeIndex(this);

            // 이후 같은 프리팹 인스턴스를 위해 결과를 저장합니다.
            if (prefab != null && registeredTypes != null)
                UN._prefabTypeCache[prefab] = registeredTypes;
        }

        _isRegistered = true;
    }

    // ── [FIX-8] 지연 타입 인덱스 재빌드 ──────────────────────────────────────

    /// <summary>
    /// 타입 인덱스 재빌드를 예약합니다.
    /// <paramref name="delay"/> == 0 이면 다음 프레임,
    /// &gt; 0 이면 파괴 완료 후 첫 LateUpdate 에서 실행됩니다.
    /// </summary>
    internal void ScheduleTypeRebuild(float delay = 0f)
    {
        float targetTime = Time.time + delay;

        if (_pendingTypeRebuild)
        {
            // 복수 파괴 예약 시 가장 늦은 파괴 이후에 재빌드한다.
            if (targetTime > _rebuildAfterTime)
                _rebuildAfterTime = targetTime;
            return;
        }

        _rebuildAfterTime = targetTime;
        _pendingTypeRebuild = true;
        enabled = true;
    }

    private void LateUpdate()
    {
        if (!_pendingTypeRebuild)
        {
            enabled = false;
            return;
        }

        // delay > 0 인 파괴가 완료될 때까지 대기한다.
        // delay == 0 일 때도 같은 프레임에서는 Time.time == _rebuildAfterTime 이므로
        // 한 프레임 자연 지연된다 — Object.Destroy 가 프레임 말에 실행되는 것과 정합.
        if (Time.time <= _rebuildAfterTime) return;

        if (_isRegistered)
            UN.RebuildTypeIndex(this);

        _pendingTypeRebuild = false;
        enabled = false;
    }

    // ── 정리 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// UNSceneRegistry.OnDestroy 에서 소멸 순서 불확실성을 처리하기 위해
    /// 명시적으로 캐시 해제를 요청합니다.
    /// 이미 OnDestroy 를 통해 해제된 트래커에 대한 중복 호출은 무시됩니다.
    /// </summary>
    internal void ForceUnregister()
    {
        CleanupCache();
    }

    private void OnDestroy()
    {
        CleanupCache();
    }

    private void CleanupCache()
    {
        if (!_isRegistered) return;
        UN.RemoveFromCache(hash, gameObject);
        UN.UnregisterTagIndex(registeredTag, gameObject);
        UN.UnregisterTypeIndex(this);
        _isRegistered = false;
    }

    // ── 드리프트 감지 (Editor 전용) ──────────────────────────────────────────
#if UNITY_EDITOR
    private void OnValidate()
    {
        // 등록되지 않은 트래커는 검사 불필요
        if (!_isRegistered) return;

        // 이름 드리프트: go.name 을 직접 변경했을 때만 발생
        if (gameObject.name != registeredName)
            Debug.LogWarning(
                $"[UNFinder] 캐시 불일치 — 이름 드리프트 감지!\n" +
                $"등록 이름: '{registeredName}'  현재 이름: '{gameObject.name}'\n" +
                $"gameObject.name 을 직접 변경하지 말고 UN.Rename() 을 사용하십시오.",
                this);

        // 태그 드리프트: go.tag 를 직접 변경했을 때만 발생
        if (!string.IsNullOrEmpty(registeredTag) && gameObject.tag != registeredTag)
            Debug.LogWarning(
                $"[UNFinder] 캐시 불일치 — 태그 드리프트 감지!\n" +
                $"등록 태그: '{registeredTag}'  현재 태그: '{gameObject.tag}'\n" +
                $"go.tag 를 직접 변경하지 말고 UN.SetTag() 를 사용하십시오.",
                this);
    }
#endif

    // ── 컨텍스트 메뉴 수동 복구 (Editor 전용) ────────────────────────────────
#if UNITY_EDITOR
    [ContextMenu("UNFinder / 캐시 상태 재동기화")]
    private void ResyncCache()
    {
        if (!_isRegistered) return;

        // 이름 복구
        if (gameObject.name != registeredName)
        {
            UN.RemoveFromCache(hash, gameObject);
            registeredName = gameObject.name;
            hash           = UN.GetHash(registeredName);
            UN.RegisterToCache(hash, registeredName, gameObject);
            Debug.Log($"[UNFinder] 이름 재동기화 완료: '{registeredName}'", this);
        }

        // 태그 복구
        if (gameObject.tag != registeredTag)
        {
            UN.ReregisterTag(registeredTag, gameObject.tag, gameObject);
            registeredTag = gameObject.tag;
            Debug.Log($"[UNFinder] 태그 재동기화 완료: '{registeredTag}'", this);
        }
    }
#endif
}