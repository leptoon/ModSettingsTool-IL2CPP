# BepInEx config + chainloader reference (verified)

> The API this mod is built on. Verified by `ilspycmd` of the actual pack DLLs
> (`BepInEx.Unity.IL2CPP.dll`, `BepInEx.Core.dll`) on 2026-06-15, BepInEx 6.0.0-be.755. These are **plain
> managed .NET types**, referenced directly, no IL2CPP marshalling. FLUID: correct in place if a pack
> update changes a signature.

## 1. Enumerate installed mods + load health

`BepInEx.Unity.IL2CPP.IL2CPPChainloader : BaseChainloader<BasePlugin>`

```
IL2CPPChainloader.Instance                       // static, set during chainload
  .Plugins          : Dictionary<string, PluginInfo>   // GUID -> info, the LOADED mods
  .DependencyErrors : List<string>                     // human-readable load/dependency failures
```

`BepInEx.PluginInfo`
```
.Metadata     : BepInPlugin   // .GUID (string), .Name (string), .Version (SemanticVersioning.Version)
.Instance     : object        // cast to BepInEx.Unity.IL2CPP.BasePlugin
.Location     : string        // the plugin DLL path
.Dependencies / .Incompatibilities / .Processes / .TypeName
```

`BepInEx.Unity.IL2CPP.BasePlugin` → `.Config : ConfigFile`, `.Log : ManualLogSource`.

**Note:** `BepInPlugin.Version` is a `SemanticVersioning.Version` (in `core/SemanticVersioning.dll`), the
csproj references that assembly so the property can be read. The BepInEx log is at
`BepInEx.Paths.BepInExRootPath` + `/LogOutput.log`.

**Health model (this mod):** loaded set = `Plugins`; failures = `DependencyErrors` (a failed plugin is
NOT in `Plugins`, so most errors become their own red entries); optional heuristic = scan `LogOutput.log`
for `[Error … : <source>]` lines and attribute to a mod by Name. See `Mods/ModRegistry.cs` +
`Mods/ModHealth.cs`.

## 2. Reflect a mod's config

`BepInEx.Configuration.ConfigFile : IDictionary<ConfigDefinition, ConfigEntryBase>`
```
foreach (var kv in configFile) { kv.Key /* ConfigDefinition */, kv.Value /* ConfigEntryBase */ }
.Values  : ICollection<ConfigEntryBase>
.Count   : int
.SettingChanged : event EventHandler<SettingChangedEventArgs>   // fires when any entry changes
```

`ConfigDefinition` → `.Section` (string), `.Key` (string).

`ConfigEntryBase`
```
.Definition   : ConfigDefinition
.Description   : ConfigDescription   // .Description (string), .AcceptableValues (AcceptableValueBase)
.SettingType   : Type                // bool / int / float / enum / string / KeyCode / …
.DefaultValue  : object
.BoxedValue    : object  { get; set; }   // <-- LIVE read/write. Setting it writes the owning mod's entry
                                          //     and BepInEx persists to the .cfg. This is the ONLY edit path.
```

`AcceptableValueBase` (on `ConfigDescription.AcceptableValues`)
```
.ValueType : Type
abstract Clamp(object), IsValid(object), ToDescriptionString()
// AcceptableValueRange<T>  -> properties MinValue / MaxValue  (read by reflection -> Slider bounds)
// AcceptableValueList<T>   -> property   AcceptableValues (T[])  (read by reflection -> dropdown choices)
```

## 3. Type → control mapping (`Config/ConfigBinding.cs`)

| `SettingType` | + AcceptableValues | Control (`ControlKind`) |
|---|---|---|
| `bool` | n/a | Toggle |
| enum (not KeyCode) | n/a | EnumDropdown (`Enum.GetNames`) |
| `UnityEngine.KeyCode` | n/a | KeyBind (press-a-key) |
| int/long/short/byte/float/double/decimal | `AcceptableValueRange` | Slider (Min..Max) |
| …numeric | none | IntInput / FloatInput |
| `string` | `AcceptableValueList` | ChoiceDropdown |
| `string` | none | TextInput |
| anything else | n/a | Unsupported (listed, read-only) |

`ConfigBinding.Value` get/set wraps `Entry.BoxedValue` (fail-soft). `ConfigBinding.FromConfigFile`
snapshots + sorts the entries; empty list ⇒ the mod shows **"No settings to change."**

## 4. Why driving the live entry (not the .cfg) is correct

Setting `ConfigEntryBase.BoxedValue` updates the **same object the owning mod reads** (`ConfigEntry<T>.Value`)
and BepInEx writes it through to the `.cfg`. So a mod that reads its config each frame/poll (like RDC Stock
Manager) picks up the change with no coupling, and mods that subscribe to `SettingChanged` are notified.
Hand-writing the `.cfg` file would NOT update the live entry (no reload), so the owning mod would not see
it, and would overwrite our edit on its next save. Always drive the entry.
