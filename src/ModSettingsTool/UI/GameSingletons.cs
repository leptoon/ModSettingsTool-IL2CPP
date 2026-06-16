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
    }
}
