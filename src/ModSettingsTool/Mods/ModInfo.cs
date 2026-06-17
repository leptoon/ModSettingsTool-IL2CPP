using System.Collections.Generic;
using ModSettingsTool.Config;

namespace ModSettingsTool.Mods
{
    // Binary load health. A mod is Unhealthy (red) ONLY when it actually FAILED TO LOAD (a chainloader
    // load/dependency failure). Everything that loaded is Healthy (green), Mod Settings Tool is a config
    // manager, not a diagnostic tool, so runtime log warnings/errors and config-read hiccups do NOT change
    // the colour. No amber/Warning tier.
    internal enum HealthStatus
    {
        Healthy,
        Unhealthy,
    }

    // One installed mod as Mod Settings Tool sees it: identity (from the BepInPlugin metadata), load
    // health, and its editable config (empty => "No settings to change."). Built by ModRegistry from the
    // BepInEx chainloader + each plugin's ConfigFile. Pure data; read-only toward the game.
    internal sealed class ModInfo
    {
        public string Guid = "";
        public string Name = "";
        public string Version = "";
        public string Location = "";      // the plugin DLL path (BepInEx.PluginInfo.Location)
        public bool Loaded;               // present in the chainloader's loaded set

        public HealthStatus Health = HealthStatus.Healthy;
        public readonly List<string> Issues = new();          // the load-failure reason(s), shown after the name when Unhealthy
        public List<ConfigBinding> Settings = new();          // editable config entries (author declaration order; the tab re-orders for display)

        public bool HasSettings => Settings.Count > 0;

        // Compact one-line issue text for the red "(why it failed to load)" beside the name.
        public string IssueSummary => Issues.Count == 0 ? "" : string.Join("; ", Issues);

        // Red: the mod failed to load. The reason is kept as the issue text.
        public void MarkUnhealthy(string issue)
        {
            Health = HealthStatus.Unhealthy;
            if (!string.IsNullOrWhiteSpace(issue) && !Issues.Contains(issue)) Issues.Add(issue);
        }
    }
}
