using System;
using HarmonyLib;

namespace ModSettingsTool.Patches
{
    // Harmony hooks for the Mods tab: two that nudge it to build when a settings window opens, and one that
    // guards the close. They are THREE separate patch classes on purpose.
    //
    // Harmony resolves and applies a patch class as a UNIT: one unresolvable target aborts every patch in
    // that class, not just its own. Held together they failed together, and the failure modes are not
    // equivalent. Host.BuildFallback can rebuild the tab without the build nudges, but NOTHING can replace
    // the close guard: SettingsClosePatch is the only caller of ModsTab.OnSettingsClosing(), which cancels an
    // in-flight keybind capture and raises the unsaved-changes modal. Bundled, a break in the (shared)
    // set_Enable target silently produced a rebuilt-by-fallback tab that discarded the player's edits on
    // close with no prompt and no log line.
    //
    // Split by TARGET METHOD, not by feature, so each class carries exactly one resolution risk, per
    // Plugin.Load applies each in its own try/catch and logs which one failed by name.

    // The store's escape menu opening. A PRE-WARM only: at this point the settings window usually has not
    // populated its rows yet, so TryBuild bails and the armed window covers the gap. SettingsOpenPatch is the
    // real build point in both scenes, so losing this one costs nothing but a little latency in the store.
    [HarmonyPatch]
    internal static class EscapeMenuOpenPatch
    {
        [HarmonyPatch(typeof(EscapeMenuManager), "OnEnable")]
        [HarmonyPostfix]
        private static void EscapeMenu_OnEnable_Postfix()
        {
            try
            {
                if (!PatchGate.InStore()) return;
                UI.ModsTab.ArmOpenRetry();
                UI.ModsTab.Poll();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Patch] EscapeMenu OnEnable nudge failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Opening the settings page (Enable = true) is our guaranteed build point, and the ONLY one on the main
    // menu (there is no EscapeMenuManager in that scene). Some game builds only instantiate settings rows
    // when the page is first opened, so this postfix builds synchronously here, before the frame renders,
    // without a recurring gameplay poll. Idempotent (AlreadyBuilt short-circuits).
    //
    // This is the hook Plugin.MenuHooksActive tracks: if it does not apply, nothing hook-driven will ever
    // build the tab in either scene, and the Host's bounded fallback takes over.
    [HarmonyPatch]
    internal static class SettingsOpenPatch
    {
        [HarmonyPatch(typeof(SettingsMenuManager), "set_Enable")]
        [HarmonyPostfix]
        private static void Settings_SetEnable_Postfix(bool value)
        {
            try
            {
                if (!value || !PatchGate.Active()) return;
                // Arm BEFORE the synchronous attempt: if the rows are not populated yet (the game may only
                // create them as the window opens), the Host retries within this same open rather than
                // leaving the tab missing until the player closes and reopens Settings.
                UI.ModsTab.ArmOpenRetry();
                UI.ModsTab.Poll();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Patch] settings-open build nudge failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // The settings page closes by setting SettingsMenuManager.Enable = false, confirmed as the Back button's
    // wired call, and the same path Esc/Cancel takes. While the Mods tab is mid keybind-capture we VETO that
    // close (cancel the capture instead); with unsaved edits we raise the confirm modal instead. Otherwise
    // transparent (returns true), so vanilla open/close is unaffected.
    //
    // Nothing else can provide this: the fallback polls for a BUILD opportunity, it cannot intercept a close.
    // If this class fails to apply, the Mods tab still works but its staged Save/Discard model degrades to
    // "closing loses unsaved edits", hence its own class, and hence the explicit error line Plugin.TryPatch
    // logs with this class's name.
    [HarmonyPatch]
    internal static class SettingsClosePatch
    {
        [HarmonyPatch(typeof(SettingsMenuManager), "set_Enable")]
        [HarmonyPrefix]
        private static bool Settings_SetEnable_Prefix(bool value)
        {
            try
            {
                if (!value && PatchGate.Active() && !UI.ModsTab.OnSettingsClosing()) return false;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Patch] settings-close guard error: {ex.GetType().Name}: {ex.Message}");
            }
            return true;
        }
    }
}
