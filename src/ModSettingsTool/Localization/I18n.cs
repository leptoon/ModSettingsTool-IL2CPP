using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using BepInEx.Configuration;
using UnityEngine;

namespace ModSettingsTool.Localization
{
    internal static class I18n
    {
        internal const string English = "en";
        internal const string ResourceName = "ModSettingsTool.Localization.LocalizationCatalog.json";

        private static readonly Dictionary<string, string> LocaleAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "en",
            ["en"] = "en",
            ["french"] = "fr",
            ["fr"] = "fr",
            ["italian"] = "it",
            ["it"] = "it",
            ["german"] = "de",
            ["de"] = "de",
            ["spanish"] = "es",
            ["spanish-spain"] = "es",
            ["spanish_spain"] = "es",
            ["es"] = "es",
            ["schinese"] = "zh-CN",
            ["simplifiedchinese"] = "zh-CN",
            ["chinesesimplified"] = "zh-CN",
            ["zh"] = "zh-CN",
            ["zh-cn"] = "zh-CN",
            ["zh-hans"] = "zh-CN",
            ["brazilian"] = "pt-BR",
            ["portuguese-brazil"] = "pt-BR",
            ["portuguese_brazil"] = "pt-BR",
            ["pt-br"] = "pt-BR",
            ["dutch"] = "nl",
            ["nl"] = "nl",
            ["japanese"] = "ja",
            ["ja"] = "ja",
            ["koreana"] = "ko",
            ["korean"] = "ko",
            ["ko"] = "ko",
            ["portuguese"] = "pt-PT",
            ["portuguese-portugal"] = "pt-PT",
            ["portuguese_portugal"] = "pt-PT",
            ["pt"] = "pt-PT",
            ["pt-pt"] = "pt-PT",
            ["russian"] = "ru",
            ["ru"] = "ru",
            ["turkish"] = "tr",
            ["tr"] = "tr",
            ["danish"] = "da",
            ["da"] = "da",
            ["finnish"] = "fi",
            ["fi"] = "fi",
            ["polish"] = "pl",
            ["pl"] = "pl",
            ["hungarian"] = "hu",
            ["hu"] = "hu",
            ["romanian"] = "ro",
            ["ro"] = "ro",
            ["czech"] = "cs",
            ["cs"] = "cs",
            ["lithuanian"] = "lt",
            ["lt"] = "lt",
        };

        private static Dictionary<string, Dictionary<string, string>> _catalog = new(StringComparer.OrdinalIgnoreCase);
        private static string _currentLocale = English;
        private static string _startupLocale = English;
        private static ConfigEntry<string>? _lastLocaleEntry;

        internal static string CurrentLocale => _currentLocale;
        internal static string StartupLocale => _startupLocale;

        internal static void Initialize(ConfigFile config)
        {
            LoadCatalog();

            string? selected = TryUnitySelectedLocale();
            string? persisted = ModSettingsTool.ModConfigDefinitions.ReadConfigString(config, ModSettingsTool.ModConfigDefinitions.LocalizationSection, ModSettingsTool.ModConfigDefinitions.LastDetectedLocaleKey)
                                ?? ModSettingsTool.ModConfigDefinitions.ReadConfigString(config, ModSettingsTool.ModConfigDefinitions.LegacyLocalizationSection, ModSettingsTool.ModConfigDefinitions.LegacyLastDetectedLocaleKey);
            string? system = TrySystemLanguage();

            _currentLocale = ResolveLocale(selected ?? persisted ?? system);
            _startupLocale = _currentLocale;
        }

        internal static void AttachLastDetectedLocale(ConfigEntry<string> entry)
        {
            _lastLocaleEntry = entry;
            PersistLocale(_currentLocale);
        }

        internal static bool RefreshRuntimeLocale()
        {
            string? selected = TryUnitySelectedLocale();
            if (string.IsNullOrWhiteSpace(selected)) return false;

            string resolved = ResolveLocale(selected);
            bool changed = !string.Equals(resolved, _currentLocale, StringComparison.OrdinalIgnoreCase);
            if (changed)
                _currentLocale = resolved;
            PersistLocale(resolved);
            return changed;
        }

        internal static string T(string key, params (string Name, string Value)[] args)
        {
            string text = Lookup(_currentLocale, key) ?? Lookup(English, key) ?? key;
            for (int i = 0; i < args.Length; i++)
            {
                string token = "{" + args[i].Name + "}";
                text = text.Replace(token, args[i].Value ?? "");
            }
            return text;
        }

        internal static string StartupT(string key, params (string Name, string Value)[] args)
        {
            string text = Lookup(_startupLocale, key) ?? Lookup(English, key) ?? key;
            for (int i = 0; i < args.Length; i++)
            {
                string token = "{" + args[i].Name + "}";
                text = text.Replace(token, args[i].Value ?? "");
            }
            return text;
        }

        internal static string ResolveLocale(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return English;

            string normalized = raw.Trim().Replace('_', '-');
            normalized = normalized.Replace(" ", "").ToLowerInvariant();

            if (LocaleAliases.TryGetValue(normalized, out string? mapped) && IsSupported(mapped)) return mapped;
            if (IsSupported(raw)) return Canonical(raw);

            int dash = normalized.IndexOf('-');
            if (dash > 0)
            {
                string neutral = normalized.Substring(0, dash);
                if (LocaleAliases.TryGetValue(neutral, out mapped) && IsSupported(mapped)) return mapped;
                if (IsSupported(neutral)) return Canonical(neutral);
            }

            return English;
        }

        private static void LoadCatalog()
        {
            if (_catalog.Count > 0) return;
            try
            {
                Assembly asm = typeof(I18n).Assembly;
                using Stream? stream = asm.GetManifestResourceStream(ResourceName);
                if (stream == null) { _catalog[English] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); return; }
                using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
                _catalog = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                if (data != null)
                {
                    foreach (KeyValuePair<string, Dictionary<string, string>> locale in data)
                        _catalog[locale.Key] = new Dictionary<string, string>(locale.Value, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                _catalog = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [English] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                };
            }
        }

        private static string? Lookup(string locale, string key)
        {
            if (_catalog.TryGetValue(locale, out Dictionary<string, string>? table) &&
                table.TryGetValue(key, out string? value) &&
                !string.IsNullOrEmpty(value))
                return value;
            return null;
        }

        private static bool IsSupported(string locale) => _catalog.ContainsKey(Canonical(locale));

        private static string Canonical(string locale)
        {
            foreach (string key in _catalog.Keys)
                if (string.Equals(key, locale, StringComparison.OrdinalIgnoreCase)) return key;
            return locale;
        }

        private static void PersistLocale(string locale)
        {
            try
            {
                if (_lastLocaleEntry == null || string.IsNullOrWhiteSpace(locale)) return;
                if (!string.Equals(_lastLocaleEntry.Value, locale, StringComparison.OrdinalIgnoreCase))
                    _lastLocaleEntry.Value = locale;
            }
            catch { }
        }

        private static string? TrySystemLanguage()
        {
            try { return Application.systemLanguage.ToString(); }
            catch { return null; }
        }

        private static string? TryUnitySelectedLocale()
        {
            try
            {
                Type? settingsType = Type.GetType("UnityEngine.Localization.Settings.LocalizationSettings, Unity.Localization");
                if (settingsType == null) return null;
                PropertyInfo? selectedProp = settingsType.GetProperty("SelectedLocale", BindingFlags.Public | BindingFlags.Static);
                object? locale = selectedProp?.GetValue(null);
                if (locale == null) return null;

                string? code = ReadLocaleCode(locale);
                if (!string.IsNullOrWhiteSpace(code)) return code;

                PropertyInfo? nameProp = locale.GetType().GetProperty("LocaleName");
                return nameProp?.GetValue(locale)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadLocaleCode(object locale)
        {
            try
            {
                object? identifier = locale.GetType().GetProperty("Identifier")?.GetValue(locale);
                if (identifier == null) return null;
                string? code = identifier.GetType().GetProperty("Code")?.GetValue(identifier)?.ToString();
                if (!string.IsNullOrWhiteSpace(code)) return code;
                return identifier.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
