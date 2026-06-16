using System;
using ModSettingsTool.Mods;
using ModSettingsTool.Patches;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ModSettingsTool.UI
{
    // The single persistent driver (DontDestroyOnLoad). It is the spine both UI features hang off:
    //   * in the main menu  -> build the installed-mod list (Part 1)
    //   * in the store      -> inject the Escape-menu "Mods" tab (Part 2)
    // On every scene change it refreshes the mod registry and logs a one-line summary (proof of life and
    // the data the views render). Injected MonoBehaviour: IntPtr ctor, no work in field initializers.
    //
    // NOTE (scaffold state): the two builders are stubs. The registry/health/config layer underneath is
    // real and exercised here; wiring it into cloned uGUI is the contracted build work.
    public sealed class Host : MonoBehaviour
    {
        public Host(IntPtr ptr) : base(ptr) { }

        private string _lastScene = "";
        private float _nextPoll;
        private bool _lastEnabled = true;
        private const float PollInterval = 0.5f;

        public void Update()
        {
            try
            {
                // A runtime flip of [General] Enabled (e.g. the player toggling Mod Settings Tool's own row in
                // the Mods tab) must take effect immediately, not just on restart: tear our UI down when it
                // turns off, restore it when it turns back on. Otherwise the built views linger and stay clickable.
                bool enabled = PatchGate.Enabled();
                if (enabled != _lastEnabled)
                {
                    _lastEnabled = enabled;
                    if (!enabled) { MainMenuModList.Invalidate(); ModsTab.Teardown(); }
                    else ModsTab.Restore();
                }
                if (!enabled) return;

                string scene = SceneManager.GetActiveScene().name;
                if (scene != _lastScene)
                {
                    _lastScene = scene;
                    OnSceneChanged(scene);
                }

                if (PatchGate.InMenu())
                {
                    if (Plugin.Settings.ShowMainMenuList.Value) MainMenuModList.EnsureBuilt();
                }
                else if (PatchGate.InStore())
                {
                    // Per-frame: key-rebind capture (cheap when idle).
                    ModsTab.Tick();

                    // Try to inject the Mods tab as soon as the store's settings structures exist (idempotent),
                    // so it is already present when the player loads in rather than popping in on first open.
                    if (Time.unscaledTime >= _nextPoll)
                    {
                        _nextPoll = Time.unscaledTime + PollInterval;
                        ModsTab.Poll();
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Host] Update error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnSceneChanged(string scene)
        {
            // A cached snapshot the views read; refreshed per scene (mods don't load/unload mid-scene).
            try
            {
                ModRegistry.Cache = ModRegistry.Snapshot(Plugin.Settings.ScanLogForHealth.Value);
                int warn = 0, bad = 0;
                foreach (ModInfo m in ModRegistry.Cache)
                {
                    if (m.Health == HealthStatus.Unhealthy) bad++;
                    else if (m.Health == HealthStatus.Warning) warn++;
                }
                Plugin.Logger.LogInfo($"[Registry] scene '{scene}': {ModRegistry.Cache.Count} mods, {warn} warning, {bad} unhealthy.");

                // The views must rebuild against the new scene's UI tree; release all bridged delegate roots
                // from the old scene's destroyed UI (they are re-rooted as the new scene's views rebuild).
                UiListeners.ClearAll();
                MainMenuModList.Invalidate();
                ModsTab.Invalidate();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Host] scene-change refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
