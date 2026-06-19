using UnityEngine.SceneManagement;

namespace ModSettingsTool.Patches
{
    // Shared run-gate. Mod Settings Tool is active on the main menu (the installed-mod list, Part 1) and in
    // both store scenes, singleplayer ("Main Scene") and co-op ("Multiplayer"), where it injects the
    // Escape-menu "Mods" tab (Part 2). It is a LOCAL config editor, so it is just as valid in co-op as in
    // singleplayer: edits go to each mod's live ConfigEntry (local, BepInEx-persisted) either way. The master
    // Enabled switch prevents patching at all when off (Plugin.Load); the postfixes/host re-check it so a
    // runtime config flip also goes inert.
    internal static class PatchGate
    {
        internal const string MainMenu = "Main Menu";
        internal const string MainScene = "Main Scene";   // singleplayer store
        internal const string CoopScene = "Multiplayer";  // co-op store (host + client both load it)

        internal static bool Enabled()
        {
            ModConfig? s = Plugin.Settings;
            return s != null && s.Enabled.Value;
        }

        internal static bool InMenu() => Enabled() && SceneManager.GetActiveScene().name == MainMenu;

        // True in EITHER store scene: singleplayer ("Main Scene") or co-op ("Multiplayer"). The "Mods" tab runs
        // in both; editing is local, so co-op needs no special handling beyond showing the InCoop() advisory.
        internal static bool InStore()
        {
            if (!Enabled()) return false;
            string scene = SceneManager.GetActiveScene().name;
            return scene == MainScene || scene == CoopScene;
        }

        // True only in the co-op store. Drives a one-line informational advisory on each Mods-tab page; it never
        // gates or hides editing, nothing about co-op is blocked.
        internal static bool InCoop() => Enabled() && SceneManager.GetActiveScene().name == CoopScene;

        internal static bool Active() => InMenu() || InStore();
    }
}
