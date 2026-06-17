using ModSettingsTool.Mods;
using UnityEngine;

namespace ModSettingsTool.UI
{
    // The single health -> color mapping both views share (the main-menu list and the Mods tab's mod list):
    // green = Healthy (loaded), red = Unhealthy (failed to load). Binary, no amber/warning tier.
    // Tuned for legibility on the game's dark menu / settings panels.
    internal static class HealthPalette
    {
        internal static readonly Color Healthy = new Color(0.40f, 0.85f, 0.45f, 1f);   // green
        internal static readonly Color Unhealthy = new Color(0.95f, 0.38f, 0.36f, 1f); // red

        internal static Color For(HealthStatus health)
            => health == HealthStatus.Unhealthy ? Unhealthy : Healthy;
    }
}
