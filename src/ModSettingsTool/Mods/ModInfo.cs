using System.Collections.Generic;
using ModSettingsTool.Config;

namespace ModSettingsTool.Mods
{
    // Worst-wins severity. A mod's row is colored by the highest level it reaches: errors (load/dependency
    // failures, config-read failures, log errors) -> Unhealthy (red); warnings (log warnings) -> Warning
    // (amber); otherwise Healthy (green). Ordering matters, see MarkWarning/MarkUnhealthy.
    internal enum HealthStatus
    {
        Healthy,
        Warning,
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
        public readonly List<string> Issues = new();          // shown after the name when Unhealthy
        public List<ConfigBinding> Settings = new();          // editable config entries (sorted by section/key)

        public bool HasSettings => Settings.Count > 0;

        // Compact one-line issue text for the red "(info about the issue)" beside the name.
        public string IssueSummary => Issues.Count == 0 ? "" : string.Join("; ", Issues);

        // Red: errors always win, even over a prior warning.
        public void MarkUnhealthy(string issue)
        {
            Health = HealthStatus.Unhealthy;
            AddIssue(issue);
        }

        // Amber: raises a still-Healthy mod to Warning; never downgrades an already-Unhealthy one.
        public void MarkWarning(string issue)
        {
            if (Health == HealthStatus.Healthy) Health = HealthStatus.Warning;
            AddIssue(issue);
        }

        private void AddIssue(string issue)
        {
            if (!string.IsNullOrWhiteSpace(issue) && !Issues.Contains(issue)) Issues.Add(issue);
        }
    }
}
