using BepInEx.Configuration;

namespace ModSettingsTool
{
    // Mod Settings Tool's OWN config. (This mod's job is to surface every OTHER mod's config; these are
    // just its handful of self-settings.) Section/key names are a frozen player contract once released:
    // add keys, never rename. English only.
    internal sealed class ModConfig
    {
        // Master switch. When false the mod applies no patches and adds no UI (vanilla menus).
        public ConfigEntry<bool> Enabled { get; }

        // Show the installed-mod list on the main menu (Part 1). Off = only the in-game "Mods" tab.
        public ConfigEntry<bool> ShowMainMenuList { get; }

        // Scan BepInEx/LogOutput.log to flag mods that logged errors/warnings as unhealthy (heuristic;
        // adds the log-derived issues on top of the reliable load/dependency failures).
        public ConfigEntry<bool> ScanLogForHealth { get; }

        public ModConfig(ConfigFile config)
        {
            Enabled = config.Bind(
                "General",
                "Enabled",
                true,
                "Master switch. When false, Mod Settings Tool does nothing and the menus behave like vanilla.");

            ShowMainMenuList = config.Bind(
                "MainMenu",
                "ShowModList",
                true,
                "Show the installed-mod list (green = healthy, red = has an issue) on the main menu, to the right of the menu buttons.");

            ScanLogForHealth = config.Bind(
                "Health",
                "ScanLog",
                true,
                "Also scan BepInEx/LogOutput.log and mark a mod unhealthy if it logged errors. Heuristic (matches log sources to mod names) and on top of the reliable load/dependency-failure detection.");
        }
    }
}
