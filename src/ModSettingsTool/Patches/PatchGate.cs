using UnityEngine.SceneManagement;

namespace ModSettingsTool.Patches
{
    // Shared run-gate. Unlike a store-only mod, Mod Settings Tool is active in TWO scenes: the main menu
    // (the installed-mod list, Part 1) and the in-store scene (the Escape-menu "Mods" tab, Part 2). The
    // master Enabled switch prevents patching at all when off (Plugin.Load); the postfixes/host re-check
    // it so a runtime config flip also goes inert.
    internal static class PatchGate
    {
        internal const string MainMenu = "Main Menu";
        internal const string MainScene = "Main Scene";

        internal static bool Enabled()
        {
            ModConfig? s = Plugin.Settings;
            return s != null && s.Enabled.Value;
        }

        internal static bool InMenu() => Enabled() && SceneManager.GetActiveScene().name == MainMenu;

        internal static bool InStore() => Enabled() && SceneManager.GetActiveScene().name == MainScene;

        internal static bool Active() => InMenu() || InStore();
    }
}
