# Supermarket Simulator modding field guide - v1.0.2

A practical reference for porting Supermarket Simulator mods from the old Mono build to the current Unity 6 / IL2CPP build, and for writing new ones. Everything here is the game-side knowledge that five mods earned the hard way: SuperDecor, SuperRestockers, SuperPricer, SuperStructure, and Custom Products Loader.

## Why this guide exists

When Supermarket Simulator moved to Unity 6 with IL2CPP, the C# game assemblies stopped being readable. BepInEx and Il2CppInterop hand you wrapper assemblies in `BepInEx/interop/`, but those wrappers are native-invoke stubs: you can see every type, field, and method signature, and you can call them, but there are no method bodies to read. `ilspycmd` on `Assembly-CSharp.dll` gives you names and shapes, never logic.

So every behavioral fact in this guide, what a method actually does, when it fires, what it returns for an unknown ID, which ones crash the process when you patch them, was discovered by shipping a no-op logging patch to confirm the method resolves, then watching it run in game. Trial, error, and a lot of log reading. The signatures are free; the behavior cost real time. This document collects both so the next modder does not have to repeat the work.

### Game build this was verified against

- Engine: Unity **6000.3.6f1**
- Scripting backend: **IL2CPP**, global-metadata version **39**
- Loader: **BepInEx 6.0.0-be.755** (Tobey's pack), Unity Doorstop 4.x
- Patching: **HarmonyX 2.x** — BepInEx's fork of Harmony, shipped as `BepInEx/core/0Harmony.dll`. One library covers both Mono and IL2CPP; there is no separate IL2CPP build of Harmony.
- Interop wrappers: ~146 assemblies generated into `BepInEx/interop/` on first launch
- Game version the interop signatures were dumped from: **v1.4.2(204)** (Steam 9.6.0.0 line), dumped 2026-06-13
- A full in-game **signature + behavior confirmation pass** re-verified this surface on v1.4.2(204) on **2026-06-18**: of 92 transcribed members, 91 resolved with the documented shape and 1 differed (`StorageSectionManager.m_StorageSections` is an array, not a `List`); none were missing. Corrections from that pass are folded in below.

The game updates. A point release already broke one patch target during the life of these mods (see `FurnitureGenerator.SpawnFurniture` below). Re-verify every signature against a fresh interop dump after any game update. Treat the line citations and signatures here as "true on the build above," not eternal truth.

### How to read the provenance tags

Each fact carries a tag so you know how much to trust it:

- **[shipped]** Relied on by a live Harmony patch or call path in one of the five mods. It works in production.
- **[probed]** Observed directly in game with runtime instrumentation. Behavior watched, not just assumed.
- **[sig]** Exact signature taken from the interop decompile. The shape is certain; the behavior next to it is documented from context and may not be independently confirmed.
- **[inferred]** Reasoning, not observation. A lead worth following, not a guarantee. Verify before you depend on it.

### Reading the interop signature tokens

The decompiled wrappers encode the marshalled signature in identifier names. The token grammar is:

```
Name_Access_[Static_][Virtual_Final_New_]ReturnType_Param1_Param2_..._N
```

For example `MoneyTransition_Public_Void_Single_TransitionType_Boolean_0` reads as `public void MoneyTransition(float, TransitionType, bool)`. `get_AvailablePosition_Public_Virtual_get_Boolean_0` is a public virtual `bool` property getter. This lets you recover exact parameter and return types even though the body is gone.

---

## Table of contents

- Part I, The porting model (start here)
- Part II, Marshalling and interop hazards
- Part III, The verified game-API surface (the type catalog)
  - 1. Registry and identity (IDManager and the SO families)
  - 2. Furniture, decor, and placement
  - 3. Shopping, delivery, and money
  - 4. Pricing
  - 5. Licenses, products, and the icon atlas
  - 6. Employees and NPCs
  - 7. Store structure and building
  - 8. Player, camera, and interaction
  - 9. Save and persistence
  - 10. Localization
  - 11. Other consumed types
  - 12. Multiplayer / co-op networking (Photon PUN)
- Part IV, World-load and day-cycle lifecycle
- Part V, Things that look right and break (the failed-approach index)
- Appendix, Re-verification checklist and the per-mod provenance map

---

## Part I, The porting model

If you are coming from the Mono build, read this whole part before you touch a patch. Most of the surprises live here, not in the game API.

### The Mono to IL2CPP cheat sheet

| Mono build | IL2CPP build |
|---|---|
| `BaseUnityPlugin` | `BasePlugin` (BepInEx.Unity.IL2CPP) |
| `Awake()` lifecycle entry | `public override void Load()` |
| `Logger` / `Debug.Log` | the `Log` property (`ManualLogSource`); never `UnityEngine.Debug.Log` |
| `MyBox.Singleton<T>.Instance` | `NoktaSingleton<T>.Instance` (and `NoktaSingletonPunCallbacks<T>` for multiplayer-aware ones) |
| `Traverse` / `AccessTools.Field` for privates | direct `instance.m_Field` access (interop exposes privates as public) |
| `KeyboardShortcut` config type | gone; read `UnityEngine.Input.GetKeyDown(KeyCode)` plus a modifier key |
| managed `T[]` arrays | `Il2CppReferenceArray<T>` / `Il2CppStructArray<T>` / `Il2CppArrayBase<T>` |
| `System.Collections.Generic.List<T>` from game calls | `Il2CppSystem.Collections.Generic.List<T>` (index it, no LINQ) |
| `obj as RectTransform` | `obj.TryCast<RectTransform>()` |
| `event += handler` / `Delegate.Combine` | `DelegateSupport.ConvertDelegate<T>` plus `Il2CppSystem.Delegate.Combine(...).Cast<T>()` |
| `StartCoroutine(managedIEnumerator)` | wrap with `.WrapToIl2Cpp()` first (unless the game handed you an `Il2CppSystem...IEnumerator`) |
| `IgnoresAccessChecksTo` publicizer | not needed; interop already exposes everything |
| publicized reflection on private value fields | hits the value-type reflection garbage bug (see Part II) |
| reference the game's `Assembly-CSharp.dll` in `Managed/` | reference the interop `Assembly-CSharp.dll` in `BepInEx/interop/` |

### Plugin shape

```csharp
[BepInPlugin("com.you.yourmod", "Your Mod", "1.0.0")]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        // bind config, apply patches, spawn a host GameObject here.
        // there is no Awake/Start. Load() is the only entry.
        Log.LogInfo("[YourMod] loaded");
    }
}
```

`BasePlugin` is not a MonoBehaviour and has no per-frame callback. If you need an `Update()` loop, inject a MonoBehaviour (see below).

### Interop wrapper naming

The wrapper assembly filenames are mostly unprefixed. Only the .NET base class library gets an `Il2Cpp` prefix.

- Unprefixed: `Assembly-CSharp.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.PhysicsModule.dll`, `UnityEngine.ImageConversionModule.dll`, `UnityEngine.AssetBundleModule.dll`, `MyBox.dll`, `Newtonsoft.Json.dll`, `Unity.Localization.dll`, and so on.
- Prefixed (the BCL only): `Il2Cppmscorlib.dll`, `Il2CppSystem.dll`, `Il2CppSystem.Core.dll`.

Type-name rules follow from that:

1. A type with a non-empty namespace keeps it verbatim: `UnityEngine.MonoBehaviour`, `UnityEngine.AssetBundle`.
2. `System.*` becomes `Il2CppSystem.*`: a game-returned list is `Il2CppSystem.Collections.Generic.List<T>`, a coroutine is `Il2CppSystem.Collections.IEnumerator`.
3. Global-namespace game types stay global. `IDManager`, `FurnitureSO`, `MarketShoppingCart`, `MoneyManager`, `NoktaSingleton` are referenced by their bare name with no `Il2Cpp` namespace.

A handful of game types live under real namespaces and must be aliased: `SupermarketSimulator.Clerk.Clerk`, `__Project__.Scripts.Multiplayer.NetworkComputer.NetworkComputer`, `__Project__.Scripts.WallPaintSystem.{BucketSo, PaintableWall, PaintableFloor}`, `__Project__.Scripts.FloorPaintSystem.FloorSo`.

The game's own `Assembly-CSharp.dll` under `<game>_Data/Managed/` is a stripped placeholder with no bodies and the wrong surface. Never reference it. Reference the one in `BepInEx/interop/`.

### Build configuration

- Target `net6.0`. The CoreCLR runtime ships inside the BepInEx pack. No `netstandard2.0`, no `net472`.
- Reference the interop and core DLLs with `<Reference>` plus a `<HintPath>` into a local-only directory (point an MSBuild property at the game's `BepInEx` folder so `core/` and `interop/` resolve). Set `<Private>false</Private>` so you never copy the player's DLLs next to your output.
- Never commit the interop DLLs, the BepInEx pack, or the game install. They are the player's, and they regenerate per machine.
- Decompile output (from `ilspycmd`) is game-derived. Keep it out of the repo too.

A pure-logic unit test project can target `net8.0` and run headless with no game references, useful for anything that does not touch Unity types (ID hashing, parsers, math).

### Inspecting the interop assemblies

```bash
# one type
ilspycmd -t IDManager "$GAME/BepInEx/interop/Assembly-CSharp.dll"
# whole assembly to a tree
ilspycmd -p -o /tmp/gamesrc "$GAME/BepInEx/interop/Assembly-CSharp.dll"
```

You get signatures, fields, properties, and the native-pointer plumbing. You do not get logic. `ilspycmd` 8.2.0.7535 is a known-good pin (newer builds want .NET 9; roll forward onto net8 with `DOTNET_ROLL_FORWARD=Major` if your SDK is 8.x).

### Singletons

The game's singleton base is `NoktaSingleton<T>` (a MonoBehaviour), with a multiplayer-aware variant `NoktaSingletonPunCallbacks<T>` (a Photon `PunCallbacks` subclass). There are exactly 95 of the former and 38 of the latter on v1.4.2(204) (a live reflection census confirmed the count; re-count after a game update). It is not MyBox `Singleton<T>`, which is the trap the Mono mods fall into first.

```csharp
// the safe access pattern
T mgr = SomeManager.HasInstance ? SomeManager.Instance : null;
```

Two things to know:

- `Instance` is a native getter that can throw or auto-create before the world is ready. Gate reads on `HasInstance` and wrap them in try/catch during early load.
- `HasInstance` can lie in the other direction. For some managers it stays `false` until something first touches `Instance`. `SFXManager` is the documented case: a `HasInstance`-gated resolver returned nothing until the player opened the pricing computer (the first thing to read `SFXManager.Instance`). When you specifically need a lazily-resolved manager, read `Instance` directly with a `FindObjectsOfType<T>()` fallback, rather than gating on `HasInstance`.

A generic resolver that survives both cases:

```csharp
static T Resolve<T>() where T : UnityEngine.Object
{
    try { var i = NoktaSingleton<T>.Instance; if (i != null) return i; } catch { }
    var found = UnityEngine.Object.FindObjectOfType<T>();
    if (found != null) return found;
    var all = Resources.FindObjectsOfTypeAll<T>();
    return all != null && all.Length > 0 ? all[0] : null;
}
```

Because `NoktaSingleton<T>.Instance` cannot be named from a generic constrained only to `UnityEngine.Object`, resolve it reflectively (the open generic is `typeof(GameType).BaseType.GetGenericTypeDefinition()`).

Most of these managers are per-scene. See the asset-lifetime hazard in Part II: a fresh instance appears on every save load, and anything you injected into the previous instance is gone.

### Private members are public

Il2CppInterop surfaces private game fields as public wrapper members. Read and write `instance.m_Foo` directly. Harmony `Traverse` and `AccessTools` do not reliably see interop-surfaced members, so the Mono idiom of `Traverse(obj).Field("m_Box").GetValue()` should become `obj.m_Box`. This is the single most common edit when porting a Mono patch.

### HarmonyX patching

Patching uses HarmonyX, BepInEx's fork of Harmony. Targeting works the same way as on Mono, against the wrapped type:

```csharp
[HarmonyPatch(typeof(IDManager), "FurnitureSO")]            // by string name
[HarmonyPatch(typeof(IDManager), nameof(IDManager.FurnitureSO))]
[HarmonyPatch(typeof(FurnitureGenerator), nameof(FurnitureGenerator.SpawnFurniture),
              new Type[] { typeof(int), typeof(Vector3), typeof(int) })]  // pick an overload
```

Transpilers do not apply to the game's own methods: IL2CPP compiles them to native code, so there is no managed IL body to rewrite — only prefix, postfix, and finalizer hooks attach to a game method. (HarmonyX can still transpile a *managed* method, such as one in your own or another plugin's assembly.) Every recipe here is therefore prefix/postfix/finalizer.

The rules that bite:

- **`PatchAll` is fail-fast and takes the whole framework down with it.** If one `[HarmonyPatch]` target does not resolve (wrong overload array, a renamed or removed method, the wrong type in a virtual hierarchy), HarmonyX throws during `PatchAll` and none of your patches apply. This actually happened: a game point release changed `SpawnFurniture(int, Vector3)` to `(int, Vector3, int)`, the old target stopped resolving, and SuperDecor's entire `PatchAll` aborted, disabling every feature at once. Two defenses, use both:
  - Apply patches one class at a time in their own try/catch, instead of one assembly-wide `PatchAll`. SuperRestockers wraps each class as `TryPatch(typeof(X))`; Custom Products Loader uses a generic `CreateClassProcessor(type).Patch()` per type. A broken hook then degrades one feature instead of the whole mod. Tier your failures: a safety-critical patch failing can flip the mod into a "do nothing" mode rather than acting on half-applied state.
  - For game-update resilience, use dynamic targeting: `[HarmonyPatch]` with a `Prepare()` that warns and skips when the target is missing, plus a `TargetMethods()` that scans by return type and parameter shape so a renamed getter still resolves.

- **A prefix returning `false` skips the original.** Return `true` to let it run. The common decor pattern: return `true` (do nothing) for vanilla input, return `false` only when you fully handle the modded case, and return `true` on any exception so a bug fails safe rather than eating the original.

- **A postfix runs even when a prefix skipped the original.** This is not obvious and it caused a real bug. If a foreign mod has a postfix on a method, and your prefix returns `false` to suppress that method, the foreign postfix still fires against the state you tried to suppress. To actually block a side effect you sometimes have to skip the underlying setter (for example `set_HologramColor`) rather than the compute method that calls it.

- **Patch the type where virtual dispatch lands.** For a method defined on a base interface or class and inherited, patch the base (`IPlacingMode.OverlapBoxes`), not the derived type (`FurniturePlacingMode`); targeting the derived type failed to resolve and triggered the fail-fast abort. For a method the derived type overrides (`FurniturePlacingMode.get_AvailablePosition`), patch the derived type, because that is where dispatch lands for its instances.

- **`[HarmonyPriority]` orders your own patches** without editing another file. Use `Priority.First` for the patch that must read input first, `Priority.Low` for the writer that must stamp the final transform after the others have run. Coordinate by explicit flags, not by fragile same-priority ordering.

- **Some methods hard-crash the process when patched** (native segfault, no managed exception). Known offenders: `FurniturePlacer.StopPlacingMode` (private) and `FurnitureInteraction.OnUse` (virtual override). Calling these methods is fine; patching them is not. When you hit a segfault on load, suspect a newly added patch target before anything else.

- **Wrap a crash-prone native method in a finalizer that returns null** to swallow whatever it threw. If `FurnitureManager.LoadFurnitureDatas` throws an NRE deep in native code, an unguarded exception aborts the calling `Awake` and you lose the whole restore. A `[HarmonyFinalizer]` returning `null` neutralizes it:

```csharp
[HarmonyFinalizer, HarmonyPatch(typeof(FurnitureManager), "LoadFurnitureDatas")]
static Exception Finalizer(Exception __exception) => null; // swallow native throw
```

- Do not swap in a `0Harmony.dll` from a BepInEx 5 / Mono pack. It is the same HarmonyX project, but the copy in this pack is built against the Il2CppInterop bridging the Mono-pack copy lacks.

### Injecting a MonoBehaviour

To get an `Update()` loop or any component, register the managed type once and give it an `IntPtr` constructor:

```csharp
public class Host : MonoBehaviour
{
    public Host(IntPtr ptr) : base(ptr) { }   // required so the runtime can instantiate it
    void Update() { /* poll here */ }
}

// once, before the first AddComponent:
if (!ClassInjector.IsTypeRegisteredInIl2Cpp<Host>())
    ClassInjector.RegisterTypeInIl2Cpp<Host>();

var go = new GameObject("YourMod Host");
UnityEngine.Object.DontDestroyOnLoad(go);
go.hideFlags = HideFlags.HideAndDontSave;
go.AddComponent<Host>();
```

A subtle trap: `Object.Instantiate` copies managed fields onto the clone, but not instance IDs. So an "is this the template" flag must not be a managed field (it would copy to every clone). Track template identity in a `static HashSet<int>` of GameObject instance IDs instead. For the same reason, a "has fired once" flag set on a template propagates to all clones unless you key it off instance identity. And a disabled injected component is inherited by clones of a cached prefab, so a component that disables itself can permanently disable the behavior on every placed copy; prefer a slow retry over self-disable.

Register lazily (on first use) rather than editing your plugin's `Load()` for every new behavior.

### Hotkeys

BepInEx 6 has no `KeyboardShortcut` type (that was BepInEx 5 / Mono). Read keys with `UnityEngine.Input.GetKeyDown(KeyCode)` and a separate `GetKey` for a modifier. If you bind a `KeyCode` config and want to trim the noisy enum hint in the `.cfg`, subclass `AcceptableValueBase` non-enforcing (accept everything, list only the useful values).

---

## Part II, Marshalling and interop hazards

This is the catalog of "my code compiled and silently did nothing." Read it once, refer back often.

### Array types

The game hands you three different array wrappers and they are not interchangeable.

- **`Il2CppStructArray<T>`** for value-type elements. Required where Unity wants a managed value array:

```csharp
var tex = new Texture2D(2, 2);
ImageConversion.LoadImage(tex, (Il2CppStructArray<byte>)pngBytes);
tex.SetPixels32((Il2CppStructArray<Color32>)pixels);
// Mesh.vertices / .normals / .uv are Il2CppStructArray<Vector3|Vector2>; .triangles is <int>
```

- **`Il2CppReferenceArray<T>`** for reference-type elements. Fixed-size game arrays show up as this (for example `EmployeeManager.m_RestockerSpawnPositions` is `Il2CppReferenceArray<Transform>`, not `Transform[]`). `.Length` and the indexer work.

  The sharp edge here cost a debugging session: **`Physics.OverlapBoxNonAlloc` wants an `Il2CppReferenceArray<Collider>`, and passing a managed `Collider[]` silently fails.** A managed array is copied into a throwaway Il2Cpp array per call, the native code fills the copy, and the results never come back to your managed buffer. It reads as zero overlaps even when objects visibly intersect. Allocate a persistent `Il2CppReferenceArray<Collider>` and reuse it; that is the array the native call writes into.

- **`Il2CppArrayBase<T>`** is what `GetComponents*`, `GetComponentsInChildren<T>`, `Physics.OverlapSphere`, and `Physics.RaycastAll` return. Iterate with `.Count` and the indexer. No managed LINQ, and no `Array.Sort` with a managed comparer delegate (do a single-pass scan instead).

### Collections

Game-returned lists and dictionaries are `Il2CppSystem.Collections.Generic.*`. Use `.Count`, the indexer, `Add`, and `foreach`; do not assume LINQ extension methods bind. Replace `list.Any(predicate)` with a hand-rolled loop. Alias long names for readability: `using PriceList = Il2CppSystem.Collections.Generic.List<Pricing>;`. If you obtained a list as `object` through reflection, get back to a usable handle with `Il2CppObjectBase.Cast<List<T>>()`.

### Null checking

Use Unity's `== null`, not `??`, `is null`, or `ReferenceEquals`. A `UnityEngine.Object`'s overloaded `==` honors the fake-null state of a destroyed or uninitialized wrapper. The managed-null operators see a live wrapper around a dead native object as non-null and walk straight into it. This matters for `Shader.Find` results, sprites, meshes, components, and `ref __result` values.

### Downcasting

Downcast interop wrappers with `.TryCast<T>()`, never C# `as`, `is`, or a direct cast. `as` on an interop wrapper skips the runtime type check and silently mis-types. The game's `RectTransform`s, colliders, and renderers all need `TryCast`:

```csharp
RectTransform rt = someTransform.TryCast<RectTransform>();
SphereCollider sc = col.TryCast<SphereCollider>();
```

### Delegates and events

There are two cases and they are different:

- **Invoking an existing engine delegate** needs no bridge. Null-check and call:

```csharp
inventoryManager.OnInventoryChanged?.Invoke();
inventoryManager.OnPurchaseCompleted?.Invoke(fromTablet);  // Il2CppSystem.Action<bool>
```

- **Assigning a managed handler to an engine `Action`/event** needs conversion, combination, and a re-cast:

```csharp
Il2CppSystem.Action bridged = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(myHandler);
Il2CppSystem.Action existing = dayCycle.OnStartedNewDay;
dayCycle.OnStartedNewDay = existing == null
    ? bridged
    : Il2CppSystem.Delegate.Combine(existing, bridged).Cast<Il2CppSystem.Action>();
```

`Combine` returns a `Delegate`-typed wrapper, so re-wrap with `.Cast<Il2CppSystem.Action>()` (a plain C# cast throws `InvalidCastException`).

**Keep bridged delegates alive yourself.** Root both the managed delegate and the bridged wrapper in `static` fields. Without a managed reference, the Il2CppInterop trampoline is garbage-collected and the native side later calls into a dead delegate. The classic symptom is "the button worked the first several times, then stopped after a while"; that "while" was a GC. This applies to bridged `UnityAction` button listeners and to `Action` subscriptions alike.

If a subscription attempt fails, do not record it as subscribed. Back off and retry, so a transient interop hiccup does not silently disable a feature for the rest of the session.

### Coroutines

A managed `IEnumerator` must be wrapped before `StartCoroutine`. But when a game method already returns an `Il2CppSystem.Collections.IEnumerator` (for example `MarketShoppingCart.GamePadControl`), pass it straight to `StartCoroutine` with no bridge. Guard with `gameObject.activeInHierarchy` before starting one.

### The ReadOnlySpan MissingMethodException family

Some Unity APIs that marshal through a `Span`/`ReadOnlySpan` can throw `MissingMethodException` at the call site (the `Il2CppSystem.ReadOnlySpan.GetPinnableReference` member was reported absent in an earlier interop build, and the Span throw is well-attested for AssetBundle / `ImageConversion` / `GUISkin` paths in the wider BepInEx-IL2CPP world). **But the exact set is build-specific and was over-stated here — verify the specific call.** Status on **v1.4.2(204)** (in-game probe):

- `GUI.Button(Rect, string)` — **callable, does NOT throw** (the F9 probe invoked it in `OnGUI` with no exception). The earlier "GUI.Button throws" claim is **refuted on this build**. [probed]
- `GUI.TextField`, `Material.SetOverrideTag` — historically observed to throw (SuperDecor), but **not re-confirmed on v1.4.2(204)** (the probe exercised only `GUI.Button`). Treat as suspect-but-unverified; test your exact call before depending on it either way. [inferred]

Even so, **prefer real runtime uGUI over IMGUI for any production UI** — not because interactive IMGUI necessarily throws (it apparently does not here), but because the whole mod family standardized on uGUI and it is the proven path: cloned uGUI prefabs (Canvas, CanvasScaler, TMP, RectMask2D) render and take input fine under IL2CPP. Drive them with manual input (legacy `UnityEngine.Input` plus `RectTransformUtility.ScreenPointToLocalPointInRectangle`) if you do not want to stand up an `EventSystem`. `GUI.Box`/`GUI.Label`/`GUI.DrawTexture` are known-safe for a passive overlay.

### The value-type reflection garbage bug

Reading a game `int`/`float`/`bool` field through `Il2CppSystem.Reflection` `GetValue` returns garbage: every int comes back as `807810400` (0x30203020). References and strings are fine; value types are not. Two mods hit this independently, and the in-game probe reproduced it head-to-head on v1.4.2(204): `MarketShoppingCart.m_MaxItemCount` (correct wrapper-property read `8000000`) and `EmployeeManager.MAX_RESTOCKER_COUNT` (correct read `6`) **both** came back as exactly `807810400` through `Il2CppSystem.Reflection`, while the wrapper-property reads were right. (A web pass doubted the mechanism; the in-game reproduction settles it — the workaround below is required, not optional.) Fixes:

- Prefer wrapper **properties**, which marshal correctly, over raw reflective field reads.
- If you must read a raw value field, resolve the managed wrapper type via `AccessTools.TypeByName(il2cppType.FullName)`, re-wrap the pointer through the wrapper's `IntPtr` constructor, then read with plain .NET reflection. Or read the field straight off `Il2CppObjectBase.Pointer + offset` with `Marshal.ReadInt32`.

Separately, `AccessTools.TypeByName` with a short name is collision-prone. `"Input"` resolved to BepInEx's own `BepInEx.Unity.IL2CPP.UnityEngine.Input` rather than `UnityEngine.Input`, and `"Locale"` is ambiguous with a global game `Locale`. Short names are only safe for the game's global-namespace types; fully qualify everything else, or verify the returned type's assembly.

### Object construction

Build game objects with `new GameType()` then assign fields, and verify a default constructor actually exists. Object initializers and some wrapper constructors do not always bind. ScriptableObjects split by case: clone an existing one with `Object.Instantiate(sample)` when you want to inherit its scripts, colliders, and grid (products, furniture, restocker SOs); use `ScriptableObject.CreateInstance<T>()` for a blank one (licenses). Note that `CreateInstance` does not populate reference fields, which is the root of the `LocalizedName` NRE in Part III.

### Asset lifetime, the works-once-then-breaks-after-reload trap

`IDManager`, `ProductLicenseManager`, `ProductAtlasManager`, and friends are per-scene objects. A new instance appears on every save load (confirmed by instance-id deltas). Two consequences, both of which produce "fine the first time, broken after a reload":

1. Any Unity object you create (ScriptableObject, Sprite, Mesh, Material, Texture2D) held only by a managed dictionary is invisible to Unity's asset GC. The game runs `Resources.UnloadUnusedAssets()` once after a scene settles and destroys it. Set `obj.hideFlags |= HideFlags.DontUnloadUnusedAsset` (or `HideAndDontSave`) at creation. An object referenced by a live component (for example a texture held by an active camera's RenderTexture) survives without the flag, because the component roots it.
2. Every registration into a manager list or dictionary must be re-applied per scene, not once per process. Gate your injection on the manager's `GetInstanceID()` changing, and re-run it when it does.

A destroyed SO is treacherous to detect: Unity `== null` reports it dead, but managed field reads (`.ID`, `.Products`) and already-baked UI keep working while native null-checks silently drop it. Trust the hideFlags, do not rely on catching the symptom.

One disagreement between the mods worth noting: Custom Products Loader was bitten using `HideFlags.HideAndDontSave` around save/load and now prefers plain `DontDestroyOnLoad` where the host can exist during a save, while SuperRestockers uses `HideAndDontSave` on its host with no issue. The safe reading: use `DontUnloadUnusedAsset` for runtime-created assets, and be deliberate about which flag you put on a host GameObject that must survive a save.

### URP runtime quirks

- A URP camera renders to a `RenderTexture` by auto-render: set `cam.targetTexture` and `cam.enabled = true`. Calling `Camera.Render()` explicitly produced nothing in this build. Use a dedicated isolated culling layer (`cam.cullingMask = 1 << layer`), and avoid layer 31 (its mask is the sign bit, which the URP renderer can drop). `RenderTextureFormat.ARGBHalf` and a Custom-mode `ReflectionProbe` give correct metallics.
- `Material.SetFloat("_Cull", n)` at runtime on a URP/Lit transparent material has no visible effect; transparent shells double-blend regardless.
- Custom-layer gizmos need the camera's `cullingMask` bit set for that layer or they do not draw.
- A runtime IMGUI `Texture2D` is eaten by `Resources.UnloadUnusedAssets` on scene reload unless you give it `HideFlags.DontUnloadUnusedAsset` / `HideAndDontSave` (not `DontDestroyOnLoad`, which is for GameObjects).

### NavMesh stripping

Managed-code stripping removes part of the NavMesh API — but the stripped set is **build-specific (a managed-stripping config artifact, not an inherent IL2CPP trait), so re-verify after any update.** On v1.4.2(204) a live reflection sweep of the interop wrapper found only `NavMesh.CalculateTriangulation` **absent**; `GetSettingsCount` and `GetSettingsByIndex` **are present** in the wrapper (an earlier SuperStructure probe reported those two stripped — either a different stripping config or a runtime-call failure, since wrapper-metadata presence does not guarantee the native body is callable: confirm by calling, not just by reflecting). Present and proven callable in game: `SamplePosition`, `CalculatePath`, `FindClosestEdge`, `GetSettingsByID`, `AddLink`, `AddNavMeshData`, `RemoveLink`, plus `NavMeshBuilder.BuildNavMeshData`. Agent type 0 is radius 0.20, height 1.70, climb 0.25, slope 45.

### A few more that throw or crash

- `Cubemap.SetPixels` with a `Color[]` hard-crashes the process. Use `SetPixelData<byte>`.
- `Physics.ComputePenetration` throws on a non-convex collider pair. Wrap it and fall back to an AABB result.
- `Input.GetAxis("Mouse X" / "Mouse Y")` throws `ArgumentException` if the game's InputManager does not define that axis. Probe once, cache the result, and fall back to a frame-to-frame `Input.mousePosition` delta.

---

## Part III, The verified game-API surface

The type catalog. Organized by subsystem. Within each entry: how to get the instance, the members that matter (with provenance), the patch recipes that work, and the traps.

A recurring pattern that underpins almost everything: the registry-append. `IDManager` holds a `List<XSO>` field (`m_Products`, `m_Furnitures`, `m_Restockers`, `m_Sections`, ...) plus an `XSO(int id)` lookup method. To add content you append a cloned SO to the list (and re-append per scene), and you postfix the lookup to fill misses for your ID range. The game's own UI then builds itself from the list.

### 1. Registry and identity

#### IDManager  (global, `NoktaSingleton<IDManager>`)

The master catalog of every ScriptableObject in the game. The injection point for custom content, and the sentinel mods use to detect "world is loaded."

Access: `IDManager.HasInstance ? IDManager.Instance : null`. Mods use `Instance.GetInstanceID()` as the world-identity token to detect load, reload, and per-scene re-injection.

Members:

- `m_Furnitures : Il2CppSystem.Collections.Generic.List<FurnitureSO>` field, the furniture registry. Append a `FurnitureSO` and the item appears on the Furnitures page and resolves via the lookup. [shipped]
- `m_Products : List<ProductSO>` field, `m_ProductLicenses : List<ProductLicenseSO>` field, `m_Sections : List<SectionSO>` field, `m_StorageSections : List<StorageSO>` field, `m_Restockers : List<RestockerSO>` field, plus the parallel `m_Cashiers`. The per-type registries. [shipped] [sig]
- `m_ProductSODictionary : Dictionary<int, ProductSO>` field. An id-to-SO fast lookup that also acts as a native GC root keeping product SOs alive. Inject custom products here too, or they get unloaded. [probed]
- `FurnitureSO(int id) : FurnitureSO`, `ProductSO(int id)`, `ProductLicenseSO(int id)`, `BucketSo(int id)`, `FloorSO(int id)` (note: method name `FloorSO`, return type `FloorSo`), `SectionSO(int id)`, `StorageSO(int id)`, `RestockerSO(int id)`. Per-ID lookups. **Each returns null for an unknown ID, no throw** (probed with 1000 and 424242). [shipped] [probed]

Patch recipe, fill the lookup for your range:

```csharp
[HarmonyPostfix, HarmonyPatch(typeof(IDManager), "FurnitureSO")]
static void Postfix(int id, ref FurnitureSO __result)
{
    if (__result == null && IsMine(id))
        __result = BuildOrGet(id);   // only fill MISSES, never shadow real game content
}
```

The dynamic-target form is more update-proof: a `[HarmonyPatch]` with `TargetMethods()` scanning `typeof(IDManager).GetMethods()` for a method returning a type whose name contains `ProductSO` (and not `License`) with one `int` parameter. That survives a renamed getter.

Gotchas: `m_Furnitures` is an Il2Cpp list, so index it (`Count` plus `this[int]`), never LINQ. Null-check each entry with Unity `== null` before touching `.ID`, because a fake-null wrapper throws on member access. Append per scene (gate on `GetInstanceID()`), since the manager is per-scene.

#### The ScriptableObject families

All share `ID : int`, a `LocalizedName : UnityEngine.Localization.LocalizedString`, and a `name : string`. Build a custom one, set its fields, append it to the matching `IDManager` list.

**FurnitureSO** (`: ScriptableObject`). Build with `CreateInstance<FurnitureSO>()`. Members: `ID`, `name`, `Cost : float`, `IsMainFurniture : bool` (set true to show on the Furnitures page), `BoxSize : BoxSize` (the full enum on v1.4.2(204) is `_8x8x8, _20x10x7, _20x20x10, _20x20x20, _30x20x20, _15x15x15, _40x26x26, _22x22x8, _8x8x8_Bakery, _20x10x7_Bakery, _15x15x15_IceCreamFlavour` — 11 values, not the 3 a draft of this guide listed), `FurnitureIcon : Sprite`, `FurnitureColor : Color`, `LocalizedName`, `FurniturePrefab : GameObject`. [shipped] Two traps:
  - `LocalizedName` is left null by `CreateInstance`, and the game's `LocalizationManager` rebuild dereferences every furniture's `LocalizedName` during its `Awake`. A null there throws an NRE that aborts the entire vanilla name rebuild and silently drops every mod's localization postfix. Copy a base furniture's table-backed `LocalizedString` reference into your SO (reference copy, do not construct one). [probed]
  - There is no `_20x20x10` furniture delivery box. `DeliveryManager.Delivery` NREs whenever a cart holds a `_20x20x10` decor item. Map large decor to `_15x15x15` for the delivery box only. [probed]

**ProductSO** (`: ScriptableObject`). Build custom ones by cloning a real product (`Object.Instantiate(baseProduct)`) so you inherit scripts and grid. Key economy fields: `BasePrice : float` (the per-box wholesale cost the player pays; this replaces the old `BoxPrice`), `MinDynamicPrice`, `MaxDynamicPrice`, `OptimumProfitRate : float` (markup percent: market selling price equals `round(CurrentCost * (1 + OptimumProfitRate/100), 2)`), `MaxProfitRate`, `ProductAmountOnPurchase` (units per box), `Category`, `ProductDisplayType`, `IsBakeryProduct`, `CanPlaceVending`, `ProductIcon : Sprite`, `AtlasIndex : int` and `AtlasPosition` (the icon atlas cell), plus `ProductName : string` and `ProductBrand : string` (the display name and brand). [shipped] [probed]
  - About `BoxPrice`: the wrapper still exposes a `public virtual float BoxPrice` getter, but treat it as gone and use `BasePrice`. There is no `Cost` or `PurchaseCost` field on `ProductSO`; the box price is computed (`BasePrice * ProductAmountOnPurchase`). [probed]
  - Critical: `BasePrice`, `MinDynamicPrice`, `MaxDynamicPrice` are purchase (wholesale) cost values, not selling prices. There is no selling-price bound anywhere on `ProductSO`. A pricing mod that clamped selling prices into `[MinDynamicPrice, MaxDynamicPrice]` priced everything at or below cost. [probed]

**ProductLicenseSO** (`: ScriptableObject`). Build with `CreateInstance`, copy all members from a sample except `ID`/`Products`/`LocalizedName`/`name`, then set: `Products : List<ProductSO>` (the license's product set), price and required-level fields. [shipped]

**SectionSO** (`: ScriptableObject`, store-size upgrades). `ID`, `Cost : float`, `LocalizedName`, `RequiredStoreLevel : int`, `RequiredSectionID : int`, `DailyRentAddition : float`, `SectionName : string` getter. Catalogued in `m_Sections`. Vanilla holds 32 (IDs 1..32, costs 350 up to 300000). [sig] [probed]

**RestockerSO** (`: ScriptableObject`, employee definitions). `ID`, `RestockerName : string`, `DailyWage : float`, `HiringCost : float`, `RackGoalToUnlock : int`, `RequiredStoreLevel : int`, `RestockerPrefab : Clerk`. Vanilla holds 6 (IDs 1..6, all wage 90). Clone with `Object.Instantiate(template)`, set `hideFlags = DontUnloadUnusedAsset`, then overwrite `ID`, `RestockerName`, `RestockerPrefab`. Never mutate a vanilla SO. [shipped] [probed]

**BucketSo** (`__Project__.Scripts.WallPaintSystem`) and **FloorSo** (`__Project__.Scripts.FloorPaintSystem`). Note the lowercase trailing `o` in the type names. Each has a `Cost : float`. Returned by `IDManager.BucketSo(int)` / `FloorSO(int)`. [shipped]

#### ID ranges and the caps that crash

The single most important content-modding fact: the game's own load, spawn, and settings paths are ID-capped, and feeding them an out-of-range modded ID either crashes the process or wipes a subsystem. Each content type needs a chosen high ID band that the mod owns end to end.

- **Furniture / decor: 760000 and up.** Expansion items have no `FurnitureSO` in the vanilla catalog, so the vanilla cart prices them at 0 or throws. SuperDecor uses the band `[760000, 999998]` and prefixes the cart math to handle them. No third-party mod registers furniture in this band (verified by a runtime ID scan), so it is clear.
- **Products: 90000 and up** (Custom Products Loader uses a 95xxx/98xxx band, with `IsProbablyCustomIdRange(id) = id >= 90000`). Custom products must be in `m_AllProducts` before purchase or the market cannot resolve them.
- **Restockers: 1000 and up.** This one is the cautionary tale. With the vanilla cap (`MAX_RESTOCKER_COUNT = 6`) left in place, `EmployeeManager.LoadData` declares a save with a modded ID corrupted, drops the IDs, and hard-crashes about two seconds later. Raise the cap past the check and LoadData accepts the IDs, natively spawns them, and the process hard-dies during load. The conclusion SuperRestockers reached: never write `MAX_RESTOCKER_COUNT`; strip modded IDs out of the data the native LoadData sees, and own the modded employee lifecycle entirely (the "shadow-hire" architecture in section 6).
- **Sections: above 32.** Whether `SectionManager.LoadUpgrade` clamps or breaks on a saved level above 32 loaded without the mod is unconfirmed, and the restocker precedent says assume it wipes. SuperStructure keeps the vanilla store-level int at 32 or below and rides a sidecar file.

The general lesson: vanilla counts are authoritative to vanilla. When you add beyond a cap, keep the native data structures vanilla-shaped and carry your additions in your own state, reconciling per scene.

### 2. Furniture, decor, and placement

This is the richest subsystem, mapped almost entirely by runtime probing because none of the placement logic is readable.

#### FurnitureManager  (global, `NoktaSingleton<FurnitureManager>`)

- `Awake()`, the init entry. The Mono build's `Start()` was renamed to `Awake()`; `Start` no longer exists. [shipped]
- `LoadFurnitureDatas()` private, restores saved furniture on load. **Throws an NRE when a saved expansion ID resolves to a null spawn result** (the native loader dereferences the null). Guard it with a finalizer that returns null, and scrub orphaned expansion entries out of `m_FurnituresDatas` before the game iterates so a missing pack drops only its own items. [probed]
- `m_FurnituresDatas : List<FurnitureData>` field, the saved-furniture list. [shipped]

#### FurnituresViewer  (`: MonoBehaviour`, not a singleton)

The Furnitures buy page. Resolve via `FindObjectOfType<FurnituresViewer>()`.

- `Start()` and `SpawnFurnitures()`, the page build methods. **`SpawnFurnitures` appends buttons to the container, it does not clear first** (verified by watching the tile count double from 47 to 94). Calling it on a not-yet-initialized viewer NREs. [probed]
- `m_ProductsContent : Transform` field, the button container. Enumerate with `childCount` and `GetChild(i)`. [shipped]
- `m_FurnitureSalesItemPrefab : FurnitureSalesItem` field, the tile prefab (present even before the grid builds). [shipped]
- `ShoppingCart : MarketShoppingCart` property (and `m_ShoppingCart` field fallback). [shipped]

To force a rebuild, clear the container first (since the game appends), and skip the rebuild entirely on a viewer that has not done its own initial spawn. Gate on the per-instance `GetInstanceID()` you have observed spawn, not on "container is empty" (a forced clear leaves the same empty state) and not on a session-global bool (a scene reload makes a new viewer instance you must treat as unobserved). For the clear, use `Object.DestroyImmediate` iterating from the end, since the rebuild runs synchronously right after and a deferred `Destroy` leaves stale buttons. Guard your `SpawnFurnitures` postfix against re-entrancy: your integration can request a refresh that re-runs `SpawnFurnitures` and re-enters the postfix without a guard.

#### FurnitureGenerator  (global, `NoktaSingleton<FurnitureGenerator>`)

- `SpawnFurniture(int id, Vector3 position, int furnitureBoxInteractionViewId = -1) : GameObject`. **This is the method whose signature changed in a game point release** (it was a 2-arg `(int, Vector3)`), which aborted the whole `PatchAll`. Specify the exact `Type[]` overload. [shipped]
- `SpawnFurniture(int, Vector3, Quaternion, Transform) : GameObject`, the load/rotation overload. **Returns null for expansion FurnitureSOs**; base-game furniture spawns fine. The reload fallback instantiates the framework's own prefab and assigns `ref __result`. [probed]

#### FurniturePlacer  (global, `NoktaSingleton<FurniturePlacer>`)

The per-frame placement driver, and the source of the most load-bearing finding in the whole decor system.

- `m_PlacingMode : bool` field, true while placing; the placement gate everything reads. [shipped]
- `m_CurrentPlacingMode : FurniturePlacingMode` field, the active placing-mode object. [shipped]
- `m_OwnPlayerInstance : PlayerInstance` field, the path to the player chain. [shipped]
- `Update()`, the per-frame driver (multiple prefixes, see ordering below). [shipped]
- `StartPlacingMode()`, `Awake()`, `MoveFurniture(Vector3)`, `BoxUp()`, `Rotate(bool clockvise)` (note the game's misspelling, and that this overload takes a bool where the Mono one was parameterless), `StopPlacingMode()` (public, the real exit-placement method), `PlaceFurniture()` (returns void in this build, not bool). [shipped]

`Update` prefix ordering, four mods patch it; coordinate by priority and flags:

1. The placement-mode reader runs first at `[HarmonyPriority(Priority.First)]`, as the sole reader of mode-toggle input and lifecycle force-exit.
2. The rest run at normal priority and early-return while a higher mode is active to avoid double-binding input.

The `MoveFurniture` postfix that writes the final transform runs last at `[HarmonyPriority(Priority.Low)]`, so it can re-stamp over the normal-priority writers without editing their files.

**The onUse commit mechanism.** To end a held item's placement programmatically (deselect, exit), fire the player's use event: `playerInteraction.onUse.Invoke(true)`. This is the only thing that works, and the alternatives were tested exhaustively:

| Approach | Result |
|---|---|
| `PlaceFurniture()` | sets `m_PlacingMode=false` but leaves the coroutine and visual up; desyncs and breaks the player's own click-to-place |
| `StopPlacingMode()` | clears `m_PlacingMode` but leaves `m_CurrentPlacingMode` and the visual; menu stays dead |
| `PlayerInteraction.Interact(true/false)` | does not place |
| `HoldClickSimulate()` | picks up when hands are free; does not place while holding |
| set `m_Use = true` (field) | no effect; the game does not poll it for the place |
| `PlayerObjectHolder.CancelPlacingMode()` | no effect on the placer flag; caused a position flash |
| null `m_CurrentPlacingMode` | orphans placement, regression |
| **`PlayerInteraction.onUse.Invoke(true)` (the event)** | **works**: triggers the place, flips the placer true to false, re-arms Esc and the pause menu |

The event is non-destructive: it never touches `m_PlacingMode` itself, so if it does not place, the item stays placeable. Do not base your follow-up on "did I fire the event," base it on `!placer.m_PlacingMode` afterward, because the event can be a no-op. Related discovery: the pause menu will not open while a placement is live, and no single placement flag gates it; only the game's own place-completion (driven by that use event) re-arms it. [probed]

#### FurniturePlacingMode  (`: IPlacingMode`)

It is an `Il2CppSystem.Object`, not a `Component`, so the Mono `placingMode.GetComponentInParent<Furniture>()` is unavailable; resolve the furniture via the `m_Furniture` GameObject field and `GetComponent<Furniture>()`.

Fields: `m_Furniture : GameObject`, `m_PlacingTag : List<string>`, `m_PhysicalColliders : Il2CppReferenceArray<Collider>`, `m_AllColliders : List<Collider>`, `m_PlacingCollider : Collider`, `m_PlacedOnCorrectSurface : bool` (settable), `m_Renderers : Il2CppReferenceArray<Renderer>`, `m_DefaultMaterials : Dictionary<Renderer, Il2CppReferenceArray<Material>>`, `m_Triggers : List<Collider>` (the overlap hit set), `m_Objects : Il2CppReferenceArray<NavmeshBuildSourceObject>`. [shipped] [sig]

The placement-validity and hologram pipeline (on the base `IPlacingMode` unless noted):

- `get_AvailablePosition() : bool`, a virtual property `FurniturePlacingMode` overrides (vanilla returns `m_Triggers.Count == 0`). Patch the derived type here. [shipped]
- `OverlapBoxes()`, protected virtual on the base, runs `Physics.OverlapBox` per frame into `m_Triggers`. **Patch the base `IPlacingMode`, not the derived type** (targeting the derived type fails to resolve and aborts `PatchAll`); virtual dispatch means the base patch catches the derived call. [shipped]
- `PlacingMode(bool, Material)`, the hologram material swap. [shipped]
- `SetAvailabilityColor`, `UpdateHologramColor`, `UpdateClientHologramColor` (the multiplayer-client twin), `TogglePlacingMaterial`, and the `set_HologramColor` setter, the green/red overlay pipeline. [shipped]

Placement validity is surface-tag based: `m_PlacingTag` versus the surface tag under the hologram sets `m_PlacedOnCorrectSurface`. Racks want the `Storage Floor` tag, shop furniture wants `Floor`. To suppress the tint for an item whose renderers wear real materials (a decor item), prefix all five hologram methods to return false. But remember a foreign mod's postfix runs even when your prefix skipped the original, so to fully block a green tint you skip `set_HologramColor` itself, not just the compute method. [probed]

#### Furniture  (`: MonoBehaviour`)

- `ID : int`, the furniture ID the expansion gate reads. [shipped]
- `Data : FurnitureData` property and `set_Data(FurnitureData)` setter. The place path triggers the setter; prefix it to capture rotation and position into save data. Be careful: the load path also calls `set_Data` with the saved position while the transform is still the prefab default, so only write live transform values when actually placing, not loading. [shipped]
- `Despawn()`, fires when an item is removed (sold or trashed); prefix it to purge per-instance caches. [probed]
- `PlacingMode : FurniturePlacingMode` property. [shipped]

Cache trap: use the owning GameObject's identity as the cache key, not just `GetInstanceID()`. Unity recycles instance IDs, so a fresh unbox can reuse a destroyed item's ID and falsely "recover" its data. Compare a live-object reference, not a stored ID.

#### FurnitureSalesItem

A buy-page tile. Found as a child of `m_ProductsContent`, or cloned from `m_FurnitureSalesItemPrefab`.

- `m_ProductId : int` field. [shipped]
- `AddToCart()`, the proven way to add an item to the cart; on re-add it bumps an existing `ItemQuantity.FirstItemCount` rather than appending. [probed]
- `Setup(int id, FurnituresViewer viewer)`, re-points a cloned tile at an ID. [probed]

Hide a tile with `SetActive(false)`, never `Destroy`. The game keeps references to the tiles for cart sync and cleanup; destroying them leaves dangling references that break the vanilla cart button and crash on exit.

#### FurnitureBoxInteraction / FurnitureBox / FurnitureBoxData

The deliver-as-box and box-open path. Read these as direct public wrapper fields, not through `Traverse`.

- `FurnitureBoxInteraction.m_Box : FurnitureBox`, `m_InAnimationTransition : bool`, `m_ScaleUpSpeed : float`, `CheckFurnitureType(GameObject, FurnitureBoxData)`, `OpenBox()`. [shipped]
- `FurnitureBox.Data : FurnitureBoxData`, `OpenBox()`, `FurnitureSpawnPosition : Vector3`. [shipped]
- `FurnitureBoxData.FurnitureID : int`. [shipped]

When you prefix `OpenBox` or `CheckFurnitureType` and take over, use an "owns this operation" latch so that once side effects begin, a later exception cannot let the original re-run and double-spawn. The DOTween scale-up continuation needs `DelegateSupport.ConvertDelegate<TweenCallback>((Action)OnComplete)`; a raw managed lambda often no-ops.

#### ProductAtlasManager  (global, `NoktaSingleton<ProductAtlasManager>`)

Drives the Furnitures-page and product icons.

- `SetFurnitureIcon(int furnitureID, MeshRenderer mRend)`, paints the icon quad with the atlased material for a stock ID. Expansion IDs are not in the atlas, so prefix and supply your own material from the item's sprite, returning false for your IDs. [shipped]

The product-icon atlas surface is covered in section 5.

### 3. Shopping, delivery, and money

#### MarketShoppingCart  (global, `NoktaSingleton<MarketShoppingCart>`)

The buy-from-computer flow. All members are public, no `Traverse` needed.

- `CartData : CartData` property; `CurrentShippingCost : float`; `ItemCountInCart : int`; `TooLateToOrderGoods : bool`. [shipped]
- `m_OrderTotalPrice : float` field (settable), `m_canPurchase : bool` field, `m_StorePointPerEachItemPurchased : int` field, `m_MaxItemCount : int` field — the buy-cart item cap, a **writable transient runtime field** (not persisted; confirmed settable in-game and restored on the same frame). [shipped] [probed]
- `Purchase(bool fromTablet)`, `UpdateTotalPrice()`, `GetTotalPrice() : float`, `UpdateAverageCosts()`, `CleanCart()`, `UpdateUI(bool hasMoney)`, `GetHasMoneyForPurchase() : bool` (new in this build), `GamePadControl(int index = 0) : Il2CppSystem.Collections.IEnumerator` (pass straight to `StartCoroutine`). [shipped]

Patch `Purchase`, `UpdateTotalPrice`, and `GetTotalPrice` as prefixes. The cart total is `sum(product.count * ProductSO.BasePrice) + sum(furniture.count * FurnitureSO.Cost) + sum(bucket.count * BucketSo.Cost) + sum(floor.count * FloorSo.Cost) + CurrentShippingCost`. The only term that changed from the Mono build is the product one (`BoxPrice` to `BasePrice`).

The hard-won ordering rule for a custom `Purchase`: deliver first, then charge. `DeliveryManager.Delivery` was throwing on expansion carts, and the old charge-then-deliver order left players charged with no goods. The safe order is deliver, set an "owns this transaction" latch, deduct money (essential, first), run nonessential side effects each in its own try/catch, and finally clean the cart. Once the latch is set, return false from the prefix even on a later exception so the original cannot double-charge.

You cannot build cart entries directly: `ItemQuantity` has no usable constructor and the cart has no `AddFurniture`. Add through `FurnitureSalesItem.AddToCart()`.

#### CartData / ItemQuantity

- `CartData` holds four `List<ItemQuantity>`: `ProductInCarts`, `FurnituresInCarts`, `BucketsInCarts`, `FloorBoxesInCarts`. [shipped]
- `ItemQuantity` exposes `FirstItemID : int` and `FirstItemCount : int` as fields, and has no usable constructor. [shipped]

#### DeliveryManager  (global, `NoktaSingleton<DeliveryManager>`)

- `Delivery(CartData)`, delivers a purchased cart. NREs on a `_20x20x10` decor item (no furniture box that size) and other malformed expansion carts. Deliver before charging and wrap it. [probed]

#### MoneyManager  (global, `NoktaSingleton<MoneyManager>`)

- `HasMoney(float amount) : bool`. [shipped]
- `MoneyTransition(float amount, TransitionType type, bool updateMoneyText = true)`, the deduct/credit call. **A positive `amount` credits, a negative `amount` deducts** (in-game probe: a `+500` then `-500` round-trip moved the balance `50 → 550 → 50` exactly). **`UpdateMoneyText` is gone**; the third parameter (pass true) refreshes the money UI instead. [shipped] [probed]
- `Money : float` property (get/set), `onMoneyTransition : Action<float, TransitionType>`.
- `TransitionType` enum: `NONE, CHECKOUT_INCOME, SUPPLY_COSTS, UPGRADE_COSTS, RENT, BILLS, LOAN_INCOME, LOAN_PAYMENT, STAFF, FURNITURE_SALE, CUSTOMIZATION, FURNITURE_SELL, GAS, VENDING_MACHINE` (the full member set confirmed verbatim on v1.4.2(204)). Products book as `SUPPLY_COSTS`; `FURNITURE_SALE`/`FURNITURE_SELL` are the **sell** side, but the **cart buy path for furniture books as `UPGRADE_COSTS`** (SuperDecor); buckets and floors as `CUSTOMIZATION`, store upgrades as `UPGRADE_COSTS`. [sig] [shipped]

#### InventoryManager  (global, `NoktaSingleton<InventoryManager>`)

- `RemoveBox(FurnitureBoxData)`. [shipped]
- `OnPurchaseCompleted : Action<bool>` and `OnInventoryChanged : Action`, engine `Action` fields you only ever invoke (null-guard then `.Invoke(...)`), never assign a managed handler to, so no bridge is needed. [shipped]

#### StoreLevelManager  (global, `NoktaSingleton<StoreLevelManager>`)

- `AddPoint(int)` and `RemovePoint(int)` (plus `*Order` multiplayer twins), `onLevelChanged : Action<bool>` (bool is "increased"), `CurrentPoint : int`, `CurrentLevel : int`, `NextLevelRequirement : int`, `m_LevelRequirements : Il2CppReferenceArray<LevelRequirement>`. A purchase that awards store progress calls `AddPoint`. [shipped] [sig]

#### OnboardingManager / InteractionHintsManager

- `OnboardingManager` (`NoktaSingleton`): `Step : int`, `NextStep()`. [shipped]
- `InteractionHintsManager` (`NoktaSingleton`): `Clear(PlayerInstance)`, clears the "Click to Place / C to box" overlay that a code-path box-up does not remove naturally. [shipped]

### 4. Pricing

The selling-price economy, mapped by SuperPricer. There is exactly one player-set price per product; shelf tags, vending tags, and the scanner all read the same value.

#### PriceManager  (global, `NoktaSingletonPunCallbacks<PriceManager>`)

- `m_PricesSetByPlayer : List<Pricing>` field, the live list of player prices (only player-priced products have a row). This is the write target: iterate it and set each row's `.Price`. [shipped]
- `m_CurrentCosts : List<Pricing>` field, per-product cost records.
- `CurrentCost(int productID) : float`, the wholesale cost basis. [sig]
- `GetPriceSetByPlayer(int productID) : Pricing`, `GetPrice(int productID) : Pricing`. [sig]
- `ChangeCurrentCost(int productID)`, a recompute trigger SuperPricer deliberately does not call. [sig]
- `CreatePricingData()` (new-game seed) and `LoadPricingData()` (load-game). **Custom Products Loader patches both; a pricing mod must not.** Wait a few seconds after `HasInstance` so these finish populating `m_PricesSetByPlayer` before your first pass. [sig] [shipped]

Derives from a Photon callbacks chain, so it pulls in `PhotonUnityNetworking` and `PhotonRealtime` references. Gate writes to single-player (the `Main Scene`).

#### Pricing  (global, `Il2CppSystem.Object`, not a MonoBehaviour)

The per-product price record.

- Writable fields: `ProductID : int`, `Price : float` (the value you write), `LastChangeDate : int` (stamp the current day on write; do not clobber a valid date with -1), `DiscountRate : int`. [shipped] [sig]
- Computed getters (native calls, wrap each in try/catch): `IsDiscounted : bool`, `AvgCost`, `PreviousCost`, `CurrentCost`, `MarketPrice`, `Profit`, `SellingPrice`. Verified identities (in-game probe, four live products): `CurrentCost == AvgCost == PriceManager.CurrentCost(id)` **always**; `SellingPrice == Price` (the player's set price); `MarketPrice` is the **optimum reference** (`round(CurrentCost * (1 + OptimumProfitRate/100), 2)`), so it equals `Price` only when the player priced at the optimum and diverges otherwise (observed `Price 6.28` vs `MarketPrice 5.98` on an off-optimum row). [probed] [sig]
- Constructors: `Pricing()`, `Pricing(int id, float price)`, `Pricing(int id, float price, float previousPrice, int lastChangeDate)`. Seed a missing row with `new Pricing(id, price)`. [shipped]

A bare `Price` write bypasses the game's broadcast, so shelf tags, vending tags, and an open pricing computer stay visually stale until you refresh each one separately (below). Confirmed in-game (v1.4.2(204)) for **both** a shelf and a vending slot: after a bare `Price` write, the cached `m_CurrentPrice` (on `DisplaySlot` and on `VendingSlot` alike) stayed at the OLD value until that slot's `SetPriceTag()` was called, which updated it. [probed]

#### DayCycleManager  (global, `NoktaSingleton<DayCycleManager>`)

The economy's primary recompute hook.

- `OnStartedNewDay : Action` settable field, the new-day hook (confirmed firing on day advance). Subscribe with the bridged-delegate idiom from Part II, re-subscribing when the instance pointer changes. [shipped] [probed]
- `StartNextDay()`, the day-start method; a postfix here also fires on day advance (a proven fallback hook). [probed]
- `CurrentDay : int` (read it for the `LastChangeDate` stamp), `NumberOfDaysSinceLastPriceChange : int`. [sig]

#### Refresh targets after a price write

- **DisplaySlot** (`: MonoBehaviour`), a shelf price-tag slot. Find the live ones with `FindObjectsOfType<DisplaySlot>()` filtered by `HasProduct`, grouped by `PeekProductSO().ID`. Call `SetPriceTag()` to refresh (verified it re-reads the new value). `PricingChanged(int productID)` is inert for the value; call it first only for other bookkeeping. `m_CurrentPrice : float` is the cached displayed value. **For the price-tag refresh path, do not rely on `DisplayManager.GetDisplaySlots` as the slot source — SuperPricer's probe got 0 here** (which is why an early build wrote every price but refreshed zero tags); use the `FindObjectsOfType` scan. (Note: the RDC spike later found `GetDisplaySlots(id, false, list)` *does* return the assigned-slot count on v1.4.2(204) — see the Negative-results note; the call is context-dependent.) [shipped] [probed]
- **VendingSlot** (`: MonoBehaviour`, a separate type from DisplaySlot). Find with `FindObjectsOfType<VendingSlot>()` grouped by `ProductID` — **skip non-positive IDs: a `ProductID` of 0 (and negatives) is an empty/unassigned slot, and a stocked machine still has empty slots reading 0** (confirmed in-game). Like `DisplaySlot`, it caches the displayed price as `m_CurrentPrice : float` (and exposes a `Price : float`). Refresh with `SetPriceTag()` — **runtime-confirmed directly** (in-game, product 24 in a vending machine): a bare `Pricing.Price` write left `VendingSlot.m_CurrentPrice` stale at 2.793, and `VendingSlot.SetPriceTag()` then updated it to the new value — identical to the shelf path. [shipped] [probed]
- **VendingPriceTag** (`: PriceTag : MonoBehaviour`): `SetPrice(float)` override; drive it through `VendingSlot.SetPriceTag()` rather than directly. [sig]
- **ScannerDevice**: needs no refresh, it re-reads on each scan. `SelectedProductID : int`, `UpdateScannerScreen(int)`. [sig]
- **PricingProductViewer** (`NoktaSingleton`), the in-store pricing computer, a separate UI that caches its rows. Null until the computer is first opened this session, so gate on `HasInstance`. `RefreshUnlockedProducts(int productID)` re-renders one row. `UpdateUnlockedProducts(int licenseID)` (single int) and `OnCostsChanged(List<int>)` also exist (both signature-confirmed on v1.4.2(204)) — but CPL deliberately leaves these two unpatched, since the pricing rows refresh natively. [shipped] [sig]
- **PricingItem**, one row on that computer. `SetPrice(string input)` is the player's manual entry (patch it as a postfix to stamp the change date). `PricingData : Pricing` property, `UpdateLastChangeDate(Locale = null)`, `UpdateProfit(float = -1)`. **Do not call `Setup(Pricing)` to refresh a price**; it writes the row's `.Price` (the selling price) into the Current Cost column. [shipped] [probed]

#### Negative results worth knowing

- `DisplayManager.GetDisplaySlots(int productID, bool, List<DisplaySlot>)` is **parameter/context-dependent — two probes disagree.** SuperPricer's probe got 0 (not a usable price-tag refresh source) [probed]. The RDC Stock Manager in-game probe on v1.4.2(204) called `GetDisplaySlots(id, false, list)` and it **returned the assigned-slot count and filled the list** (e.g. Oil → 21, incl. empty facings = the shelf capacity) [probed]. A third probe (a later in-game pass, a different save) got **0** for six product IDs while 16 `DisplaySlot`s existed in-scene — so the 0 correlates with the product not being currently shelf-assigned in that save, not with a broken method [probed]. The `bool` arg / call context / current shelf assignment decide it — verify the return for your exact call; `FindObjectsOfType<DisplaySlot>()` is the always-safe fallback.
- `PriceEvaluationManager.PurchaseChance(int)` exists but `HasInstance` is false in single-player and the type has no callers in `Assembly-CSharp`; it appears to be multiplayer or simulation only. A demand signal is not sourceable in single-player.
- There is no `AutoPriceUpdater` game type. That name belongs to an old Mono Nexus mod whose behavior SuperPricer re-implements; the game's own recompute path is `PriceManager.ChangeCurrentCost` / `CreatePricingData` / `LoadPricingData` driven by the day cycle.

### 5. Licenses, products, and the icon atlas

Custom Products Loader's domain: register new products and licenses, let the native pipeline build all the UI, and re-assert after every native rebuild. The model is custodian, not parallel authority.

#### ProductLicenseManager  (global, `NoktaSingleton<ProductLicenseManager>`)

Owns license and product unlock state. Per-scene, and its native rebuilds wipe hand-injected list entries, which is the central lesson here.

- Backing fields (`m_`-prefixed; all correctly spelled and confirmed present on v1.4.2(204)): `m_Licenses : List<ProductLicenseSO>`, `m_DefaultUnlockedLicenses`, `m_UnlockedLicenses : List<int>`, `m_ActiveLicenses`, `m_UnlockedProducts`, `m_ActiveProducts`, `m_AllProducts`, `m_DiscountedProducts : List<int>`, `m_DisabledProductsByLicense : Dictionary<int, List<int>>`. Custom products must be in `m_AllProducts` before purchase or the market cannot resolve them (a live read showed `m_AllProducts` = 522 with Custom Products Loader installed, `m_UnlockedProducts`/`m_ActiveProducts` = 6). [sig] [shipped] [probed]
- **Spelling trap — bind the `m_` fields, NOT the public convenience getters.** The public getters are mis-spelled in the game: `AllPoducts` (missing the `r`), and the British `UnlockedLicences` / `ActiveLicences` / `HasAllLicencesUnlocked` / `ClearAllLicences` / `onSlaveLicencesLoaded` (`c`, not `s`) — while `UnlockedProducts` / `ActiveProducts` / `DiscountedProducts` / `Licenses` are spelled normally. Binding the "obvious" `AllProducts` or `UnlockedLicenses` *getter* silently resolves to nothing; the `m_`-prefixed fields above are correctly spelled and are the safe target. (Confirmed by a member dump on v1.4.2(204).) [probed]
- `SuppressNetworkBroadcast : bool` property, set true around bulk native writes to suppress Photon broadcasts. [shipped]
- Events: `onLicensePurchased : Action<int>`, `onLicenseActivated : Action<int, bool>` (the signal the live UI rides), `onProductActivated : Action<int, bool>`, `onProductActivatedInLicense : Action<int, int, bool>`, `onSlaveLicencesLoaded : Action`. [sig]
- Methods: `PurchaseLicense(int)` (and `PurchaseLicense_Order` for multiplayer), `SetLicenseState(int licenseID, bool isActive)` (the toggle entry that rebuilds lists and fires `onLicenseActivated`), `SetProductState(int, int, bool)`, the network twins `ApplyLicenseState_FromNetwork(int, bool)` / `ApplyProductState_FromNetwork(int, int, bool)`, the `IsLicenseUnlocked/Active` and `IsProductLicenseUnlocked/Active` family, `UpdateUnlockedProducts()` (private, parameterless, the observed wipe point), `LoadAllProducts()` (private), `LoadSaveProgress()`, `ClearAllLicences()`. [sig]
- Gone from this build: `GetProductLicenseSO` (use `IDManager.ProductLicenseSO(int)`), `ProductListResult`. Guard legacy patches on these with a `Prepare()` that warns and skips.

Patch recipe shape: postfix the `IsLicense*` checks to return true for your custom IDs; inject on the lifecycle methods (`Awake`, `Start`, `LoadAllProducts`, `LoadSaveProgress`) via `TargetMethods()`; postfix `UpdateUnlockedProducts` with a reconciler that re-adds the custom entries the rebuild just dropped, before `onLicenseActivated` fires. The reconciler sets `SuppressNetworkBroadcast=true`, re-adds SOs to `m_Licenses` and the `IDManager`, and adds custom IDs into `m_AllProducts` always (and the unlocked/active lists if purchased), monotonically (never removing IDs it did not persist). Guard it against re-entrancy.

#### LicenseItem  (`: MonoBehaviour`), the license card

- `Setup(int licenseID)`, `Purchase()`, `SetActiveState(bool)` (drives `SetLicenseState`), `UpdateUIAfterPurchase(bool applyActiveState)` (the card's own purchased-UI builder, the method whose not-firing was the missing-toggle bug). [probed]
- UI geography: the activation switch is a `Button` (`Interraction Zone/LicenceSlider/Toggle_Switch/Handle/HandleImage`) with zero persistent listeners and `interactable=false`; wiring is code-side, not `Button.onClick`. License rows carry no uGUI `Toggle`. [probed]
- The recipe that works: prefix `Purchase` to reconcile custom state then let the original run (the game charges money and calls `PurchaseLicense` natively); postfix to call `UpdateUIAfterPurchase(true)` only if the purchase actually went through this save. The deleted failed approach hand-built the purchased UI and skipped the original (so the player was never charged).

#### ProductViewer  (`: MonoBehaviour`), the Market products page

- `SpawnUnlockedProducts()` (full tile build); `UpdateUnlockedProducts(int licenseID, bool active)` (per-license incremental, fires even while the viewer is inactive); `UpdateSingleProduct(int productID, bool active)`; `RefreshFilters()`. [sig]
- **Signature divergence trap:** `ProductViewer.UpdateUnlockedProducts` is `(int, bool)`, but `PricingProductViewer.UpdateUnlockedProducts` is `(int)`. Never assume symmetric overloads. A lookup for the `(int)` shape on `ProductViewer` returned null and silently killed an entire live-refresh path (the headline "toggle bug" root cause).

#### ProductAtlasManager  (global), the icon atlas

- `atlasedMaterials : List<Material>` (11 vanilla pages plus appended custom pages; the in-game census saw 14 with Custom Products Loader co-installed = 11 vanilla + 3 of CPL's shared pages, confirming the append scheme).
- `GetProductIcon(int productID, RawImage image)` sets both `image.texture` (from `atlasedMaterials[product.AtlasIndex]`) and `image.uvRect` (the `AtlasPosition` cell divided by 8). `ApplyLabelData(int, MeshRenderer)`, `SetLabelData(int, MeshRenderer)`, `SetFurnitureIcon(int, MeshRenderer)`. [probed]

Atlas facts:

- The vanilla `_Atlas` is a `Texture2DArray` (`DLCTextureArrayNew`) of fixed depth 36 (11 built-in pages plus 25 DLC slots). Cells are 8x8 of 128px on a 1024 texture, indexed `y*8 + x`. The label shader is `Shader Graphs/Furniture_Shader` with `_Atlas`/`_AtlasIndexMatrix`/`_IDMatrix`.
- The "icons revert past about 25 custom products" bug: one appended material per product reaches array depth 36 at the 26th and the shader clamps to a vanilla layer. The fix is shared pages, one appended page material holding 64 products, so `AtlasIndex` climbs once per 64.
- The game writes label state into the material instance, never a MaterialPropertyBlock (every dump showed an empty block). Material writes are the right target.
- `SetLabelData` indexes `atlasedMaterials[AtlasIndex]`; an out-of-range index throws inside `Box.Initialize`, so skip the vanilla body for out-of-list-range customs. The label-mask controllers (`LabelMaskController`, `RackLabelMaskController`, `VendingLabelMaskController`) each need a rebuild postfix; the vending one is easy to miss.

#### CPL identity facts

- Build custom products by cloning a vanilla `ProductSO` (to inherit scripts and grid) and shielding from scene unload; build licenses with `CreateInstance` and a member copy. Set `LocalizedName` non-null (same NRE risk as furniture) using a `LocalizedString { TableReference="Products", TableEntryReference="_custom_product_"+id }`.
- Custom IDs round-trip in the game save by int product ID. Per-save mirror text files are a recovery record, not cross-save authority; an early design leaked one save's purchases into fresh saves.
- ID collisions are currently a load-order-dependent skip-and-continue with a log line. A sticky-allocation scheme (a persisted `packId:authoredId` to effective-ID map, reserved before the load loop) is designed but unimplemented.
- Mods coordinate through a frozen static reflection surface (`CustomProductRuntime.CustomProductSOs` and friends, keyed by effective ID); other mods reflect in by exact name, so it cannot be renamed.

#### Localization the additive way

```csharp
StringTable table = LocalizationSettings.StringDatabase.GetTable("Products");
var entry = table.GetEntry(key);
if (entry == null) table.AddEntry(key, value); else entry.Value = value;
LocalizationManager.Instance.UpdateLocalization();
```

### 6. Employees and NPCs

#### EmployeeManager  (global, `NoktaSingleton<EmployeeManager>`), and the shadow-hire architecture

Hire, fire, and spawn for all staff, and the centerpiece of the most defensive design in any of these mods.

- `HireRestocker(int restockerID, float hiringCost)`, records the hire and charges money, but silently skips spawning IDs past the vanilla cap. [shipped] [probed]
- `FireRestocker(int restockerID)`, despawns a matching NPC. [shipped]
- `IsRestockerHired(int) : bool`, returns false for an unknown ID without throwing. [probed]
- `GetRestockerByID(int) : Clerk`, the live-NPC lookup — returns the spawned `Clerk` for a hired ID; SuperRestockers uses it as the "already alive?" guard. [shipped]
- `GetSpawnPosition(int restockerID) : Transform`, indexes `m_RestockerSpawnPositions` by `id-1` and **throws `IndexOutOfRangeException` for modded IDs** in vanilla. Prefix it: return true (run original) for vanilla IDs, and for modded IDs set `__result` to a planned spot and return false to avoid the throw. (Patch-sensitive: with SuperRestockers co-installed, its own prefix already handles modded IDs, so the in-game probe saw `GetSpawnPosition(1000)` return a planned `Transform` rather than throw — the vanilla throw stands only when no such prefix is loaded.) [probed]
- `LoadData(SaveManager.EmployeesData data)`, applies saved hires during world load (fires the same frame the managers appear, long before any UI). [probed]
- `HandleCorruptEmployeeData()`, vanilla recovery: on a save with an unregistered or over-cap restocker ID, it logs "Corrupted Restocker data detected. Clearing data." and clears the whole roster. [probed]
- `static int MAX_RESTOCKER_COUNT` writable field, vanilla 6. It caps the spawn path but not hire or persistence (10 hired produced exactly 6 NPCs). Siblings exist for every staff type (`MAX_CASHIER_COUNT`, `MAX_JANITOR_COUNT`, `MAX_BAKER_COUNT`, and so on). **Do not write it** (see below). [probed]
- `m_RestockerSpawnPositions : Il2CppReferenceArray<Transform>` (fixed length 6), `m_RestockersData : List<int>` (the live hired-ID list), `m_ActiveRestockers : List<Clerk>` (live NPC instances), `m_OccupiedProductsByRestockers : HashSet<int>` (work-claim coordination). [sig] [shipped]

The shadow-hire architecture exists because both vanilla load paths hard-fail on modded IDs (two instrumented crash sessions confirmed it). The design:

1. **LoadData prefix:** strip and stash every modded ID (1000 and up) and its management data out of the incoming data, so native LoadData only ever sees vanilla-shaped data. Reverse-iterate when removing from the list. Reset the mod's shadow state at this seam (not on a separate poll, which could run late and wipe the spawn queue).
2. **LoadData postfix:** re-add the kept modded IDs to the live `m_RestockersData`, restore the stashed management data into the live store, and queue NPC spawns.
3. **HireRestocker / FireRestocker postfixes:** for modded IDs, ensure the store entry and queue or cancel a shadow spawn.
4. **Shadow spawn** (from the mod's host, staggered two per quarter-second so any failure pinpoints the exact spawn): `Object.Instantiate(RestockerSO.RestockerPrefab)` at the prefix-mapped spawn position, set `Clerk.EmployeeId`, initialize the sibling `Restocker` brain, wire the shared management data, and append to `m_ActiveRestockers`.

Two governing rules learned the hard way: never write `MAX_RESTOCKER_COUNT` (raising it past the check is what hard-crashed the load), and the configured employee count is authoritative (register exactly the configured count and auto-release saved hires above it; a "save floor" that kept extra hires left 96 NPCs drawing wages with no card to fire them).

#### The restocker support types

- **RestockerManagementData** (`Il2CppSystem.Object`), per-restocker task permissions: `RestockerID : int`, `IsActive`, `UseUnlabeledRacks`, `PickUpBoxGround`, `DropEmptyBox`, `RemoveLabelRack`, `RestockShelf`, `RestockFromVehicles` (all bool). Constructor `RestockerManagementData(int restockerID, bool isActive = true, ...)`. One shared instance per ID must be converged across the store, the card, the clerk, and the Restocker brain (compare `.Pointer` and insert into the live store); skipping the store-restore is exactly how a toggle silently reset on reload. [sig] [shipped]
- **RestockerManager** (`NoktaSingleton`): `SetRestockerManagementData(RestockerManagementData)`. Prefix it: for vanilla IDs run the original, for modded IDs route through the mod and return false even on exception, because the vanilla body NREs for modded IDs (its `FirstOrDefault` predicate dereferences a `Clerk` that resolves null). [shipped] [probed]
- **Clerk** (`SupermarketSimulator.Clerk`, alias it): the NPC body. `EmployeeId : int` (virtual, set on a spawned clone), `SetRestockerManagementData(...)`, `LoadRestockerManagementData()`. Spawn by `Object.Instantiate(prefab, pos, rot)` then set `EmployeeId`. [shipped] [sig]
- **Restocker** (global), the task-AI brain, a separate component not on the Clerk's own GameObject (use `GetComponentInChildren<Restocker>(true)`). `m_RestockerID : int` (stamp the real ID, a clone keeps the donor prefab's serialized ID), `m_CheckTasks : bool` and `m_Available : bool` (set true on spawn), `m_ManagementData : RestockerManagementData`, `m_State : RestockerState`, `m_TargetRackSlot`, `m_TargetBox`, `m_TargetProductID`. A plain `Instantiate` leaves the brain at prefab defaults that native spawn would normally overwrite, so a shadow spawn must stamp these by hand. [sig] [shipped]
- **RestockerItem** (`: MonoBehaviour`), a Management-page card. `Setup()` (populates the card once per world load), `RestockerId : int`, `RestockerManagementData` (bind to the store instance), `m_RestockerSetup : RestockerSO`, `m_RestockerName : LocalizeStringEvent` (disable it on clones and write the TMP text directly, or it re-localizes a shared entry), `m_ManagementUI : RestockerManagementUI` (the card→panel link — reach `SetRestockerData` through it), `m_HireButton`, `m_Hired`. Inject clones with `Object.Instantiate(template.gameObject, parent, false)` into a `GridLayoutGroup` content (this vertical management grid reflows automatically — but see the settings-taskbar note in §8: a single-row `GridLayoutGroup` does **not**). [sig] [shipped]
- **RestockerManagementUI** (`: MonoBehaviour`): `SetRestockerData(RestockerManagementData)`, the toggle painter; call it directly to paint the switches from the store instance, since the vanilla no-arg rebind no-ops for modded IDs (gray switches). [sig] [shipped]
- **NetworkComputer** (`__Project__.Scripts.Multiplayer.NetworkComputer`, alias it; `NoktaSingletonPunCallbacks`): `m_RestockerItems : List<RestockerItem>`, a registry MP RPC lookups use. Stays absent the whole single-player session, so hire and cards work without it; register modded cards into it best-effort for multiplayer only. [sig] [probed]

#### Janitors, garbage, and cleaning (research-stage signatures)

- **JanitorManager** (`NoktaSingleton`): `ActiveJanitor : List<Janitor>`, `GetGarbage(Janitor)`, `GetDirt(Janitor)`, `SelectCleaningObject(Janitor)`, `IsAnyCleaningObject() : bool`. [sig]
- **Janitor** (`__Project__.Scripts.Janitor`): a `NavMeshAgent`-driven NPC with an explicit `StateMachine` and states for garbage, dirt, dust, and trash-bag; `m_JanitorID`, `TargetObject`, `cleaningType : CleaningType`, `Clean()`, `SetGarbageCleaningState()` and the other state setters, `ManualUpdate()`. [sig]
- **GarbageGenerator** (`NoktaSingletonPunCallbacks`): `m_DirtPrefab : Dirt`, `m_Dusts : List<Dust>`, fixed Y offsets `m_yGarbageOffSet` / `m_yDirtOffSet`, `IsPointInRange(float x, float z) : bool` (XZ only, Y-blind), `IsOnNavMesh(Vector3) : bool`, `RandomPoint(out Vector3) : bool`, `SpawnAllGarbages(GarbageSaveData)`. [sig]
- **GarbageManager** (`NoktaSingleton`): `m_DustRate`, `m_MaxGarbageCount`, `SpawnGarbage()`, `OnStoreOpened()` / `OnStoreClosed()`, `GetGarbageSpawnRate() : float`. [sig]
- **Dirt** (`MonoBehaviourPunCallbacks`): `m_DirtMaterials`, `m_DirtRenderer`, `m_BubbleParticle : ParticleSystem`, `Setup(int, float)`, `AddProgress(float)`, `Cleaning()`, `OnCleaned : Action`. Window dirt is the `Dust` channel of the same system. [sig]

These behaviors are observation-gated: the cleaning logic lives in the native binary, not the stubs, so whether a second floor gets trash and dirt depends on where the fixed-Y spawn point lands. Instrument before changing.

#### Customers and checkout (research-stage signatures)

- **Customer** (`: MonoBehaviour`): `m_Agent : NavMeshAgent`, `ShoppingCart`/`ShoppingList : ItemQuantity`, `GoToStore(Vector3)`, `GoToCheckout()`, `MoveCheckoutPosition(Checkout, TransformData, bool)`, `ExitStore(bool) : IEnumerator`, `InteractWithCheckout()`, `FinishShopping(bool)`. All movement uses full `Vector3`, no XZ-plane assumption. [sig]
- **CustomerManager** (`NoktaSingleton`): `SpawnCustomer()` / `SpawnCustomer(Vector3)`, `CreateShoppingList() : ItemQuantity`, `SpawnShoplifter()`, `ShoppingCustomers`/`CheckoutCustomers : List<Customer>`, `m_ProductsToBuy : HashSet<int>`. [sig]
- **Checkout** (`: MonoBehaviour`): both `m_Queue : Queue` and `m_Customers : List<Customer>` exist (index `m_Customers`, front = `[0]` for the served customer — also exposed as the `Customers` getter, with `Subscribe`/`Unsubscribe(Customer)`); `m_Belt : CheckoutBelt`, `m_CheckoutPosition : Transform`, `m_ID`, `m_IsSelfCheckout : bool`, `CurrentShoppingCart : ItemQuantity`, `EnterCheckout(string)`, `CashierCompletedCheckout()`, plus `TookCustomersCash(float, PlayerInstance)` / `TookCustomersCard(PlayerInstance)` (+ `_Order` twins). [sig] [probed]
- Verified by probe: vanilla customer AI serves a second floor end to end with no AI modification once the navmesh connects (pathing, shelf interaction, and checkout all just work). [probed]

#### RackManager  (global, `NoktaSingleton<RackManager>`)

The storage racks the restocker AI works against (rack and slot driven, floor-agnostic).

- `m_Racks : List<Rack>`, `m_RackSlots : Dictionary<int, List<RackSlot>>` (property `RackSlots`), `ProductsInRacks : List<int>`. Methods: `AddRack`/`RemoveRack`, `GetAllRackslots(int)`, `GetEmptyRacks(int)`, the two overloads `GetRackSlotThatHasSpaceFor(int, bool)` and `GetRackSlotThatHasSpaceFor(int, int, Restocker)`, `GetProductCountById(int) : int`. **No shipped sibling mod actually references `RackManager`** (the §6 placement and an earlier provenance note implied SuperRestockers/SuperStructure use it — neither does) — every member here is **signature-confirmed by the in-game probe** on v1.4.2(204), but its behavior is otherwise observation-only; `GetProductCountById` is racked-only (smaller than total shelf + warehouse), per the in-game probe. [sig] [probed]

### 7. Store structure and building

SuperStructure's domain (research and spike stage; facts are probe-observed or signature-only as tagged).

#### SectionManager  (global, `NoktaSingleton<SectionManager>`)

Store-size upgrades. Lives at `/---GAME---/Store/Store &&/Sections`.

- `sections : Il2CppReferenceArray<Section>` (**33** scene objects, named `Section 2`..`Section 33` — do NOT conflate with the **32**-entry `IDManager.m_Sections` `SectionSO` catalog, which is also the max store level of 32; the scene shell has one more element than the catalog), `m_StoreUpgradeLevel : int` (the current size), `IsMaxStoreUpgradeLevel : bool`, `onSectionPurchased : Action<int>` and `onSectionPurchasedAnimated : Action<int>`, `UpgradeStore()` (the unlock entry), `UpgradeStoreOrder(int)` (multiplayer), `LoadUpgrade()` / `LoadSaveProgress()` (save re-apply). Vanilla max level is 32. [sig] [probed]

The whole 33-section store shell is pre-authored and toggled, not built. A `Section` (`: MonoBehaviour`) carries `m_ToBeDisabled` and `m_ToBeEnabled : Il2CppReferenceArray<GameObject>` (walls removed and floor added when it opens) and `OpenSection(int id, bool playAnimation = true)`. The parallel storage path is `StorageSection` and `StorageSectionManager` (whose `m_StorageSections` is an `Il2CppReferenceArray<StorageSection>` — a fixed array, **not** a `List`, the one signature this guide had wrong — of length 20 on v1.4.2(204)).

#### GrowthTab and GrowthSectionItem, the upgrades UI

- **GrowthTab** (`: MonoBehaviour`): `m_GrowthSectionItemPrefab`, `m_GrowthSectionsContent : Transform`, `m_GrowthSectionItems : List<GrowthSectionItem>`. Its `Start()` auto-builds a card for every unpurchased `SectionSO` in `IDManager.m_Sections`. An injected SO yields a fully functional native card (cost, lock, purchase button, layout) with no UI work, with the native name falling back to the format `"Section {id+1}"`. The trap: instantiating the prefab yourself alongside the auto-build produces a duplicate card. Inject the SO before the tab's `Start`, let the pipeline build the card, then relabel it (disable the shared `LocalizeStringEvent`, write the TMP text directly), and remove the SO from the catalog at purchase. After removing a card, call `GrowthTab.CheckSecitonsCount(int)` (the game's misspelling) so the native "purchased everything" banner (`m_PurchasedAllText`) re-evaluates. [probed]
- **GrowthSectionItem** (`: MonoBehaviour`): `m_CostText`, `m_Cost` (the numeric field, distinct from `m_CostText`), `m_PurchaseButton : Button`, `m_sectionID`, `m_Section : SectionSO`, `m_SectionName : LocalizeStringEvent` (the shared localizer — disable it and write the TMP text directly), `Setup(int sectionID)`, `Purchase()` (Button-wired only). The proposed custom-purchase patch prefixes `Purchase` for IDs at or above the modded band: do `HasMoney`, `MoneyTransition(-cost, UPGRADE_COSTS)`, unlock, and write the sidecar, then return false; never call `SectionManager.UpgradeStore()` for a mod card (it would bump the vanilla int). [sig]

#### Paint (research-stage)

- **PaintableWall** (`__Project__.Scripts.WallPaintSystem`): `wallRenderer : MeshRenderer`, `materialIndex : int`, `extraWalls : List<PaintableWall>`, `Initialize(PaintData)`, `StartPaintMode(BucketInteraction)`, plus the `IInteractable` virtuals. The failed approach: painting an L1 wall painted its L2 clone too, because `Instantiate` copies material references and `PaintableWall` mutates the shared per-wall material instance. The fix is decoupling: on cloned renderers, replace each shared wallpaint or floor material with `new Material(orig)`. [sig] [probed]
- **PaintableFloor** (same namespace): `floorRenderers : List<MeshRenderer>`, `floorID : int`, `Initialize(FloorTextureData)`, `BePainted(FloorTextureData)`. [sig]

#### NavMesh (the additive recipe)

The registry path is dead in single-player (`m_LocalBounds` reads zero, `Rebuild()` no-ops). The strategy that works is additive data that leaves the vanilla navmesh untouched:

```csharp
var settings = NavMesh.GetSettingsByID(0);
var sources = new Il2CppSystem.Collections.Generic.List<NavMeshBuildSource>(); // each: shape Box, transform Matrix4x4.TRS(...), size, area 0
var data = NavMeshBuilder.BuildNavMeshData(settings, sources, localBounds, Vector3.zero, Quaternion.identity);
NavMesh.AddNavMeshData(data);                 // vanilla data untouched
// separate datas do not auto-stitch; bridge with a link:
NavMesh.AddLink(new NavMeshLinkData { startPosition = a, endPosition = b, width = w,
    costModifier = -1, bidirectional = true, area = 0, agentTypeID = 0 }, Vector3.zero, Quaternion.identity);
```

The link must land directly on the destination floor; a link landing on a connecting ramp fails because the ramp navmesh is an island that does not weld for the agent radius. Clean up with `NavMesh.RemoveLink` and `Object.Destroy(data)`. The relevant types: `StoreNavmesh.IsReady` (static global ready flag), `NavmeshBuildRegistry`, `NavmeshBuildSourceObject`, `NavmeshBuildLoader` (which holds the `NavMeshSurface`). Customers and restockers then serve the new area with no AI changes.

#### Lights

- **StoreLightManager** (`NoktaSingletonPunCallbacks`): `m_InteriorLights : List<InteriorSpotLight>`, `m_IsOn : bool`, `TurnOn : bool`, `AddLight(InteriorSpotLight)`, `RemoveLight(...)`, `StoreLightSwitchTurnOn_RPC(bool)`. [sig]
- **InteriorSpotLight** (`: MonoBehaviour`): `m_EmissionOn`/`m_EmissionOff`/`m_Light : GameObject`, `m_BelongToSection : string`, `TurnOn : bool`, `Start()` (calls `AddToManager`), `AddToManager()`, `RemoveFromManager()`. A cloned fixture registers natively just by keeping its `InteriorSpotLight` component (it self-registers on activate and follows the store light state); call `RemoveFromManager()` before destroying the clone. [sig]

#### Shell-clone mechanics

Clone the store geometry with `Object.Instantiate(src.gameObject, root.transform, true)` (worldPositionStays true). `Instantiate` copies material references (so decouple paint materials, above). The building envelope LOD meshes have `Mesh.isReadable = false`, so runtime mesh surgery is impossible; the facade has to be rebuilt from meshes extracted out of `sharedassets2.assets`. Classify each cloned unit by its own renderer bounds, not combined child bounds (combined bounds let a baseboard stretch a doorway riser into a half-wall).

### 8. Player, camera, and interaction

#### PlayerInstance  (`MonoBehaviourPunCallbacks` aggregate, `PlayerManager.LocalPlayer`)

Exposes the player sub-systems as properties: `PlayerController`, `PlayerInteraction`, `PlayerObjectHolder`, `FurniturePlacer`, `ComputerInteraction`, `FirstPersonController`, `UserId`. Reach it from `FurniturePlacer.m_OwnPlayerInstance`. Read `UserId` defensively; it throws a NullReferenceException in single-player. [shipped]

#### PlayerController and FirstPersonController, freezing and driving the body

- **PlayerController.EnableController(bool, bool includeCamera)**: `EnableController(false, true)` freezes look and movement (the camera goes rock-solid), `EnableController(true, true)` restores. Confirmed by instrumentation: freezing with `includeCamera:true` leaves the body's `CharacterController` enabled, so you can still drive locomotion through it while the camera is frozen, and the game does not re-lock the cursor while the controller is disabled (a one-time cursor takeover suffices). Re-enabling while airborne snaps the player to the ground; settle the body down to grounded first, then re-enable. [probed]
- **FirstPersonController**: `_controller : CharacterController` (stays enabled while the PlayerController is frozen; `_controller.Move(dir * speed * Time.deltaTime)` walks the frozen body with collision and grounding intact), `_cinemachineTargetPitch : float`, `SetCameraXRotation(float)`, `TopClamp`/`BottomClamp`, `MoveSpeed`. Drive yaw via the body transform (`body.Rotate(Vector3.up, deg, Space.World)`) and pitch via the field plus `SetCameraXRotation` (writing the field alone gets overwritten). Never write the main camera or camera-rig transform directly; the controller overwrites those every frame. [probed]

#### PlayerInteraction

- `onUse : Action<bool>`, the event whose `Invoke(true)` is the only reliable programmatic place trigger (section 2). [probed]
- `OnUse(InputAction.CallbackContext)` and `OnScroll(InputAction.CallbackContext)`, the input callbacks (the parameter is `UnityEngine.InputSystem.InputAction.CallbackContext`, where the Mono source typed it `object`). Prefix to suppress during a custom mode. [shipped]
- `HoldClickSimulate()`, the game's instant hold-interaction (the legitimate pickup that manages pause-menu input cleanly). It starts a new hold without releasing the current one, so do not call it while the player carries a vanilla box (it orphans the box until restart). [probed]
- `m_CurrentInteractable`/`CurrentInteractable : IInteractable`, `m_InteractionDistance`/`m_InteractionRange : float`, `SetCurrentInteraction(InteractactableType)` (note the game's spelling), `Interact(bool)`. `m_CurrentInteractable` is null every frame while holding or placing an item (the game suppresses interaction while you hold, and the held item occludes the aim ray), which forces an empty-handed selection model. [shipped] [probed]

#### PlayerObjectHolder

`m_CurrentObject : GameObject` (the held slot, but stale after a sell or deliver), `m_ObjectHolder : Transform` (the hand), `ThrowObjectToBin()`, `CancelPlacingMode()`. The reliable "is something in my hands right now" check is `m_CurrentObject` being active and parented under `m_ObjectHolder`; `m_CurrentObject != null` alone is a stale reference. [shipped]

#### ComputerInteraction / Computer

- `ComputerInteraction.CurrentComputer : Computer`, **sticky, never re-nulls after first use** (it points at the last-used computer forever). `Computer.m_IsOpen : bool` toggles per open and close. Gate computer-open detection on the rising edge of `m_IsOpen`, not on `CurrentComputer != null`. [probed]

#### EscapeMenuManager  (global, `NoktaSingleton`)

`IsPauseMenuOpened : bool`, `isSaving : bool`, `m_Paused : bool`. Read each in try/catch ("flag not ready, treat as not open"). The pause action is Input-System driven, so a half-started placement that disabled it without re-enabling surfaces as a stuck flag here. Also `settingsMenu : SettingsMenuManager`, the in-store settings window (the next subsection). [shipped]

#### Settings menu, tabs, and reading another mod's config

The settings-window + config surface a settings mod injects into (Mod Settings Tool's domain; all signatures confirmed on v1.4.2(204)).

- **The settings window.** `EscapeMenuManager.settingsMenu : SettingsMenuManager` (in-store) and `MainMenuManager.m_SettingsMenu : SettingsMenuManager` (the main-menu twin) — reach both via the manager's `Instance` (a robust lookup; no `GameObject.Find`). `SettingsMenuManager.m_tabManager : TabManager`.
- **Tabs.** `TabManager.m_Tabs : Il2CppReferenceArray<WindowTab>` and `TabManager.OpenTab(string)`. `m_Tabs` is a **fixed array, not a `List`** — to add a tab you allocate `new Il2CppReferenceArray<WindowTab>(n+1)`, copy, append, and reassign (no `.Add`). A `WindowTab` carries `TabName` and its content under `Scroll/Viewport/Content`.
- **Taskbar grid gotcha.** The settings **taskbar** is a single-row `GridLayoutGroup`; inserting a button makes it **wrap to a second row** unless you pin `constraint = FixedRowCount`, `constraintCount = 1`. (This is the opposite of the vertical management grid in §6, which reflows cleanly — "a GridLayoutGroup reflows automatically" is true only for the multi-row vertical case.)
- **Reading another mod's BepInEx config is PLAIN MANAGED .NET — no Il2Cpp marshalling.** This is how Mod Settings Tool surfaces RDC Stock Manager's settings. Enumerate `IL2CPPChainloader.Instance.Plugins` (`Dictionary<string, PluginInfo>`); per entry read `PluginInfo.Metadata` (`BepInPlugin.GUID/Name/Version`), `.Location`, and cast `.Instance` to `BasePlugin` for `.Config` (a `ConfigFile`). Iterate the `ConfigFile` for `ConfigEntryBase` (`.SettingType`, `.Definition.Section/.Key`, `.Description.Description`, `.DefaultValue`, and the read/write target `.BoxedValue`); `AcceptableValueRange<T>` / `AcceptableValueList<T>` and the `ConfigurationManagerAttributes` bag are read by reflection / matched by type-name. None of these are interop wrappers, so they bind directly. (Tobey's pack also loads hidden utility plugins — `Tobey.FileTree`, `Tobey.BepInEx.GameInfo` — which show up in `Plugins` and a config UI must filter out.) [shipped]

#### Teleporting the player (research-stage recipe)

Disable the CharacterController, set `transform.position`, re-enable. Capsule-bottom math: `pos.y = groundY + clearance - cc.center.y + cc.height/2`. Settle with a downward `RaycastAll` filtering the player's own hierarchy (`t.IsChildOf(player) || player.IsChildOf(t)`, or a settle ray hits your own capsule). Watch for static blockers that intrude into a second-floor space (the "Store Shadow Cube" and the ceiling attic collider both do); suspend them with `col.enabled = false` and restore on the way down. There is no store-bounds Y-clamp; `m_IsInside` is a roof raycast, and walls constrain the player physically.

### 9. Save and persistence

#### SaveManager  (global, `NoktaSingleton<SaveManager>`)

The game persists through Easy Save 3 (`.es3` files) at `.../LocalLow/Nokta Games/Supermarket Simulator/slot_N.es3` (with `slot_N_bk_*.es3` backups). The file is plain-text JSON with one quirk: integer dictionary keys are unquoted (`{151:20}`), so it is not strict JSON; quote them with a regex (`([{,\[]\s*)(-?\d+)(\s*:)` to quoted group 2) before parsing. Saves are version-stamped (`GameVersion: v1.4.2(204)`).

- `CurrentSaveFilePath : string`, keys a per-slot sidecar. **It is unset at `FurnitureManager.LoadFurnitureDatas` time on the game-start auto-load**; defer any reconcile that needs it to an `Update`-driven pass once it resolves. (It is set early enough on the pricing path, so the trap is path-specific.) [probed]
- `Save()`, `Save(string saveName)` (private), `Save(SaveInfo)`, the write funnel. Postfix it to commit a sidecar. [shipped] [sig]
- `ApplySaveData()` (private), the load-apply funnel; postfix it as a "save is applied" build trigger. [sig]
- `CreateLoadNewSave(SaveInfo)`, a third save-pipeline funnel — the path that creates/loads a fresh save. Patch it so a brand-new save does not inherit a stale side-car (SuperStructure relies on this). [sig]
- The data containers: `Progression`, `Settings`, `Onboarding`, `Price`, `Expenses`, `Employees`, `Storage`, `Customization`/`NewCustomization`, `Cleaning`, `OnlineOrders`, `Vending`. [sig]
- For employees: `SaveManager.Instance.Employees.RestockerManagementDatas` is the live store that saving serializes and that cards default-create into. The nested `EmployeesData` (passed to `EmployeeManager.LoadData`) holds `RestockersData : List<int>` and `RestockerManagementDatas`, plus parallel rosters for cashiers, guards, and bakers. [shipped] [sig]
- For pricing: `SaveManager.Instance.Price.PricesSetByPlayer` is the same list object (pointer-equal) as `PriceManager.m_PricesSetByPlayer`. So a live `Pricing.Price` write is what the game serializes; no save call and no parallel store needed. [probed]

Two persistence patterns the mods use:

- **Side-car JSON** next to the ES3 save (`<CurrentSaveFilePath>.superdecor.json`), written with plain `System.IO` and `System.Text.Json` in a `SaveManager.Save` postfix. The reason: the interop Newtonsoft and ES3 serializers cannot round-trip managed POCOs through the interop boundary, and a sibling file can never corrupt the real save. Hook `Save` on a separate Harmony id in try/catch so a future game-API change degrades gracefully instead of aborting `PatchAll`. The save postfix is also where physics props capture their rolled-to-rest position, after the base save has serialized the reload position. [shipped]
- **Ride the live list** when the game already serializes the object you mutate (the pricing case above), which needs no save hook at all.

Furniture and boxes persist full 3D transforms (`TransformData { Position xyz, Rotation xyzw }`), and an existing item already sits at a raised Y, so multi-floor furniture round-trips with no schema change.

### 10. Localization

#### LocalizationManager  (global, `NoktaSingleton<LocalizationManager>`)

- `m_LocalizedFurnitureNames : Dictionary<int, string>` field (and `m_LocalizedSectionNames` for sections). Upsert names through the indexer. [shipped]
- `UpdateLocalization()`, rebuilds the name maps; runs during the manager's `Awake` and **NREs on a null `LocalizedName` of any registered SO**, which aborts the entire vanilla name rebuild and drops every mod's `UpdateLocalization` postfix. This is why every custom SO must have a non-null `LocalizedName`. [probed]
- `LocalizedFurnitureName(int) : string` (and `LocalizedSectionName(int)`), returns a managed string, so a prefix that sets `ref string __result` and returns false overrides it cleanly. [shipped]

Patch shape: postfix `UpdateLocalization` to re-inject your names into the dictionary, and prefix `LocalizedFurnitureName` to short-circuit for your IDs.

The game also ships the Unity Localization package. Current language is `LocalizationSettings.SelectedLocale.Identifier.Code` (read it fail-soft). A bare `Locale` is ambiguous with a global game `Locale`; fully qualify `UnityEngine.Localization.Locale`. Add string-table entries with `LocalizationSettings.StringDatabase.GetTable(name)` then `AddEntry`/`entry.Value`.

### 11. Other consumed types

- **SFXManager** (`NoktaSingleton`), the sound bus. `HasInstance` can be false on a healthy instance until something first reads `Instance` (the lazy-resolve case in the Singletons section — read `Instance` directly with a `FindObjectsOfType` fallback). Most methods are positional, `Play*SFX(Vector3 position)` — e.g. `PlayCheckoutWarningSFX(Vector3)`, `PlayScaleWarning(Vector3)`, `PlayCashRegister(bool, Vector3)`, `PlayScanningProductSFX(Vector3)`; the global UI/warning sounds take no argument — `PlayMouseClickSFX()`, `PlayWholesaleOfferSFX()`, `PlayOnlineOrderSFX()`, `PlayCityEdgeWarning()`, `PlayDirtyStoreWarningSFX()`. (Note the non-obvious `Vector3` on the place/checkout SFX — they are spatialized.) [shipped] [sig]
- **Extensions.SetCollisions(GameObject, bool)**, a static game helper. It was a C# extension method, but interop drops the `this` modifier, so call it statically: `Extensions.SetCollisions(go, true)`. [shipped]
- **NaisuPorter.CommonScripts.SerializableVector3 / SerializableQuaternion**, the serializable transform types baked into `FurnitureData.Transform` (`Position`, `Rotation`). `new SerializableQuaternion(Quaternion)` and `new SerializableVector3(...)` with implicit conversions to and from the Unity types. Namespaced, no `Il2Cpp` prefix. [shipped]
- **DOTween** (unprefixed interop): `TweenCallback` and `Tweener.OnComplete` drive the box-open scale animation. Bridge the callback with `DelegateSupport.ConvertDelegate<TweenCallback>`. [shipped]
- **Display / DisplaySlot / Highlightable / Canvas**: when repurposing a shop prefab as plain decor, strip the shelf behavior by destroying `Display` and its `DisplaySlot` children, destroy price and tag `Canvas` children, and add `Highlightable` to a fallback prefab. `GetComponentsInChildren<T>(true)` returns `Il2CppArrayBase<T>`; iterate by index. [shipped]
- **BasicRigidBodyPush** (Unity Starter Assets, consumed not patched): `PushRigidBodies` drops the push for anything it reads as below the player (`if (hit.moveDirection.y < -0.3f) return;`). A low prop on ground that sits below the sidewalk reads as "below" and never gets the impulse, so a physics-prop mod has to supply the push from the prop's side. The player capsule is on Unity layer 3. [probed]

---

### 12. Multiplayer / co-op networking (Photon PUN)

Co-op runs on **Photon PUN**. A read-only two-instance probe (host + a joining client, 2026-06-16) confirmed the authority surface a plugin reads and how game state behaves on a client:

- **Authority API.** Read from `Photon.Pun.PhotonNetwork` (in `PhotonUnityNetworking.dll`) + `Photon.Realtime.{Room, Player, ClientState}` (`PhotonRealtime.dll`):
  - `PhotonNetwork.OfflineMode : bool` — true in singleplayer (the game runs PUN in offline mode for SP).
  - `PhotonNetwork.IsConnected : bool`, `PhotonNetwork.InRoom : bool`, `PhotonNetwork.NetworkClientState` (a `ClientState` enum; `Joined` in a room).
  - `PhotonNetwork.IsMasterClient : bool` — **the host is the master client.**
  - `PhotonNetwork.CurrentRoom` (`Name`, `PlayerCount`, `MaxPlayers`, `MasterClientId`), `PhotonNetwork.LocalPlayer` (`ActorNumber`, `NickName`, `IsMasterClient`), `PhotonNetwork.PlayerList` (`Il2CppReferenceArray<Player>`).
  - Practical classification: **OFFLINE** = `OfflineMode || !IsConnected`; **HOST** = connected + in-room + `IsMasterClient`; **CLIENT** = connected + in-room + not master. Resolve fail-soft (default OFFLINE if the Photon types are absent). [probed]
- **The co-op store scene is named `Multiplayer`** (the single loaded scene), distinct from the singleplayer **`Main Scene`** (the menu is **`Main Menu`**). A scene-name gate that only accepts `Main Scene` is therefore **inert in co-op** — gate on `Main Scene || Multiplayer` to run in both. [probed]
- **State replicates to clients.** A joining client reads the same authoritative inventory the host does: `DisplayManager.GetDisplayedProductCount`, `InventoryManager.GetInventoryAmount`, `RackManager.GetProductCountById`, and the computer's per-row `SalesItem` shelf/warehouse/box text all return **host-identical** values on a client. No mod-side replication is needed to read stock on a client. [probed]
- **UI refresh on remote changes is partial.** A *remote* player's **warehouse** change repaints an open client viewer (the game calls `SalesItem.UpdateInventoryAmountText` on the client), but a remote **shelf** change does **not** call `UpdateSlotAmountText` on the client — so a UI mod that augments shelf rows needs its own refresh trigger for network-originated shelf changes. [probed]
- **`MarketShoppingCart` is per-player**, each capped at the vanilla `m_MaxItemCount` (50) on a client; a client can place a Market App order. Whether a *raised* cap survives the networked purchase + `DeliveryManager.Delivery` is unverified. [probed]
- **Singletons + types.** The networked managers use the `NoktaSingletonPunCallbacks<T>` variant (a Photon `PunCallbacks`); `__Project__.Scripts.Multiplayer.NetworkComputer` is the networked computer; many managers carry `*_Order` / `*_RPC` twins; the products manager toggles `SuppressNetworkBroadcast` around bulk writes. (Cross-refs: § Singletons; the per-section `*Order` notes.) [sig]

---

## Part IV, World-load and day-cycle lifecycle

The order things happen in, frame-attributed by probe where known. Useful when you are deciding which hook to attach to.

### World load

1. The managers (`IDManager`, `EmployeeManager`, `PriceManager`, and the rest) appear together, around frame 223 in the restocker probe. A host polling for `IDManager.HasInstance` and watching `GetInstanceID()` detects the world here. Each manager is a fresh per-scene instance.
2. `EmployeeManager.LoadData` and `FurnitureManager.LoadFurnitureDatas` fire the same frame the managers appear, long before any UI. This is where your load-time prefixes must already be applied. If a saved modded ID has no resolvable SO, the native loader NREs (furniture) or treats the save as corrupt and wipes the roster (employees), so strip or scrub modded data here.
3. The world settles. Mods that spawn content wait a short delay (two seconds for restockers, three for pricing so the license loaders finish populating their lists) before acting, and stagger spawns so a failure is post-load and pinpointable.
4. UI views build lazily, on first open. The Management page (`RestockerItem.Setup`, around frame 635), the pricing computer (`PricingProductViewer`, null until first opened), and the Furnitures page (`FurnituresViewer.Start`) each build the first time the player opens them, and append rather than rebuild on subsequent opens.

### Day cycle and pricing

- The new-day signal is `DayCycleManager.OnStartedNewDay` (an `Action` you subscribe to with the bridged-delegate idiom, re-subscribing on a fresh instance) or a postfix on `StartNextDay()`. Both fire on day rollover.
- A price pass reads each row's basis (`Pricing.MarketPrice`, or `round(CurrentCost * (1 + OptimumProfitRate/100), 2)`), applies the chosen rule, and writes `Pricing.Price` only when it changed, stamping `LastChangeDate = CurrentDay`. The write rides the save for free because the list is the same object the save serializes. Then refresh the visible tags: `DisplaySlot.SetPriceTag()` for shelves, `VendingSlot.SetPriceTag()` for vending, and `PricingProductViewer.RefreshUnlockedProducts(id)` for an open computer.

---

## Part V, Things that look right and break

A scan-list of traps, each one paid for in debugging time. Most are detailed above; this is the index.

- **`PatchAll` is all-or-nothing.** One missing target disables every patch. Patch per-class in try/catch instead.
- **A postfix runs even if your prefix skipped the original.** To block a side effect, skip the setter, not the compute method.
- **Patching `FurniturePlacer.StopPlacingMode` or `FurnitureInteraction.OnUse` hard-crashes** the process. Calling them is fine.
- **`Physics.OverlapBoxNonAlloc` with a managed `Collider[]`** quietly returns zero hits. Use a persistent `Il2CppReferenceArray<Collider>`.
- **Reflecting a game `int`/`float`/`bool` field** returns `807810400`. Use wrapper properties or re-wrap through the `IntPtr` ctor.
- **`as` / `is` on an interop wrapper** silently mis-types. Use `.TryCast<T>()`.
- **`?? `/ `is null` / `ReferenceEquals` on a Unity object** treats a destroyed wrapper as alive. Use `== null`.
- **A bridged delegate with no managed root gets GC'd** and the button dies "after a while." Hold it in a static field.
- **A runtime-created SO/Sprite/Mesh/Material/Texture with no live reference** is destroyed by `UnloadUnusedAssets` after a scene settles. Set `HideFlags.DontUnloadUnusedAsset`.
- **Per-scene managers** mean every injection must re-run per scene, gated on `GetInstanceID()`, not once per process.
- **`CreateInstance` leaves `LocalizedName` null**, and the localization rebuild NREs on it during `Awake`, taking out all furniture names. Copy a real `LocalizedString` reference.
- **`MoneyManager.UpdateMoneyText` is gone.** Pass `updateMoneyText:true` to `MoneyTransition`.
- **`FurnitureManager.Start` is now `Awake`.** Retarget.
- **`SpawnFurnitures` appends, it does not clear.** Clear the container yourself with `DestroyImmediate` from the end.
- **`ComputerInteraction.CurrentComputer` is sticky.** Gate on `Computer.m_IsOpen`.
- **`EmployeeManager.GetSpawnPosition` throws for modded IDs.** Prefix and return a planned spot.
- **Raising `MAX_RESTOCKER_COUNT` hard-crashes the load.** Keep native data vanilla-shaped and own the modded lifecycle.
- **`DisplayManager.GetDisplaySlots` is context-dependent** — SuperPricer's probe got 0; RDC's spike got the assigned-slot count with `(id, false, list)` on v1.4.2(204). Verify per call; `FindObjectsOfType<DisplaySlot>()` is the safe fallback.
- **`ProductViewer.UpdateUnlockedProducts` is `(int,bool)` but `PricingProductViewer.UpdateUnlockedProducts` is `(int)`.** Do not assume symmetric overloads.
- **`HasInstance` can be false on a healthy lazily-resolved manager** (`SFXManager`). Read `Instance` directly there.
- **Span-marshalling APIs can throw `MissingMethodException`** — but the set is build-specific: on v1.4.2(204) `GUI.Button(Rect, string)` was found **callable** (no throw); `GUI.TextField` / `Material.SetOverrideTag` are unconfirmed on this build. Verify the exact call; prefer uGUI for production UI regardless.
- **`Cubemap.SetPixels` hard-crashes.** Use `SetPixelData`.
- **`Instantiate` copies material references and managed fields** (not instance IDs). Decouple shared materials; key template flags on instance identity.
- **The game `Assembly-CSharp.dll` in `Managed/` is a stripped placeholder.** Reference the one in `BepInEx/interop/`.

---

## Appendix

### Re-verification checklist after a game update

1. Delete `BepInEx/cache/` to force interop regeneration (it is keyed on `assembly-hash.txt` against `global-metadata.dat`).
2. Re-dump the types you patch with `ilspycmd` and diff the signatures. A renamed or re-shaped method is the most common breakage, and `PatchAll` will not warn you, it will just disable everything.
3. Rebuild, deploy, and smoke the load path first (does the framework still apply its patches), then each feature.
4. Pay special attention to overload shapes (the `SpawnFurniture` precedent) and to any method that moved between `Start` and `Awake`.

### How these facts were earned

No method bodies are readable, so the workflow throughout was: write a prefix or postfix that only logs and confirms the target resolves, deploy it, run the game, and read `BepInEx/LogOutput.log` in batch afterward. From there, instrument deltas (log a field before and after a native call), watch for NREs in the log to find the crash-prone methods, and run head-to-head state dumps to find which flag actually gates a behavior. Smoke hygiene that paid off: deploy the exact build you think you are testing and verify it (a stale DLL gives a false signal), read the log in batch rather than live, and never use pattern-based process kills (they self-match the install path in the command line).

### Per-mod provenance map

Which mod proved which surface, so a claim can be traced:

- **SuperDecor** (decoration framework): furniture registry and the Furnitures page, `FurniturePlacer` and the whole placement and placing-mode surface, the `onUse` commit mechanism, the shopping and money rewrite, the hologram pipeline, the icon path, save side-car, player-controller freeze and camera driving, the runtime glTF and animation marshalling, the uGUI-over-IMGUI escape.
- **SuperRestockers** (employee mod): `EmployeeManager` and the staff caps, the shadow-hire architecture, the Clerk and Restocker anatomy, the Management-page card injection, the corrupt-data wipe behavior, NavMesh sampling.
- **SuperPricer** (pricing mod): `PriceManager` and `Pricing`, the day-cycle hook and the bridged-delegate idiom, the shelf, vending, scanner, and computer refresh targets, the pointer-equal save list, the `SFXManager` `HasInstance` lie.
- **SuperStructure** (store building, research stage): `SectionManager` and the growth UI, the section and storage shell topology, the additive NavMesh recipe, paint material decoupling, lights, garbage and janitor signatures, customer and checkout signatures, the player teleport recipe, shell-clone mechanics.
- **Custom Products Loader** (custom products): `ProductLicenseManager` and the reconciler pattern, the product and license SO construction, the icon atlas and shared-page scheme, the signature-divergence trap, the value-type reflection bug, custom-ID allocation and conflict handling, the additive localization pattern.
- **Mod Settings Tool** (config UI): the settings-menu / tab-injection surface (`EscapeMenuManager.settingsMenu`, `MainMenuManager.m_SettingsMenu`, `SettingsMenuManager.m_tabManager`, `TabManager.m_Tabs`/`OpenTab`, `WindowTab`), the fixed-array `m_Tabs` grow-and-copy, the single-row taskbar pin, and reading another mod's BepInEx config as plain-managed .NET (`IL2CPPChainloader.Plugins`, `BasePlugin.Config`, `ConfigEntryBase.BoxedValue`).
- **RDC Stock Manager in-game probe** (the field-guide confirmation pass, 2026-06-18): the reflection signature audit of 92 documented members, the 95/38 singleton census, the `807810400` reflection-garbage reproduction, the NavMesh presence sweep, the unknown-id lookups, the Pricing computed-getter identities, the `MoneyTransition` sign, the `ProductLicenseManager` `AllPoducts`/`UnlockedLicences` spelling trap, the `BoxSize` 11-value enum, and the `Checkout` `m_Queue`+`m_Customers` finding.

### A note on accuracy

Everything here was true on the build named at the top. Signatures come from a real interop dump; behaviors come from real in-game observation or shipped code. Where a behavior is only inferred, it says so. The surface was last re-verified end-to-end against the live build by the in-game probe on **2026-06-18** (signature audit + read-only and mutating runtime probes). When in doubt, instrument it yourself, the same way these were found, and correct the record. The goal is to save the next person the months of trial and error, not to be the last word.
