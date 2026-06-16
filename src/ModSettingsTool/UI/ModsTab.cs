using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ModSettingsTool.Config;
using ModSettingsTool.Mods;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace ModSettingsTool.UI
{
    // Part 2: a "Mods" tab injected into the in-game (Escape menu) Settings window, built entirely from
    // cloned game uGUI (no IMGUI). The tab-injection machinery (find the TabManager via FindObjectOfType<
    // EscapeMenuManager> -> settingsMenu.m_tabManager, clone a real settings tab panel, register it, add a
    // taskbar button) is the PROVEN recipe ported from RDC Stock Manager. On top of it this builds a
    // TWO-PANE page: a LEFT scrollable alphabetical mod list (one selectable button per ModInfo, tinted by
    // health) and a RIGHT config page generated from the selected mod's ConfigBinding list. Every edit
    // writes through the live ConfigEntry (ConfigBinding.Value / SetNumber / SetChoice / SetKey), never the
    // .cfg, never the game save.
    internal static class ModsTab
    {
        private const string TabName = "Mods";

        private static bool _warned;
        private static bool _warnedOnce;
        private static bool _pendingLogged;

        // Build state (reset on Invalidate / rebuild). The templates are cloned per control; the right pane
        // is rebuilt per selection.
        private static TextMeshProUGUI? _labelTemplate;
        private static GameObject? _refToggle, _refSlider, _refDropdown, _refInput;
        private static Transform? _rightContent;
        private static ModInfo? _selected;   // selected mod by INSTANCE, unloaded-failure rows share an empty GUID
        private static readonly List<ModButton> _modButtons = new();

        // Key-rebind capture (press-a-key, per OQ-03), driven each frame by Tick().
        private static ConfigBinding? _capturing;
        private static TextMeshProUGUI? _capturingLabel;
        private static KeyCode[]? _captureKeys;
        private static string _capturePrevText = "";

        // Staged edits, controls write HERE, never to the live ConfigEntry, until the player Saves. This is
        // what guarantees nothing persists without Save, and makes Discard a clean no-op (live was untouched).
        private static readonly Dictionary<ConfigBinding, Action> _staged = new();
        private static GameObject? _modal;
        private static Action? _backAction;   // the window Back button's original action (closes the settings)
        private static GameObject? _builtPanel;     // our cloned Mods-tab panel (kept for runtime teardown)
        private static GameObject? _taskbarButton;  // our "Mods" taskbar button (kept for runtime teardown)

        private sealed class ModButton
        {
            public ModInfo Mod = null!;
            public TextMeshProUGUI? Label;
        }

        private sealed class PendingDropdown
        {
            public TMP_Dropdown Dd = null!;
            public ConfigBinding B = null!;
            public float Until;   // unscaled-time deadline: keep re-applying options until then, to outlast the game's late re-init
        }

        private static readonly List<PendingDropdown> _pendingDropdowns = new();

        // Reset so the tab rebuilds against a fresh scene's UI tree (called by the Host on scene change).
        internal static void Invalidate()
        {
            _warned = false;
            _warnedOnce = false;
            _pendingLogged = false;
            _labelTemplate = null;
            _refToggle = _refSlider = _refDropdown = null;
            _rightContent = null;
            _selected = null;
            _modButtons.Clear();
            _pendingDropdowns.Clear();
            _staged.Clear();
            _backAction = null;
            _modal = null;
            _capturing = null;
            _capturingLabel = null;
            _builtPanel = null;
            _taskbarButton = null;
        }

        // The runtime gate (PatchGate.Enabled) just turned OFF, e.g. the player set Mod Settings Tool's own
        // Enabled row to false and saved. Make the injected store UI inert immediately WITHOUT array surgery:
        // deactivate our taskbar button (so the Mods tab can't be opened) and our tab panel (so its controls
        // aren't clickable), and drop any open capture/modal. The WindowTab stays in m_Tabs (inactive is
        // harmless); Restore() reactivates the button if the gate comes back on without a scene change.
        internal static void Teardown()
        {
            _capturing = null;
            _capturingLabel = null;
            try { HideModal(); } catch { }
            try { if (_taskbarButton != null) _taskbarButton.SetActive(false); } catch { }
            try { if (_builtPanel != null) _builtPanel.SetActive(false); } catch { }
        }

        internal static void Restore()
        {
            try { if (_taskbarButton != null) _taskbarButton.SetActive(true); } catch { }
        }

        // Driven from the Host (every ~0.5s in the store) and synchronously when the Settings page opens. We
        // try to build the moment the settings structures EXIST, even while the submenu is still inactive,
        // so the tab is already present when the player loads the save, not popped in on first open. TryBuild
        // is idempotent (AlreadyBuilt) and no-ops until it finds a clean toggle + slider template, so an early
        // call before the rows resolve just retries; a game build that only creates the rows on first open is
        // covered by the synchronous set_Enable(true) hook in MenuPatches.
        internal static void Poll()
        {
            try
            {
                if (!EscapeMenuManager.HasInstance) return;
                EscapeMenuManager menu = EscapeMenuManager.Instance;
                if (menu == null) return;
                SettingsMenuManager? sm = menu.settingsMenu;
                if (sm == null || sm.gameObject == null) return;
                TryBuild(menu);
            }
            catch
            {
                // fail-soft; the main-menu list does not depend on this
            }
        }

        // Per-frame (called by the Host every frame in the store). Cheap when idle; only does work while a
        // key rebind is being captured.
        internal static void Tick()
        {
            // Deferred dropdown population: re-apply options the frame AFTER a page builds, past the game
            // CustomDropdown's own init (it otherwise shows a stale "Option 1" until the page is rebuilt).
            if (_pendingDropdowns.Count > 0)
            {
                float now = Time.unscaledTime;
                for (int i = _pendingDropdowns.Count - 1; i >= 0; i--)
                {
                    PendingDropdown pd = _pendingDropdowns[i];
                    if (pd.Dd == null) { _pendingDropdowns.RemoveAt(i); continue; }
                    // Re-apply ONLY while the game's CustomDropdown re-init has blanked our options back to
                    // "Option 1"; once they hold (and keep holding through the window) we leave the control
                    // alone, so we never fight a dropdown the player has opened or changed.
                    if (!DropdownApplied(pd.Dd, pd.B)) ApplyDropdown(pd.Dd, pd.B);
                    if (now >= pd.Until) _pendingDropdowns.RemoveAt(i);
                }
            }

            if (_capturing == null) return;

            // If the player switched to another Settings tab mid-capture (our panel got deactivated by the
            // game's OpenTab) without closing the window, the set_Enable(false) cancel path never fired, so
            // cancel here, or we would bind the next key typed in another tab as the hidden mod's keybind.
            if (_builtPanel == null || !_builtPanel.activeInHierarchy) { EndCapture(); return; }

            try
            {
                // Esc is NOT a cancel here (it closes the vanilla menu); a second click on the control cancels.
                KeyCode[] keys = CaptureKeys();
                for (int i = 0; i < keys.Length; i++)
                {
                    KeyCode k = keys[i];
                    if (!IsBindable(k)) continue;
                    if (Input.GetKeyDown(k)) { CommitKey(k); return; }
                }
            }
            catch { EndCapture(); }
        }

        // Keys we let the player bind: not None, not Escape (closes the menu), not mouse buttons (a mouse
        // click is how they cancel/confirm the row).
        private static bool IsBindable(KeyCode k)
        {
            if (k == KeyCode.None || k == KeyCode.Escape) return false;
            if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6) return false;
            return true;
        }

        private static void TryBuild(EscapeMenuManager menu)
        {
            try
            {
                if (menu == null) return;
                SettingsMenuManager? settings = menu.settingsMenu;
                if (settings == null) return;
                TabManager? tabs = settings.m_tabManager;
                if (tabs == null || tabs.transform == null) return;
                if (AlreadyBuilt(tabs)) return;

                // Pick clone templates from a tab's CONTENT ROWS (direct children), not by name and not via
                // a blind GetComponentInChildren, the latter finds a dropdown's hidden item-template Toggle.
                WindowTab? source = null;
                GameObject? refToggle = null, refSlider = null, refDropdown = null, refInput = null;
                Il2CppReferenceArray<WindowTab>? tabArr = tabs.m_Tabs;
                if (tabArr != null)
                {
                    for (int i = 0; i < tabArr.Length; i++)
                    {
                        WindowTab? t = tabArr[i];
                        if (t == null) continue;
                        if (source == null)
                        {
                            GameObject? tog = FindRowTemplate(t, wantSlider: false);
                            GameObject? sli = FindRowTemplate(t, wantSlider: true);
                            if (tog != null && sli != null) { source = t; refToggle = tog; refSlider = sli; }
                        }
                        if (refDropdown == null) refDropdown = FindDropdownTemplate(t);
                        if (refInput == null) refInput = FindInputTemplate(t);
                    }
                }
                if (refInput == null) refInput = FindAnyInputField(); // settings tabs have none, clone any loaded field
                if (source == null || refToggle == null || refSlider == null)
                {
                    // Transient, not a failure: the settings window's tab rows aren't populated yet on this open.
                    // The Host poll + the EscapeMenu OnEnable nudge re-run this until they are, so it's a retry.
                    // Trace once per scene at Debug so it never surfaces as a warning in a release log.
                    if (!_pendingLogged) { _pendingLogged = true; Plugin.Logger.LogDebug("[ModsTab] settings tab rows not ready yet; the Mods tab will inject on a later open."); }
                    return;
                }

                GameObject panel = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
                panel.name = "Mod Settings Tool Tab";
                WindowTab? windowTab = panel.GetComponent<WindowTab>();
                if (windowTab == null) { UnityEngine.Object.Destroy(panel); return; }
                windowTab.TabName = TabName;

                // Hide our clone until the player clicks the Mods button (the game's OpenTab activates ours +
                // deactivates the others); otherwise it superimposes on whatever tab is open.
                panel.SetActive(false);

                Transform? content = ContentOf(windowTab);
                if (content == null) { UnityEngine.Object.Destroy(panel); return; }
                ClearChildren(content);
                try { windowTab.firstSelectedObject = null; windowTab.backupSelectedObject = null; } catch { }

                // Hook the window Save/Back buttons NOW, before PopulateMods adds the left-pane mod rows, so
                // the scan only sees the cleared window frame, never a mod row whose name/label contains
                // "save"/"back" (e.g. a mod called "BetterSave") that could otherwise steal the wiring.
                HookWindowButtons(panel);

                Plugin.Logger.LogDebug($"[ModsTab] building two-pane, templates toggle:{refToggle != null} slider:{refSlider != null} dropdown:{refDropdown != null} input:{refInput != null}; mods:{ModRegistry.Cache.Count}.");

                PopulateMods(content, refToggle!, refSlider!, refDropdown, refInput);

                // Build the taskbar button BEFORE committing the tab. If the taskbar isn't ready yet, this
                // returns null, treat that as an incomplete build: destroy the clone and return WITHOUT
                // appending, so AlreadyBuilt stays false and a later poll retries. Otherwise the WindowTab
                // would be appended (AlreadyBuilt → true) with no button to open it, hidden forever.
                GameObject? taskbarButton = AddTaskbarButton(tabs, windowTab);
                if (taskbarButton == null)
                {
                    try { UnityEngine.Object.Destroy(panel); } catch { }
                    return;
                }
                AppendTab(tabs, windowTab);
                _builtPanel = panel;
                _taskbarButton = taskbarButton;
                Plugin.Logger.LogInfo("[ModsTab] Mods settings tab injected.");
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Logger.LogWarning($"[ModsTab] could not build the tab (the main-menu list still works): {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ── the two-pane page ─────────────────────────────────────────────────────────────────────────

        private static void PopulateMods(Transform content, GameObject refToggle, GameObject refSlider, GameObject? refDropdown, GameObject? refInput)
        {
            try
            {
                TextMeshProUGUI? template = refToggle.GetComponentInChildren<TextMeshProUGUI>(true);
                if (template == null)
                {
                    LogOnce("[ModsTab] no TMP label template on the toggle row; page skipped.");
                    return;
                }

                _labelTemplate = template;
                _refToggle = refToggle;
                _refSlider = refSlider;
                _refDropdown = refDropdown;
                _refInput = refInput;
                _selected = null;
                _modButtons.Clear();

                // Stop the tab's own scroll/layout from fighting our two panes, then carve the content into a
                // LEFT slot (mod list) and a RIGHT slot (config page), each its own independent scroll view.
                NeutralizeContent(content);
                Transform leftSlot = MakeSlot(content, "MST LeftSlot", 0f, 0.34f);
                Transform rightSlot = MakeSlot(content, "MST RightSlot", 0.35f, 1f);

                Transform leftContent = UiBuild.BuildVerticalScroll(template, leftSlot, new Color(0f, 0f, 0f, 0.50f));
                _rightContent = UiBuild.BuildVerticalScroll(template, rightSlot, new Color(0f, 0f, 0f, 0.40f));
                Plugin.Logger.LogDebug("[ModsTab] two-pane scaffold built; populating the mod list.");

                UiBuild.MakeLabel(template, leftContent, $"Installed Mods ({ModRegistry.Cache.Count})", new Color(0.93f, 0.93f, 0.93f, 1f), 18f, false, false);
                foreach (ModInfo mod in ModRegistry.Cache) AddModButton(leftContent, mod);
                UiBuild.ResetScrollAndRefreshHint(leftContent); // left list is static after this, top + evaluate its hint

                ShowRightMessage("Select a mod on the left to view its settings.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] populate failed: {ex}");
            }
        }

        private static void AddModButton(Transform parent, ModInfo mod)
        {
            try
            {
                GameObject go = UiBuild.MakeLabel(_labelTemplate!, parent, "  " + mod.Name, HealthPalette.For(mod.Health), 16f, true, true);
                go.name = "MST Mod " + mod.Name;

                LayoutElement le = go.GetComponent<LayoutElement>();
                if (le == null) le = go.AddComponent<LayoutElement>();
                le.minHeight = 26f;
                le.preferredHeight = -1f;
                le.flexibleWidth = 1f;

                Button btn = go.AddComponent<Button>();
                TextMeshProUGUI? label = go.GetComponent<TextMeshProUGUI>();
                UiListeners.OnClick(btn, () => Select(mod));
                _modButtons.Add(new ModButton { Mod = mod, Label = label });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] mod button '{mod.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Select(ModInfo mod)
        {
            try
            {
                // Reselecting the mod already shown is a no-op: rebuilding would repaint the controls from the
                // LIVE config and visually drop staged edits while Save would still apply them.
                if (ReferenceEquals(mod, _selected)) return;
                // Switching mods with unsaved edits raises the confirm modal first. Save/Discard then PROCEED
                // to the new mod (they do NOT leave the settings); Cancel keeps the current page.
                if (IsDirty) { ShowUnsavedModal(() => DoSelect(mod)); return; }
                DoSelect(mod);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] select '{mod.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void DoSelect(ModInfo mod)
        {
            _selected = mod;
            foreach (ModButton mb in _modButtons)
            {
                if (mb.Label == null) continue;
                bool sel = ReferenceEquals(mb.Mod, mod);
                mb.Label.text = (sel ? "> " : "  ") + mb.Mod.Name;
                try { mb.Label.fontStyle = sel ? FontStyles.Bold : FontStyles.Normal; } catch { }
            }
            BuildConfigPage(mod);
        }

        private static void BuildConfigPage(ModInfo mod)
        {
            if (_rightContent == null || _labelTemplate == null) return;
            _capturing = null;
            _capturingLabel = null;
            ClearChildren(_rightContent);

            // Page-scoped delegate roots: clear the previous mod's right-pane handlers (its controls were just
            // destroyed) and collect this page's into the page scope, so browsing mods doesn't leak them.
            UiListeners.BeginPageScope();
            try
            {
                string ver = string.IsNullOrEmpty(mod.Version) ? "" : $"  v{mod.Version}";
                UiBuild.MakeLabel(_labelTemplate, _rightContent, mod.Name + ver, new Color(0.96f, 0.96f, 0.96f, 1f), 20f, true, false);
                if (!string.IsNullOrEmpty(mod.Guid))
                    UiBuild.MakeLabel(_labelTemplate, _rightContent, mod.Guid, new Color(0.80f, 0.80f, 0.80f, 1f), 12f, true, false);
                if (mod.Health != HealthStatus.Healthy && !string.IsNullOrEmpty(mod.IssueSummary))
                    UiBuild.MakeLabel(_labelTemplate, _rightContent, mod.IssueSummary, HealthPalette.For(mod.Health), 14f, true, false);

                if (!mod.HasSettings)
                {
                    UiBuild.MakeLabel(_labelTemplate, _rightContent, "No settings to change.", new Color(0.82f, 0.82f, 0.82f, 1f), 16f, true, false);
                    return;
                }

                string lastSection = "\0";
                bool firstHeader = true;
                foreach (ConfigBinding b in mod.Settings)
                {
                    if (b.Section != lastSection)
                    {
                        lastSection = b.Section;
                        if (!string.IsNullOrEmpty(b.Section))
                        {
                            if (!firstHeader) UiBuild.MakeSeparator(_rightContent); // subtle divider between sections
                            firstHeader = false;
                            GameObject sec = UiBuild.MakeLabel(_labelTemplate, _rightContent, b.Section, new Color(0.78f, 0.86f, 1f, 1f), 15f, true, false);
                            try { TextMeshProUGUI? st = sec.GetComponent<TextMeshProUGUI>(); if (st != null) st.fontStyle = FontStyles.Bold; } catch { }
                        }
                    }
                    AddControl(b);
                }
            }
            finally
            {
                UiListeners.EndPageScope();
                UiBuild.ResetScrollAndRefreshHint(_rightContent); // page just (re)built, scroll to its header + refresh hint
            }
        }

        private static void AddControl(ConfigBinding b)
        {
            try
            {
                switch (b.Kind)
                {
                    case ControlKind.Toggle:
                        AddToggle(_refToggle!, _rightContent!, b.Key, b.Value is bool bv && bv, v => StageOrClear(b, v != AsBool(b.Value), () => { b.Value = v; }));
                        break;
                    case ControlKind.Slider:
                        bool whole = b.IsWholeNumber;
                        AddSlider(_refSlider!, _rightContent!, b.Key, (float)b.Min, (float)b.Max, ToFloat(b.Value), whole, v => StageOrClear(b, !SliderEquals(v, ToFloat(b.Value), b), () => b.SetNumber(v)));
                        break;
                    case ControlKind.EnumDropdown:
                    case ControlKind.ChoiceDropdown:
                        AddDropdown(b);
                        break;
                    case ControlKind.KeyBind:
                        AddKeyBind(b);
                        break;
                    case ControlKind.IntInput:
                    case ControlKind.FloatInput:
                    case ControlKind.TextInput:
                        AddInputField(b);
                        break;
                    default: // Unsupported
                        AddReadOnly(b);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] control '{b.Key}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AddDropdown(ConfigBinding b)
        {
            if (_refDropdown == null) { AddReadOnly(b); return; }
            try
            {
                GameObject go = UnityEngine.Object.Instantiate(_refDropdown, _rightContent, false);
                go.name = "MST " + b.Key;
                TameRow(go, 54f);

                TMP_Dropdown? dd = go.GetComponentInChildren<TMP_Dropdown>(true);

                TextMeshProUGUI? title = RowTitle(go, dd);
                if (title != null)
                {
                    UiBuild.DisableLoc(title);
                    title.text = b.Key;
                    try { title.enableWordWrapping = false; title.overflowMode = TextOverflowModes.Overflow; } catch { }
                }

                if (dd != null)
                {
                    // CRITICAL: replace the cloned dropdown's PERSISTENT onValueChanged (a settings dropdown
                    // drives a real game setting, e.g. resolution) BEFORE touching its value, UiListeners
                    // swaps the whole event object and roots ours.
                    UiListeners.OnChanged(dd, i => StageOrClear(b, i != b.CurrentChoiceIndex(), () => b.SetChoice(i)));

                    // Speed up the dropdown's own popup-list scrolling (the Template's ScrollRect).
                    try { ScrollRect tsr = dd.GetComponentInChildren<ScrollRect>(true); if (tsr != null) tsr.scrollSensitivity = 45f; } catch { }

                    // Populate now AND again next frame (Tick): the game's CustomDropdown re-initialises after
                    // we build, blanking our options ("Option 1") until the page is rebuilt, the deferred
                    // re-apply lands past that init.
                    ApplyDropdown(dd, b);
                    _pendingDropdowns.Add(new PendingDropdown { Dd = dd, B = b, Until = Time.unscaledTime + 0.35f });
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] dropdown '{b.Key}' failed: {ex.GetType().Name}: {ex.Message}");
                AddReadOnly(b);
            }
        }

        // ClearOptions/AddOptions (not raw options.Add) so the dropdown rebuilds its caption + item template.
        private static void ApplyDropdown(TMP_Dropdown dd, ConfigBinding b)
        {
            try
            {
                var opts = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (string c in b.Choices) opts.Add(c);
                dd.ClearOptions();
                dd.AddOptions(opts);
                dd.SetValueWithoutNotify(b.CurrentChoiceIndex());
                dd.RefreshShownValue();
                try { dd.Hide(); } catch { }                                              // ensure the popup is closed
                try { if (dd.template != null) dd.template.gameObject.SetActive(false); } catch { } // hide the stuck template
            }
            catch { }
        }

        // True when the dropdown currently shows exactly our choices, count matches AND the selected row's
        // label matches, i.e. the game's CustomDropdown has NOT blanked them back to "Option 1".
        private static bool DropdownApplied(TMP_Dropdown dd, ConfigBinding b)
        {
            try
            {
                var opts = dd.options;
                if (opts == null || opts.Count != b.Choices.Length) return false;
                // Every option must match, a stale NON-selected label (template still holding "Medium" instead
                // of "High") would otherwise let the player pick a value that maps to the wrong setting index.
                for (int i = 0; i < b.Choices.Length; i++)
                {
                    var od = opts[i];
                    if (od == null || !string.Equals(od.text, b.Choices[i], StringComparison.Ordinal)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        // A keybind row: the setting name on the left, a distinct boxed "keycap" button on the right showing
        // the bound key. Click it to rebind (it shows "Press a key…"); press a key to set, or click again to cancel.
        private static void AddKeyBind(ConfigBinding b)
        {
            try
            {
                GameObject row = UiBuild.NewRect("MST Key " + b.Key, _rightContent!);
                TameRow(row, 46f);

                GameObject lblGo = UiBuild.MakeLabel(_labelTemplate!, row.transform, b.Key, new Color(0.9f, 0.9f, 0.9f, 1f), 16f, false, false);
                RectTransform lrt = lblGo.GetComponent<RectTransform>();
                if (lrt != null) { lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0.55f, 1f); lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = new Vector2(-4f, 0f); }

                // A light "keycap": pale raised box, dark bold key text, a bevel-ish outline. Compact, right-aligned.
                GameObject cap = UiBuild.NewRect("KeyCap", row.transform);
                RectTransform crt = cap.GetComponent<RectTransform>();
                if (crt != null) { crt.anchorMin = new Vector2(0.70f, 0.13f); crt.anchorMax = new Vector2(1f, 0.87f); crt.offsetMin = new Vector2(4f, 0f); crt.offsetMax = new Vector2(-10f, 0f); }
                Image capBg = cap.AddComponent<Image>();
                if (capBg != null) { capBg.color = new Color(0.88f, 0.89f, 0.92f, 1f); capBg.raycastTarget = true; }
                Outline ol = cap.AddComponent<Outline>();
                if (ol != null) { ol.effectColor = new Color(0.30f, 0.32f, 0.38f, 0.95f); ol.effectDistance = new Vector2(2f, -2f); }
                Shadow sh = cap.AddComponent<Shadow>();
                if (sh != null) { sh.effectColor = new Color(1f, 1f, 1f, 0.5f); sh.effectDistance = new Vector2(-1f, 1f); } // top-left highlight
                Button btn = cap.AddComponent<Button>();

                GameObject capLbl = UiBuild.MakeLabel(_labelTemplate!, cap.transform, KeyName(b.Value), new Color(0.10f, 0.10f, 0.13f, 1f), 16f, false, false);
                RectTransform clrt = capLbl.GetComponent<RectTransform>();
                if (clrt != null) { clrt.anchorMin = Vector2.zero; clrt.anchorMax = Vector2.one; clrt.offsetMin = new Vector2(8f, 0f); clrt.offsetMax = new Vector2(-8f, 0f); }
                TextMeshProUGUI? capTmp = capLbl.GetComponent<TextMeshProUGUI>();
                if (capTmp != null) { capTmp.alignment = TextAlignmentOptions.Center; try { capTmp.fontStyle = FontStyles.Bold; } catch { } }

                UiListeners.OnClick(btn, () =>
                {
                    if (_capturing == b) { EndCapture(); return; } // a second click cancels
                    if (_capturing != null) EndCapture();          // restore a DIFFERENT row left mid-capture
                    _capturing = b;
                    _capturingLabel = capTmp;
                    _capturePrevText = capTmp != null ? capTmp.text : "";
                    if (capTmp != null) capTmp.text = "Press a key…";
                });

                // A small "Clear" button so KeyCode.None (the disabled/unbound value many mods use) is reachable:
                // capture alone can never produce None, since there is no key to press for it.
                GameObject clr = UiBuild.NewRect("KeyClear", row.transform);
                RectTransform xrt = clr.GetComponent<RectTransform>();
                if (xrt != null) { xrt.anchorMin = new Vector2(0.55f, 0.16f); xrt.anchorMax = new Vector2(0.69f, 0.84f); xrt.offsetMin = new Vector2(2f, 0f); xrt.offsetMax = new Vector2(-2f, 0f); }
                Image xbg = clr.AddComponent<Image>();
                if (xbg != null) { xbg.color = new Color(1f, 1f, 1f, 0.10f); xbg.raycastTarget = true; }
                Button xbtn = clr.AddComponent<Button>();
                GameObject xlbl = UiBuild.MakeLabel(_labelTemplate!, clr.transform, "Clear", new Color(0.82f, 0.82f, 0.86f, 1f), 12f, false, false);
                RectTransform xlrt = xlbl.GetComponent<RectTransform>();
                if (xlrt != null) { xlrt.anchorMin = Vector2.zero; xlrt.anchorMax = Vector2.one; xlrt.offsetMin = Vector2.zero; xlrt.offsetMax = Vector2.zero; }
                TextMeshProUGUI? xtmp = xlbl.GetComponent<TextMeshProUGUI>();
                if (xtmp != null) xtmp.alignment = TextAlignmentOptions.Center;
                UiListeners.OnClick(xbtn, () =>
                {
                    if (_capturing != null) EndCapture(); // cancel/restore any active capture, this row OR another
                    StageOrClear(b, KeyCode.None.ToString() != (b.Value?.ToString() ?? ""), () => b.SetKey(KeyCode.None));
                    try { if (capTmp != null) capTmp.text = KeyCode.None.ToString(); } catch { }
                });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] keybind '{b.Key}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Unsupported types: listed read-only with the current value.
        private static void AddReadOnly(ConfigBinding b)
        {
            string val = b.Value?.ToString() ?? "";
            UiBuild.MakeLabel(_labelTemplate!, _rightContent!, $"{b.Key}: {val}  (read-only)", new Color(0.78f, 0.78f, 0.78f, 1f), 15f, true, false);
        }

        // IntInput / FloatInput / TextInput: a left label + a right TMP_InputField. CLONE a real loaded field
        // when one exists (it has a working caret + selection); otherwise fall back to building one from
        // scratch. TextInput rows also get a colour swatch previewing any colour code the value parses to.
        private static void AddInputField(ConfigBinding b)
        {
            if (_refInput != null) AddClonedInput(b);
            else AddScratchInput(b);
        }

        private static Image? MakeSwatch(Transform row)
        {
            try
            {
                GameObject swGo = UiBuild.NewRect("Swatch", row);
                RectTransform srt = swGo.GetComponent<RectTransform>();
                if (srt != null) { srt.anchorMin = new Vector2(1f, 0.5f); srt.anchorMax = new Vector2(1f, 0.5f); srt.pivot = new Vector2(1f, 0.5f); srt.sizeDelta = new Vector2(26f, 26f); srt.anchoredPosition = new Vector2(-4f, 0f); }
                Image swatch = swGo.AddComponent<Image>();
                if (swatch != null) swatch.raycastTarget = false;
                return swatch;
            }
            catch { return null; }
        }

        private static void AddClonedInput(ConfigBinding b)
        {
            try
            {
                bool isText = b.Kind == ControlKind.TextInput;

                GameObject row = UiBuild.NewRect("MST " + b.Key, _rightContent!);
                TameRow(row, 46f);

                GameObject lblGo = UiBuild.MakeLabel(_labelTemplate!, row.transform, b.Key, new Color(0.9f, 0.9f, 0.9f, 1f), 16f, false, false);
                RectTransform lrt = lblGo.GetComponent<RectTransform>();
                if (lrt != null) { lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0.48f, 1f); lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = new Vector2(-4f, 0f); }

                GameObject inGo = UnityEngine.Object.Instantiate(_refInput!, row.transform, false);
                inGo.name = "Input";
                inGo.SetActive(true);
                RectTransform irt = inGo.GetComponent<RectTransform>();
                if (irt != null) { irt.anchorMin = new Vector2(0.48f, 0.12f); irt.anchorMax = new Vector2(1f, 0.88f); irt.offsetMin = new Vector2(4f, 0f); irt.offsetMax = new Vector2(isText ? -34f : -4f, 0f); irt.localScale = Vector3.one; }

                TMP_InputField? input = inGo.GetComponent<TMP_InputField>();
                if (input == null) { UnityEngine.Object.Destroy(inGo); AddScratchInput(b); return; }

                foreach (TextMeshProUGUI t in inGo.GetComponentsInChildren<TextMeshProUGUI>(true)) UiBuild.DisableLoc(t);
                NormalizeClonedInput(inGo, input);

                input.contentType = b.Kind == ControlKind.IntInput ? TMP_InputField.ContentType.IntegerNumber
                                  : b.Kind == ControlKind.FloatInput ? TMP_InputField.ContentType.DecimalNumber
                                  : TMP_InputField.ContentType.Standard;
                input.lineType = TMP_InputField.LineType.SingleLine;
                input.readOnly = false;
                input.interactable = true;
                input.SetTextWithoutNotify(b.Value?.ToString() ?? "");

                Image? swatch = isText ? MakeSwatch(row.transform) : null;
                if (swatch != null) UpdateSwatch(swatch, input.text);

                WireInputEndEdit(b, input);
                if (swatch != null) { Image sw = swatch; UiListeners.OnValueChanged(input, s => UpdateSwatch(sw, s)); }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] cloned input '{b.Key}' failed: {ex.GetType().Name}: {ex.Message}");
                AddScratchInput(b);
            }
        }

        // The clone source is whatever TMP_InputField Resources hands back first (often a game/other-mod field
        // with a rounded "pill" background and a placeholder or title text), so normalize the clone to a plain,
        // readable rectangle that holds exactly ONE visible text, the editable textComponent. Without this a
        // pill-sprite source renders oval and its placeholder/label overlaps the value text.
        private static void NormalizeClonedInput(GameObject inGo, TMP_InputField input)
        {
            try
            {
                // Background → flat rectangle (drop any rounded sprite on the root and the Selectable target).
                Image? rootBg = inGo.GetComponent<Image>();
                FlattenImage(rootBg);
                Image? selImg = null;
                try { selImg = input.image; } catch { }
                FlattenImage(selImg);

                TMP_Text? txt = input.textComponent;
                int keepText = txt != null ? txt.GetInstanceID() : 0;
                int keepBg = rootBg != null ? rootBg.GetInstanceID() : 0;
                int keepSel = selImg != null ? selImg.GetInstanceID() : 0;

                // The field must hold exactly ONE visible text. Drop the placeholder entirely (so the field can't
                // re-show it when the value is empty) and disable every OTHER graphic the source carried, a
                // mirrored duplicate of the value text and any numeric-stepper "+/-" marks. Those marks may be
                // legacy UI.Text or Image, so sweep the Graphic base, not just TMP.
                try { input.placeholder = null; } catch { }

                // Drop any serialized onValueChanged / onEndEdit the arbitrary clone source carried, by replacing
                // the whole event object (MST-RULE-12). AddClonedInput re-adds our staged handlers afterward; a
                // numeric input adds NO onValueChanged, so without this its template's persistent listener (a real
                // game/mod action) would fire on every keystroke.
                try { input.onValueChanged = new TMP_InputField.OnChangeEvent(); } catch { }
                try { input.onEndEdit = new TMP_InputField.SubmitEvent(); } catch { }
                // ...and the focus/submit/selection events too, the arbitrary clone source may have serialized
                // onSelect/onDeselect/onSubmit/text-selection actions that would fire on focus, blur, or submit.
                try { input.onSubmit = new TMP_InputField.SubmitEvent(); } catch { }
                try { input.onSelect = new TMP_InputField.SelectionEvent(); } catch { }
                try { input.onDeselect = new TMP_InputField.SelectionEvent(); } catch { }
                try { input.onTextSelection = new TMP_InputField.TextSelectionEvent(); } catch { }
                try { input.onEndTextSelection = new TMP_InputField.TextSelectionEvent(); } catch { }

                foreach (Graphic g in inGo.GetComponentsInChildren<Graphic>(true))
                {
                    if (g == null) continue;
                    int id = g.GetInstanceID();
                    if (id == keepText || id == keepBg || id == keepSel) continue;
                    if (g.gameObject.name.Contains("Caret")) continue; // the field regenerates this on focus
                    g.enabled = false;
                }

                // Neutralize any cloned sub-controls (e.g. a stepper's +/- buttons) so a stray click can't drive
                // the source field's serialized action (MST-RULE-12). The input itself stays interactable.
                foreach (Selectable s in inGo.GetComponentsInChildren<Selectable>(true))
                {
                    if (s == null) continue;
                    try { if (s.GetInstanceID() == input.GetInstanceID()) continue; } catch { }
                    try { s.interactable = false; s.enabled = false; } catch { }
                }

                if (txt != null)
                {
                    txt.enableAutoSizing = false;
                    txt.fontSize = 18f;
                    txt.richText = false; // show colour codes etc. literally, never parse the value as TMP markup
                    // Strip any negative/mirror scale baked into the source transforms on the kept text's chain.
                    Transform tr = txt.transform;
                    int rootId = inGo.transform.GetInstanceID();
                    for (int i = 0; i < 8 && tr != null; i++)
                    {
                        tr.localScale = Vector3.one;
                        if (tr.GetInstanceID() == rootId) break;
                        tr = tr.parent;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] normalize input '{input?.name}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void FlattenImage(Image? img)
        {
            if (img == null) return;
            img.overrideSprite = null;
            img.sprite = null;
            img.type = Image.Type.Simple;
            img.color = new Color(1f, 1f, 1f, 0.12f);
            img.raycastTarget = true;
        }

        // Fallback when no real TMP_InputField is available to clone.
        private static void AddScratchInput(ConfigBinding b)
        {
            try
            {
                bool isText = b.Kind == ControlKind.TextInput;

                GameObject row = UiBuild.NewRect("MST " + b.Key, _rightContent!);
                TameRow(row, 46f);

                GameObject lblGo = UiBuild.MakeLabel(_labelTemplate!, row.transform, b.Key, new Color(0.9f, 0.9f, 0.9f, 1f), 16f, false, false);
                RectTransform lrt = lblGo.GetComponent<RectTransform>();
                if (lrt != null) { lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0.48f, 1f); lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = new Vector2(-4f, 0f); }

                GameObject inGo = UiBuild.NewRect("Input", row.transform);
                RectTransform irt = inGo.GetComponent<RectTransform>();
                if (irt != null) { irt.anchorMin = new Vector2(0.48f, 0.12f); irt.anchorMax = new Vector2(1f, 0.88f); irt.offsetMin = new Vector2(4f, 0f); irt.offsetMax = new Vector2(isText ? -34f : -4f, 0f); }
                Image inBg = inGo.AddComponent<Image>();
                if (inBg != null) { inBg.color = new Color(1f, 1f, 1f, 0.12f); inBg.raycastTarget = true; }

                GameObject area = UiBuild.NewRect("Text Area", inGo.transform);
                RectTransform art = area.GetComponent<RectTransform>();
                if (art != null) { art.anchorMin = new Vector2(0f, 0f); art.anchorMax = new Vector2(1f, 1f); art.offsetMin = new Vector2(6f, 2f); art.offsetMax = new Vector2(-6f, -2f); }
                area.AddComponent<RectMask2D>();

                GameObject txtGo = UiBuild.MakeLabel(_labelTemplate!, area.transform, b.Value?.ToString() ?? "", new Color(1f, 1f, 1f, 1f), 15f, false, false);
                RectTransform trt = txtGo.GetComponent<RectTransform>();
                if (trt != null) { trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 1f); trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero; }
                TextMeshProUGUI? txt = txtGo.GetComponent<TextMeshProUGUI>();

                Image? swatch = null;
                if (isText)
                {
                    GameObject swGo = UiBuild.NewRect("Swatch", row.transform);
                    RectTransform srt = swGo.GetComponent<RectTransform>();
                    if (srt != null) { srt.anchorMin = new Vector2(1f, 0.5f); srt.anchorMax = new Vector2(1f, 0.5f); srt.pivot = new Vector2(1f, 0.5f); srt.sizeDelta = new Vector2(26f, 26f); srt.anchoredPosition = new Vector2(-4f, 0f); }
                    swatch = swGo.AddComponent<Image>();
                    if (swatch != null) swatch.raycastTarget = false;
                }

                TMP_InputField input = inGo.AddComponent<TMP_InputField>();
                if (input != null && txt != null)
                {
                    input.textViewport = art;
                    input.textComponent = txt;
                    input.contentType = b.Kind == ControlKind.IntInput ? TMP_InputField.ContentType.IntegerNumber
                                      : b.Kind == ControlKind.FloatInput ? TMP_InputField.ContentType.DecimalNumber
                                      : TMP_InputField.ContentType.Standard;
                    input.lineType = TMP_InputField.LineType.SingleLine;
                    input.caretWidth = 3;
                    input.customCaretColor = true;
                    input.caretColor = new Color(1f, 1f, 1f, 1f);
                    input.selectionColor = new Color(0.30f, 0.55f, 1f, 0.55f);
                    input.caretBlinkRate = 0f; // solid (always visible) caret
                    input.text = b.Value?.ToString() ?? "";

                    WireInputEndEdit(b, input);
                    if (swatch != null)
                    {
                        Image sw = swatch;
                        UpdateSwatch(sw, input.text);
                        UiListeners.OnValueChanged(input, s => UpdateSwatch(sw, s));
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] input '{b.Key}' failed: {ex.GetType().Name}: {ex.Message}");
                AddReadOnly(b);
            }
        }

        // End-edit commit for a cloned/scratch input. A text field stages the raw string; a NUMERIC field only
        // stages when the text parses, invalid/blank numeric input is rejected and the field reverts to the live
        // value, so SaveStaged can never clear an unparsable edit and log it as "saved" while the live
        // ConfigEntry stays unchanged (and the box still shows the bad text).
        private static void WireInputEndEdit(ConfigBinding b, TMP_InputField input)
        {
            bool numeric = b.Kind == ControlKind.IntInput || b.Kind == ControlKind.FloatInput;
            UiListeners.OnEndEdit(input, s =>
            {
                if (numeric)
                {
                    // Convert STRAIGHT to the target type (no double round-trip) so a wide ulong/long/decimal
                    // keeps full precision, and reject anything not representable OR out of the declared range,
                    // dropping the staged edit and reverting the box, so Save can never log a no-op (or a rounded
                    // / out-of-range value) as saved.
                    object? parsed = b.ParseTyped(s);
                    if (parsed != null && b.InRange(parsed))
                        StageOrClear(b, !Equals(parsed, b.Value), () => { b.Value = parsed; });
                    else
                    {
                        _staged.Remove(b);
                        try { input.SetTextWithoutNotify(b.Value?.ToString() ?? ""); } catch { }
                    }
                }
                else
                {
                    StageOrClear(b, !string.Equals(s, b.Value?.ToString() ?? "", StringComparison.Ordinal), () => CommitInput(b, s));
                }
            });
        }

        private static void CommitInput(ConfigBinding b, string s)
        {
            try
            {
                if (b.Kind == ControlKind.IntInput || b.Kind == ControlKind.FloatInput)
                {
                    if (double.TryParse(s, out double d)) b.SetNumber(d);
                }
                else
                {
                    b.Value = s;
                }
            }
            catch { }
        }

        private static void UpdateSwatch(Image? swatch, string s)
        {
            if (swatch == null) return;
            try
            {
                if (TryParseColor(s, out Color c)) { swatch.color = c; swatch.enabled = true; }
                else swatch.enabled = false;
            }
            catch { }
        }

        // Parse a colour code of any common form: HTML hex (#RGB / #RRGGBB / #RRGGBBAA), HTML/CSS names
        // (red, cyan, …), or rgb()/rgba()/comma lists (0–1 or 0–255 components).
        private static bool TryParseColor(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (ColorUtility.TryParseHtmlString(s, out c)) return true;
            if (!s.StartsWith("#") && ColorUtility.TryParseHtmlString("#" + s, out c)) return true;

            string body = s;
            int p = body.IndexOf('(');
            int q = body.LastIndexOf(')');
            if (p >= 0 && q > p) body = body.Substring(p + 1, q - p - 1);
            string[] parts = body.Split(',');
            if (parts.Length == 3 || parts.Length == 4)
            {
                if (TryComp(parts[0], out float r) && TryComp(parts[1], out float g) && TryComp(parts[2], out float bl))
                {
                    float a = 1f;
                    if (parts.Length == 4) TryComp(parts[3], out a);
                    c = new Color(r, g, bl, a);
                    return true;
                }
            }
            return false;
        }

        private static bool TryComp(string s, out float v)
        {
            v = 0f;
            if (!float.TryParse(s.Trim(), out float f)) return false;
            v = f > 1f ? Mathf.Clamp01(f / 255f) : Mathf.Clamp01(f);
            return true;
        }

        private static void ShowRightMessage(string msg)
        {
            if (_rightContent == null || _labelTemplate == null) return;
            ClearChildren(_rightContent);
            UiBuild.MakeLabel(_labelTemplate, _rightContent, msg, new Color(0.8f, 0.8f, 0.8f, 1f), 16f, true, false);
            UiBuild.ResetScrollAndRefreshHint(_rightContent); // top + single-line message never overflows, so clears any stale hint
        }

        // ── staged save / discard ─────────────────────────────────────────────────────────────────────

        private static void Stage(ConfigBinding b, Action apply) => _staged[b] = apply;

        // Stage only an ACTUAL change; if a control is set back to its original value (or a text field is just
        // focused and left unedited), drop the stage so the mod isn't flagged dirty / the modal isn't raised.
        private static void StageOrClear(ConfigBinding b, bool changed, Action apply)
        {
            if (changed) _staged[b] = apply;
            else _staged.Remove(b);
        }

        // Scale-aware change test for sliders: a fixed epsilon hides real edits on a tiny range (e.g. a 0..0.001
        // multiplier), so the tolerance tracks the range. Whole-number sliders snap to integers, so a real change
        // always exceeds it.
        private static bool SliderEquals(float a, float c, ConfigBinding b)
        {
            double range = b.Max - b.Min;
            double tol = range > 0 ? Math.Max(1e-7, range * 1e-6) : 1e-7;
            return Math.Abs(a - c) < tol;
        }
        private static bool AsBool(object? v) => v is bool x && x;

        private static bool IsDirty => _staged.Count > 0;

        private static void SaveStaged()
        {
            if (_staged.Count == 0) return;
            foreach (KeyValuePair<ConfigBinding, Action> kv in _staged)
            {
                try { kv.Value(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[ModsTab] save '{kv.Key.Key}' failed: {ex.Message}"); }
            }
            int n = _staged.Count;
            _staged.Clear();
            Plugin.Logger.LogInfo($"[ModsTab] saved {n} staged mod setting(s) to the live config.");
        }

        private static void DiscardStaged() => _staged.Clear();   // live entries were never touched

        private static void RebuildCurrentPage()
        {
            try
            {
                foreach (ModInfo m in ModRegistry.Cache)
                    if (ReferenceEquals(m, _selected)) { BuildConfigPage(m); return; }
            }
            catch { }
        }

        // Hook the window's Save / Back buttons (cloned into our tab panel): Save also flushes our staging;
        // Back is guarded so unsaved changes raise the confirm modal first. Diagnostics report what we found.
        private static void HookWindowButtons(GameObject panel)
        {
            try
            {
                Il2CppArrayBase<Button> buttons = panel.GetComponentsInChildren<Button>(true);
                bool save = false, back = false;
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button btn = buttons[i];
                    if (btn == null) continue;
                    if (!save && ButtonMatches(btn, "Save", SaveHints))
                    {
                        UiListeners.AddClick(btn, SaveStaged); // additive: vanilla save + our flush
                        save = true;
                    }
                    else if (!back && ButtonMatches(btn, "Back", BackHints))
                    {
                        Button.ButtonClickedEvent original = btn.onClick;
                        _backAction = () => { try { original.Invoke(); } catch { } };
                        UiListeners.OnClick(btn, () => { if (IsDirty) ShowUnsavedModal(CloseSettings); else _backAction?.Invoke(); });
                        back = true;
                    }
                }
                Plugin.Logger.LogDebug($"[ModsTab] window buttons hooked, save:{save} back:{back}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ModsTab] hook buttons failed: {ex.Message}"); }
        }

        private static string ButtonText(Button btn)
        {
            try { TextMeshProUGUI t = btn.GetComponentInChildren<TextMeshProUGUI>(true); return t != null ? (t.text ?? "").Trim() : ""; }
            catch { return ""; }
        }

        // Identifiers (GameObject name + serialized onClick method) are authored in code and never localized,
        // so prefer them over the visible label, then a non-English UI language still wires Save/Back correctly.
        private static readonly string[] SaveHints = { "save" };
        private static readonly string[] BackHints = { "back" };

        private static bool ButtonMatches(Button btn, string englishLabel, string[] hints)
        {
            try
            {
                if (ButtonText(btn).Equals(englishLabel, StringComparison.OrdinalIgnoreCase)) return true; // English UI
                string n = btn.gameObject != null ? (btn.gameObject.name ?? "") : "";
                for (int i = 0; i < hints.Length; i++) if (n.IndexOf(hints[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
                Button.ButtonClickedEvent ev = btn.onClick;
                if (ev != null)
                {
                    int c = ev.GetPersistentEventCount();
                    for (int i = 0; i < c; i++)
                    {
                        string m = ev.GetPersistentMethodName(i) ?? "";
                        for (int h = 0; h < hints.Length; h++) if (m.IndexOf(hints[h], StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ── Esc / close guard ─────────────────────────────────────────────────────────────────────────

        internal static bool IsCapturing => _capturing != null;
        internal static void CancelCapture() => EndCapture();

        // Called from the SettingsMenuManager.Enable=false patch, the settings-close path that BOTH the Back
        // button and the Esc/Cancel input use. Returns false to VETO the close: while capturing a keybind we
        // cancel the capture instead; with unsaved edits we raise the confirm modal instead.
        internal static bool OnSettingsClosing()
        {
            try
            {
                if (_capturing != null) { EndCapture(); return false; }
                if (IsDirty) { ShowUnsavedModal(CloseSettings); return false; }
            }
            catch { }
            return true;
        }

        // No TMP_InputField exists in the settings tabs, so clone any one loaded in memory, a real field has
        // a working caret + selection, unlike one assembled from scratch.
        private static GameObject? FindAnyInputField()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<TMP_InputField>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        TMP_InputField f = all[i];
                        if (f == null || f.gameObject == null) continue;
                        Plugin.Logger.LogDebug($"[ModsTab] input-field template = '{f.gameObject.name}'.");
                        return f.gameObject;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ModsTab] input-field search failed: {ex.Message}"); }
            return null;
        }

        // ── the unsaved-changes modal (Save / Discard Changes / Cancel) ───────────────────────────────

        // onProceed = what happens AFTER Save/Discard: close the settings (Back) or switch to the new mod
        // (mod-switch). Save/Discard never leave the tab on a mod-switch; Cancel always just keeps editing.
        private static void ShowUnsavedModal(Action onProceed)
        {
            ShowModal(
                () => { SaveStaged(); onProceed(); },                        // Save: persist + proceed
                () => { DiscardStaged(); RebuildCurrentPage(); onProceed(); }, // Discard: drop staged + proceed
                HideModal);                                                  // Cancel: keep editing
        }

        private static void CloseSettings()
        {
            HideModal();
            _backAction?.Invoke();
        }

        private static void ShowModal(Action onSave, Action onDiscard, Action onCancel)
        {
            try
            {
                // Cancel any in-progress keybind capture first: a Save/Discard/Cancel prompt is now up, so the
                // next key the player presses must NOT be committed to the hidden keybind behind the modal.
                EndCapture();
                HideModal();
                Transform? parent = ModalParent();
                if (parent == null || _labelTemplate == null) { Plugin.Logger.LogWarning("[ModsTab] no canvas for the modal; not blocking."); onCancel(); return; }

                GameObject backdrop = UiBuild.NewRect("MST Modal", parent);
                RectTransform brt = backdrop.GetComponent<RectTransform>();
                if (brt != null) { brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero; }
                Image bdImg = backdrop.AddComponent<Image>();
                if (bdImg != null) { bdImg.color = new Color(0f, 0f, 0f, 0.65f); bdImg.raycastTarget = true; }
                backdrop.transform.SetAsLastSibling();
                _modal = backdrop;

                GameObject box = UiBuild.NewRect("Box", backdrop.transform);
                RectTransform xrt = box.GetComponent<RectTransform>();
                if (xrt != null) { xrt.anchorMin = xrt.anchorMax = new Vector2(0.5f, 0.5f); xrt.pivot = new Vector2(0.5f, 0.5f); xrt.sizeDelta = new Vector2(560f, 240f); xrt.anchoredPosition = Vector2.zero; }
                Image boxImg = box.AddComponent<Image>();
                if (boxImg != null) { boxImg.color = new Color(0.13f, 0.13f, 0.16f, 0.98f); boxImg.raycastTarget = true; }

                GameObject msg = UiBuild.MakeLabel(_labelTemplate, box.transform, "You have unsaved mod settings.\nSave them before leaving?", new Color(0.95f, 0.95f, 0.95f, 1f), 18f, true, false);
                RectTransform mrt = msg.GetComponent<RectTransform>();
                if (mrt != null) { mrt.anchorMin = new Vector2(0f, 0.42f); mrt.anchorMax = new Vector2(1f, 1f); mrt.offsetMin = new Vector2(24f, 0f); mrt.offsetMax = new Vector2(-24f, -18f); }
                TextMeshProUGUI? mtmp = msg.GetComponent<TextMeshProUGUI>();
                if (mtmp != null) mtmp.alignment = TextAlignmentOptions.Center;

                ModalButton(box.transform, "Save", -178f, new Color(0.22f, 0.55f, 0.32f, 1f), () => { HideModal(); onSave(); });
                ModalButton(box.transform, "Discard Changes", 0f, new Color(0.60f, 0.32f, 0.30f, 1f), () => { HideModal(); onDiscard(); });
                ModalButton(box.transform, "Cancel", 178f, new Color(0.30f, 0.30f, 0.36f, 1f), () => { HideModal(); onCancel(); });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] modal failed: {ex.Message}");
                try { onCancel(); } catch { }
            }
        }

        private static void ModalButton(Transform parent, string text, float x, Color color, Action onClick)
        {
            GameObject go = UiBuild.NewRect("Btn " + text, parent);
            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt != null) { rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f); rt.sizeDelta = new Vector2(168f, 48f); rt.anchoredPosition = new Vector2(x, 28f); }
            Image img = go.AddComponent<Image>();
            if (img != null) { img.color = color; img.raycastTarget = true; }
            Button btn = go.AddComponent<Button>();

            GameObject lblGo = UiBuild.MakeLabel(_labelTemplate!, go.transform, text, new Color(1f, 1f, 1f, 1f), 15f, false, false);
            RectTransform lrt = lblGo.GetComponent<RectTransform>();
            if (lrt != null) { lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero; }
            TextMeshProUGUI? lt = lblGo.GetComponent<TextMeshProUGUI>();
            if (lt != null) lt.alignment = TextAlignmentOptions.Center;

            UiListeners.OnClick(btn, onClick);
        }

        private static void HideModal()
        {
            if (_modal != null) { try { UnityEngine.Object.Destroy(_modal); } catch { } _modal = null; }
        }

        private static Transform? ModalParent()
        {
            try
            {
                EscapeMenuManager? menu = EscapeMenuManager.HasInstance ? EscapeMenuManager.Instance : null;
                SettingsMenuManager? sm = menu != null ? menu.settingsMenu : null;
                if (sm != null) { Canvas c = sm.GetComponentInParent<Canvas>(); if (c != null) return c.transform; }
            }
            catch { }
            return null;
        }

        // ── small helpers ─────────────────────────────────────────────────────────────────────────────

        private static Transform MakeSlot(Transform parent, string name, float xMin, float xMax)
        {
            GameObject slot = UiBuild.BareHost(_labelTemplate!, parent, name);
            RectTransform rt = slot.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(xMin, 0f);
                rt.anchorMax = new Vector2(xMax, 1f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.localScale = Vector3.one;
            }
            return slot.transform;
        }

        private static void NeutralizeContent(Transform content)
        {
            try
            {
                // Disable the tab's own ScrollRect (content.parent = Viewport, its parent = Scroll) so it does
                // not reposition our content, then disable any layout/fitter and stretch content to fill.
                Transform? viewport = content.parent;
                Transform? scrollObj = viewport != null ? viewport.parent : null;
                if (scrollObj != null) { ScrollRect sr = scrollObj.GetComponent<ScrollRect>(); if (sr != null) sr.enabled = false; }

                DisableIfPresent<VerticalLayoutGroup>(content);
                DisableIfPresent<HorizontalLayoutGroup>(content);
                DisableIfPresent<GridLayoutGroup>(content);
                DisableIfPresent<ContentSizeFitter>(content);

                RectTransform rt = content.GetComponent<RectTransform>();
                if (rt != null) { rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 1f); rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero; }
            }
            catch { }
        }

        private static void DisableIfPresent<T>(Transform t) where T : Behaviour
        {
            try { T c = t.GetComponent<T>(); if (c != null) c.enabled = false; } catch { }
        }

        // The dropdown row's own title label = the first TMP that is NOT inside the dropdown (whose caption /
        // item-template labels live under dd.transform).
        private static TextMeshProUGUI? RowTitle(GameObject row, TMP_Dropdown? dd)
        {
            try
            {
                foreach (TextMeshProUGUI t in row.GetComponentsInChildren<TextMeshProUGUI>(true))
                    if (t != null && (dd == null || !t.transform.IsChildOf(dd.transform))) return t;
            }
            catch { }
            return null;
        }

        private static string KeyName(object? v) => v == null ? "None" : (v.ToString() ?? "None");

        private static void EndCapture()
        {
            TextMeshProUGUI? lbl = _capturingLabel;
            _capturing = null;
            _capturingLabel = null;
            try { if (lbl != null) lbl.text = _capturePrevText; } catch { }
        }

        // A captured rebind is STAGED (shown immediately, applied on Save); the label reflects the new key.
        private static void CommitKey(KeyCode k)
        {
            ConfigBinding? b = _capturing;
            TextMeshProUGUI? lbl = _capturingLabel;
            _capturing = null;
            _capturingLabel = null;
            if (b == null) return;
            StageOrClear(b, k.ToString() != (b.Value?.ToString() ?? ""), () => b.SetKey(k));
            try { if (lbl != null) lbl.text = k.ToString(); } catch { }
        }

        private static KeyCode[] CaptureKeys() => _captureKeys ??= (KeyCode[])Enum.GetValues(typeof(KeyCode));

        private static float ToFloat(object? v)
        {
            try { return v == null ? 0f : Convert.ToSingle(v); } catch { return 0f; }
        }


        // Force a cloned settings row to behave in our VerticalLayoutGroup: fixed height, full pane width,
        // no self-sizing fitter. Without this the complex game rows collapse / overflow.
        private static void TameRow(GameObject row, float height)
        {
            try
            {
                ContentSizeFitter csf = row.GetComponent<ContentSizeFitter>();
                if (csf != null) csf.enabled = false;
                LayoutElement le = row.GetComponent<LayoutElement>();
                if (le == null) le = row.AddComponent<LayoutElement>();
                le.ignoreLayout = false;
                le.minHeight = height;
                le.preferredHeight = height;
                le.minWidth = -1f;
                le.preferredWidth = -1f;
                le.flexibleWidth = 1f;
                le.flexibleHeight = -1f;
            }
            catch { }
        }

        // ── tab discovery / structure (proven helpers, unchanged) ─────────────────────────────────────

        private static bool AlreadyBuilt(TabManager tabs)
        {
            Il2CppReferenceArray<WindowTab>? arr = tabs.m_Tabs;
            if (arr == null) return false;
            for (int i = 0; i < arr.Length; i++)
            {
                WindowTab? t = arr[i];
                if (t != null && t.TabName == TabName) return true;
            }
            return false;
        }

        // A clean control-row template = a DIRECT child of a tab's Content that holds the wanted control
        // (Slider or Toggle) and is NOT a dropdown row (whose internal item-template Toggle is a 0-width
        // trap) and not the other control type.
        private static GameObject? FindRowTemplate(WindowTab tab, bool wantSlider)
        {
            Transform? content = ContentOf(tab);
            if (content == null) return null;
            int n = content.childCount;
            for (int i = 0; i < n; i++)
            {
                Transform row;
                try { row = content.GetChild(i); } catch { continue; }
                try
                {
                    if (row.GetComponentInChildren<TMP_Dropdown>(true) != null) continue;
                    bool hasSlider = row.GetComponentInChildren<Slider>(true) != null;
                    bool hasToggle = row.GetComponentInChildren<Toggle>(true) != null;
                    if (wantSlider) { if (hasSlider && !hasToggle) return row.gameObject; }
                    else { if (hasToggle && !hasSlider) return row.gameObject; }
                }
                catch { }
            }
            return null;
        }

        // A dropdown row to clone for Enum/Choice controls (the opposite of FindRowTemplate's exclusion).
        private static GameObject? FindDropdownTemplate(WindowTab tab)
        {
            Transform? content = ContentOf(tab);
            if (content == null) return null;
            int n = content.childCount;
            for (int i = 0; i < n; i++)
            {
                Transform row;
                try { row = content.GetChild(i); } catch { continue; }
                try { if (row.GetComponentInChildren<TMP_Dropdown>(true) != null) return row.gameObject; }
                catch { }
            }
            return null;
        }

        // A TMP_InputField anywhere in the tab to clone for text/number entry (often absent in the settings
        // tabs, then it stays null and those controls render read-only).
        private static GameObject? FindInputTemplate(WindowTab tab)
        {
            try
            {
                TMP_InputField? f = tab.GetComponentInChildren<TMP_InputField>(true);
                return f != null ? f.gameObject : null;
            }
            catch { return null; }
        }

        // Warn at most once per scene for a genuine structural anomaly (own flag, kept separate from the build
        // exception flag so a transient retry can never suppress a real warning).
        private static void LogOnce(string msg)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;
            Plugin.Logger.LogWarning(msg);
        }

        private static Transform? ContentOf(WindowTab tab)
        {
            try
            {
                Transform? byName = tab.transform.Find("Scroll/Viewport/Content");
                if (byName != null) return byName;
                Transform t = tab.transform;
                if (t.childCount > 1)
                {
                    Transform c1 = t.GetChild(1);
                    if (c1.childCount > 0 && c1.GetChild(0).childCount > 0) return c1.GetChild(0).GetChild(0);
                }
            }
            catch { }
            return null;
        }

        private static void ClearChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                try { UnityEngine.Object.Destroy(parent.GetChild(i).gameObject); } catch { }
            }
        }

        private static void AppendTab(TabManager tabs, WindowTab tab)
        {
            Il2CppReferenceArray<WindowTab>? old = tabs.m_Tabs;
            int n = old != null ? old.Length : 0;
            var grown = new Il2CppReferenceArray<WindowTab>(n + 1);
            for (int i = 0; i < n; i++) grown[i] = old![i];
            grown[n] = tab;
            tabs.m_Tabs = grown;
        }

        private static GameObject? AddTaskbarButton(TabManager tabs, WindowTab tab)
        {
            Transform? windowBg = tabs.transform.parent;
            Transform? taskbar = windowBg != null ? windowBg.Find("Taskbar") : null;
            if (taskbar == null || taskbar.childCount == 0) return null;

            Transform buttonsRow = taskbar.GetChild(0);
            if (buttonsRow.childCount == 0) return null;
            GameObject template = buttonsRow.GetChild(0).gameObject;

            // The buttons sit in a GridLayoutGroup; a new button would wrap to a second row. Pin to one row.
            try
            {
                GridLayoutGroup? grid = buttonsRow.GetComponent<GridLayoutGroup>();
                if (grid != null)
                {
                    grid.constraint = GridLayoutGroup.Constraint.FixedRowCount;
                    grid.constraintCount = 1;
                }
            }
            catch { }

            GameObject button = UnityEngine.Object.Instantiate(template, buttonsRow, false);
            button.name = "Mod Settings Tool Tab Button";

            TextMeshProUGUI? label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
            {
                LocalizeStringEvent? loc = label.GetComponent<LocalizeStringEvent>();
                if (loc != null) loc.enabled = false;
                label.text = "Mods";
            }

            Button? btn = button.GetComponent<Button>();
            if (btn != null) UiListeners.OnClick(btn, () => { try { tabs.OpenTab(TabName); } catch { } });
            return button;
        }

        // ── cloned controls (Toggle / Slider, proven, unchanged) ─────────────────────────────────────

        internal static void AddToggle(GameObject refToggle, Transform parent, string label, bool initial, Action<bool> onChange)
        {
            try
            {
                GameObject go = UnityEngine.Object.Instantiate(refToggle, parent, false);
                go.name = "MST " + label;
                TameRow(go, 44f);

                TextMeshProUGUI? title = go.GetComponentInChildren<TextMeshProUGUI>(true);
                if (title != null)
                {
                    LocalizeStringEvent? loc = title.GetComponent<LocalizeStringEvent>();
                    if (loc != null) loc.enabled = false;
                    title.text = label;
                    try { title.enableWordWrapping = false; title.overflowMode = TextOverflowModes.Overflow; } catch { }
                }

                Toggle? toggle = go.GetComponentInChildren<Toggle>(true);
                if (toggle != null)
                {
                    UiListeners.OnChanged(toggle, onChange); // replaces the cloned source's listeners + roots ours
                    toggle.SetIsOnWithoutNotify(initial);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] toggle '{label}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static void AddSlider(GameObject refSlider, Transform parent, string label, float min, float max, float initial, bool wholeNumbers, Action<float> onChange)
        {
            try
            {
                GameObject go = UnityEngine.Object.Instantiate(refSlider, parent, false);
                go.name = "MST " + label;
                TameRow(go, 50f);

                TextMeshProUGUI? title = go.GetComponentInChildren<TextMeshProUGUI>(true);
                if (title != null)
                {
                    LocalizeStringEvent? loc = title.GetComponent<LocalizeStringEvent>();
                    if (loc != null) loc.enabled = false;
                }

                // Blank the slider's secondary value text (the cloned "current value" label); our title
                // carries the value. Its own updater was a persistent listener we remove, so it would freeze.
                try
                {
                    foreach (TextMeshProUGUI t in go.GetComponentsInChildren<TextMeshProUGUI>(true))
                    {
                        if (t != null && t != title)
                        {
                            LocalizeStringEvent? vloc = t.GetComponent<LocalizeStringEvent>();
                            if (vloc != null) vloc.enabled = false;
                            t.text = "";
                        }
                    }
                }
                catch { }

                Slider? slider = go.GetComponentInChildren<Slider>(true);
                if (slider != null)
                {
                    // CRITICAL ORDER (MST-RULE-12 + no spurious staging): FIRST drop the source's PERSISTENT
                    // onValueChanged (e.g. SetSensitivity) by replacing the event with an empty one, so the next
                    // lines can't fire it and corrupt a real game setting. THEN set range/value: Unity fires
                    // onValueChanged when it clamps the old cloned value into the new range, but the event is
                    // empty now, so nothing stages. THEN attach our handler, so only real user drags stage.
                    UiListeners.ClearChanged(slider);
                    slider.minValue = min;
                    slider.maxValue = max;
                    slider.wholeNumbers = wholeNumbers;
                    slider.SetValueWithoutNotify(Mathf.Clamp(initial, min, max));
                    UiListeners.AddChanged(slider, f =>
                    {
                        float v = wholeNumbers ? Mathf.Round(Mathf.Clamp(f, min, max)) : Mathf.Clamp(f, min, max);
                        onChange(v);
                        if (title != null) title.text = $"{label}: {Format(v, wholeNumbers)}";
                    });
                }

                if (title != null) title.text = $"{label}: {Format(Mathf.Clamp(initial, min, max), wholeNumbers)}";
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] slider '{label}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string Format(float v, bool wholeNumbers) =>
            wholeNumbers ? ((int)v).ToString() : v.ToString("0.######"); // up to 6 dp so small values (0..0.001) aren't shown as 0
    }
}
