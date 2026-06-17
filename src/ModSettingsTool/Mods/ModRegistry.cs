using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ModSettingsTool.Config;

namespace ModSettingsTool.Mods
{
    // Builds the installed-mod list the UI renders, from the BepInEx chainloader (plain managed API, not
    // IL2CPP-wrapped):
    //   * IL2CPPChainloader.Instance.Plugins        -> the loaded mods (GUID, Name, Version, DLL path) = green
    //   * each plugin's BasePlugin.Config           -> its editable settings (via ConfigBinding)
    //   * IL2CPPChainloader.Instance.DependencyErrors -> mods that FAILED to load (missing/incompatible deps) = red
    // Health is binary: loaded = green, failed-to-load = red. Mod Settings Tool is a config manager, not a
    // diagnostic tool, it does NOT scan the log or flag runtime warnings/errors. Read-only toward every other
    // mod and the game; setting a value later goes through the live ConfigEntry, that mod's own object. Never throws.
    internal static class ModRegistry
    {
        // The latest snapshot, refreshed per scene by the Host and read by the views. Replaced wholesale,
        // never mutated in place.
        internal static List<ModInfo> Cache = new();

        // Plugins bundled inside BepInEx itself, Tobey's BepInEx Pack ships utilities (File Tree, Game Info,
        // Timestamp, and possibly more later) under a hidden BepInEx/plugins/.tobey.bepinex.pack/ folder. They
        // load as real BasePlugin plugins, so the chainloader lists them, but they are loader infrastructure,
        // not mods the player chose, the list hides them. Matched by their install folder so future pack
        // additions drop out automatically; the GUID set is a backstop for the current three in case a plugin
        // ever reports no Location.
        private const string PackFolderSegment = ".tobey.bepinex.pack";
        private static readonly HashSet<string> PackGuids = new(StringComparer.OrdinalIgnoreCase)
        {
            "Tobey.FileTree",
            "Tobey.BepInEx.GameInfo",
            "Tobey.BepInEx.Timestamp",
        };

        internal static List<ModInfo> Snapshot()
        {
            var result = new List<ModInfo>();
            var byGuid = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                IL2CPPChainloader? chain = IL2CPPChainloader.Instance;
                if (chain == null)
                {
                    Plugin.Logger.LogWarning("[Registry] chainloader instance not available.");
                    return result;
                }

                // 1. The loaded plugins + their config.
                foreach (KeyValuePair<string, PluginInfo> kv in chain.Plugins)
                {
                    PluginInfo info = kv.Value;
                    BepInPlugin? meta = info?.Metadata;
                    if (meta == null) continue;

                    string guid = string.IsNullOrEmpty(meta.GUID) ? kv.Key : meta.GUID;
                    string location = info!.Location ?? "";

                    // Hide BepInEx-pack-bundled utility plugins (loader infrastructure, not player mods).
                    if (IsPackBundled(location, guid)) continue;

                    var mod = new ModInfo
                    {
                        Guid = guid,
                        Name = string.IsNullOrEmpty(meta.Name) ? guid : meta.Name,
                        Version = SafeVersion(meta),
                        Location = location,
                        Loaded = true,
                    };

                    try
                    {
                        if (info.Instance is BasePlugin bp)
                            mod.Settings = ConfigBinding.FromConfigFile(bp.Config);
                    }
                    catch (Exception ex)
                    {
                        // The mod LOADED fine; we just couldn't read its config. It stays green (and shows
                        // "No settings to change."), a read hiccup is not a load failure.
                        Plugin.Logger.LogDebug($"[Registry] config read failed for '{mod.Name}' ({ex.GetType().Name}); listing it with no settings.");
                    }

                    result.Add(mod);
                    byGuid[mod.Guid] = mod;
                }

                // 2. Dependency / load failures: a failed plugin is NOT in Plugins, so most of these become
                //    their own red entries; if a message clearly names a loaded mod, annotate that instead.
                foreach (string err in chain.DependencyErrors)
                {
                    if (string.IsNullOrWhiteSpace(err)) continue;
                    if (IsPackError(err)) continue;   // a pack-bundled plugin stays hidden even if it fails to load
                    ModInfo? target = MatchLoaded(byGuid, err);
                    if (target != null)
                    {
                        target.MarkUnhealthy(err.Trim());
                    }
                    else
                    {
                        result.Add(new ModInfo
                        {
                            Name = NameFromError(err),
                            Loaded = false,
                            Health = HealthStatus.Unhealthy,
                            // .Issues seeded below so the dedup in MarkUnhealthy is consistent
                        }.WithIssue(err.Trim()));
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Registry] snapshot failed: {ex.GetType().Name}: {ex.Message}");
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static string SafeVersion(BepInPlugin meta)
        {
            try { return meta.Version?.ToString() ?? ""; } catch { return ""; }
        }

        // True for a plugin bundled in the BepInEx pack: its DLL sits under a plugins/.tobey.bepinex.pack/
        // folder (covers the current three AND anything the pack adds later), or, as a backstop when no
        // install Location is reported, its GUID is one of the known pack utilities.
        private static bool IsPackBundled(string location, string guid)
        {
            if (!string.IsNullOrEmpty(location))
            {
                // Match the folder as a path segment, separator-agnostic: the player runs Windows ('\'), the
                // dev box is Linux ('/').
                string normalized = location.Replace('\\', '/');
                if (normalized.IndexOf("/" + PackFolderSegment + "/", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return !string.IsNullOrEmpty(guid) && PackGuids.Contains(guid);
        }

        // True only when the plugin that FAILED is itself a pack-bundled utility, so its synthetic red entry is
        // suppressed. BepInEx phrases these as "Could not load [<failed plugin>] because it has missing
        // dependencies: <dep guids>", the pack GUID can appear on EITHER side: as the failed plugin (suppress)
        // or merely as a missing dependency of a real mod (must NOT suppress, or that mod is wrongly hidden
        // instead of shown red). So match the GUID only in the HEAD of the message (before the dependency list),
        // which names the failed plugin. A future pack plugin not in the GUID set could still surface (an
        // accepted, low-likelihood gap, since the pack utilities effectively never fail to load).
        private static bool IsPackError(string err)
        {
            string head = err;
            int d = err.IndexOf("dependenc", StringComparison.OrdinalIgnoreCase); // "dependency" / "dependencies"
            if (d >= 0) head = err.Substring(0, d);
            foreach (string guid in PackGuids)
                if (head.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Annotate a loaded mod if the error names its GUID or Name; else leave it for a synthetic entry. The
        // GUID is distinctive enough for a substring match, but the display name is matched as a WHOLE TOKEN so
        // a short/common name (e.g. "API" or "Core") cannot attach an unrelated error and hide the real failure.
        private static ModInfo? MatchLoaded(Dictionary<string, ModInfo> byGuid, string err)
        {
            foreach (KeyValuePair<string, ModInfo> kv in byGuid)
            {
                if (!string.IsNullOrEmpty(kv.Key) && err.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
                if (ContainsToken(err, kv.Value.Name)) return kv.Value;
            }
            return null;
        }

        // True if token occurs in text bounded by non-identifier characters (quotes, spaces, punctuation, or the
        // string edges), so a short name does not match inside a longer word. Case-insensitive.
        private static bool ContainsToken(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return false;
            for (int from = 0; from <= text.Length - token.Length; )
            {
                int i = text.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return false;
                bool leftOk = i == 0 || !IsIdent(text[i - 1]);
                int end = i + token.Length;
                bool rightOk = end >= text.Length || !IsIdent(text[end]);
                if (leftOk && rightOk) return true;
                from = i + 1;
            }
            return false;
        }

        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

        // Best-effort display name from a dependency-error string: the first single-quoted token, else the
        // content of the first '[...]' bracket (BepInEx formats these as "Could not load [<Name> <Version>]
        // because it has missing dependencies: <guid>", strip a trailing version token), else a generic label.
        // The full message is kept as the issue text regardless.
        private static string NameFromError(string err)
        {
            try
            {
                int a = err.IndexOf('\'');
                if (a >= 0)
                {
                    int b = err.IndexOf('\'', a + 1);
                    if (b > a + 1) return err.Substring(a + 1, b - a - 1);
                }

                int lb = err.IndexOf('[');
                if (lb >= 0)
                {
                    int rb = err.IndexOf(']', lb + 1);
                    if (rb > lb + 1) return StripTrailingVersion(err.Substring(lb + 1, rb - lb - 1).Trim());
                }
            }
            catch
            {
                // fall through
            }
            return "Unloaded mod";
        }

        // "TestFailingMod 1.0.0" -> "TestFailingMod": drop a trailing token that looks like a version (starts
        // with a digit, dots/digits only). Leaves a name with no version suffix untouched.
        private static string StripTrailingVersion(string s)
        {
            int sp = s.LastIndexOf(' ');
            if (sp <= 0 || sp >= s.Length - 1) return s;
            string tail = s.Substring(sp + 1);
            if (tail.Length == 0 || !char.IsDigit(tail[0])) return s;
            foreach (char c in tail)
                if (!char.IsDigit(c) && c != '.') return s;
            return s.Substring(0, sp);
        }
    }

    internal static class ModInfoExtensions
    {
        // Fluent seed used when constructing a synthetic unhealthy entry inline.
        internal static ModInfo WithIssue(this ModInfo mod, string issue)
        {
            mod.MarkUnhealthy(issue);
            return mod;
        }
    }
}
