# UN (Unity-Native) Fast Object Finder SDK

![](https://img.shields.io/badge/unity-2021.3%2B-black)
![](https://img.shields.io/badge/license-MIT-blue)

> **UNFinder** is a high-performance object lookup framework for Unity that replaces
> **`GameObject.Find`** and **`GetComponent`** with **O(1)** constant-time access.  

By combining **string hashing**, **pure C# memory dictionaries**, and
**`ConditionalWeakTable`**, UNFinder enables **zero-allocation object lookup**
while avoiding Unity’s native C++ marshalling overhead.

During the build process, the SDK automatically injects hidden trackers into scenes.
Developers can continue using familiar lookup patterns while internally benefiting from
a fully cached runtime registry.

<img width="798" height="85" alt="image" src="https://github.com/user-attachments/assets/5f04341e-a445-4614-aae7-43d25f632744" />

``https://github.com/NightWish-0827/UNFinder.git`` UPM Add package from git URL 

---

# Table of Contents

* [Core Features](#core-features)
* [API Reference & Usage](#api-reference--usage)

  * [Object & Component Lookup](#object--component-lookup)
  * [Dynamic Instantiation](#dynamic-instantiation)
  * [Rename & Manual Binding](#rename--manual-binding)
* [Lifecycle & Callback Mechanism](#lifecycle--callback-mechanism)
* [Performance Comparison](#performance-comparison)

---

# Core Features

### O(1) Fast Lookup

Removes the native **C++ marshalling cost** inside `GameObject.Find`
and performs object retrieval directly from **pure C# memory structures**.

---

### Zero GC Allocation

Cache hits generate **no garbage allocations** and bypass Unity’s
**Fake-Null bridge overhead**.

---

### Memory Leak Prevention

Component caching uses **`ConditionalWeakTable`**, allowing cached entries
to be automatically released when the associated `GameObject` is destroyed.

---

### Auto Baking System

During build time, the SDK processes scenes and **automatically registers
all objects into a runtime cache registry** without requiring manual setup.

---

# API Reference & Usage

The API is designed as a **drop-in replacement for common Unity lookup patterns.**

---

## Object & Component Lookup

If the object exists in cache, it is returned in **O(1)** time.
If not, a **lazy fallback to native lookup** is performed and the result is cached.

```csharp
// Replacement for GameObject.Find("Player")
GameObject player = UN.Find("Player");

// Lookup and cache component access
PlayerController controller = UN.FindComponent<PlayerController>("Player");
```

---

## Dynamic Instantiation

Instantiated prefabs are **immediately registered into the lookup cache**.

The system also removes the default `"(Clone)"` suffix and preserves
the original object name.

```csharp
GameObject enemyInstance =
    UN.Instantiate(enemyPrefab, spawnPos, Quaternion.identity);

EnemyAI ai =
    UN.Instantiate<EnemyAI>(enemyAIPrefab, spawnPos, Quaternion.identity);
```

---

## Rename & Manual Binding

When renaming objects dynamically, the provided API should be used
to maintain **cache integrity**.

```csharp
// Manual registration
UN.Bind(dynamicObject, "NewDynamicBoss");

// Rename while synchronizing cache hash
UN.Rename(playerObject, "Player_Awakened");
```

---

# Lifecycle & Callback Mechanism

A key advantage of **UNFinder** is that developers **do not need to manually
register or unregister objects**.

The SDK synchronizes automatically by integrating with Unity’s
**build-time callbacks** and **runtime lifecycle hooks**.

```
Build Phase
-----------
IProcessSceneWithReport.OnProcessScene
→ Traverse all scene Transforms
→ Inject hidden UNTracker components
→ Generate ~UN_AutoRegistry bindings

Bootstrapping Phase
-------------------
DefaultExecutionOrder(-32000)
→ UNSceneRegistry.Awake()
→ Hash all scene objects using FNV-1a
→ Register into _fastBucket

Runtime Lookup
--------------
UN.Find("Player")
→ Immediate dictionary return (No native call)

Destroy Phase
-------------
UNTracker.OnDestroy()
→ UN.RemoveFromCache()
→ Automatic cache cleanup
```

---

# Performance Comparison

As scene complexity increases, `GameObject.Find` scales linearly
due to **O(N) hierarchy traversal**.

UNFinder maintains **constant-time O(1) lookup** regardless of scene size.

### Default Unity API

* Native **C++ marshalling**
* Full hierarchy traversal
* Performance degradation under repeated calls

### UNFinder SDK

* Pure **C# dictionary lookup**
* **0 byte GC allocation**
* Deterministic **O(1)** object retrieval

# Benchmark

<img width="1600" height="960" alt="PerformGraph" src="https://github.com/user-attachments/assets/4a7c5e16-473a-4242-a73c-625392d421a9" />

