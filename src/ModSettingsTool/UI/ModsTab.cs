using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ModSettingsTool.Config;
using ModSettingsTool.Mods;
using ModSettingsTool.Patches;
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
        private static Transform? _leftContent;      // the mod-list pane content (for our custom wheel scrolling)
        private static Transform? _controlParent;   // where a setting's control(s) parent (its setting block); falls back to _rightContent
        private static ModInfo? _selected;   // selected mod by INSTANCE, unloaded-failure rows share an empty GUID
        private static readonly List<ModButton> _modButtons = new();

        // Key-rebind capture (press-a-key, per OQ-03), driven each frame by Tick().
        private static ConfigBinding? _capturing;
        private static TextMeshProUGUI? _capturingLabel;
        private static KeyCode[]? _captureKeys;
        private static string _capturePrevText = "";

        // Staged edits, controls write HERE, never to the live ConfigEntry, until the player Saves. This is
        // what guarantees nothing persists without Save, and makes Discard a clean no-op (live was untouched).
        // Apply runs on Save; Effective is the value the entry WILL hold after Apply (kept so the "modified"
        // marker can compare a staged value against the default without touching the live entry).
        private sealed class StagedEdit
        {
            public Action Apply = null!;
            public object? Effective;
        }
        private static readonly Dictionary<ConfigBinding, StagedEdit> _staged = new();

        // Per-row widgets for the live "modified" marker + per-row reset, plus a Snap that visually restores the
        // control to its default (used by reset). Rebuilt with each page; keyed by binding instance.
        private sealed class RowUi
        {
            public GameObject? Dot;    // the accent "modified" dot (shown when live-or-staged != default)
            public GameObject? Reset;  // the per-row "Reset" button (shown when modified, honoring HideDefaultButton/ReadOnly)
            public Action? Snap;       // set the control to its default value WITHOUT notifying (no stage)
        }
        private static readonly Dictionary<ConfigBinding, RowUi> _rows = new();

        // Tier 3 navigation/density state (per selected mod; reset on mod switch). The search box is PERSISTENT
        // (it lives in the right slot, outside the rebuilt scroll content) so live-filtering keeps focus + caret.
        private static TMP_InputField? _searchField;                 // the persistent per-mod search box
        private static string _filter = "";                          // current search query (empty = show all)
        private static bool _filterDirty;                            // a keystroke queued a rebuild for the next Tick
        private static string _filterPending = "";                  // the query to apply on that deferred rebuild
        private static readonly HashSet<string> _collapsedSections = new(StringComparer.OrdinalIgnoreCase); // folded section names
        private static bool _advancedExpanded;                       // the bottom Advanced container's fold state (default collapsed)
        private static int _zebra;                                   // running row index for the alternating row band

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
        }

        // The current page's dropdowns, kept healthy for as long as they live (until the page rebuilds and they
        // are destroyed). The game's CustomDropdown re-initialises AFTER we build, and, on the main-menu
        // settings window, can do so well after any fixed timeout, blanking our options back to "Option A"; the
        // per-frame Tick re-applies them whenever it sees them blanked (and the dropdown is closed). No deadline:
        // a deadline that expired before the game's late re-init was what left a stale, stuck "Option A" dropdown.
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
            _leftContent = null;
            _controlParent = null;
            _selected = null;
            _modButtons.Clear();
            _pendingDropdowns.Clear();
            _staged.Clear();
            _rows.Clear();
            _searchField = null;
            _filter = "";
            _filterDirty = false;
            _filterPending = "";
            _collapsedSections.Clear();
            _advancedExpanded = false;
            _zebra = 0;
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
                SettingsMenuManager? sm = FindSettingsManager();
                if (sm == null || sm.gameObject == null) return;
                TryBuild(sm);
            }
            catch
            {
                // fail-soft; the main-menu list does not depend on this
            }
        }

        // The Settings window hosting our tab differs by scene but is the SAME SettingsMenuManager type either
        // way: in the store it hangs off the Escape menu; on the main menu it hangs off MainMenuManager. Both
        // expose m_tabManager, so TryBuild clones into either identically.
        private static SettingsMenuManager? FindSettingsManager()
        {
            try
            {
                if (PatchGate.InStore())
                {
                    if (!EscapeMenuManager.HasInstance) return null;
                    EscapeMenuManager menu = EscapeMenuManager.Instance;
                    return menu != null ? menu.settingsMenu : null;
                }
                if (PatchGate.InMenu())
                {
                    MainMenuManager? mm = GameSingletons.Get<MainMenuManager>();
                    return mm != null ? mm.m_SettingsMenu : null;
                }
            }
            catch { }
            return null;
        }

        // Per-frame (called by the Host every frame in the store). Cheap when idle; only does work while a
        // key rebind is being captured.
        internal static void Tick()
        {
            // Deferred dropdown population: re-apply options the frame AFTER a page builds, past the game
            // CustomDropdown's own init (it otherwise shows a stale "Option 1" until the page is rebuilt).
            if (_pendingDropdowns.Count > 0)
            {
                for (int i = _pendingDropdowns.Count - 1; i >= 0; i--)
                {
                    PendingDropdown pd = _pendingDropdowns[i];
                    if (pd.Dd == null) { _pendingDropdowns.RemoveAt(i); continue; } // destroyed on a page rebuild
                    // NEVER touch a dropdown the player has opened, re-applying options calls Hide() and would
                    // slam shut the popup they just clicked open, which reads as "the dropdown won't open on the
                    // first click." While it's expanded the options are already ours (it can't have been blanked
                    // and stay open), so skip; heal it whenever it is closed and the game has blanked our options.
                    bool expanded = false;
                    try { expanded = pd.Dd.IsExpanded; } catch { }
                    if (!expanded && !DropdownApplied(pd.Dd, pd.B)) ApplyDropdown(pd.Dd, pd.B);
                }
            }

            // A queued search keystroke rebuilds the page here (out of the input's onValueChanged), then refocuses.
            if (_filterDirty) ApplyPendingFilter();

            WheelScroll();

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

        private static void TryBuild(SettingsMenuManager settings)
        {
            try
            {
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

                Transform leftContent = UiBuild.BuildVerticalScroll(template, leftSlot, UiTheme.LeftPaneBg);
                _leftContent = leftContent;

                // Right side (Tier 3): a persistent search strip pinned to the top, then the scrollable config
                // page filling the rest. The search box lives OUTSIDE the rebuilt scroll content so live-filtering
                // never destroys the field mid-keystroke (it keeps focus + caret).
                const float searchH = 42f;
                GameObject scrollHost = UiBuild.NewRect("MST RightScrollHost", rightSlot);
                RectTransform scrt = scrollHost.GetComponent<RectTransform>();
                if (scrt != null) { scrt.anchorMin = Vector2.zero; scrt.anchorMax = Vector2.one; scrt.offsetMin = Vector2.zero; scrt.offsetMax = new Vector2(0f, -searchH); }
                _rightContent = UiBuild.BuildVerticalScroll(template, scrollHost.transform, UiTheme.PaneBg);

                GameObject searchHost = UiBuild.NewRect("MST SearchHost", rightSlot);
                RectTransform sehrt = searchHost.GetComponent<RectTransform>();
                if (sehrt != null) { sehrt.anchorMin = new Vector2(0f, 1f); sehrt.anchorMax = new Vector2(1f, 1f); sehrt.pivot = new Vector2(0.5f, 1f); sehrt.sizeDelta = new Vector2(0f, searchH); sehrt.anchoredPosition = Vector2.zero; }
                BuildSearchField(searchHost.transform);
                Plugin.Logger.LogDebug("[ModsTab] two-pane scaffold built; populating the mod list.");

                UiBuild.MakeLabel(template, leftContent, $"Installed Mods ({ModRegistry.Cache.Count})", UiTheme.TitleText, 20f, false, false);
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
                GameObject go = UiBuild.MakeLabel(_labelTemplate!, parent, "  " + mod.Name, HealthPalette.For(mod.Health), 18f, true, true);
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
            // A new mod has different sections, reset the per-mod search + collapse state and clear the box.
            _filter = "";
            _filterDirty = false;
            _filterPending = "";
            _collapsedSections.Clear();
            // Debug sections render at the bottom and start COLLAPSED, pre-seed them into the collapsed set (the
            // user can still expand one; that's remembered until the next mod switch).
            try { foreach (ConfigBinding cb in mod.Settings) if (IsDebugSection(cb.Section)) _collapsedSections.Add(cb.Section ?? ""); } catch { }
            _advancedExpanded = false;
            try { _searchField?.SetTextWithoutNotify(""); } catch { }
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
            _rows.Clear();
            _pendingDropdowns.Clear(); // the previous page's dropdowns are being destroyed; this page re-adds its own
            _zebra = 0;

            // Page-scoped delegate roots: clear the previous mod's right-pane handlers (its controls were just
            // destroyed) and collect this page's into the page scope, so browsing mods doesn't leak them.
            _controlParent = _rightContent;
            UiListeners.BeginPageScope();
            try
            {
                BuildHeaderCard(mod);
                if (!mod.HasSettings) return; // the header card already shows "No settings to change."

                // Tier 3 search: filter to the matching settings. With a filter active, sections render expanded
                // (so matches aren't hidden in a folded section); with no filter, the remembered collapse state
                // applies. Staged edits survive a filtered rebuild (they key on ConfigBinding, not the GameObject).
                bool filtering = !string.IsNullOrEmpty(_filter);
                List<ConfigBinding> visible = filtering ? FilterSettings(mod.Settings, _filter) : mod.Settings;
                if (filtering && visible.Count == 0)
                {
                    UiBuild.MakeLabel(_labelTemplate, _rightContent, $"No settings match \"{_filter}\".", UiTheme.DimText, 16f, true, false);
                    return;
                }

                List<SectionGroup> groups = OrderForDisplay(visible);

                // Normal (non-advanced) settings of NON-Debug sections: one collapsible titled section per BepInEx
                // section (sections in display order; entries ordered within). Each setting becomes a block (control
                // + meta + optional description). A section whose every entry is advanced gets no card, those live
                // in the Advanced container below. Debug sections are deferred to the very bottom (step 3).
                foreach (SectionGroup g in groups)
                {
                    if (IsDebugSection(g.Section)) continue;
                    if (g.Normal.Count == 0) continue;
                    string sectionKey = g.Section ?? "";
                    string title = string.IsNullOrEmpty(sectionKey) ? "General" : sectionKey;
                    RenderCollapsibleSection(sectionKey, title, g.Normal, filtering);
                }

                // Advanced / non-browsable settings across every NON-Debug section collect into one collapsible
                // "Advanced Settings" container (default collapsed), surfaced, never hidden. Grouped by their origin
                // section (a sub-header per contributing named section) so context isn't lost. (Debug-section
                // settings are NOT pulled in here, a Debug section stays whole at the bottom.)
                int advCount = 0;
                foreach (SectionGroup g in groups) if (!IsDebugSection(g.Section)) advCount += g.Advanced.Count;
                if (advCount > 0)
                {
                    bool advExpanded = filtering || _advancedExpanded;
                    Transform advBody = UiBuild.MakeAdvancedContainer(_labelTemplate, _rightContent, advCount, advExpanded, exp =>
                    {
                        _advancedExpanded = exp;
                        UiBuild.RefreshScrollHint(_rightContent);
                    });
                    foreach (SectionGroup g in groups)
                    {
                        if (IsDebugSection(g.Section)) continue;
                        if (g.Advanced.Count == 0) continue;
                        if (!string.IsNullOrEmpty(g.Section)) UiBuild.MakeSubHeader(_labelTemplate, advBody, g.Section);
                        foreach (ConfigBinding b in g.Advanced) AddSetting(b, advBody);
                    }
                }

                // Debug section(s) at the VERY bottom, below Advanced, collapsed by default (pre-seeded in
                // DoSelect). Each keeps ALL its settings together (normal + advanced), never split into Advanced.
                foreach (SectionGroup g in groups)
                {
                    if (!IsDebugSection(g.Section)) continue;
                    var all = new List<ConfigBinding>(g.Normal);
                    all.AddRange(g.Advanced);
                    if (all.Count == 0) continue;
                    string sectionKey = g.Section ?? "";
                    RenderCollapsibleSection(sectionKey, sectionKey, all, filtering);
                }
            }
            finally
            {
                _controlParent = _rightContent;
                UiListeners.EndPageScope();
                UiBuild.ResetScrollAndRefreshHint(_rightContent); // page just (re)built, scroll to top + refresh the hint
            }
        }

        // A "Debug" section is always pushed to the very bottom (below Advanced) and collapsed by default, its
        // contents are diagnostics most players never touch. Matched by name containing "debug" (case-insensitive),
        // so "Debug", "Debugging", "Debug Tools" all qualify.
        private static bool IsDebugSection(string? section)
            => !string.IsNullOrEmpty(section) && section!.IndexOf("debug", StringComparison.OrdinalIgnoreCase) >= 0;

        // Render one collapsible titled section (header + body card) holding `entries`, grouped by their
        // ConfigurationManagerAttributes Category (a sub-header per category). Collapse state is remembered in
        // _collapsedSections (a filter forces expand so matches aren't hidden). Shared by the normal + Debug paths.
        private static void RenderCollapsibleSection(string sectionKey, string title, List<ConfigBinding> entries, bool filtering)
        {
            bool expanded = filtering || !_collapsedSections.Contains(sectionKey);
            Transform body = UiBuild.MakeCollapsibleSection(_labelTemplate!, _rightContent!, title, expanded, exp =>
            {
                if (exp) _collapsedSections.Remove(sectionKey); else _collapsedSections.Add(sectionKey);
                UiBuild.RefreshScrollHint(_rightContent);
            });

            string lastCat = "\0";
            foreach (ConfigBinding b in entries)
            {
                string cat = b.Category ?? "";
                if (!string.Equals(cat, lastCat, StringComparison.Ordinal))
                {
                    lastCat = cat;
                    if (!string.IsNullOrEmpty(cat)) UiBuild.MakeSubHeader(_labelTemplate!, body, cat);
                }
                AddSetting(b, body);
            }
        }

        // A section's entries split into normal + advanced, for one section card.
        private sealed class SectionGroup
        {
            public readonly string Section;
            public readonly List<ConfigBinding> Normal = new();
            public readonly List<ConfigBinding> Advanced = new();
            public SectionGroup(string section) { Section = section; }
        }

        // Group a mod's settings into ordered section cards. Base order = the [UI] SettingOrder key (author
        // declaration order, the binding list's insertion order, or alphabetical). Sections appear in
        // first-appearance order over that base. Within a section, the ConfigurationManagerAttributes Order
        // (higher = earlier) is applied as a STABLE overlay so unordered entries keep the base order.
        private static List<SectionGroup> OrderForDisplay(List<ConfigBinding> settings)
        {
            var groups = new List<SectionGroup>();
            try
            {
                bool alpha = false;
                try { alpha = Plugin.Settings.SettingOrder.Value == SettingOrderMode.Alphabetical; } catch { }

                var baseSeq = new List<ConfigBinding>(settings);
                if (alpha)
                    baseSeq.Sort((x, y) =>
                    {
                        int c = string.Compare(x.Section, y.Section, StringComparison.OrdinalIgnoreCase);
                        return c != 0 ? c : string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase);
                    });

                var index = new Dictionary<string, SectionGroup>(StringComparer.OrdinalIgnoreCase);
                foreach (ConfigBinding b in baseSeq)
                {
                    string sec = b.Section ?? "";
                    if (!index.TryGetValue(sec, out SectionGroup? g)) { g = new SectionGroup(sec); groups.Add(g); index[sec] = g; }
                    if (b.IsAdvanced) g.Advanced.Add(b); else g.Normal.Add(b);
                }

                foreach (SectionGroup g in groups)
                {
                    ApplyOrderOverlay(g.Normal);
                    ApplyOrderOverlay(g.Advanced);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] ordering failed; using raw order: {ex.Message}");
                if (groups.Count == 0)
                {
                    var g = new SectionGroup("");
                    g.Normal.AddRange(settings);
                    groups.Add(g);
                }
            }
            return groups;
        }

        // Stable sort by ConfigurationManagerAttributes Order, descending (higher = earlier); ties keep the base
        // order (LINQ OrderByDescending is a stable sort). Entries without Order all sort as 0.
        private static void ApplyOrderOverlay(List<ConfigBinding> list)
        {
            if (list.Count < 2) return;
            var sorted = list.OrderByDescending(b => b.Order ?? 0).ToList();
            list.Clear();
            list.AddRange(sorted);
        }

        // The per-mod header: a title card with name + version, the GUID, a health banner with the issue text
        // when not healthy, and the settings count (or the "No settings to change." note for a config-less mod).
        private static void BuildHeaderCard(ModInfo mod)
        {
            Transform card = UiBuild.MakeCard(_rightContent!, UiTheme.HeaderCardBg);

            string ver = string.IsNullOrEmpty(mod.Version) ? "" : $"   v{mod.Version}";
            GameObject name = UiBuild.MakeLabel(_labelTemplate!, card, mod.Name + ver, UiTheme.TitleText, 27f, true, false);
            try { TextMeshProUGUI? nt = name.GetComponent<TextMeshProUGUI>(); if (nt != null) nt.fontStyle = FontStyles.Bold; } catch { }

            if (!string.IsNullOrEmpty(mod.Guid))
                UiBuild.MakeLabel(_labelTemplate!, card, mod.Guid, UiTheme.FaintText, 14f, true, false);

            if (mod.Health != HealthStatus.Healthy && !string.IsNullOrEmpty(mod.IssueSummary))
                UiBuild.MakeBanner(_labelTemplate!, card, mod.IssueSummary, UiTheme.Health(mod.Health), UiTheme.HealthBanner(mod.Health));

            string count = mod.HasSettings
                ? $"{mod.Settings.Count} setting{(mod.Settings.Count == 1 ? "" : "s")}"
                : "No settings to change.";
            UiBuild.MakeLabel(_labelTemplate!, card, count, UiTheme.DimText, 15f, true, false);

            // Page-level "Reset all to defaults" (staged like any edit; Discard reverts, Save persists). Only
            // when the mod has settings; hidden for a config-less mod.
            if (mod.HasSettings)
                AddResetAllButton(card);
        }

        // A muted "Reset all to defaults" button in the header card, stages a reset for EVERY setting (visible
        // or in a collapsed group); Discard reverts, Save persists.
        private static void AddResetAllButton(Transform card)
        {
            try
            {
                GameObject row = UiBuild.NewRect("MST ResetAll", card);
                LayoutElement le = row.AddComponent<LayoutElement>();
                if (le != null) { le.minHeight = 34f; le.preferredHeight = 34f; le.flexibleWidth = 1f; }

                GameObject box = UiBuild.NewRect("Box", row.transform);
                RectTransform brt = box.GetComponent<RectTransform>();
                if (brt != null) { brt.anchorMin = new Vector2(0f, 0.12f); brt.anchorMax = new Vector2(0f, 0.88f); brt.pivot = new Vector2(0f, 0.5f); brt.sizeDelta = new Vector2(220f, 0f); brt.anchoredPosition = new Vector2(2f, 0f); }
                Image img = box.AddComponent<Image>();
                if (img != null) { img.color = new Color(0.55f, 0.33f, 0.30f, 0.55f); img.raycastTarget = true; }
                Button btn = box.AddComponent<Button>();

                GameObject lbl = UiBuild.MakeLabel(_labelTemplate!, box.transform, "Reset all to defaults", UiTheme.LabelText, 14f, false, false);
                RectTransform lrt = lbl.GetComponent<RectTransform>();
                if (lrt != null) { lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero; }
                TextMeshProUGUI? lt = lbl.GetComponent<TextMeshProUGUI>();
                if (lt != null) lt.alignment = TextAlignmentOptions.Center;

                UiListeners.OnClick(btn, ResetAll);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ModsTab] reset-all button failed: {ex.Message}"); }
        }

        private static void ResetAll()
        {
            try
            {
                if (_selected == null) return;
                foreach (ConfigBinding b in _selected.Settings) if (CanReset(b)) StageReset(b);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ModsTab] reset all failed: {ex.Message}"); }
        }

        // A binding may be reset only when the UI can actually edit it: NOT ReadOnly (a protected entry whose
        // control is disabled) and NOT Unsupported (rendered read-only via AddReadOnly, a type we can't
        // render/edit). Both the per-row Reset (its visibility) and Reset-all use this, so neither path can stage
        // a reset that Save would then write through BoxedValue for an entry the UI never exposed for editing.
        private static bool CanReset(ConfigBinding b) => !b.ReadOnly && b.Kind != ControlKind.Unsupported && b.HasDefault;

        // Stage restoring this entry's default (a no-op if live already equals default) and snap the control to
        // show it. StageOrClear refreshes the row marker; the live entry is untouched until Save.
        private static void StageReset(ConfigBinding b)
        {
            try
            {
                StageOrClear(b, !ValuesEqual(b.Value, b.Default), b.Default, b.ResetToDefault);
                if (_rows.TryGetValue(b, out RowUi? r)) { try { r.Snap?.Invoke(); } catch { } }
            }
            catch { }
        }

        // Wrap one binding in a setting block: its control, then a compact meta line (modified dot + default /
        // bounds + per-row Reset), then its wrapped description. The block sizes to its contents.
        private static void AddSetting(ConfigBinding b, Transform sectionBody)
        {
            Transform block = UiBuild.MakeSettingBlock(sectionBody, (_zebra++ & 1) == 1); // subtle alternating band (Tier 3)
            var row = new RowUi();
            _rows[b] = row;
            _controlParent = block;
            try
            {
                AddControl(b);
                if (b.ReadOnly) DisableInteractables(block);
                AddMetaLine(b, block, row);
                if (ShowDescriptionsEnabled() && !string.IsNullOrEmpty(b.Description))
                    UiBuild.MakeDescription(_labelTemplate!, block, b.Description);
                UpdateMarker(b); // initial dot/reset state (a row may already differ from default)
            }
            finally
            {
                _controlParent = _rightContent;
            }
        }

        // The compact secondary line under a control: a "modified" dot (left), the default value + acceptable
        // bounds (dim), and a per-row "Reset" button (right). Dot + Reset are toggled by UpdateMarker.
        private static void AddMetaLine(ConfigBinding b, Transform block, RowUi row)
        {
            try
            {
                GameObject meta = UiBuild.NewRect("MST Meta " + b.Key, block);
                LayoutElement le = meta.AddComponent<LayoutElement>();
                if (le != null) { le.minHeight = 22f; le.preferredHeight = 22f; le.flexibleWidth = 1f; }

                GameObject dot = UiBuild.NewRect("Dot", meta.transform);
                RectTransform drt = dot.GetComponent<RectTransform>();
                if (drt != null) { drt.anchorMin = drt.anchorMax = new Vector2(0f, 0.5f); drt.pivot = new Vector2(0f, 0.5f); drt.sizeDelta = new Vector2(9f, 9f); drt.anchoredPosition = new Vector2(9f, 0f); }
                Image dimg = dot.AddComponent<Image>();
                if (dimg != null) { dimg.color = UiTheme.Accent; dimg.raycastTarget = false; }
                dot.SetActive(false);
                row.Dot = dot;

                GameObject lbl = UiBuild.MakeLabel(_labelTemplate!, meta.transform, MetaText(b), UiTheme.FaintText, 13f, false, false);
                RectTransform lrt = lbl.GetComponent<RectTransform>();
                if (lrt != null) { lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(1f, 1f); lrt.offsetMin = new Vector2(24f, 0f); lrt.offsetMax = new Vector2(-86f, 0f); }
                TextMeshProUGUI? lt = lbl.GetComponent<TextMeshProUGUI>();
                if (lt != null) { lt.alignment = TextAlignmentOptions.MidlineLeft; try { lt.overflowMode = TextOverflowModes.Ellipsis; } catch { } }

                GameObject rst = UiBuild.NewRect("Reset", meta.transform);
                RectTransform rrt = rst.GetComponent<RectTransform>();
                if (rrt != null) { rrt.anchorMin = rrt.anchorMax = new Vector2(1f, 0.5f); rrt.pivot = new Vector2(1f, 0.5f); rrt.sizeDelta = new Vector2(76f, 20f); rrt.anchoredPosition = new Vector2(-6f, 0f); }
                Image rimg = rst.AddComponent<Image>();
                if (rimg != null) { rimg.color = new Color(1f, 1f, 1f, 0.10f); rimg.raycastTarget = true; }
                Button rbtn = rst.AddComponent<Button>();
                GameObject rlbl = UiBuild.MakeLabel(_labelTemplate!, rst.transform, "Reset", UiTheme.LabelText, 12f, false, false);
                RectTransform rlrt = rlbl.GetComponent<RectTransform>();
                if (rlrt != null) { rlrt.anchorMin = Vector2.zero; rlrt.anchorMax = Vector2.one; rlrt.offsetMin = Vector2.zero; rlrt.offsetMax = Vector2.zero; }
                TextMeshProUGUI? rlt = rlbl.GetComponent<TextMeshProUGUI>();
                if (rlt != null) rlt.alignment = TextAlignmentOptions.Center;
                UiListeners.OnClick(rbtn, () => StageReset(b));
                rst.SetActive(false);
                row.Reset = rst;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ModsTab] meta line '{b.Key}' failed: {ex.Message}"); }
        }

        // "default: X" plus the acceptable bounds (slider/ranged range, dropdown allowed set).
        private static string MetaText(ConfigBinding b)
        {
            string def = b.Default != null ? Stringify(b.Default) : "-";
            string s = "default: " + def;
            try
            {
                if (b.Kind == ControlKind.Slider || ((b.Kind == ControlKind.IntInput || b.Kind == ControlKind.FloatInput) && b.HasRange))
                    s += "      range " + TrimNum(b.Min) + "–" + TrimNum(b.Max);
                else if ((b.Kind == ControlKind.EnumDropdown || b.Kind == ControlKind.ChoiceDropdown) && b.Choices != null && b.Choices.Length > 0)
                    s += b.Choices.Length <= 6 ? "      options: " + string.Join(" / ", b.Choices) : "      " + b.Choices.Length + " options";
            }
            catch { }
            return s;
        }

        private static string Stringify(object o)
        {
            try { return IsNumericValue(o) ? TrimNum(Convert.ToDouble(o)) : (o.ToString() ?? ""); }
            catch { return o.ToString() ?? ""; }
        }

        private static string TrimNum(double d) => d.ToString("0.######");

        private static void DisableInteractables(Transform block)
        {
            try
            {
                foreach (Selectable s in block.GetComponentsInChildren<Selectable>(true))
                {
                    if (s == null) continue;
                    try { s.interactable = false; } catch { }
                }
            }
            catch { }
        }

        private static void SetSnap(ConfigBinding b, Action snap)
        {
            if (_rows.TryGetValue(b, out RowUi? r)) r.Snap = snap;
        }

        private static void AddControl(ConfigBinding b)
        {
            try
            {
                switch (b.Kind)
                {
                    case ControlKind.Toggle:
                    {
                        Toggle? tog = AddToggle(_refToggle!, _controlParent!, b.DisplayName, AsBool(EffectiveValue(b)), v => StageOrClear(b, v != AsBool(b.Value), v, () => { b.Value = v; }));
                        if (tog != null) SetSnap(b, () => { try { tog.SetIsOnWithoutNotify(b.Default is bool db && db); } catch { } });
                        break;
                    }
                    case ControlKind.Slider:
                    {
                        bool whole = b.IsWholeNumber;
                        (Slider? sl, Action<float>? set) = AddSlider(_refSlider!, _controlParent!, b.DisplayName, (float)b.Min, (float)b.Max, ToFloat(EffectiveValue(b)), whole, v => StageOrClear(b, !SliderEquals(v, ToFloat(b.Value), b), v, () => b.SetNumber(v)));
                        if (sl != null && set != null) SetSnap(b, () => set(ToFloat(b.Default)));
                        break;
                    }
                    case ControlKind.EnumDropdown:
                    case ControlKind.ChoiceDropdown:
                        AddDropdown(b);
                        break;
                    case ControlKind.KeyBind:
                        AddKeyBind(b);
                        break;
                    case ControlKind.IntInput:
                    case ControlKind.FloatInput:
                        AddInputField(b);
                        break;
                    case ControlKind.TextInput:
                        if (_refSlider != null && IsColorBinding(b)) AddColorEditor(b);
                        else AddInputField(b);
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
                GameObject go = UnityEngine.Object.Instantiate(_refDropdown, _controlParent, false);
                go.name = "MST " + b.Key;
                TameRow(go, 54f);

                TMP_Dropdown? dd = go.GetComponentInChildren<TMP_Dropdown>(true);

                TextMeshProUGUI? title = RowTitle(go, dd);
                if (title != null)
                {
                    UiBuild.DisableLoc(title);
                    title.text = b.DisplayName;
                    try { title.enableWordWrapping = false; title.overflowMode = TextOverflowModes.Overflow; } catch { }
                }

                if (dd != null)
                {
                    // CRITICAL: replace the cloned dropdown's PERSISTENT onValueChanged (a settings dropdown
                    // drives a real game setting, e.g. resolution) BEFORE touching its value, UiListeners
                    // swaps the whole event object and roots ours.
                    UiListeners.OnChanged(dd, i => StageOrClear(b, i != b.CurrentChoiceIndex(), b.ChoiceValue(i), () => b.SetChoice(i)));
                    SetSnap(b, () => { try { dd.SetValueWithoutNotify(b.DefaultChoiceIndex()); dd.RefreshShownValue(); } catch { } });

                    // Speed up the dropdown's own popup-list scrolling (the Template's ScrollRect).
                    try { ScrollRect tsr = dd.GetComponentInChildren<ScrollRect>(true); if (tsr != null) tsr.scrollSensitivity = 45f; } catch { }

                    // Populate now AND again next frame (Tick): the game's CustomDropdown re-initialises after
                    // we build, blanking our options ("Option 1") until the page is rebuilt, the deferred
                    // re-apply lands past that init.
                    ApplyDropdown(dd, b);
                    _pendingDropdowns.Add(new PendingDropdown { Dd = dd, B = b });
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
                dd.SetValueWithoutNotify(EffectiveChoiceIndex(b)); // staged pick survives a search rebuild
                dd.RefreshShownValue();
                // Only force the popup shut / hide the template when the dropdown is CLOSED. Doing this on an open
                // dropdown would dismiss the popup the player just opened (the "won't open on first click" bug).
                bool expanded = false;
                try { expanded = dd.IsExpanded; } catch { }
                if (!expanded)
                {
                    try { dd.Hide(); } catch { }                                              // ensure the popup is closed
                    try { if (dd.template != null) dd.template.gameObject.SetActive(false); } catch { } // hide the stuck template
                }
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
                // A compact keycap + a Clear button, both FIXED width and right-aligned so they read as a small
                // keyboard key rather than stretching across the wide pane (the smoke report). Row chrome + title
                // are cloned from a real settings row so this matches the toggle / slider / dropdown rows.
                const float capW = 92f, clearW = 60f, edge = 8f, gap = 8f;
                GameObject row = CloneControlRow(b.DisplayName, edge + capW + gap + clearW, out _);

                // A light "keycap": pale raised box, dark bold key text, a bevel-ish outline + top-left highlight.
                GameObject cap = UiBuild.NewRect("KeyCap", row.transform);
                RectTransform crt = cap.GetComponent<RectTransform>();
                if (crt != null) { crt.anchorMin = crt.anchorMax = new Vector2(1f, 0.5f); crt.pivot = new Vector2(1f, 0.5f); crt.sizeDelta = new Vector2(capW, 30f); crt.anchoredPosition = new Vector2(-edge, 0f); }
                Image capBg = cap.AddComponent<Image>();
                if (capBg != null) { capBg.color = new Color(0.88f, 0.89f, 0.92f, 1f); capBg.raycastTarget = true; }
                Outline ol = cap.AddComponent<Outline>();
                if (ol != null) { ol.effectColor = new Color(0.30f, 0.32f, 0.38f, 0.95f); ol.effectDistance = new Vector2(2f, -2f); }
                Shadow sh = cap.AddComponent<Shadow>();
                if (sh != null) { sh.effectColor = new Color(1f, 1f, 1f, 0.5f); sh.effectDistance = new Vector2(-1f, 1f); } // top-left highlight
                Button btn = cap.AddComponent<Button>();

                GameObject capLbl = UiBuild.MakeLabel(_labelTemplate!, cap.transform, KeyName(EffectiveValue(b)), new Color(0.10f, 0.10f, 0.13f, 1f), 16f, false, false);
                RectTransform clrt = capLbl.GetComponent<RectTransform>();
                if (clrt != null) { clrt.anchorMin = Vector2.zero; clrt.anchorMax = Vector2.one; clrt.offsetMin = new Vector2(6f, 0f); clrt.offsetMax = new Vector2(-6f, 0f); }
                TextMeshProUGUI? capTmp = capLbl.GetComponent<TextMeshProUGUI>();
                // Auto-size so a long key name ("LeftControl", "PageDown") shrinks to fit the fixed-width cap.
                if (capTmp != null) { capTmp.alignment = TextAlignmentOptions.Center; try { capTmp.fontStyle = FontStyles.Bold; capTmp.enableAutoSizing = true; capTmp.fontSizeMin = 9f; capTmp.fontSizeMax = 16f; } catch { } }

                SetSnap(b, () => { try { if (capTmp != null) capTmp.text = KeyName(b.Default); } catch { } });

                UiListeners.OnClick(btn, () =>
                {
                    if (_capturing == b) { EndCapture(); return; } // a second click cancels
                    if (_capturing != null) EndCapture();          // restore a DIFFERENT row left mid-capture
                    _capturing = b;
                    _capturingLabel = capTmp;
                    _capturePrevText = capTmp != null ? capTmp.text : "";
                    if (capTmp != null) capTmp.text = "Press a key…";
                });

                // A small "Clear" button so KeyCode.None (the disabled/unbound value many mods use) is reachable,
                // capture alone can never produce None, since there is no key to press for it.
                GameObject clr = UiBuild.NewRect("KeyClear", row.transform);
                RectTransform xrt = clr.GetComponent<RectTransform>();
                if (xrt != null) { xrt.anchorMin = xrt.anchorMax = new Vector2(1f, 0.5f); xrt.pivot = new Vector2(1f, 0.5f); xrt.sizeDelta = new Vector2(clearW, 26f); xrt.anchoredPosition = new Vector2(-(edge + capW + gap), 0f); }
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
                    StageOrClear(b, KeyCode.None.ToString() != (b.Value?.ToString() ?? ""), KeyCode.None, () => b.SetKey(KeyCode.None));
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
            UiBuild.MakeLabel(_labelTemplate!, _controlParent!, $"{b.DisplayName}: {val}  (read-only)", UiTheme.FaintText, 15f, true, false);
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
                float inputW = isText ? 300f : 170f;        // a fixed-width well, right-aligned (not a % of the wide pane)
                float swatchRoom = isText ? 32f : 0f;       // space reserved at the row's right edge for the colour swatch

                GameObject row = CloneControlRow(b.DisplayName, inputW + swatchRoom, out _); // game row chrome + title

                GameObject inGo = UnityEngine.Object.Instantiate(_refInput!, row.transform, false);
                inGo.name = "Input";
                inGo.SetActive(true);
                RectTransform irt = inGo.GetComponent<RectTransform>();
                if (irt != null) { irt.anchorMin = irt.anchorMax = new Vector2(1f, 0.5f); irt.pivot = new Vector2(1f, 0.5f); irt.sizeDelta = new Vector2(inputW, 30f); irt.anchoredPosition = new Vector2(-(swatchRoom + 4f), 0f); irt.localScale = Vector3.one; }

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
                input.SetTextWithoutNotify(EffectiveValue(b)?.ToString() ?? ""); // staged text survives a search rebuild

                Image? swatch = isText ? MakeSwatch(row.transform) : null;
                if (swatch != null) UpdateSwatch(swatch, input.text);

                WireInputEndEdit(b, input);
                if (swatch != null) { Image sw = swatch; UiListeners.OnValueChanged(input, s => UpdateSwatch(sw, s)); }
                SetSnap(b, () => { try { input.SetTextWithoutNotify(b.Default?.ToString() ?? ""); if (swatch != null) UpdateSwatch(swatch, input.text); } catch { } });
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

                // Disable any FOREIGN behaviour the clone source carried. The clone source is just "some loaded
                // TMP_InputField" (Resources.FindObjectsOfTypeAll), on the main menu that is often the money-HUD
                // field, whose updater script keeps running on our clone and rewrites it every frame with the
                // game's "$0.<size=...>00" string (shown literally since we set richText=false) AND thrashes
                // layout (which destabilises the right-pane scroll). Keep ONLY the input field, its text/bg
                // graphics, and the essential masking/scroll bits; switch every other component off. Foreign
                // game types resolve to a base wrapper, but Behaviour.enabled works regardless of the real type.
                foreach (Behaviour comp in inGo.GetComponentsInChildren<Behaviour>(true))
                {
                    if (comp == null) continue;
                    try
                    {
                        if (comp.TryCast<TMP_InputField>() != null) continue; // the field itself
                        if (comp.TryCast<Graphic>() != null) continue;        // Image bg + TMP text/placeholder
                        if (comp.TryCast<RectMask2D>() != null) continue;
                        if (comp.TryCast<Mask>() != null) continue;
                        if (comp.TryCast<ScrollRect>() != null) continue;     // a long-text field's own viewport scroll
                        comp.enabled = false;
                    }
                    catch { }
                }

                if (txt != null)
                {
                    txt.enableAutoSizing = false;
                    txt.fontSize = 18f;
                    txt.richText = false; // show colour codes etc. literally, never parse the value as TMP markup
                    txt.color = UiTheme.InputText; // dark text on the pale well (readable)
                    // Vertical mode MUST be Middle (Left), not Midline: the Midline metric sits low so the caret's
                    // top clips off (the half-caret); Left = horizontal-Left + vertical-Middle gives a full caret.
                    txt.alignment = TextAlignmentOptions.Left;
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

                // Dark caret on the pale well + a delineating outline, matching the scratch/search fields.
                try { input.customCaretColor = true; input.caretColor = UiTheme.InputText; input.selectionColor = new Color(0.30f, 0.55f, 1f, 0.45f); } catch { }
                try { Outline ol = inGo.GetComponent<Outline>(); if (ol == null) ol = inGo.AddComponent<Outline>(); if (ol != null) { ol.effectColor = UiTheme.InputOutline; ol.effectDistance = new Vector2(1f, -1f); } } catch { }
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
            img.color = UiTheme.InputBg; // pale field (readable dark text), matching the search/scratch wells
            img.raycastTarget = true;
        }

        // Fallback when no real TMP_InputField is available to clone.
        private static void AddScratchInput(ConfigBinding b)
        {
            try
            {
                bool isText = b.Kind == ControlKind.TextInput;
                float inputW = isText ? 300f : 170f;        // a fixed-width well, right-aligned (not a % of the wide pane)
                float swatchRoom = isText ? 32f : 0f;       // space reserved at the row's right edge for the colour swatch

                GameObject row = CloneControlRow(b.DisplayName, inputW + swatchRoom, out _); // game row chrome + title

                GameObject inGo = UiBuild.NewRect("Input", row.transform);
                RectTransform irt = inGo.GetComponent<RectTransform>();
                if (irt != null) { irt.anchorMin = irt.anchorMax = new Vector2(1f, 0.5f); irt.pivot = new Vector2(1f, 0.5f); irt.sizeDelta = new Vector2(inputW, 30f); irt.anchoredPosition = new Vector2(-(swatchRoom + 4f), 0f); }
                Image inBg = inGo.AddComponent<Image>();
                if (inBg != null) { inBg.color = UiTheme.InputBg; inBg.raycastTarget = true; }
                Outline obg = inGo.AddComponent<Outline>();
                if (obg != null) { obg.effectColor = UiTheme.InputOutline; obg.effectDistance = new Vector2(1f, -1f); }

                GameObject area = UiBuild.NewRect("Text Area", inGo.transform);
                RectTransform art = area.GetComponent<RectTransform>();
                if (art != null) { art.anchorMin = new Vector2(0f, 0f); art.anchorMax = new Vector2(1f, 1f); art.offsetMin = new Vector2(6f, 2f); art.offsetMax = new Vector2(-6f, -2f); }
                area.AddComponent<RectMask2D>();

                TextMeshProUGUI? txt = ConfigInputText(area.transform);
                Image? swatch = isText ? MakeSwatch(row.transform) : null;

                TMP_InputField input = inGo.AddComponent<TMP_InputField>();
                if (input != null && txt != null)
                {
                    input.textViewport = art;
                    input.textComponent = txt;
                    input.contentType = b.Kind == ControlKind.IntInput ? TMP_InputField.ContentType.IntegerNumber
                                      : b.Kind == ControlKind.FloatInput ? TMP_InputField.ContentType.DecimalNumber
                                      : TMP_InputField.ContentType.Standard;
                    input.lineType = TMP_InputField.LineType.SingleLine;
                    ConfigInputCaret(input);
                    input.text = EffectiveValue(b)?.ToString() ?? ""; // staged text survives a search rebuild

                    WireInputEndEdit(b, input);
                    if (swatch != null)
                    {
                        Image sw = swatch;
                        UpdateSwatch(sw, input.text);
                        UiListeners.OnValueChanged(input, s => UpdateSwatch(sw, s));
                    }
                    TMP_InputField inputRef = input;
                    Image? swatchRef = swatch;
                    SetSnap(b, () => { try { inputRef.SetTextWithoutNotify(b.Default?.ToString() ?? ""); if (swatchRef != null) UpdateSwatch(swatchRef, inputRef.text); } catch { } });
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
                        StageOrClear(b, !Equals(parsed, b.Value), parsed, () => { b.Value = parsed; });
                    else
                    {
                        _staged.Remove(b);
                        UpdateMarker(b);
                        try { input.SetTextWithoutNotify(b.Value?.ToString() ?? ""); } catch { }
                    }
                }
                else
                {
                    StageOrClear(b, !string.Equals(s, b.Value?.ToString() ?? "", StringComparison.Ordinal), s, () => CommitInput(b, s));
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
                    // A 4th component that is present but unparseable must REJECT the whole value, not silently
                    // become 0 (transparent), otherwise "rgba(255,0,0,bad)" stages a different colour than typed.
                    if (parts.Length == 4 && !TryComp(parts[3], out a)) return false;
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

        // ── colour editor (option A: RGBA sliders + hex + live swatch) ─────────────────────────────────

        // A colour-valued string gets a richer editor than a plain text box: a large live swatch, a hex field,
        // and R/G/B/A sliders (0–255). Editing any path updates the others WITHOUT notifying (no feedback loop)
        // and stages the canonical hex string through the live ConfigEntry. Non-colour strings never reach here.
        private static void AddColorEditor(ConfigBinding b)
        {
            Transform parent = _controlParent!; // captured outside the try so the catch can restore _controlParent
            try
            {
                // Seed from the EFFECTIVE value (staged if staged, else live) so a search rebuild repaints a
                // staged colour, falling back to the default then white.
                if (!TryParseColor(EffectiveValue(b)?.ToString() ?? "", out Color c0) &&
                    !TryParseColor(b.Default?.ToString() ?? "", out c0))
                    c0 = Color.white;

                // Header = the colour row: name (left, cloned game-row chrome) + a centred "click to edit" hint + a
                // live swatch (right). The whole row is a button that expands/collapses the editor body below; the
                // body (R/G/B/A sliders + hex) is COLLAPSED BY DEFAULT so the colour rows stay compact until edited.
                GameObject head = CloneControlRow(b.DisplayName, 84f, out _);
                Image? headImg = head.GetComponent<Image>();
                if (headImg != null) headImg.raycastTarget = true; // the row bg is the click surface
                Button headBtn = head.AddComponent<Button>();

                GameObject swGo = UiBuild.NewRect("Swatch", head.transform);
                RectTransform srt = swGo.GetComponent<RectTransform>();
                if (srt != null) { srt.anchorMin = new Vector2(1f, 0.5f); srt.anchorMax = new Vector2(1f, 0.5f); srt.pivot = new Vector2(1f, 0.5f); srt.sizeDelta = new Vector2(72f, 28f); srt.anchoredPosition = new Vector2(-6f, 0f); }
                Image? swatch = swGo.AddComponent<Image>();
                if (swatch != null) { swatch.raycastTarget = false; swatch.color = c0; }
                Outline so = swGo.AddComponent<Outline>();
                if (so != null) { so.effectColor = new Color(0f, 0f, 0f, 0.55f); so.effectDistance = new Vector2(1f, -1f); }

                GameObject hintGo = UiBuild.MakeLabel(_labelTemplate!, head.transform, "( click to edit )", new Color(0.16f, 0.16f, 0.22f, 0.92f), 14f, false, false);
                RectTransform hrt = hintGo.GetComponent<RectTransform>();
                if (hrt != null) { hrt.anchorMin = new Vector2(0f, 0f); hrt.anchorMax = new Vector2(1f, 1f); hrt.offsetMin = new Vector2(0f, 0f); hrt.offsetMax = new Vector2(-90f, 0f); } // centred, clear of the swatch
                TextMeshProUGUI? hintTmp = hintGo.GetComponent<TextMeshProUGUI>();
                if (hintTmp != null) { hintTmp.alignment = TextAlignmentOptions.Center; try { hintTmp.fontStyle = FontStyles.Italic; } catch { } }

                // The collapsible editor body (collapsed by default), holding the hex field + R/G/B/A sliders.
                Transform container = UiBuild.MakeCard(parent, UiTheme.CardBg);
                container.gameObject.SetActive(false);

                // Sync plumbing, assigned after the controls exist; the wiring lambdas capture these vars and
                // call the latest assignment. `updating` guards the (already no-notify) cross-updates.
                bool updating = false;
                Action push = () => { };
                Action<string> pull = _ => { };

                // Route the hex row (CloneControlRow uses _controlParent) + sliders into the collapsible body.
                _controlParent = container;
                TMP_InputField? hex = MakeHexField(container, b, Canon(c0), s => pull(s));
                (Slider? slR, Action<float>? setR) = AddSlider(_refSlider!, container, "R", 0f, 255f, c0.r * 255f, true, _ => push());
                (Slider? slG, Action<float>? setG) = AddSlider(_refSlider!, container, "G", 0f, 255f, c0.g * 255f, true, _ => push());
                (Slider? slB, Action<float>? setB) = AddSlider(_refSlider!, container, "B", 0f, 255f, c0.b * 255f, true, _ => push());
                (Slider? slA, Action<float>? setA) = AddSlider(_refSlider!, container, "A", 0f, 255f, c0.a * 255f, true, _ => push());
                _controlParent = parent;

                UiListeners.OnClick(headBtn, () =>
                {
                    bool show = !container.gameObject.activeSelf;
                    container.gameObject.SetActive(show);
                    if (hintTmp != null) hintTmp.text = show ? "( click to collapse )" : "( click to edit )";
                    UiBuild.RefreshScrollHint(_rightContent);
                });

                push = () =>
                {
                    if (updating || slR == null || slG == null || slB == null) return;
                    updating = true;
                    try
                    {
                        float a = slA != null ? slA.value : 255f;
                        Color c = new Color(slR.value / 255f, slG.value / 255f, slB.value / 255f, a / 255f);
                        if (swatch != null) swatch.color = c;
                        string canon = Canon(c);
                        if (hex != null) hex.SetTextWithoutNotify(canon);
                        StageColor(b, canon);
                    }
                    finally { updating = false; }
                };

                pull = s =>
                {
                    if (updating || !TryParseColor(s, out Color c)) return;
                    updating = true;
                    try
                    {
                        setR?.Invoke(c.r * 255f); setG?.Invoke(c.g * 255f); setB?.Invoke(c.b * 255f); setA?.Invoke(c.a * 255f);
                        if (swatch != null) swatch.color = c;
                        StageColor(b, Canon(c));
                    }
                    finally { updating = false; }
                };

                SetSnap(b, () =>
                {
                    if (!TryParseColor(b.Default?.ToString() ?? "", out Color d)) return;
                    updating = true;
                    try
                    {
                        setR?.Invoke(d.r * 255f); setG?.Invoke(d.g * 255f); setB?.Invoke(d.b * 255f); setA?.Invoke(d.a * 255f);
                        if (swatch != null) swatch.color = d;
                        if (hex != null) hex.SetTextWithoutNotify(Canon(d));
                    }
                    finally { updating = false; }
                });
            }
            catch (Exception ex)
            {
                _controlParent = parent; // a mid-build throw may have left it pointed at the (collapsed) container
                Plugin.Logger.LogWarning($"[ModsTab] colour editor '{b.Key}' failed: {ex.GetType().Name}: {ex.Message}");
                AddInputField(b); // degrade to the plain text field + swatch
            }
        }

        // A cloned single-line hex input ("#RRGGBB[AA]") for the colour editor. Returns null if no field template
        // exists (the caller keeps the sliders). Reuses NormalizeClonedInput to flatten the arbitrary clone.
        private static TMP_InputField? MakeHexField(Transform parent, ConfigBinding b, string initial, Action<string> onEdit)
        {
            if (_refInput == null) return null;
            try
            {
                const float hexW = 200f;
                GameObject row = CloneControlRow("Hex", hexW, out _); // game row chrome + title, matching the other rows

                GameObject inGo = UnityEngine.Object.Instantiate(_refInput, row.transform, false);
                inGo.name = "Hex Input";
                inGo.SetActive(true);
                RectTransform irt = inGo.GetComponent<RectTransform>();
                if (irt != null) { irt.anchorMin = irt.anchorMax = new Vector2(1f, 0.5f); irt.pivot = new Vector2(1f, 0.5f); irt.sizeDelta = new Vector2(hexW, 30f); irt.anchoredPosition = new Vector2(-4f, 0f); irt.localScale = Vector3.one; }

                TMP_InputField? input = inGo.GetComponent<TMP_InputField>();
                if (input == null) { UnityEngine.Object.Destroy(inGo); return null; }

                foreach (TextMeshProUGUI t in inGo.GetComponentsInChildren<TextMeshProUGUI>(true)) UiBuild.DisableLoc(t);
                NormalizeClonedInput(inGo, input);
                input.contentType = TMP_InputField.ContentType.Standard;
                input.lineType = TMP_InputField.LineType.SingleLine;
                input.characterLimit = 9; // "#RRGGBBAA"
                input.readOnly = false;
                input.interactable = true;
                input.SetTextWithoutNotify(initial);

                UiListeners.OnValueChanged(input, onEdit); // live as they type
                UiListeners.OnEndEdit(input, onEdit);       // and on commit
                return input;
            }
            catch { return null; }
        }

        private static void StageColor(ConfigBinding b, string canon)
            => StageOrClear(b, !ValuesEqual(canon, b.Value), canon, () => b.Value = canon);

        // The canonical colour string written back: "#RRGGBB" (opaque) or "#RRGGBBAA".
        private static string Canon(Color c)
            => c.a >= 0.999f ? "#" + ColorUtility.ToHtmlStringRGB(c) : "#" + ColorUtility.ToHtmlStringRGBA(c);

        private static bool IsColorBinding(ConfigBinding b)
            => IsColorLike(b.Value?.ToString() ?? "") || IsColorLike(b.Default?.ToString() ?? "");

        // Stricter than TryParseColor: only treat a string as a colour when it LOOKS like one (#hex, rgb(...), a
        // comma list, or a bare 6/8-hex), so a plain word that happens to be an HTML colour name (e.g. "red") or
        // an arbitrary string isn't hijacked into the colour editor.
        private static bool IsColorLike(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("#")) return TryParseColor(s, out _);
            if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return TryParseColor(s, out _);
            if (s.IndexOf(',') >= 0) return TryParseColor(s, out _);
            if ((s.Length == 6 || s.Length == 8) && IsHex(s)) return TryParseColor("#" + s, out _);
            return false;
        }

        private static bool IsHex(string s)
        {
            foreach (char ch in s)
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))) return false;
            return true;
        }

        private static void ShowRightMessage(string msg)
        {
            if (_rightContent == null || _labelTemplate == null) return;
            ClearChildren(_rightContent);
            UiBuild.MakeLabel(_labelTemplate, _rightContent, msg, new Color(0.8f, 0.8f, 0.8f, 1f), 16f, true, false);
            UiBuild.ResetScrollAndRefreshHint(_rightContent); // top + single-line message never overflows, so clears any stale hint
        }

        // Custom mouse-wheel scrolling for our two panes (the ScrollRect's built-in wheel is disabled). Moves a
        // fixed FRACTION of the viewport per notch, so it feels the same on a short or a long page and a single
        // notch can never jump to the bottom (the fixed-pixel built-in did, on short pages). Scrolls the pane the
        // cursor is over; if hover can't be resolved, defaults to the right (config) pane.
        private static void WheelScroll()
        {
            // Only when our Mods tab is the visible tab, otherwise we'd react to wheel input on other settings
            // tabs or while the window is closed (and drive a hidden pane).
            try { if (_builtPanel == null || !_builtPanel.activeInHierarchy) return; } catch { return; }
            float wheel;
            try { wheel = Input.mouseScrollDelta.y; } catch { return; }
            if (wheel > -0.01f && wheel < 0.01f) return;
            // While a dropdown popup is open, let ITS option list consume the wheel, don't also scroll the
            // config pane underneath (the same notch would move both).
            if (AnyDropdownExpanded()) return;
            if (TryScrollPane(_rightContent, wheel, true)) return;
            if (TryScrollPane(_leftContent, wheel, true)) return;
            TryScrollPane(_rightContent, wheel, false); // hover unresolved (e.g. cursor over the search strip), scroll the config pane
        }

        // True while any of the current page's dropdowns has its popup open.
        private static bool AnyDropdownExpanded()
        {
            for (int i = 0; i < _pendingDropdowns.Count; i++)
            {
                TMP_Dropdown dd = _pendingDropdowns[i].Dd;
                try { if (dd != null && dd.IsExpanded) return true; } catch { }
            }
            return false;
        }

        private static bool TryScrollPane(Transform? content, float wheel, bool requireHover)
        {
            try
            {
                if (content == null) return false;
                ScrollRect sr = content.GetComponentInParent<ScrollRect>();
                if (sr == null || sr.viewport == null || sr.content == null) return false;
                if (requireHover && !MouseOverRect(sr.viewport)) return false;
                float vh = sr.viewport.rect.height;
                float range = sr.content.rect.height - vh;
                if (range <= 1f) return true; // mouse is over this pane but there's nothing to scroll
                float stepPx = Mathf.Min(vh * 0.6f, vh * 0.22f * Mathf.Abs(wheel)); // ~a fifth of a screenful per notch, capped at 0.6
                float dn = (stepPx / range) * (wheel > 0f ? 1f : -1f);            // wheel up (+) -> toward the top (normalized 1)
                sr.verticalNormalizedPosition = Mathf.Clamp01(sr.verticalNormalizedPosition + dn);
                return true;
            }
            catch { return false; }
        }

        // True when the cursor is inside this RectTransform. Uses world corners, which for a screen-space-overlay
        // canvas (the settings window) are already screen pixels, so it compares directly against the mouse.
        private static bool MouseOverRect(RectTransform rt)
        {
            try
            {
                var c = new Il2CppStructArray<Vector3>(4); // [BL, TL, TR, BR]
                rt.GetWorldCorners(c);
                Vector3 m = Input.mousePosition;
                return m.x >= c[0].x && m.x <= c[2].x && m.y >= c[0].y && m.y <= c[2].y;
            }
            catch { return false; }
        }

        // ── Tier 3: per-mod search + density ────────────────────────────────────────────────────────────

        // Whether to render the per-setting description sub-text ([UI] ShowDescriptions; default on, fail-soft).
        private static bool ShowDescriptionsEnabled()
        {
            try { return Plugin.Settings.ShowDescriptions.Value; } catch { return true; }
        }

        // Shared crisp-text setup for a scratch input field's text component: an opaque white label that fills the
        // text area, a small left margin so the caret at position 0 is not half-clipped by the area's mask, and
        // richText off so a value is never parsed as TMP markup. Returns the TextMeshProUGUI (null on failure).
        private static TextMeshProUGUI? ConfigInputText(Transform area)
        {
            GameObject txtGo = UiBuild.MakeLabel(_labelTemplate!, area, "", new Color(1f, 1f, 1f, 1f), 16f, false, false);
            RectTransform trt = txtGo.GetComponent<RectTransform>();
            if (trt != null) { trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero; trt.pivot = new Vector2(0.5f, 0.5f); }
            TextMeshProUGUI? txt = txtGo.GetComponent<TextMeshProUGUI>();
            if (txt != null)
            {
                txt.enableAutoSizing = false;
                txt.fontSize = 16f;
                txt.richText = false;
                txt.color = UiTheme.InputText; // dark text on the pale well (readable; the dark well washed it out before)
                try { txt.enableVertexGradient = false; txt.alpha = 1f; } catch { }
                // Vertical mode MUST be Middle (geometric centre), not Midline: TMP derives the caret height from the
                // line's vertical metric, and the Midline metric sits low so the glyph box rides up and the caret's
                // top clips off, the "half caret". TextAlignmentOptions.Left = horizontal-Left + vertical-Middle.
                txt.alignment = TextAlignmentOptions.Left;
                try { txt.margin = new Vector4(3f, 0f, 3f, 0f); } catch { } // keep the caret off the mask edge
                try { txt.ForceMeshUpdate(); } catch { } // refresh metrics so the caret takes our font size's full height
            }
            return txt;
        }

        // Shared caret/selection setup: a solid (non-blinking) white caret, blue selection, and NO select-all on
        // focus, so re-focusing after a deferred search rebuild leaves the caret where we placed it (at the end)
        // instead of highlighting everything (which the next keystroke would replace).
        private static void ConfigInputCaret(TMP_InputField input)
        {
            input.caretWidth = 3;
            input.customCaretColor = true;
            input.caretColor = UiTheme.InputText; // dark caret on the pale well
            input.selectionColor = new Color(0.30f, 0.55f, 1f, 0.45f);
            input.caretBlinkRate = 0f; // solid (always visible) caret
            try { input.onFocusSelectAll = false; } catch { }
        }

        // Anchor a setting label to fill its row from the left, stopping a fixed distance from the right edge so a
        // FIXED-width control (input box / keycap) sits to its right without scaling with the pane width (the smoke
        // report: text/keybind controls were "far too wide" because they used percentage anchors of a wide pane).
        private static void LayoutInputLabel(GameObject lblGo, float rightReserve)
        {
            RectTransform lrt = lblGo.GetComponent<RectTransform>();
            if (lrt != null) { lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(1f, 1f); lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = new Vector2(-(rightReserve + 14f), 0f); }
        }

        // Clone a real settings row (the toggle template) so our CUSTOM controls (keybind, text/number input,
        // colour editor) reuse the GAME's row chrome, the blue panel + bold dark title, and read IDENTICALLY to
        // the cloned toggle/slider/dropdown rows (the smoke report: keybind + input rows were formatted differently
        // from the others). The cloned toggle's checkbox is neutralized + hidden (MST-RULE-12: replace its event
        // first) leaving the right side free for the caller's control; the title is set, kept in the game's style,
        // and re-anchored to reserve `rightReserve` px on the right. Falls back to a plain scratch row if there is
        // no toggle template (then the title uses our light theme colour, legible on the fallback's neutral bg).
        private static GameObject CloneControlRow(string title, float rightReserve, out TextMeshProUGUI? titleTmp)
        {
            titleTmp = null;
            if (_refToggle != null)
            {
                try
                {
                    GameObject go = UnityEngine.Object.Instantiate(_refToggle, _controlParent!, false);
                    go.name = "MST Row " + title;
                    TameRow(go, 46f);

                    TextMeshProUGUI? t = go.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (t != null)
                    {
                        UiBuild.DisableLoc(t);
                        t.text = title;
                        try { t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Overflow; } catch { }
                        LayoutInputLabel(t.gameObject, rightReserve); // keep the game's font/colour; just reserve the control's space
                        titleTmp = t;
                    }

                    // Hide + neutralize the cloned toggle's checkbox so only the row chrome + title remain. NEVER
                    // disable a graphic that IS the row root (that would erase the blue panel we are cloning FOR).
                    foreach (Toggle tog in go.GetComponentsInChildren<Toggle>(true))
                    {
                        if (tog == null) continue;
                        try { tog.onValueChanged = new Toggle.ToggleEvent(); } catch { } // MST-RULE-12: drop the serialized game action
                        try { tog.interactable = false; tog.enabled = false; } catch { }
                        try { if (tog.graphic != null) tog.graphic.enabled = false; } catch { }                                    // checkmark
                        try { if (tog.targetGraphic != null && tog.targetGraphic.gameObject != go) tog.targetGraphic.enabled = false; } catch { } // checkbox box (never the row bg)
                        try { UnityEngine.Object.Destroy(tog); } catch { } // remove the Selectable so a row-level Button (colour header) has no rival
                    }
                    return go;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[ModsTab] clone row chrome '{title}' failed: {ex.GetType().Name}: {ex.Message}"); }
            }

            // Fallback: a plain scratch row + label (no game chrome).
            GameObject row = UiBuild.NewRect("MST Row " + title, _controlParent!);
            TameRow(row, 46f);
            GameObject lblGo = UiBuild.MakeLabel(_labelTemplate!, row.transform, title, UiTheme.LabelText, 16f, false, false);
            LayoutInputLabel(lblGo, rightReserve);
            titleTmp = lblGo.GetComponent<TextMeshProUGUI>();
            return row;
        }

        // The persistent per-mod search box (right pane top strip), lives OUTSIDE the rebuilt scroll content so
        // live-filtering keeps focus. Built FROM SCRATCH (not cloned) on purpose: cloning "any loaded
        // TMP_InputField" pulled in, on the main menu, the money-HUD field, whose updater script kept rewriting
        // the box with the game's "$0.<size=...>00" string and could not be reliably stripped off the clone. A
        // scratch field carries zero game components, so it stays clean and left-aligned.
        private static void BuildSearchField(Transform host)
        {
            try
            {
                // The "Search" label sits on the light settings strip, so it is near-black (the smoke report: the
                // old faint-grey label was almost invisible there) and bold for legibility.
                GameObject lblGo = UiBuild.MakeLabel(_labelTemplate!, host, "Search", new Color(0.06f, 0.06f, 0.09f, 1f), 16f, false, false);
                RectTransform lrt = lblGo.GetComponent<RectTransform>();
                if (lrt != null) { lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0f, 1f); lrt.pivot = new Vector2(0f, 0.5f); lrt.sizeDelta = new Vector2(70f, 0f); lrt.anchoredPosition = new Vector2(10f, 0f); }
                TextMeshProUGUI? lt = lblGo.GetComponent<TextMeshProUGUI>();
                if (lt != null) { lt.alignment = TextAlignmentOptions.MidlineLeft; try { lt.fontStyle = FontStyles.Bold; } catch { } }

                // A taller well + text area gives the caret full vertical room (the smoke report: a half-height caret).
                GameObject inGo = UiBuild.NewRect("MST SearchInput", host);
                RectTransform irt = inGo.GetComponent<RectTransform>();
                if (irt != null) { irt.anchorMin = new Vector2(0f, 0.06f); irt.anchorMax = new Vector2(1f, 0.94f); irt.offsetMin = new Vector2(80f, 0f); irt.offsetMax = new Vector2(-10f, 0f); }
                Image bg = inGo.AddComponent<Image>();
                if (bg != null) { bg.color = UiTheme.InputBg; bg.raycastTarget = true; }
                Outline obg = inGo.AddComponent<Outline>();
                if (obg != null) { obg.effectColor = UiTheme.InputOutline; obg.effectDistance = new Vector2(1f, -1f); }

                GameObject area = UiBuild.NewRect("Text Area", inGo.transform);
                RectTransform art = area.GetComponent<RectTransform>();
                if (art != null) { art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one; art.offsetMin = new Vector2(8f, 3f); art.offsetMax = new Vector2(-8f, -3f); }
                area.AddComponent<RectMask2D>();

                TextMeshProUGUI? txt = ConfigInputText(area.transform);

                TMP_InputField input = inGo.AddComponent<TMP_InputField>();
                if (input == null || txt == null) return;
                input.textViewport = art;
                input.textComponent = txt;
                input.contentType = TMP_InputField.ContentType.Standard;
                input.lineType = TMP_InputField.LineType.SingleLine;
                input.characterLimit = 64;
                ConfigInputCaret(input);
                input.readOnly = false;
                input.interactable = true;
                input.SetTextWithoutNotify("");

                UiListeners.OnValueChanged(input, OnFilterChanged); // live filter as the player types
                _searchField = input;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] search box failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The search box changed. DO NOT rebuild here: this fires from inside the TMP_InputField's own
        // onValueChanged, and tearing down + rebuilding the page synchronously from within that callback corrupts
        // the field's edit state, it deactivates after a single keystroke (the smoke report). Instead queue the
        // rebuild for the next Tick (out of the input's event), which also lets us re-assert focus afterward.
        private static void OnFilterChanged(string q)
        {
            _filterPending = (q ?? "").Trim();
            _filterDirty = true;
        }

        // Apply a queued search rebuild, then keep the search box focused with the caret at the end so typing
        // continues uninterrupted (the rebuild can drop EventSystem focus). Called from Tick(), never from the
        // input's own callback. Staged edits survive the rebuild (they key on ConfigBinding, not the GameObject).
        private static void ApplyPendingFilter()
        {
            _filterDirty = false;
            try
            {
                if (string.Equals(_filterPending, _filter, StringComparison.Ordinal)) return;
                _filter = _filterPending;
                if (_selected != null) RebuildCurrentPage();
            }
            catch { }

            // Re-assert focus only if the rebuild dropped it (deferring usually preserves it, so this is a no-op
            // that doesn't disturb the caret); place the caret at the end so the next keystroke appends.
            try
            {
                if (_searchField != null && !_searchField.isFocused)
                {
                    _searchField.ActivateInputField();
                    int end = _searchField.text != null ? _searchField.text.Length : 0;
                    _searchField.caretPosition = end;
                    _searchField.selectionAnchorPosition = end;
                    _searchField.selectionFocusPosition = end;
                }
            }
            catch { }
        }

        // Filter a mod's settings: every whitespace-separated token of the query must match (case-insensitive
        // substring) somewhere in the key / display label / description / section, so a multi-word query narrows
        // progressively. A parse error fails open (returns all) rather than hiding settings.
        private static List<ConfigBinding> FilterSettings(List<ConfigBinding> all, string query)
        {
            var matched = new List<ConfigBinding>();
            try
            {
                string[] tokens = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (ConfigBinding b in all)
                {
                    string hay = (b.Key ?? "") + "\n" + (b.DisplayName ?? "") + "\n" + (b.Description ?? "") + "\n" + (b.Section ?? "");
                    bool ok = true;
                    foreach (string tk in tokens)
                        if (hay.IndexOf(tk, StringComparison.OrdinalIgnoreCase) < 0) { ok = false; break; }
                    if (ok) matched.Add(b);
                }
            }
            catch { return all; }
            return matched;
        }

        // ── staged save / discard ─────────────────────────────────────────────────────────────────────

        // Stage only an ACTUAL change; if a control is set back to its original value (or a text field is just
        // focused and left unedited), drop the stage so the mod isn't flagged dirty / the modal isn't raised.
        // Effective is the value the entry WILL hold after Save, kept so the "modified" marker can compare
        // against the default without touching the live entry. Refreshes the row marker either way.
        private static void StageOrClear(ConfigBinding b, bool changed, object? effective, Action apply)
        {
            if (changed) _staged[b] = new StagedEdit { Apply = apply, Effective = effective };
            else _staged.Remove(b);
            UpdateMarker(b);
        }

        // The value a row effectively holds right now: its staged value if staged, else the live entry value.
        private static object? EffectiveValue(ConfigBinding b)
            => _staged.TryGetValue(b, out StagedEdit? e) ? e.Effective : b.Value;

        // Modified only when we actually know the default (HasDefault) and the effective value differs from it,
        // so an entry whose default we couldn't read shows neither a "modified" dot nor a (non-functional) reset.
        private static bool IsModified(ConfigBinding b) => b.HasDefault && !ValuesEqual(EffectiveValue(b), b.Default);

        // Index of the EFFECTIVE value (staged if staged, else live) within Choices, so a search rebuild repaints
        // a staged dropdown pick, not the live one. Falls back to the live index.
        private static int EffectiveChoiceIndex(ConfigBinding b)
        {
            try
            {
                string s = EffectiveValue(b)?.ToString() ?? "";
                if (b.Choices != null)
                    for (int i = 0; i < b.Choices.Length; i++)
                        if (string.Equals(b.Choices[i], s, StringComparison.Ordinal)) return i;
            }
            catch { }
            return b.CurrentChoiceIndex();
        }

        // Tolerant value-equality for the modified marker: numeric values compare as doubles within a small
        // relative epsilon (a slider's float vs an int/double default); everything else compares by ToString.
        private static bool ValuesEqual(object? a, object? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (IsNumericValue(a) && IsNumericValue(b))
            {
                // Integral/decimal values compare EXACTLY (via decimal), a relative double tolerance can merge
                // distinct large ulong/long/decimal values (two ulongs ~1e18 can differ by thousands yet fall
                // under it), which would hide the modified marker and skip a real reset. Only fall back to the
                // float tolerance when a float/double is actually involved.
                bool aFloat = a is float || a is double;
                bool bFloat = b is float || b is double;
                if (!aFloat && !bFloat)
                {
                    try { return Convert.ToDecimal(a) == Convert.ToDecimal(b); } catch { }
                }
                try
                {
                    double da = Convert.ToDouble(a), db = Convert.ToDouble(b);
                    double tol = Math.Max(1e-9, Math.Max(Math.Abs(da), Math.Abs(db)) * 1e-6);
                    return Math.Abs(da - db) <= tol;
                }
                catch { }
            }
            string sa = a.ToString() ?? "";
            string sb = b.ToString() ?? "";
            if (string.Equals(sa, sb, StringComparison.Ordinal)) return true;
            // Colour-aware: two colour strings of any format/case are equal when they parse to the same colour
            // (so a colour row staged as canonical hex isn't flagged "modified" against a differently-cased default).
            if (IsColorLike(sa) && IsColorLike(sb) && TryParseColor(sa, out Color ca) && TryParseColor(sb, out Color cb))
            {
                Color32 x = ca, y = cb;
                return x.r == y.r && x.g == y.g && x.b == y.b && x.a == y.a;
            }
            return false;
        }

        private static bool IsNumericValue(object o)
            => o is byte || o is sbyte || o is short || o is ushort || o is int || o is uint
               || o is long || o is ulong || o is float || o is double || o is decimal;

        // Toggle a row's "modified" dot and per-row Reset to match its current state. Safe to call before the
        // row's widgets exist (during build), it no-ops until AddMetaLine registers them.
        private static void UpdateMarker(ConfigBinding b)
        {
            try
            {
                if (!_rows.TryGetValue(b, out RowUi? r)) return;
                bool modified = IsModified(b);
                if (r.Dot != null && r.Dot.activeSelf != modified) r.Dot.SetActive(modified);
                bool showReset = modified && !b.HideDefaultButton && CanReset(b);
                if (r.Reset != null && r.Reset.activeSelf != showReset) r.Reset.SetActive(showReset);
            }
            catch { }
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
            foreach (KeyValuePair<ConfigBinding, StagedEdit> kv in _staged)
            {
                try { kv.Value.Apply(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[ModsTab] save '{kv.Key.Key}' failed: {ex.Message}"); }
            }
            int n = _staged.Count;
            _staged.Clear();
            Plugin.Logger.LogInfo($"[ModsTab] saved {n} staged mod setting(s) to the live config.");
            // Mod Settings Tool's own [UI] keys (SettingOrder / ShowDescriptions) change how pages render; rebuild
            // the current page so the change takes effect immediately rather than only on the next mod switch.
            try { if (_selected != null && string.Equals(_selected.Guid, Plugin.PluginGuid, StringComparison.OrdinalIgnoreCase)) RebuildCurrentPage(); } catch { }
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
            // Prefer the canvas our cloned tab panel lives under, context-agnostic, so the modal shows over
            // BOTH the store Escape-menu Settings and the main-menu Settings. Fall back to the active Settings
            // manager's canvas (the scene-aware finder), then to any canvas.
            try
            {
                if (_builtPanel != null)
                {
                    Canvas pc = _builtPanel.GetComponentInParent<Canvas>();
                    if (pc != null) return pc.transform;
                }
                SettingsMenuManager? sm = FindSettingsManager();
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
            StageOrClear(b, k.ToString() != (b.Value?.ToString() ?? ""), k, () => b.SetKey(k));
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

        internal static Toggle? AddToggle(GameObject refToggle, Transform parent, string label, bool initial, Action<bool> onChange)
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
                return toggle;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] toggle '{label}' failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        internal static (Slider? slider, Action<float>? set) AddSlider(GameObject refSlider, Transform parent, string label, float min, float max, float initial, bool wholeNumbers, Action<float> onChange)
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

                // A no-notify setter (value + the "label: value" title) for snap-to-default and colour-channel
                // sync, programmatic sets must not fire onValueChanged (no spurious staging).
                Action<float> set = v =>
                {
                    float c = wholeNumbers ? Mathf.Round(Mathf.Clamp(v, min, max)) : Mathf.Clamp(v, min, max);
                    try { if (slider != null) slider.SetValueWithoutNotify(c); } catch { }
                    if (title != null) title.text = $"{label}: {Format(c, wholeNumbers)}";
                };
                return (slider, set);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ModsTab] slider '{label}' failed: {ex.GetType().Name}: {ex.Message}");
                return (null, null);
            }
        }

        private static string Format(float v, bool wholeNumbers) =>
            wholeNumbers ? ((int)v).ToString() : v.ToString("0.######"); // up to 6 dp so small values (0..0.001) aren't shown as 0
    }
}
