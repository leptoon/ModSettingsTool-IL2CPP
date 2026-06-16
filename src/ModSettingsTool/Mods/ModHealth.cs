using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;

namespace ModSettingsTool.Mods
{
    // Health signals beyond the chainloader's own load/dependency failures (which ModRegistry reads from
    // IL2CPPChainloader.DependencyErrors directly). This adds the optional, heuristic per-mod LOG SCAN:
    // parse BepInEx/LogOutput.log and bucket Error/Warning lines by their bracketed source name, so a mod
    // that threw at runtime (e.g. a failed Harmony patch) can be flagged. Heuristic because it matches a
    // log source string to a mod Name, noisy mods or shared source names can mis-attribute; treat the
    // result as advisory. Read-only; never writes the log.
    internal static class ModHealth
    {
        // BepInEx log line: "[Error  : Some Source] message" / "[Warning:Some Source] ...". Spacing around
        // the colon varies between versions; tolerate it and trim the captured source.
        private static readonly Regex LogLine =
            new(@"^\[(?<level>Error|Warning|Fatal)\s*:\s*(?<src>[^\]]+)\]", RegexOptions.Compiled);

        internal readonly struct Counts
        {
            public readonly int Errors;
            public readonly int Warnings;
            public Counts(int errors, int warnings) { Errors = errors; Warnings = warnings; }
        }

        // source-name (trimmed) -> (errors, warnings). Empty on any failure (missing log, IO error).
        internal static Dictionary<string, Counts> ScanLog()
        {
            var bySource = new Dictionary<string, Counts>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string? path = LogPath();
                if (path == null || !File.Exists(path)) return bySource;

                // Read a bounded tail so a multi-megabyte log can't stall the scan. The recent tail is what
                // reflects the current session's health anyway.
                string[] lines = ReadTail(path, 8000);
                var errs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var warns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (string line in lines)
                {
                    Match m = LogLine.Match(line);
                    if (!m.Success) continue;
                    string src = m.Groups["src"].Value.Trim();
                    if (src.Length == 0) continue;
                    string level = m.Groups["level"].Value;
                    if (level == "Warning") warns[src] = warns.TryGetValue(src, out int w) ? w + 1 : 1;
                    else errs[src] = errs.TryGetValue(src, out int e) ? e + 1 : 1;
                }

                var keys = new HashSet<string>(errs.Keys, StringComparer.OrdinalIgnoreCase);
                keys.UnionWith(warns.Keys);
                foreach (string k in keys)
                {
                    bySource[k] = new Counts(errs.TryGetValue(k, out int e) ? e : 0,
                                             warns.TryGetValue(k, out int w) ? w : 0);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Health] log scan failed: {ex.GetType().Name}: {ex.Message}");
            }
            return bySource;
        }

        private static string? LogPath()
        {
            try
            {
                string root = Paths.BepInExRootPath; // <game>/BepInEx
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "LogOutput.log");
            }
            catch
            {
                return null;
            }
        }

        // Last `maxLines` lines, reading at most the final ~2 MB of the file so a huge multi-mod session log
        // cannot hitch a scene change by being scanned start-to-end. Opens shared-read so it works while
        // BepInEx still holds the log open for writing.
        private static string[] ReadTail(string path, int maxLines)
        {
            const long maxBytes = 2_000_000;
            var ring = new string[maxLines];
            int count = 0, idx = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long start = Math.Max(0, fs.Length - maxBytes);
                bool partial = start > 0;
                if (partial) fs.Seek(start, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs))
                {
                    if (partial) sr.ReadLine(); // drop the first, likely partial, line after the mid-file seek
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        ring[idx] = line;
                        idx = (idx + 1) % maxLines;
                        count++;
                    }
                }
            }

            int n = Math.Min(count, maxLines);
            var result = new string[n];
            int from = count <= maxLines ? 0 : idx;
            for (int i = 0; i < n; i++) result[i] = ring[(from + i) % maxLines];
            return result;
        }
    }
}
