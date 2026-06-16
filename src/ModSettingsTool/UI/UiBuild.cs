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
            if (img != null) { img.color = new Color(1f, 1f, 1f, 0.10f); img.raycastTarget = false; }
            return row;
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
            scroll.scrollSensitivity = 800f; // both Mods-tab panes; long config pages felt slow at 250

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
