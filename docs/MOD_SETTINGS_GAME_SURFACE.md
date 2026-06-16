# Game UI surface: Mod Settings Tool

> The game UI this mod binds to. FLUID living document: REPLACE stale lines as the surface is mapped;
> never append dated addenda. The Escape-menu tab surface is **proven** (inherited from the RDC Stock
> Manager mod). The **Main Menu** surface is now **mapped** (§C, via the read-only spike).

## A. Scenes

| Scene | Role | This mod |
|---|---|---|
| `Main Menu` | the main menu | **Part 1**, the installed-mod list (surface PROVEN, see §C) |
| `Main Scene` | singleplayer store | **Part 2**, the Escape-menu "Mods" tab (surface PROVEN, see §B) |
| `Multiplayer` | co-op | inert |

`Patches/PatchGate.cs` gates by scene name (`InMenu()` / `InStore()`).

## B. Escape-menu Settings tab (PROVEN, ported from RDC Stock Manager)

Reached WITHOUT `GameObject.Find` literals:

```
FindObjectOfType<EscapeMenuManager>()            // global; .HasInstance / .Instance also exist
  .settingsMenu        : SettingsMenuManager
    .m_tabManager      : TabManager
      .m_Tabs          : Il2CppReferenceArray<WindowTab>   // FIXED ARRAY, grow + copy, not .Add
      .OpenTab(string tabName)
  (taskbar) : <Window BG>/Taskbar/<buttons row>            // a GridLayoutGroup; pin to 1 row when adding
```

`WindowTab` → `.TabName` (string), `.firstSelectedObject` / `.backupSelectedObject` (null them after
clearing cloned content), content at `Scroll/Viewport/Content`. A real settings tab's rows are the clone
templates for Toggle / Slider (find a DIRECT child of Content that has the control and is NOT a dropdown
row, the dropdown's item-template Toggle is a 0-width trap).

**Critical hazards (already handled in `UI/ModsTab.cs`):**
- The settings-tab controls only resolve once the **Settings submenu is active**, poll for that, then
  build once (idempotent). At Esc-menu open the submenu is still inactive.
- Cloned controls carry **serialized `onValueChanged`** (e.g. mouse sensitivity), replace the whole
  event object before wiring ours (`UI/UiListeners.cs`), or editing our control flips a real game setting.
- Adding a 6th taskbar button **wraps** the GridLayoutGroup, pin `constraint = FixedRowCount`,
  `constraintCount = 1`.

`UI/ModsTab.cs` ports the full clone-tab + taskbar-button + control-factory recipe and **builds the two-pane
"Mods" tab** (`PopulateMods`): a left scrollable alphabetical mod list + a right per-mod config page (§D),
with a staged Save / Discard / Cancel model and a close-guard on the Escape-menu close path
(`SettingsMenuManager.set_Enable`) for keybind-capture and unsaved edits.

## C. Main Menu (PROVEN, mapped by the spike)

Reached WITHOUT `GameObject.Find` literals, via the menu controller:

```
FindObjectOfType<MainMenuManager>()      // global namespace; one per Main Menu; sits ON the menu canvas
  .transform                             // == the "Menu" canvas root
```

The root **`Menu`** IS the main-menu Canvas (`<RectTransform, Canvas, CanvasScaler, GraphicRaycaster,
MainMenuManager, …>`, 1920×1080 reference). Its direct children include the six landing buttons, stacked in
a vertical column at **x = 0**, center-anchored (aMin=aMax=0.5,0.5), each **239.9 × 72**, 100 px apart:

| Button (direct child of `Menu`) | anchoredPosition Y |
|---|---|
| `New Game Button`    | +100 |
| `Continue Button`    |    0 |
| `Load Button`        | −100 |
| `Multiplayer Button` | −200 |
| `Settings Button`    | −300 |
| `Quit Button`        | −400 |

Each button is a real `UnityEngine.UI.Button` (`<RectTransform, CanvasRenderer, Image, Outline, Button>`)
with a single child label `Text (TMP)` = `<RectTransform, CanvasRenderer, TextMeshProUGUI,
LocalizeStringEvent>`. Also present: `Window BG` (panel behind the buttons, pos (1.5,−103.9), size 323×718),
`Title`, `Icon`, social buttons.

Build recipe for `UI/MainMenuModList.cs`:
- **Parent the list under** the `Menu` canvas (`MainMenuManager.transform`).
- **Anchor it to the RIGHT of the column.** The buttons span x≈[−120,+120]; `Window BG`'s right edge ≈ x+163.
  Place the list panel center-anchored at x ≳ +200 (tune at smoke), occupying the vertical band y≈[+136,−436].
- **Clone-source for a row label:** a landing button's child `Text (TMP)` (a `TextMeshProUGUI`). **Disable its
  `LocalizeStringEvent`** on the clone (else the game's localization overwrites our text), the same hazard
  handled for the tab, then set text + color (green = Healthy / amber = Warning / red = Unhealthy).
- **Overflow:** the menu has clonable `ScrollRect`s (`Menu/LoadPanel`, `Road Map BG/Scroll View`); wrap the
  list in one when it exceeds the band (dozens of mods / low resolution).

The Host's per-frame `MainMenuModList.EnsureBuilt()` builds it the frame the `Menu` canvas exists, no
precise Harmony hook needed, so `Patches/MenuPatches.cs` stays EscapeMenu-only.

## D. The generic config controls (BUILT)

`Config/ConfigBinding.cs` classifies each entry into a `ControlKind`; `UI/ModsTab.cs` generates the matching
control, each persistent-listener-safe (`UI/UiListeners.cs`) and staged (written live only on Save):
- **Toggle** / **Slider**: cloned from a settings tab's own toggle/slider row (`FindRowTemplate`, which
  excludes dropdown rows; the dropdown's item-template Toggle is a 0-width trap).
- **Dropdown** (`TMP_Dropdown` / the game's `CustomDropdown`) for `EnumDropdown` / `ChoiceDropdown`, options
  are applied AND re-applied on the next frame (`_pendingDropdowns` in `Tick`) plus `Hide()` + template
  deactivate, or the first open shows "Option 1" and gets stuck.
- **`TMP_InputField`** for `TextInput` / `IntInput` / `FloatInput`, the settings tabs contain NO input
  field, so one is cloned from any loaded `TMP_InputField` (`Resources.FindObjectsOfTypeAll`) to get a real
  caret/selection. That source is non-deterministic, so `NormalizeClonedInput` forces a flat rectangle,
  drops the placeholder, disables every other graphic (legacy `Text`/`Image` stepper "+/-" included) and
  resets the kept text's transform scale (kills a mirrored duplicate). `TextInput` rows also show a colour
  swatch (`ColorUtility.TryParseHtmlString` + rgb/csv).
- **KeyBind**: a press-a-key keycap capture (reads `UnityEngine.Input`; no IMGUI). Esc cancels the capture
  without closing Settings; mouse buttons + Esc are excluded via `IsBindable`.

Every edit writes `ConfigBinding.Value` (the live `ConfigEntry.BoxedValue`), only on Save. The Escape-menu
close path is guarded in `Patches/MenuPatches.cs` (`SettingsMenuManager.set_Enable`).
