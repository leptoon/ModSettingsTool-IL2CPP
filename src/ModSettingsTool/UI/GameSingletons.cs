using UnityEngine;

namespace ModSettingsTool.UI
{
    // The single resolution seam for scene objects this mod looks up (EscapeMenuManager, the main-menu
    // manager, TabManager, etc.). Pure FindObjectOfType, none of the types this mod touches expose a
    // NoktaSingleton fast path the way the stock managers do. Returns null on any failure (the type isn't
    // in the active scene yet). Read-only.
    internal static class GameSingletons
    {
        internal static T? Get<T>() where T : UnityEngine.Object
        {
            try
            {
                T found = UnityEngine.Object.FindObjectOfType<T>();
                return found == null ? null : found;
            }
            catch
            {
                return null;
            }
        }

        // ── shared main-menu manager ──────────────────────────────────────────────────────────────────
        // TWO features look this up on the main menu, the mod list (MainMenuModList.EnsureBuilt) and the
        // Mods tab's build fallback (ModsTab.FindSettingsManager), and they were scanning independently.
        // One cache serves both: whichever resolves it first, both get it, and once it is cached neither
        // scans again (which also retires the mod list's per-frame scan once it has built).
        //
        // Neither caller may DEPEND on the other having run. The mod list is optional ([Main Menu] Show Mod
        // List) and the Mods tab is reached by its own path, so this is a shared benefit, never a shared
        // prerequisite, a fix that only worked while the list was enabled was itself a review finding.
        //
        // Invalidate() on every scene change, a stale manager from the previous scene must never leak.
        private static MainMenuManager? _mainMenu;

        // The manager if it is ALREADY known, without ever scanning.
        private static MainMenuManager? CachedMainMenu()
        {
            MainMenuManager? mm = _mainMenu;
            // Unity's overloaded == makes a destroyed object compare null, so a stale entry reads as absent.
            if (mm != null && mm.gameObject != null) return mm;
            _mainMenu = null;
            return null;
        }

        // The manager, scanning once if it is not already cached.
        internal static MainMenuManager? MainMenu()
        {
            MainMenuManager? mm = CachedMainMenu();
            if (mm != null) return mm;
            mm = Get<MainMenuManager>();
            _mainMenu = mm;
            return mm;
        }

        internal static void Invalidate() => _mainMenu = null;
    }
}
