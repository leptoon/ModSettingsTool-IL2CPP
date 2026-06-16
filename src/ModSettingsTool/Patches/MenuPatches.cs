using System;
using HarmonyLib;

namespace ModSettingsTool.Patches
{
    // Harmony hooks that nudge the two views to (re)build at the right moments. The Host already polls per
    // frame, so these are an optimization/robustness layer, not the sole trigger, the mod works on the
    // Host poll alone, and each patch is fail-soft (a broken target degrades only its nudge).
    //
    // DONE: EscapeMenuManager.OnEnable -> ModsTab.Poll() (the in-store settings tab; target confirmed by
    // the RDC mod). STUBBED: the main-menu hook, the "Main Menu" manager type is not yet mapped (the stock
    // mod never touched that scene). The Host's per-scene MainMenuModList.EnsureBuilt() covers it until a
    // spike identifies a precise main-menu target to postfix here.
    [HarmonyPatch]
    internal static class MenuPatches
    {
        [HarmonyPatch(typeof(EscapeMenuManager), "OnEnable")]
        [HarmonyPostfix]
        private static void EscapeMenu_OnEnable_Postfix()
        {
            try
            {
                if (!PatchGate.InStore()) return;
                UI.ModsTab.Poll();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Patch] EscapeMenu OnEnable nudge failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The settings page closes by setting SettingsMenuManager.Enable = false, confirmed as the Back
        // button's wired call, and the same path Esc/Cancel takes. While the Mods tab is mid keybind-capture
        // we VETO that close (cancel the capture instead); with unsaved edits we raise the confirm modal
        // instead. Otherwise transparent (returns true), so vanilla open/close is unaffected.
        [HarmonyPatch(typeof(SettingsMenuManager), "set_Enable")]
        [HarmonyPrefix]
        private static bool Settings_SetEnable_Prefix(bool value)
        {
            try
            {
                if (!value && PatchGate.InStore() && !UI.ModsTab.OnSettingsClosing()) return false;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Patch] settings-close guard error: {ex.GetType().Name}: {ex.Message}");
            }
            return true;
        }

        // ...and opening it (Enable = true) is our guaranteed build point: the Host poll already tries to build
        // the tab the moment the store loads, but if a game build only instantiates the settings rows when the
        // page is first opened, this postfix builds synchronously here, before the frame renders, so the tab
        // never visibly pops in. Idempotent (AlreadyBuilt short-circuits if the poll already built it).
        [HarmonyPatch(typeof(SettingsMenuManager), "set_Enable")]
        [HarmonyPostfix]
        private static void Settings_SetEnable_Postfix(bool value)
        {
            try
            {
                if (value && PatchGate.InStore()) UI.ModsTab.Poll();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Patch] settings-open build nudge failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // TODO (build task): once the Main Menu manager is mapped (spike), add a postfix on its enable/show
        // method that calls UI.MainMenuModList.EnsureBuilt() so the list appears the instant the menu shows,
        // rather than on the next Host poll.
    }
}
