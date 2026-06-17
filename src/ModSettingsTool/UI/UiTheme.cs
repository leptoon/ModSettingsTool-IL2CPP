using ModSettingsTool.Mods;
using UnityEngine;

namespace ModSettingsTool.UI
{
    // Central palette for the in-game "Mods" tab's right pane (Phase 4, "cohesive, clearer"). One source of
    // truth so the panes, section cards, rows, and text tiers read as a deliberate, legible set on the game's
    // dark settings window, replacing the scattered inline new Color(...) the right pane used to carry. Health
    // colours stay in HealthPalette and are surfaced through Health() so callers theme from one place.
    internal static class UiTheme
    {
        // Pane backgrounds (the two scroll views). Near-opaque: the in-game menu behind the Settings window is
        // bright and busy, so a translucent pane washed out every grey label (the first-smoke "hard to read"
        // report). A near-solid dark panel gives the descriptions and secondary text real contrast.
        internal static readonly Color PaneBg = new Color(0.05f, 0.05f, 0.07f, 0.93f);
        internal static readonly Color LeftPaneBg = new Color(0.04f, 0.04f, 0.06f, 0.95f);

        // Cards: a raised panel over the dark pane (the per-mod header gets a slightly stronger one).
        internal static readonly Color CardBg = new Color(1f, 1f, 1f, 0.06f);
        internal static readonly Color HeaderCardBg = new Color(1f, 1f, 1f, 0.10f);

        // Accent (section titles, the section card's left bar, value highlights, the modified marker).
        internal static readonly Color Accent = new Color(0.62f, 0.80f, 1f, 1f);

        // Text tiers (raised for contrast on the dark pane).
        internal static readonly Color TitleText = new Color(0.98f, 0.98f, 0.99f, 1f);
        internal static readonly Color LabelText = new Color(0.92f, 0.93f, 0.96f, 1f);
        internal static readonly Color ValueText = new Color(0.74f, 0.83f, 1f, 1f);
        internal static readonly Color DimText = new Color(0.80f, 0.81f, 0.85f, 1f);   // descriptions
        internal static readonly Color FaintText = new Color(0.66f, 0.68f, 0.74f, 1f); // guid / meta / secondary

        // Hairline separators / the plain input surface. The input box is a PALE field with dark text + caret
        // (like the game's own fields and our keycap): a dark interior made the value text hard to read (the smoke
        // report), so the well is light + opaque with a dark outline to delineate its edge.
        internal static readonly Color Separator = new Color(1f, 1f, 1f, 0.16f);
        internal static readonly Color InputBg = new Color(0.86f, 0.87f, 0.91f, 1f);
        internal static readonly Color InputText = new Color(0.10f, 0.10f, 0.13f, 1f);
        internal static readonly Color InputOutline = new Color(0f, 0f, 0f, 0.35f);

        // Every setting row sits on its own background so toggle / slider / keybind / input rows read as one
        // consistent set (the smoke report: keybind + input rows looked "naked" next to the others). A gentle
        // alternation (Tier 3 zebra) still aids scanning without competing with the card/header tiers above it.
        internal static readonly Color RowBg = new Color(1f, 1f, 1f, 0.045f);
        internal static readonly Color RowBgAlt = new Color(1f, 1f, 1f, 0.085f);

        // A faint health-tinted banner background (the per-mod header's issue strip).
        internal static Color HealthBanner(HealthStatus h)
        {
            Color c = HealthPalette.For(h);
            c.a = 0.16f;
            return c;
        }

        // Single source for health text/foreground colour.
        internal static Color Health(HealthStatus h) => HealthPalette.For(h);
    }
}
