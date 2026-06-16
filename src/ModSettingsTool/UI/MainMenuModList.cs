using System;
using ModSettingsTool.Mods;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace ModSettingsTool.UI
{
    // Part 1: a left-aligned installed-mod list shown on the MAIN MENU, to the right of the menu buttons,
    // before loading a save. Each mod's name is green (Healthy) / amber (Warning) / red (Unhealthy); an
    // unhealthy mod shows its issue text (ModInfo.IssueSummary) beside the name. Data comes from the Host's
    // per-scene ModRegistry.Cache; this is cloned-uGUI (no IMGUI).
    //
    // Surface mapped by the spike (docs/MOD_SETTINGS_GAME_SURFACE.md §C): FindObjectOfType<MainMenuManager>()
    // gives the "Menu" canvas; its six landing buttons are direct children (column at x=0). We parent a panel
    // under that canvas, to the right of the column, clone the styled "Window BG" for its background, and
    // clone a button's child "Text (TMP)" (a TextMeshProUGUI whose LocalizeStringEvent we disable) for each
    // row. The rows live in a ScrollRect so a long stack scrolls instead of running off the panel.
    //
    // Panel geometry constants are sensible defaults to TUNE at the smoke (exact offset/size depend on the
    // player's resolution / UI scale).
    internal static class MainMenuModList
    {
        private const string PanelName = "ModSettingsToolMenuList";

        // Anchored to the canvas TOP-RIGHT, pinned a constant margin from the right edge so it clears the
        // centre logo at any resolution, and down from the top edge.
        private static readonly Vector2 PanelSize = new Vector2(430f, 620f);
        private const float PanelRightMargin = 60f;  // gap from the screen right edge (keeps the list clear of the centre logo)
        private const float PanelTopMargin = 70f;     // gap below the screen top
        private const float Padding = 14f;

        private static GameObject? _panel;
        private static bool _warned;

        // Build once per menu entry (idempotent). Driven every frame by the Host while in the menu scene; it
        // no-ops until the "Menu" canvas exists, then builds, so no precise Harmony hook is needed.
        internal static void EnsureBuilt()
        {
            try
            {
                if (_panel != null) return;

                MainMenuManager? mgr = GameSingletons.Get<MainMenuManager>();
                if (mgr == null || mgr.transform == null) return; // menu not ready yet, retry next frame

                Transform menu = mgr.transform;
                TextMeshProUGUI? template = FindLabelTemplate(menu);
                if (template == null)
                {
                    LogOnce("[MenuList] no TMP label template found on the menu; list skipped.");
                    return;
                }

                Build(menu, template);
            }
            catch (Exception ex)
            {
                LogOnce($"[MenuList] build failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Drop the built panel + flag so the next menu entry rebuilds against a fresh canvas.
        internal static void Invalidate()
        {
            _warned = false;
            if (_panel != null)
            {
                try { UnityEngine.Object.Destroy(_panel); } catch { }
                _panel = null;
            }
        }

        private static void Build(Transform menu, TextMeshProUGUI template)
        {
            // Panel = cloned styled "Window BG" (gives an Image background + a raycast surface for the wheel).
            GameObject panel = CloneContainer(menu, template);
            panel.name = PanelName;

            RectTransform prt = panel.GetComponent<RectTransform>();
            if (prt != null)
            {
                prt.anchorMin = prt.anchorMax = new Vector2(1f, 1f); // top-RIGHT → pinned clear of the centre logo
                prt.pivot = new Vector2(1f, 1f);
                prt.anchoredPosition = new Vector2(-PanelRightMargin, -PanelTopMargin);
                prt.sizeDelta = PanelSize;
                prt.localScale = Vector3.one;
            }
            Image panelBg = panel.GetComponent<Image>();
            if (panelBg != null) panelBg.raycastTarget = true; // so the mouse wheel over the panel scrolls

            // Viewport = a clipping host filling the panel (minus padding).
            GameObject viewport = BareHost("Viewport", panel.transform, template);
            RectTransform vrt = viewport.GetComponent<RectTransform>();
            if (vrt != null)
            {
                vrt.anchorMin = new Vector2(0f, 0f);
                vrt.anchorMax = new Vector2(1f, 1f);
                vrt.pivot = new Vector2(0.5f, 1f);
                vrt.offsetMin = new Vector2(Padding, Padding);
                vrt.offsetMax = new Vector2(-Padding, -Padding);
            }
            viewport.AddComponent<RectMask2D>();

            // Content = top-anchored, grows with its rows (ContentSizeFitter), laid out vertically.
            GameObject content = BareHost("Content", viewport.transform, template);
            RectTransform crt = content.GetComponent<RectTransform>();
            if (crt != null)
            {
                crt.anchorMin = new Vector2(0f, 1f);
                crt.anchorMax = new Vector2(1f, 1f);
                crt.pivot = new Vector2(0.5f, 1f);
                crt.anchoredPosition = Vector2.zero;
                crt.sizeDelta = Vector2.zero;
            }
            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.spacing = 3f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            // ScrollRect drives content within viewport (vertical only).
            var scroll = panel.AddComponent<ScrollRect>();
            scroll.viewport = vrt;
            scroll.content = crt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            int count = ModRegistry.Cache.Count;
            AddRow(content.transform, template, $"Installed Mods ({count})", new Color(0.92f, 0.92f, 0.92f, 1f), 22f, 30f);
            foreach (ModInfo mod in ModRegistry.Cache)
                AddRow(content.transform, template, RowText(mod), HealthPalette.For(mod.Health), 18f, 24f);

            // "scroll for more" hint: a grey italic label pinned over the BOTTOM of the panel (a child of the
            // panel, not the scrolled content, and a later sibling than the viewport so it renders on top).
            // Shown only while the list overflows AND is not scrolled to the bottom.
            GameObject hint = MakeHint(panel.transform, template);
            try { Canvas.ForceUpdateCanvases(); LayoutRebuilder.ForceRebuildLayoutImmediate(crt); } catch { }
            RefreshHint(scroll, crt!, vrt!, hint);
            UiListeners.OnChanged(scroll, _ => RefreshHint(scroll, crt!, vrt!, hint));

            _panel = panel;
            Plugin.Logger.LogInfo($"[MenuList] main-menu mod list built ({count} mods, scrollable).");
        }

        private static void AddRow(Transform parent, TextMeshProUGUI template, string text, Color color, float fontSize, float height)
        {
            try
            {
                GameObject row = MakeLabel(parent, template, text, color, fontSize);
                LayoutElement le = row.GetComponent<LayoutElement>();
                if (le == null) le = row.AddComponent<LayoutElement>();
                le.minHeight = height;        // floor for a single line
                le.preferredHeight = -1f;     // defer to the TMP's wrapped preferred height so the row extends, never overlaps
                le.flexibleWidth = 1f;
            }
            catch (Exception ex)
            {
                LogOnce($"[MenuList] row '{text}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Clone the label template into a configured, display-only TMP label (anchors reset so a layout group
        // controls it cleanly).
        private static GameObject MakeLabel(Transform parent, TextMeshProUGUI template, string text, Color color, float fontSize)
        {
            GameObject go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            go.name = "MST Label";
            go.SetActive(true);

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.localScale = Vector3.one;
            }

            TextMeshProUGUI? tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp == null) tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
            {
                DisableLoc(tmp);
                tmp.enabled = true;
                tmp.text = text;
                tmp.color = color;
                tmp.fontSize = fontSize;
                tmp.alignment = TextAlignmentOptions.Left;
                tmp.raycastTarget = false; // display only; the panel background takes the wheel
                try { tmp.enableWordWrapping = true; tmp.overflowMode = TextOverflowModes.Overflow; } catch { }
            }
            return go;
        }

        // The grey italic "scroll for more" overlay pinned to the bottom of the panel (starts hidden;
        // RefreshHint toggles it).
        private static GameObject MakeHint(Transform panel, TextMeshProUGUI template)
        {
            GameObject hint = MakeLabel(panel, template, "scroll down for more…", new Color(0.72f, 0.72f, 0.72f, 0.95f), 16f);
            hint.name = "MST ScrollHint";

            TextMeshProUGUI? tmp = hint.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                tmp.alignment = TextAlignmentOptions.Center;
                try { tmp.fontStyle = FontStyles.Italic; } catch { }
            }

            RectTransform rt = hint.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(-2f * Padding, 30f);
                rt.anchoredPosition = new Vector2(0f, 4f);
            }
            hint.SetActive(false);
            return hint;
        }

        // Show the hint only when the content overflows the viewport AND we are not yet at the bottom.
        // (Content size is fixed once built, so this reads cached rects, no per-scroll layout rebuild.)
        private static void RefreshHint(ScrollRect scroll, RectTransform content, RectTransform viewport, GameObject hint)
        {
            try
            {
                if (scroll == null || content == null || viewport == null || hint == null) return;
                bool overflow = content.rect.height > viewport.rect.height + 1f;
                bool atBottom = scroll.verticalNormalizedPosition <= 0.02f;
                bool show = overflow && !atBottom;
                if (hint.activeSelf != show) hint.SetActive(show);
            }
            catch { }
        }

        // A bare RectTransform host (clone the label, drop its graphic) for the viewport / content scaffolding.
        private static GameObject BareHost(string name, Transform parent, TextMeshProUGUI template)
        {
            GameObject go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            go.name = name;
            go.SetActive(true);
            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                try { UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject); } catch { }
            }
            TextMeshProUGUI? tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp != null) { DisableLoc(tmp); try { UnityEngine.Object.Destroy(tmp); } catch { } }
            return go;
        }

        // A clean RectTransform host for the panel: clone the styled "Window BG" behind the buttons if present
        // (gives an Image background matching the game), else fall back to the label template's GameObject.
        private static GameObject CloneContainer(Transform menu, TextMeshProUGUI template)
        {
            Transform? bg = menu.Find("Window BG");
            GameObject src = bg != null ? bg.gameObject : template.gameObject;
            GameObject go = UnityEngine.Object.Instantiate(src, menu, false);

            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                try { UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject); } catch { }
            }

            // If we fell back to cloning the label, neutralize its own text rendering, we only want its host.
            TextMeshProUGUI? ownTmp = go.GetComponent<TextMeshProUGUI>();
            if (ownTmp != null) { DisableLoc(ownTmp); try { ownTmp.text = ""; ownTmp.enabled = false; } catch { } }

            go.SetActive(true);
            return go;
        }

        // A landing button's child "Text (TMP)" is the cleanest clone source (§C); fall back to any TMP label
        // under the menu canvas.
        private static TextMeshProUGUI? FindLabelTemplate(Transform menu)
        {
            string[] buttons = { "New Game Button", "Continue Button", "Load Button", "Settings Button", "Quit Button" };
            foreach (string name in buttons)
            {
                Transform? b = menu.Find(name);
                if (b == null) continue;
                TextMeshProUGUI? tmp = b.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp != null) return tmp;
            }
            try { return menu.GetComponentInChildren<TextMeshProUGUI>(true); } catch { return null; }
        }

        private static string RowText(ModInfo mod)
        {
            string ver = string.IsNullOrEmpty(mod.Version) ? "" : $"  v{mod.Version}";
            string issue = mod.Health == HealthStatus.Healthy ? "" : mod.IssueSummary; // full text; it wraps in the box
            return string.IsNullOrEmpty(issue) ? $"{mod.Name}{ver}" : $"{mod.Name}{ver}: {issue}";
        }

        private static void DisableLoc(TextMeshProUGUI tmp)
        {
            try { LocalizeStringEvent? loc = tmp.GetComponent<LocalizeStringEvent>(); if (loc != null) loc.enabled = false; }
            catch { }
        }

        private static void LogOnce(string msg)
        {
            if (_warned) return;
            _warned = true;
            Plugin.Logger.LogWarning(msg);
        }
    }
}
