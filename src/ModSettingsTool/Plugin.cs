using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using ModSettingsTool.UI;
using UnityEngine;

namespace ModSettingsTool
{
    // Entry point. BasePlugin/Load; fail-soft per-class patching; inert when disabled. Mod Settings Tool
    // surfaces every installed mod's BepInEx config in-game and shows an installed-mod health list on the
    // main menu. It reads other mods' chainloader metadata + ConfigFile (managed BepInEx API) and edits
    // them through their own live ConfigEntry objects, it never reaches into another mod's internals.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BasePlugin
    {
        public const string PluginGuid = "com.leptoon.modsettingstool";
        public const string PluginName = "Mod Settings Tool";
        public const string PluginVersion = "0.4.3";

        internal static ManualLogSource Logger = null!;
        internal static ModConfig Settings = null!;

        // True once the settings-OPEN build hook has applied. That hook (SettingsOpenPatch) is the guaranteed
        // build point in both scenes and the only one on the main menu, so if it fails to patch, the
        // compatibility failure TryPatch exists to tolerate, nothing hook-driven would ever build the Mods
        // tab. The Host watches this and falls back to a bounded build poll (Host.BuildFallback).
        //
        // Deliberately NOT "all menu patches applied": the other two classes fail independently and the
        // fallback answers neither of them. EscapeMenuOpenPatch is a redundant pre-warm (SettingsOpenPatch
        // still covers the store), and SettingsClosePatch guards the CLOSE, which no amount of build polling
        // can replace, its failure is reported by TryPatch's error line, not by this flag.
        internal static bool MenuHooksActive { get; private set; }

        // True once the settings-CLOSE guard has applied. Tracked separately because splitting the patches by
        // class did NOT make them fail independently: SettingsOpenPatch and SettingsClosePatch hook the same
        // SettingsMenuManager.set_Enable, so a game version that renames that setter kills BOTH. In that case
        // the build fallback would happily re-inject a Mods tab with no close guard, and an unguarded tab
        // loses staged edits silently (ModsTab.Invalidate is the only place that drops them without asking).
        // Separate classes still buy something real, a per-patch failure, e.g. another mod's prefix
        // conflicting with ours, takes down only its own hook, but the shared target means this flag must be
        // read on its own and never inferred from MenuHooksActive.
        internal static bool CloseGuardActive { get; private set; }

        private Harmony? _harmony;

        public override void Load()
        {
            Logger = base.Log;
            Settings = new ModConfig(Config);

            if (!Settings.Enabled.Value)
            {
                Logger.LogInfo($"[Init] {PluginName} {PluginVersion} is disabled in config; staying inert (vanilla menus).");
                return;
            }

            RegisterInjectedTypes();
            SpawnHost();

            _harmony = new Harmony(PluginGuid);

            // One class per target method, each in its own try/catch: a target the next game
            // version renames takes down its own hook and nothing else. Only the settings-open hook gates the
            // Host's build fallback, see MenuHooksActive.
            TryPatch(typeof(Patches.EscapeMenuOpenPatch));
            MenuHooksActive = TryPatch(typeof(Patches.SettingsOpenPatch));
            CloseGuardActive = TryPatch(typeof(Patches.SettingsClosePatch));

            Logger.LogInfo($"[Init] {PluginName} {PluginVersion} loaded.");
        }

        private static void RegisterInjectedTypes()
        {
            try
            {
                if (!ClassInjector.IsTypeRegisteredInIl2Cpp<Host>())
                    ClassInjector.RegisterTypeInIl2Cpp<Host>();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Init] failed to register injected types: {ex}");
            }
        }

        private static void SpawnHost()
        {
            try
            {
                var host = new GameObject("ModSettingsToolHost");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.hideFlags = HideFlags.HideAndDontSave;
                host.AddComponent<Host>();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Init] failed to spawn the host (UI off): {ex}");
            }
        }

        // One patch class in its own try/catch so a single broken target degrades that feature only, never
        // the whole mod (the PatchAll fail-fast lesson from the mod family). Returns whether it applied, so a
        // caller can arrange a fallback for what that class would have driven.
        private bool TryPatch(Type patchClass)
        {
            try
            {
                _harmony!.CreateClassProcessor(patchClass).Patch();
                Logger.LogInfo($"[Patch] applied {patchClass.Name}.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Patch] FAILED to apply {patchClass.Name}; that feature is off, the rest stays up: {ex}");
                return false;
            }
        }
    }
}
