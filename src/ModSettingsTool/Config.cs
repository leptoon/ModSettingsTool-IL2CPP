using BepInEx.Configuration;

namespace ModSettingsTool
{
    // How a mod's settings are ordered on its page in the in-game "Mods" tab.
    public enum SettingOrderMode
    {
        AuthorDeclaration, // the order the mod author called Bind() (the config-UI norm); default
        Alphabetical,      // sorted by section then key
    }

    // Mod Settings Tool's OWN config. (This mod's job is to surface every OTHER mod's config; these are
    // just its handful of self-settings.) Section/key names are a frozen player contract once released:
    // add keys, never rename. English only.
    internal sealed class ModConfig
    {
        // Master switch. When false the mod applies no patches and adds no UI (vanilla menus).
        public ConfigEntry<bool> Enabled { get; }

        // Show the installed-mod list on the main menu (Part 1). Off = only the in-game "Mods" tab.
        public ConfigEntry<bool> ShowMainMenuList { get; }

        // Order each mod's settings on its "Mods"-tab page: by the author's Bind() declaration order (default)
        // or alphabetically. Editable on Mod Settings Tool's own row in the tab.
        public ConfigEntry<SettingOrderMode> SettingOrder { get; }

        // Show each setting's description sub-text under its control on the "Mods" tab. Off = a more compact page
        // (control + meta line only). Editable on Mod Settings Tool's own row in the tab.
        public ConfigEntry<bool> ShowDescriptions { get; }

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

            SettingOrder = config.Bind(
                "UI",
                "SettingOrder",
                SettingOrderMode.AuthorDeclaration,
                "Order each mod's settings on its page by the author's declaration order (the order they call Bind), or alphabetically by section and key.");

            ShowDescriptions = config.Bind(
                "UI",
                "ShowDescriptions",
                true,
                "Show each setting's description text under its control on the Mods tab. Turn off for a more compact page.");
        }
    }
}
