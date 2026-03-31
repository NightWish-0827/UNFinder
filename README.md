# UN (Unity-Native) Fast Object Finder SDK
## Current Version : 2.0.0  

![](https://img.shields.io/badge/unity-2021.3%2B-black)
![](https://img.shields.io/badge/license-MIT-blue)

> **UNFinder** is a high-performance lookup and query framework for Unity.
> It accelerates name lookups (`GameObject.Find`-style), component access, and
> type/tag-based filtering with deterministic runtime behavior.

UNFinder combines **FNV-1a hashing**, **pure C# buckets**, and
**pooling-based query pipelines** to reduce native bridge costs and runtime allocations.
It also provides lifecycle-safe APIs for rename/tag/component changes, so cache integrity
is preserved under dynamic gameplay.

During the build process, the SDK injects hidden trackers and scene registries.
At runtime, objects are indexed into name/type/tag buckets and queried without full scene scans.

<img width="798" height="85" alt="image" src="https://github.com/user-attachments/assets/5f04341e-a445-4614-aae7-43d25f632744" />

``https://github.com/NightWish-0827/UNFinder.git?path=/com.nightwishlab.unfinder`` UPM Add package from git URL

---

# Table of Contents

* [Core Features](#core-features)
* [API Reference & Usage](#api-reference--usage)
  * [Object & Component Lookup](#object--component-lookup)
  * [Scene-Aware Lookup](#scene-aware-lookup)
  * [Dynamic Instantiation & Destroy](#dynamic-instantiation--destroy)
  * [Rename, Binding, and State Sync](#rename-binding-and-state-sync)
  * [Fluent Query API](#fluent-query-api)
  * [Cached Query Mode](#cached-query-mode)
* [Lifecycle & Callback Mechanism](#lifecycle--callback-mechanism)
* [Operational Notes](#operational-notes)
* [Performance Comparison](#performance-comparison)

---

# Core Features

### O(1) Name Lookup Path

`UN.Find(name)` uses hashed name buckets first, then performs a one-time lazy fallback
to native lookup only when needed. Cache hits stay in pure C# memory.

---

### Query-Driven Type/Tag Filtering

`UN.Query()` supports **With**, **Without**, **Tag**, and **Scene** filters.
The engine picks the smallest candidate bucket first to reduce traversal cost.

---

### Low-Allocation Runtime

UNFinder uses pooled query builders and pooled query results.
Common hot paths avoid transient allocations in repeated frame-by-frame calls.

---

### Lifecycle-Safe Cache Integrity

Dedicated APIs (`Rename`, `SetTag`, `AddComponent`, `NotifyComponentChanged`, `Destroy`)
keep indices synchronized with live Unity objects.

---

### Build-Time Auto Baking

During build processing, UNFinder adds hidden trackers for objects that contain
at least one component marked with `[UNBake]`.

---

# API Reference & Usage

The API is designed as a drop-in workflow upgrade for common Unity lookup patterns.

---

## Object & Component Lookup

```csharp
// Replacement for GameObject.Find("Player")
GameObject player = UN.Find("Player");

// Lookup and cache component access
PlayerController controller = UN.FindComponent<PlayerController>("Player");
```

---

## Scene-Aware Lookup

Useful in additive scene setups where name/type collisions are valid.

```csharp
using UnityEngine.SceneManagement;

Scene combatScene = SceneManager.GetSceneByName("Combat");
GameObject boss = UN.FindInScene(combatScene, "Boss");
BossController bossCtrl = UN.FindComponentInScene<BossController>(combatScene, "Boss");
```

---

## Dynamic Instantiation & Destroy

Instances are registered immediately, and the default `"(Clone)"` suffix is removed.
Component overloads are also supported.

```csharp
GameObject enemyInstance =
    UN.Instantiate(enemyPrefab, spawnPos, Quaternion.identity);

EnemyAI ai =
    UN.Instantiate(enemyAIPrefab, spawnPos, Quaternion.identity);

// Destroy wrappers keep lifecycle behavior explicit and consistent.
UN.Destroy(enemyInstance);
UN.Destroy(ai);
```

---

## Rename, Binding, and State Sync

Use UN APIs for mutable runtime state that affects indexing.

```csharp
// Manual registration
UN.Bind(dynamicObject, "DynamicBoss");

// Rename while keeping name cache integrity
UN.Rename(dynamicObject, "DynamicBoss_Phase2");

// Tag update with tag-index synchronization
UN.SetTag(dynamicObject, "Respawn");

// Safe component addition with type-index rebuild
UN.AddComponent<FrozenState>(dynamicObject);

// If you changed components through raw Unity APIs, notify UNFinder
UN.NotifyComponentChanged(dynamicObject);
```

---

## Fluent Query API

```csharp
using var result = UN.Query()
    .WithComponent<IDamageable>()
    .WithoutComponent<IFrozen>()
    .WithTag("Enemy")
    .Execute();

foreach (var go in result)
{
    // Use result objects here
}
```

Additional terminal operations:

```csharp
// Scene filter
using var sceneOnly = UN.Query()
    .WithComponent<IEnemy>()
    .WithScene(combatScene)
    .Execute();

// Callback iteration
UN.Query()
  .WithComponent<ITickable>()
  .ForEach(go => go.GetComponent<ITickable>().Tick(Time.deltaTime));

// Early stop
bool completed = UN.Query()
    .WithComponent<IEnemy>()
    .TryForEach(go =>
    {
        if (!go.activeInHierarchy) return true; // continue
        return false;                            // break
    });

// Fast first-match retrieval
GameObject firstEnemy = UN.Query().WithComponent<IEnemy>().First();
```

---

## Cached Query Mode

Cached mode reuses query results within the same frame for identical query fingerprints.
Cache entries are invalidated automatically on frame boundaries and object graph changes.

```csharp
using var result = UN.Query()
    .WithComponent<IDamageable>()
    .WithScene(combatScene)
    .Cached()
    .Execute();
```

`Cached()` returns `UNCachedQuery`, which intentionally exposes `Execute()` only.

---

# Lifecycle & Callback Mechanism

UNFinder synchronizes through Unity build/runtime callbacks and tracker lifecycle hooks.

```
Build Phase
-----------
IProcessSceneWithReport.OnProcessScene
→ Scan scene roots
→ Attach hidden UNTracker to [UNBake] targets
→ Create ~UN_AutoRegistry with baked tracker references

Bootstrapping Phase
-------------------
DefaultExecutionOrder(-32000)
→ UNSceneRegistry.Awake()
→ Initialize baked trackers
→ Register name/type/tag indices

Runtime Mutation
----------------
UN.Bind / UN.Rename / UN.SetTag / UN.AddComponent / UN.Destroy
→ Keep cache and indices synchronized

Destroy Phase
-------------
UNTracker.OnDestroy()
→ Remove from name/type/tag buckets
→ Prevent stale references
```

---

# Operational Notes

### Main-Thread Only

UNFinder APIs are designed for Unity main thread usage.
Do not call them from worker threads or jobs.

---

### Prefer UN APIs for Mutable Indexed State

For tracked objects, avoid direct `go.name` and `go.tag` writes.
Use `UN.Rename` and `UN.SetTag` so query/index consistency is preserved.

---

### Use `using` for Query Results

`UNQueryResult` and `UNCachedQuery` are pooled/disposable types.
Always release them promptly:

```csharp
using var result = UN.Query().WithComponent<IEnemy>().Execute();
```

---

### Optional Memory Trimming

After large destruction waves or scene transitions, you can reclaim bucket memory:

```csharp
int trimmedBuckets = UN.TrimTypeBuckets();
```

---

# Performance Comparison

As scene complexity increases, native lookup APIs tend to scale with hierarchy traversal cost.
UNFinder avoids full-scene scans for indexed lookups and narrows query traversal by bucket selection.

<img width="1000" height="600" alt="Code_Generated_Image (2)" src="https://github.com/user-attachments/assets/cfe59351-e4f6-46c2-88c7-26978186bf6e" />

### Default Unity API

* Native C++ bridge and hierarchy scan costs
* Repeated calls amplify total traversal overhead
* Filter-heavy workflows require repeated scene-wide passes

### UNFinder SDK

* Name lookup through hashed C# buckets
* Type/tag query through indexed sets
* Frame-cached query mode for repeated same-frame requests
* Pool-backed query/result objects for predictable runtime behavior

---

### In a word...

UNFinder turns lookup-heavy gameplay code into an indexed runtime workflow.
You keep familiar usage patterns while gaining deterministic and scalable access paths.

---
