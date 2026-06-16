using ModSettingsTool.Mods;
using UnityEngine;

namespace ModSettingsTool.UI
{
    // The single health -> color mapping both views share (the main-menu list and the Mods tab's mod list):
    // green = Healthy, amber = Warning (logged warnings), red = Unhealthy (load/dependency/error failures).
    // Tuned for legibility on the game's dark menu / settings panels.
    internal static class HealthPalette
    {
        internal static readonly Color Healthy = new Color(0.40f, 0.85f, 0.45f, 1f);   // green
        internal static readonly Color Warning = new Color(0.96f, 0.76f, 0.24f, 1f);   // amber
        internal static readonly Color Unhealthy = new Color(0.95f, 0.38f, 0.36f, 1f); // red

        internal static Color For(HealthStatus health)
        {
            switch (health)
            {
                case HealthStatus.Unhealthy: return Unhealthy;
                case HealthStatus.Warning: return Warning;
                default: return Healthy;
            }
        }
    }
}
