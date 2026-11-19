using System;
using ModSettingsTool.Localization;
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
        private float _nextLocalePoll;
        private float _nextBuildPoll;
        private bool _lastEnabled = true;
        private const float LocalePollInterval = 1.0f;
        // Fast inside an armed post-open window (a real Settings open just happened, so a build is imminent);
        // slow while merely probing in degraded mode, where the surface may not appear for minutes.
        private const float BuildPollInterval = 0.5f;
        private const float DegradedPollInterval = 2.0f;

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

                if (Time.unscaledTime >= _nextLocalePoll)
                {
                    _nextLocalePoll = Time.unscaledTime + LocalePollInterval;
                    if (I18n.RefreshRuntimeLocale())
                    {
                        MainMenuModList.RefreshLocaleText();
                        ModsTab.RefreshLocaleText();
                    }
                }

                string scene = SceneManager.GetActiveScene().name;
                if (scene != _lastScene)
                {
                    _lastScene = scene;
                    // This Host survives the load (DontDestroyOnLoad), so the throttle carries a deadline set
                    // against the OLD scene. Clear it so the new scene's first build attempt is immediate
                    // rather than up to DegradedPollInterval late.
                    _nextBuildPoll = 0f;
                    OnSceneChanged(scene);
                }

                if (PatchGate.InMenu())
                {
                    // A runtime flip of [General] ShowMainMenuList must take effect live, both ways: build it when
                    // on, tear it down when off. EnsureBuilt is idempotent; Invalidate no-ops when nothing is built.
                    if (Plugin.Settings.ShowMainMenuList.Value) MainMenuModList.EnsureBuilt();
                    else MainMenuModList.Invalidate();

                    // The Mods tab is injected by the SettingsMenuManager.set_Enable(true) hook when Settings
                    // opens. Do not poll for it during normal menu frames; Unity object scans during failed
                    // build retries are visible as periodic frame hitches.
                    ModsTab.Tick();
                    BuildFallback();
                }
                else if (PatchGate.InStore())
                {
                    // Per-frame: key-rebind capture (cheap when idle).
                    ModsTab.Tick();
                    BuildFallback();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Host] Update error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The bounded build retry. It covers two distinct gaps, and ModsTab.WantsBuildRetry owns the policy:
        //
        //   1. A transient miss on a HEALTHY open. The settings rows may not exist yet at the instant
        //      set_Enable(true) fires (1.5.0 defers tab init), and TryBuild bails. The open hooks arm a short
        //      window so we retry inside the CURRENT open instead of stranding the tab until the player
        //      closes and reopens Settings.
        //   2. Degraded mode. SettingsOpenPatch is the guaranteed build point in both scenes and the only one
        //      on the main menu, so if it fails to patch, the compatibility failure Plugin.TryPatch exists to
        //      tolerate, nothing hook-driven would ever build the tab and the mod would silently lose its
        //      whole reason to exist. (It cannot restore SettingsClosePatch; that one degrades on its own and
        //      says so in the log. See MenuPatches.cs for why those are separate classes.)
        //
        // Neither can run away: the retry stops the instant the tab exists, costs no Unity object scan while
        // the settings manager resolves (ModsTab caches it, and backs off when it does NOT resolve), and
        // spends its capped budget only on attempts against a surface that is actually populated, so
        // 0.4.1's frame-hitch fix still holds. An unbounded 2 Hz FindObjectOfType is exactly what that
        // release removed.
        //
        // Two rates, because the two halves cost very different amounts. Observing whether the settings
        // window is open is a few field reads, and has to be per-frame: it is the edge that refunds the
        // build budget and arms the fast window, and sampling it at the idle rate would miss an
        // open-and-close between ticks. Actually attempting a build walks the UI tree, so it stays throttled.
        private void BuildFallback()
        {
            if (!ModsTab.WantsBuildRetry(Plugin.MenuHooksActive)) return;

            // A fresh open just armed the fast window. Any deadline booked at the slow idle rate, including
            // the one this method booked on the tick that resolved the manager, is now stale, so drop it and
            // attempt the build on this frame rather than honouring a wait that predates the open.
            if (ModsTab.ObserveOpenState()) _nextBuildPoll = 0f;

            if (Time.unscaledTime < _nextBuildPoll) return;
            _nextBuildPoll = Time.unscaledTime + (ModsTab.RetryWindowArmed ? BuildPollInterval : DegradedPollInterval);
            ModsTab.PollRetry();
        }

        private static void OnSceneChanged(string scene)
        {
            // A cached snapshot the views read; refreshed per scene (mods don't load/unload mid-scene).
            try
            {
                ModRegistry.Cache = ModRegistry.Snapshot();
                int bad = 0;
                foreach (ModInfo m in ModRegistry.Cache)
                    if (m.Health == HealthStatus.Unhealthy) bad++;
                Plugin.Logger.LogInfo($"[Registry] scene '{scene}': {ModRegistry.Cache.Count} mods, {bad} failed to load.");

                // The views must rebuild against the new scene's UI tree; release all bridged delegate roots
                // from the old scene's destroyed UI (they are re-rooted as the new scene's views rebuild).
                UiListeners.ClearAll();
                GameSingletons.Invalidate();   // never carry a manager across a scene
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
