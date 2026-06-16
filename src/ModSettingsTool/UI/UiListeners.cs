using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ModSettingsTool.UI
{
    // Wires managed handlers onto cloned uGUI controls AND keeps them alive.
    //
    // Two hazards handled here:
    //  1) Persistent-listener bleed: a cloned game control carries the SOURCE's *serialized* onValueChanged
    //     wiring (e.g. the Invert-X toggle calls SettingsMenuManager.InvertXAxis; a Products-tab dropdown
    //     drives the game's filter). RemoveAllListeners() removes only runtime listeners, NOT serialized
    //     ones, so we REPLACE the whole event object with a fresh one before adding ours. Otherwise a
    //     Stock Manager toggle would also flip a real game setting.
    //  2) Delegate GC: bridged Il2Cpp delegates are collected unless something managed roots them (the
    //     legacy "worked a few times then a GC killed the click" bug). Every bridged UnityAction goes into
    //     a static keep-alive list for the session.
    internal static class UiListeners
    {
        // Bridged delegates must stay rooted or a GC collects them (the legacy "worked then died" bug). To stop
        // an unbounded leak as the player browses mods / re-enters scenes, roots are SCOPED: persistent roots
        // hold the tab scaffold (Save/Back, taskbar, the left mod list); page roots hold the right-pane controls
        // for the selected mod and are dropped each time that page rebuilds. ClearAll drops both when the whole
        // scene UI is torn down.
        private static readonly List<object> Roots = new();      // persistent within a scene
        private static readonly List<object> PageRoots = new();  // current right-pane page (cleared on rebuild)
        private static bool _pageScope;

        internal static void BeginPageScope() { PageRoots.Clear(); _pageScope = true; }
        internal static void EndPageScope() { _pageScope = false; }
        internal static void ClearAll() { Roots.Clear(); PageRoots.Clear(); _pageScope = false; }

        private static T Root<T>(T bridged) where T : class
        {
            (_pageScope ? PageRoots : Roots).Add(bridged!);
            return bridged;
        }

        internal static void OnClick(Button button, Action handler)
        {
            if (button == null) return;
            UnityAction bridged = Root(DelegateSupport.ConvertDelegate<UnityAction>(handler)!);
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(bridged);
        }

        // ADD a handler without dropping the existing (vanilla) listeners, for hooking a real game button
        // (e.g. the settings Save) so its native action still runs alongside ours.
        internal static void AddClick(Button button, Action handler)
        {
            if (button == null) return;
            UnityAction bridged = Root(DelegateSupport.ConvertDelegate<UnityAction>(handler)!);
            button.onClick.AddListener(bridged);
        }

        internal static void OnChanged(TMP_Dropdown dropdown, Action<int> handler)
        {
            if (dropdown == null) return;
            UnityAction<int> bridged = Root(DelegateSupport.ConvertDelegate<UnityAction<int>>(handler)!);
            dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
            dropdown.onValueChanged.AddListener(bridged);
        }

        internal static void OnChanged(Toggle toggle, Action<bool> handler)
        {
            if (toggle == null) return;
            UnityAction<bool> bridged = Root(DelegateSupport.ConvertDelegate<UnityAction<bool>>(handler)!);
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.onValueChanged.AddListener(bridged);
        }

        internal static void OnChanged(Slider slider, Action<float> handler)
        {
            if (slider == null) return;
            UnityAction<float> bridged = Root(DelegateSupport.ConvertDelegate<UnityAction<float>>(handler)!);
            slider.onValueChanged = new Slider.SliderEvent();
            slider.onValueChanged.AddListener(bridged);
        }

        // Two-phase wiring for a cloned slider that must be CONFIGURED before it listens. ClearChanged replaces
        // the event object (dropping the source's serialized onValueChanged); then min/max/value are set; then
        // AddChanged attaches our handler. Splitting it this way stops Unity's range setters, which fire
        // onValueChanged when they clamp the old cloned value, from staging a value the user never chose.
        internal static void ClearChanged(Slider slider)
        {
            if (slider == null) return;
            slider.onValueChanged = new Slider.SliderEvent();
        }

        internal static void AddChanged(Slider slider, Action<float> handler)
        {
            if (slider == null) return;
            UnityAction<float> bridged = Root(DelegateSupport.ConvertDelegate<UnityAction<float>>(handler)!);
            slider.onValueChanged.AddListener(bridged);
        }

        // For our own freshly-added ScrollRects (e.g. the main-menu list) there is no serialized listener to
        // bleed, but we still static-root the bridged delegate so a GC can't drop the scroll-position handler.
        internal static void OnChanged(ScrollRect scroll, Action<Vector2> handler)
        {
            if (scroll == null) return;
            UnityAction<Vector2> bridged = Root(DelegateSupport.ConvertDelegate<UnityAction<Vector2>>(handler)!);
            scroll.onValueChanged = new ScrollRect.ScrollRectEvent();
            scroll.onValueChanged.AddListener(bridged);
        }

        // Commit-on-end (write the config value when the player finishes editing a TMP_InputField).
        internal static void OnEndEdit(TMP_InputField input, Action<string> handler)
        {
            if (input == null) return;
            UnityAction<string> bridged = Root(DelegateSupport.ConvertDelegate<UnityAction<string>>(handler)!);
            input.onEndEdit = new TMP_InputField.SubmitEvent();
            input.onEndEdit.AddListener(bridged);
        }

        // Live value (e.g. to refresh a colour swatch as the player types).
        internal static void OnValueChanged(TMP_InputField input, Action<string> handler)
        {
            if (input == null) return;
            UnityAction<string> bridged = Root(DelegateSupport.ConvertDelegate<UnityAction<string>>(handler)!);
            input.onValueChanged = new TMP_InputField.OnChangeEvent();
            input.onValueChanged.AddListener(bridged);
        }
    }
}
