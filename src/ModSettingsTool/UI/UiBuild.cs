using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace ModSettingsTool.UI
{
    // Shared cloned-uGUI builders (no IMGUI). Clone a real TMP label so we never construct RectTransforms
    // from scratch, the same proven technique as the main-menu list. Used by the Mods tab's two-pane layout.
    internal static class UiBuild
    {
        // Clone the TMP label template into a configured label.
        internal static GameObject MakeLabel(TextMeshProUGUI template, Transform parent, string text, Color color, float fontSize, bool wrap, bool raycast)
        {
            GameObject go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            go.name = "MST Label";
            go.SetActive(true);

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt != null) { rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f); rt.localScale = Vector3.one; }

            TextMeshProUGUI? tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp == null) tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
            {
                DisableLoc(tmp);
                tmp.enabled = true;
                tmp.text = text;
                tmp.color = color;
                try { tmp.enableAutoSizing = false; } catch { } // the cloned template may carry auto-size; pin to our fontSize so sizes are predictable
                tmp.fontSize = fontSize;
                tmp.alignment = TextAlignmentOptions.Left;
                tmp.raycastTarget = raycast;
                try { tmp.enableWordWrapping = wrap; tmp.overflowMode = TextOverflowModes.Overflow; } catch { }
            }
            return go;
        }

        // A bare RectTransform host (clone the label, strip its graphic + children) for scaffolding.
        internal static GameObject BareHost(TextMeshProUGUI template, Transform parent, string name)
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

        // A fresh, Graphic-free RectTransform GameObject. Unlike a cloned label (which already carries a
        // TextMeshProUGUI Graphic, so AddComponent<Image> returns null), a fresh object accepts an Image.
        internal static GameObject NewRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            rt.localScale = Vector3.one;
            return go;
        }

        // A thin, faint horizontal divider row, for subtle visual separation between config sections in a
        // vertical layout. A short layout row carrying a centred ~2px line, inset slightly from the edges.
        internal static GameObject MakeSeparator(Transform parent)
        {
            GameObject row = NewRect("MST Separator", parent);
            LayoutElement le = row.AddComponent<LayoutElement>();
            if (le != null) { le.minHeight = 11f; le.preferredHeight = 11f; le.flexibleWidth = 1f; }

            GameObject line = NewRect("Line", row.transform);
            RectTransform lrt = line.GetComponent<RectTransform>();
            if (lrt != null)
            {
                lrt.anchorMin = new Vector2(0f, 0.5f); lrt.anchorMax = new Vector2(1f, 0.5f); lrt.pivot = new Vector2(0.5f, 0.5f);
                lrt.offsetMin = new Vector2(2f, -1f); lrt.offsetMax = new Vector2(-2f, 1f); // ~2px tall, 2px inset each side
                lrt.anchoredPosition = Vector2.zero;
            }
            Image img = line.AddComponent<Image>();
            if (img != null) { img.color = UiTheme.Separator; img.raycastTarget = false; }
            return row;
        }

        // ── Phase-4 right-pane cards & rows ("cohesive, clearer") ──────────────────────────────────────

        // A card panel added to `parent` (a vertical-layout content): a faint raised background + an inner
        // VerticalLayoutGroup. Returns the body transform to add rows into. Height comes from the parent's
        // childControlHeight reading this card's preferred height, so NO ContentSizeFitter here (a fitter on a
        // layout-controlled child fights the parent and warns).
        internal static Transform MakeCard(Transform parent, Color bg)
        {
            GameObject card = NewRect("MST Card", parent);
            Image img = card.AddComponent<Image>();
            if (img != null) { img.color = bg; img.raycastTarget = false; }
            VerticalLayoutGroup vlg = card.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 8, 10);
            vlg.spacing = 6f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            return card.transform;
        }

        // A collapsible titled section: a clickable header (bold accent left-bar + large bold UPPERCASE accent
        // title + a [-]/[+] caret) above a separate body card (the returned transform) holding the section's
        // setting blocks. Clicking the header folds/expands the body (Tier 3). startExpanded sets the initial
        // state; onToggle(expanded) reports each toggle so the caller can remember collapse state across rebuilds.
        // The title is deliberately prominent (the first-smoke "headers too small" report).
        internal static Transform MakeCollapsibleSection(TextMeshProUGUI template, Transform parent, string title, bool startExpanded, System.Action<bool>? onToggle)
            => BuildCollapsible(template, parent, title, startExpanded, onToggle);

        // A small italic sub-header for a ConfigurationManagerAttributes Category within a section card.
        internal static void MakeSubHeader(TextMeshProUGUI template, Transform parent, string text)
        {
            GameObject go = MakeLabel(template, parent, text, UiTheme.FaintText, 16f, false, false);
            LayoutElement le = go.AddComponent<LayoutElement>();
            if (le != null) { le.minHeight = 22f; le.preferredHeight = 22f; le.flexibleWidth = 1f; }
            TextMeshProUGUI? t = go.GetComponent<TextMeshProUGUI>();
            if (t != null) { try { t.fontStyle = FontStyles.Bold | FontStyles.Italic; t.margin = new Vector4(6f, 4f, 0f, 0f); } catch { } }
        }

        // A page-level collapsible "Advanced Settings (N)" container at the BOTTOM of a mod page (default
        // collapsed), every IsAdvanced / non-browsable entry across all sections collects here, surfaced
        // (never hidden), just tucked away. Same collapsible chrome as a section (Tier 3); the body holds the
        // advanced blocks (and their per-section sub-headers).
        internal static Transform MakeAdvancedContainer(TextMeshProUGUI template, Transform parent, int count, bool startExpanded, System.Action<bool>? onToggle)
            => BuildCollapsible(template, parent, $"Advanced Settings ({count})", startExpanded, onToggle);

        // Shared collapsible builder: a clickable header (accent bar + [-]/[+] caret + bold UPPERCASE accent
        // title) above a separate body card (returned). Clicking toggles the body's active state, flips the
        // caret, and reports the new state via onToggle. ASCII [+]/[-] caret (always in the font atlas).
        private static Transform BuildCollapsible(TextMeshProUGUI template, Transform parent, string title, bool startExpanded, System.Action<bool>? onToggle)
        {
            GameObject head = NewRect("MST Section Head", parent);
            LayoutElement hle = head.AddComponent<LayoutElement>();
            if (hle != null) { hle.minHeight = 36f; hle.preferredHeight = 36f; hle.flexibleWidth = 1f; }
            Image himg = head.AddComponent<Image>();
            if (himg != null) { himg.color = UiTheme.HeaderCardBg; himg.raycastTarget = true; }
            Button btn = head.AddComponent<Button>();

            GameObject bar = NewRect("Accent", head.transform);
            RectTransform brt = bar.GetComponent<RectTransform>();
            if (brt != null) { brt.anchorMin = new Vector2(0f, 0.14f); brt.anchorMax = new Vector2(0f, 0.86f); brt.pivot = new Vector2(0f, 0.5f); brt.sizeDelta = new Vector2(5f, 0f); brt.anchoredPosition = Vector2.zero; }
            Image bimg = bar.AddComponent<Image>();
            if (bimg != null) { bimg.color = UiTheme.Accent; bimg.raycastTarget = false; }

            GameObject caretGo = MakeLabel(template, head.transform, startExpanded ? "[-]" : "[+]", UiTheme.FaintText, 16f, false, false);
            RectTransform crt = caretGo.GetComponent<RectTransform>();
            if (crt != null) { crt.anchorMin = new Vector2(1f, 0f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(1f, 0.5f); crt.sizeDelta = new Vector2(40f, 0f); crt.anchoredPosition = new Vector2(-10f, 0f); }
            TextMeshProUGUI? caret = caretGo.GetComponent<TextMeshProUGUI>();
            if (caret != null) { caret.alignment = TextAlignmentOptions.MidlineRight; try { caret.fontStyle = FontStyles.Bold; } catch { } }

            GameObject lbl = MakeLabel(template, head.transform, title, UiTheme.Accent, 21f, false, false);
            RectTransform lrt = lbl.GetComponent<RectTransform>();
            if (lrt != null) { lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = new Vector2(16f, 0f); lrt.offsetMax = new Vector2(-46f, 0f); }
            TextMeshProUGUI? lt = lbl.GetComponent<TextMeshProUGUI>();
            if (lt != null) { lt.alignment = TextAlignmentOptions.MidlineLeft; try { lt.fontStyle = FontStyles.Bold | FontStyles.UpperCase; lt.characterSpacing = 4f; } catch { } }

            Transform body = MakeCard(parent, UiTheme.CardBg);
            body.gameObject.SetActive(startExpanded);

            UiListeners.OnClick(btn, () =>
            {
                bool show = !body.gameObject.activeSelf;
                body.gameObject.SetActive(show);
                if (caret != null) caret.text = show ? "[-]" : "[+]";
                if (onToggle != null) onToggle(show);
            });
            return body;
        }

        // A per-setting block (control row + optional wrapped description) inside a section card body. A vertical
        // group so the description nests tightly under its control; the card VLG separates the blocks.
        internal static Transform MakeSettingBlock(Transform parent, bool zebra)
        {
            GameObject block = NewRect("MST Setting", parent);
            // EVERY row gets a background (consistent across toggle / slider / keybind / input); the alternate
            // band is just slightly stronger so the zebra still aids scanning (Tier 3).
            Image z = block.AddComponent<Image>();
            if (z != null) { z.color = zebra ? UiTheme.RowBgAlt : UiTheme.RowBg; z.raycastTarget = false; }
            VerticalLayoutGroup vlg = block.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = 1f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            return block.transform;
        }

        // A dim, wrapped description sub-label under a control (variable height, the block sizes to the wrapped
        // text). Indented slightly so it reads as secondary to the control above it.
        internal static void MakeDescription(TextMeshProUGUI template, Transform parent, string text)
        {
            GameObject d = MakeLabel(template, parent, text, UiTheme.DimText, 15f, true, false);
            LayoutElement le = d.AddComponent<LayoutElement>();
            if (le != null) le.flexibleWidth = 1f;
            TextMeshProUGUI? tmp = d.GetComponent<TextMeshProUGUI>();
            if (tmp != null) { try { tmp.margin = new Vector4(10f, 0f, 6f, 2f); } catch { } }
        }

        // A faint health-tinted banner (the per-mod header's issue strip): a wrapped label on a tinted bg.
        internal static void MakeBanner(TextMeshProUGUI template, Transform parent, string text, Color fg, Color bg)
        {
            GameObject row = NewRect("MST Banner", parent);
            Image img = row.AddComponent<Image>();
            if (img != null) { img.color = bg; img.raycastTarget = false; }
            VerticalLayoutGroup vlg = row.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 5, 5);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            GameObject lbl = MakeLabel(template, row.transform, text, fg, 15f, true, false);
            LayoutElement le = lbl.AddComponent<LayoutElement>();
            if (le != null) le.flexibleWidth = 1f;
        }

        // Build a vertical ScrollRect filling `parent`, from fresh GameObjects. Returns the Content transform
        // (add rows there). The pane root gets a background Image that is also the wheel/drag raycast surface.
        internal static Transform BuildVerticalScroll(TextMeshProUGUI template, Transform parent, Color background)
        {
            const float pad = 6f;

            GameObject root = NewRect("MST Pane", parent);
            RectTransform rrt = root.GetComponent<RectTransform>();
            if (rrt != null) { rrt.anchorMin = new Vector2(0f, 0f); rrt.anchorMax = new Vector2(1f, 1f); rrt.offsetMin = Vector2.zero; rrt.offsetMax = Vector2.zero; }
            Image bg = root.AddComponent<Image>();
            if (bg != null) { bg.color = background; bg.raycastTarget = true; }

            GameObject viewport = NewRect("Viewport", root.transform);
            RectTransform vrt = viewport.GetComponent<RectTransform>();
            if (vrt != null) { vrt.anchorMin = new Vector2(0f, 0f); vrt.anchorMax = new Vector2(1f, 1f); vrt.offsetMin = new Vector2(pad, pad); vrt.offsetMax = new Vector2(-pad, -pad); }
            viewport.AddComponent<RectMask2D>();

            GameObject content = NewRect("Content", viewport.transform);
            RectTransform crt = content.GetComponent<RectTransform>();
            if (crt != null) { crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0.5f, 1f); crt.anchoredPosition = Vector2.zero; crt.sizeDelta = Vector2.zero; }
            VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.spacing = 6f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            ContentSizeFitter csf = content.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            ScrollRect scroll = root.AddComponent<ScrollRect>();
            scroll.viewport = vrt;
            scroll.content = crt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            // Wheel is driven by ModsTab.WheelScroll (a fixed FRACTION of the viewport per notch) rather than the
            // ScrollRect's own px-per-notch handling: a fixed px value (we had 800) overshoots short pages, one
            // notch jumped straight to the bottom, while a small value crawls on long ones. 0 disables the
            // built-in wheel so the two don't double up.
            scroll.scrollSensitivity = 0f;

            // Bottom "scroll for more" hint (grey, like the main-menu list): shown only while this pane overflows
            // AND is not scrolled to the bottom. Re-evaluated on scroll here; the caller invokes
            // ResetScrollAndRefreshHint once the pane's rows are (re)built, since the content fills/refills AFTER this returns.
            GameObject hint = MakeScrollHint(template, root.transform);
            UiListeners.OnChanged(scroll, _ => UpdateScrollHint(scroll, hint));

            return content.transform;
        }

        private const string ScrollHintName = "MST ScrollHint";

        // The grey italic "scroll down for more…" overlay pinned to the bottom of a pane (starts hidden;
        // UpdateScrollHint toggles it). A child of the pane root and a later sibling than the viewport, so it
        // renders on top of the scrolled content and is not clipped by the viewport mask.
        private static GameObject MakeScrollHint(TextMeshProUGUI template, Transform paneRoot)
        {
            GameObject hint = MakeLabel(template, paneRoot, "scroll down for more…", new Color(0.72f, 0.72f, 0.72f, 0.95f), 16f, false, false);
            hint.name = ScrollHintName;

            TextMeshProUGUI? tmp = hint.GetComponent<TextMeshProUGUI>();
            if (tmp == null) tmp = hint.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
            {
                tmp.alignment = TextAlignmentOptions.Center;
                try { tmp.fontStyle = FontStyles.Italic; } catch { }
            }

            RectTransform? rt = hint.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(-12f, 26f); // span the pane width minus the 6px viewport pad each side
                rt.anchoredPosition = new Vector2(0f, 3f);
            }
            hint.SetActive(false);
            return hint;
        }

        // Call after a pane's rows are (re)built (the left mod list fills after BuildVerticalScroll; the right
        // config page rebuilds per mod): snap the pane back to the TOP, a freshly built page should start at its
        // header, and it gives the hint a known baseline, then re-evaluate the bottom scroll hint. Content size
        // only changes at these points, so a one-shot layout rebuild keeps the overflow read accurate without
        // per-frame work.
        internal static void ResetScrollAndRefreshHint(Transform? content)
        {
            try
            {
                if (content == null) return;
                ScrollRect? scroll = content.GetComponentInParent<ScrollRect>();
                if (scroll == null) return;
                Transform? hint = scroll.transform.Find(ScrollHintName);
                if (hint == null) return;
                RectTransform? crt = content.GetComponent<RectTransform>();
                try { Canvas.ForceUpdateCanvases(); if (crt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(crt); } catch { }
                try { scroll.verticalNormalizedPosition = 1f; } catch { } // 1 = top
                UpdateScrollHint(scroll, hint.gameObject);
            }
            catch { }
        }

        // Re-evaluate the overflow hint WITHOUT moving the scroll position, for a collapse/expand toggle, where
        // the content height changes but the player's scroll position should be left where it is (a reset-to-top
        // would be jarring mid-page). Forces a one-shot layout rebuild so the overflow read is accurate.
        internal static void RefreshScrollHint(Transform? content)
        {
            try
            {
                if (content == null) return;
                ScrollRect? scroll = content.GetComponentInParent<ScrollRect>();
                if (scroll == null) return;
                Transform? hint = scroll.transform.Find(ScrollHintName);
                if (hint == null) return;
                RectTransform? crt = content.GetComponent<RectTransform>();
                try { Canvas.ForceUpdateCanvases(); if (crt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(crt); } catch { }
                UpdateScrollHint(scroll, hint.gameObject);
            }
            catch { }
        }

        // Show the hint only while the content overflows the viewport AND we are not scrolled to the bottom,
        // matching the main-menu list. Reads cached rects + the scroll position; no per-frame work.
        private static void UpdateScrollHint(ScrollRect scroll, GameObject hint)
        {
            try
            {
                if (scroll == null || hint == null) return;
                RectTransform? content = scroll.content;
                RectTransform? viewport = scroll.viewport;
                if (content == null || viewport == null) return;
                bool overflow = content.rect.height > viewport.rect.height + 1f;
                bool atBottom = scroll.verticalNormalizedPosition <= 0.02f;
                bool show = overflow && !atBottom;
                if (hint.activeSelf != show) hint.SetActive(show);
            }
            catch { }
        }

        internal static void DisableLoc(TextMeshProUGUI tmp)
        {
            try { LocalizeStringEvent? loc = tmp.GetComponent<LocalizeStringEvent>(); if (loc != null) loc.enabled = false; } catch { }
        }
    }
}
