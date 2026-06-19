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
        public const string PluginVersion = "0.3.0";

        internal static ManualLogSource Logger = null!;
        internal static ModConfig Settings = null!;

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
            TryPatch(typeof(Patches.MenuPatches));

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
        // the whole mod (the PatchAll fail-fast lesson from the mod family).
        private void TryPatch(Type patchClass)
        {
            try
            {
                _harmony!.CreateClassProcessor(patchClass).Patch();
                Logger.LogInfo($"[Patch] applied {patchClass.Name}.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Patch] FAILED to apply {patchClass.Name}; that feature is off, the rest stays up: {ex}");
            }
        }
    }
}
